using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OeXYZ.Updater;

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    string ReleasePage,
    string AssetName,
    string AssetUrl,
    string ChecksumsUrl)
{
    public bool IsUpdateAvailable => Normalize(LatestVersion) > Normalize(CurrentVersion);
    public bool IsCurrentNewer => Normalize(CurrentVersion) > Normalize(LatestVersion);

    private static Version Normalize(Version value) =>
        new(value.Major, value.Minor, Math.Max(value.Build, 0));
}

public static class GitHubUpdateService
{
    private const string OfficialRepository = "Oexyz/OeXYZ-Minecraft-Console-Client";
    private const string LegacyX64AssetName = "OeXYZ-Console-Client-win-x64.zip";
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
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException(
                $"No OeXYZ Windows update asset exists for {RuntimeInformation.ProcessArchitecture}.")
        };
        string expectedSuffix = $"-{architecture}.zip";
        GitHubAsset? archive = release.Assets.FirstOrDefault(asset =>
            asset.Name.StartsWith("OeXYZ-Minecraft-Console-Client-", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase));
        if (archive is null && architecture == "win-x64")
            archive = release.Assets.FirstOrDefault(asset =>
                string.Equals(asset.Name, LegacyX64AssetName, StringComparison.OrdinalIgnoreCase));
        if (archive is null) throw new InvalidDataException($"The release does not contain an asset for {architecture}.");
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

            string actualHash;
            await using (FileStream downloaded = new(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellationToken)
                    .ConfigureAwait(false));
            }
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
        string? configured = null;
#if DEBUG
        configured = Environment.GetEnvironmentVariable("OEXYZ_UPDATE_REPOSITORY");
        configured ??= Assembly.GetEntryAssembly()?
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, "RepositoryUrl", StringComparison.Ordinal))?
            .Value;
#endif
        return ResolveRepositoryForTesting(configured, allowOverride: IsDebugBuild);
    }

    internal static (string Owner, string Repository) ResolveRepositoryForTesting(
        string? configured,
        bool allowOverride)
    {
        string trustedRepository = allowOverride && !string.IsNullOrWhiteSpace(configured)
            ? configured
            : OfficialRepository;
        return ParseRepository(trustedRepository);
    }

    private static (string Owner, string Repository) ParseRepository(string configured)
    {
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

    private const bool IsDebugBuild =
#if DEBUG
        true;
#else
        false;
#endif

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

public sealed class UpdateSourceNotConfiguredException : InvalidOperationException;
