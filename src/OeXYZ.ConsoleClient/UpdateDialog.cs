using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OeXYZ.ConsoleClient;

internal static class UpdateDialog
{
    public static async Task ShowForAsync(IWin32Window owner)
    {
        Control? control = owner as Control;
        Cursor? previousCursor = control?.Cursor;
        if (control is not null) control.Cursor = Cursors.WaitCursor;
        try
        {
            UpdateCheckResult result = await GitHubUpdateService.CheckAsync().ConfigureAwait(true);
            if (!result.IsUpdateAvailable)
            {
                MessageBox.Show(owner,
                    $"You are up to date.\n\nInstalled: {result.CurrentVersion}\nLatest: {result.LatestVersion}",
                    "OeXYZ Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult choice = MessageBox.Show(owner,
                $"OeXYZ {result.LatestVersion} is available.\n\n" +
                $"Installed: {result.CurrentVersion}\n\n" +
                "Download the Windows release now? The ZIP is accepted only when its SHA-256 hash matches the release checksum.",
                "OeXYZ Update Available", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (choice != DialogResult.Yes) return;

            using SaveFileDialog save = new()
            {
                Title = "Save verified OeXYZ update",
                FileName = result.AssetName,
                Filter = "ZIP archive (*.zip)|*.zip",
                AddExtension = true,
                DefaultExt = "zip",
                OverwritePrompt = true
            };
            if (save.ShowDialog(owner) != DialogResult.OK) return;

            if (control is not null) control.Cursor = Cursors.WaitCursor;
            await GitHubUpdateService.DownloadVerifiedAsync(result, save.FileName).ConfigureAwait(true);
            MessageBox.Show(owner,
                "The update was downloaded and its SHA-256 checksum was verified.\n\n" +
                "Close OeXYZ, extract the ZIP and replace the previous application file.",
                "Verified Update Downloaded", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(save.FileName)!) { UseShellExecute = true });
        }
        catch (UpdateSourceNotConfiguredException)
        {
            MessageBox.Show(owner,
                "This local developer build has no update source. GitHub release builds receive the repository URL automatically during publishing.",
                "OeXYZ Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(owner,
                "The update could not be checked or verified. Nothing was installed.\n\n" + exception.Message,
                "OeXYZ Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (control is not null) control.Cursor = previousCursor;
        }
    }
}

internal sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string ReleasePage,
    string AssetName,
    string AssetUrl,
    string ChecksumsUrl)
{
    public bool IsUpdateAvailable => LatestVersion > CurrentVersion;
}

internal static class GitHubUpdateService
{
    private const string ReleaseAssetName = "OeXYZ-Console-Client-win-x64.zip";
    private const string ChecksumsAssetName = "SHA256SUMS";
    private const long MaximumReleaseBytes = 500L * 1024 * 1024;
    private static readonly HttpClient Http = CreateClient();

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        (string owner, string repository) = ResolveRepository();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string apiUrl = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/latest";
        using HttpResponseMessage response = await Http.GetAsync(apiUrl, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        GitHubRelease release = await JsonSerializer.DeserializeAsync<GitHubRelease>(body, cancellationToken: timeout.Token)
                                .ConfigureAwait(false)
                            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        Version latest = ParseVersion(release.TagName);
        Version current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        GitHubAsset archive = release.Assets.FirstOrDefault(asset =>
                                  string.Equals(asset.Name, ReleaseAssetName, StringComparison.OrdinalIgnoreCase))
                              ?? throw new InvalidDataException($"The release does not contain {ReleaseAssetName}.");
        GitHubAsset checksums = release.Assets.FirstOrDefault(asset =>
                                    string.Equals(asset.Name, ChecksumsAssetName, StringComparison.OrdinalIgnoreCase))
                                ?? throw new InvalidDataException($"The release does not contain {ChecksumsAssetName}.");
        return new UpdateCheckResult(current, latest, release.HtmlUrl, archive.Name,
            archive.BrowserDownloadUrl, checksums.BrowserDownloadUrl);
    }

    public static async Task DownloadVerifiedAsync(
        UpdateCheckResult release,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        byte[] checksumDocument = await DownloadBytesAsync(release.ChecksumsUrl, 1024 * 1024, cancellationToken)
            .ConfigureAwait(false);
        string expectedHash = FindExpectedHash(Encoding.UTF8.GetString(checksumDocument), release.AssetName);
        string temporaryPath = destinationPath + ".download";
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            using HttpResponseMessage response = await Http.GetAsync(release.AssetUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length > MaximumReleaseBytes)
                throw new InvalidDataException("The release archive is larger than the safety limit.");
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream destination = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                if (destination.Length > MaximumReleaseBytes)
                    throw new InvalidDataException("The release archive is larger than the safety limit.");
            }

            await using FileStream downloaded = new(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken)
                .ConfigureAwait(false));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
                throw new CryptographicException("The downloaded archive does not match the published SHA-256 checksum.");
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task<byte[]> DownloadBytesAsync(
        string url,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new InvalidDataException("The checksum document is larger than the safety limit.");
        byte[] value = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (value.Length > maximumBytes) throw new InvalidDataException("The checksum document is larger than the safety limit.");
        return value;
    }

    private static string FindExpectedHash(string document, string assetName)
    {
        foreach (string rawLine in document.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            int separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0) continue;
            string hash = line[..separator];
            string name = line[separator..].Trim().TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase) &&
                hash.Length == 64 && hash.All(Uri.IsHexDigit))
                return hash.ToUpperInvariant();
        }
        throw new InvalidDataException($"No valid SHA-256 entry for {assetName} was found.");
    }

    private static (string Owner, string Repository) ResolveRepository()
    {
        string? configured = Environment.GetEnvironmentVariable("OEXYZ_UPDATE_REPOSITORY");
        configured ??= Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "RepositoryUrl", StringComparison.Ordinal))?
            .Value;
        if (string.IsNullOrWhiteSpace(configured)) throw new UpdateSourceNotConfiguredException();
        configured = configured.Trim().TrimEnd('/');
        if (Uri.TryCreate(configured, UriKind.Absolute, out Uri? repositoryUri))
        {
            if (!string.Equals(repositoryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(repositoryUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update repository must be an HTTPS github.com URL.");
            configured = repositoryUri.AbsolutePath.Trim('/');
        }
        string[] parts = configured.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(part => part.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
            throw new InvalidDataException("The update repository must have the form owner/repository.");
        return (parts[0], parts[1]);
    }

    private static Version ParseVersion(string tag)
    {
        string value = tag.Trim().TrimStart('v', 'V');
        int suffix = value.IndexOfAny(['-', '+']);
        if (suffix >= 0) value = value[..suffix];
        return Version.TryParse(value, out Version? parsed)
            ? parsed
            : throw new InvalidDataException($"The release tag '{tag}' is not a supported version number.");
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OeXYZ-Console-Client/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
}

internal sealed class UpdateSourceNotConfiguredException : InvalidOperationException;
