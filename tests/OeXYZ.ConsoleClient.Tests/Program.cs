using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using OeXYZ.Updater;

List<string> passed = [];

Run("semantic version normalization", () =>
{
    UpdateCheckResult equal = Result(new Version(1, 0, 0, 0), new Version(1, 0, 0));
    False(equal.IsUpdateAvailable, "1.0.0 and 1.0.0.0 must compare as the same release.");
    False(equal.IsCurrentNewer, "An assembly revision of zero must not make a build newer.");

    UpdateCheckResult older = Result(new Version(0, 9, 0, 0), new Version(1, 0, 0));
    True(older.IsUpdateAvailable, "A newer public release was not detected.");

    UpdateCheckResult newer = Result(new Version(1, 1, 0, 0), new Version(1, 0, 0));
    True(newer.IsCurrentNewer, "A developer build newer than the public release was not detected.");
});

await RunAsync("verified download closes the temporary file", async () =>
{
    byte[] archive = Encoding.UTF8.GetBytes("deterministic OeXYZ updater payload\n");
    string hash = Convert.ToHexString(SHA256.HashData(archive));
    byte[] manifest = Encoding.UTF8.GetBytes($"{hash}  OeXYZ-Console-Client-win-x64.zip\n");

    using TcpListener server = new(IPAddress.Loopback, 0);
    server.Start();
    int port = ((IPEndPoint)server.LocalEndpoint).Port;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    Task responses = ServeAsync(server, manifest, archive, timeout.Token);
    string destination = Path.Combine(Path.GetTempPath(), $"oexyz-updater-test-{Guid.NewGuid():N}.zip");
    try
    {
        UpdateCheckResult release = new(
            new Version(0, 9, 0), new Version(1, 0, 0), "https://example.invalid/release",
            "OeXYZ-Console-Client-win-x64.zip", $"http://127.0.0.1:{port}/archive",
            $"http://127.0.0.1:{port}/checksums");

        await GitHubUpdateService.DownloadVerifiedAsync(release, destination, timeout.Token);
        await responses;
        Equal(archive, await File.ReadAllBytesAsync(destination, timeout.Token));
        False(File.Exists(destination + ".download"), "The temporary download was not removed.");
    }
    finally
    {
        server.Stop();
        if (File.Exists(destination)) File.Delete(destination);
        if (File.Exists(destination + ".download")) File.Delete(destination + ".download");
    }
});

await RunAsync("checksum mismatch is rejected", async () =>
{
    byte[] archive = Encoding.UTF8.GetBytes("tampered payload\n");
    byte[] manifest = Encoding.UTF8.GetBytes(
        $"{new string('0', 64)}  OeXYZ-Console-Client-win-x64.zip\n");

    using TcpListener server = new(IPAddress.Loopback, 0);
    server.Start();
    int port = ((IPEndPoint)server.LocalEndpoint).Port;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
    Task responses = ServeAsync(server, manifest, archive, timeout.Token);
    string destination = Path.Combine(Path.GetTempPath(), $"oexyz-updater-reject-{Guid.NewGuid():N}.zip");
    try
    {
        UpdateCheckResult release = new(
            new Version(0, 9, 0), new Version(1, 0, 0), "https://example.invalid/release",
            "OeXYZ-Console-Client-win-x64.zip", $"http://127.0.0.1:{port}/archive",
            $"http://127.0.0.1:{port}/checksums");

        await ThrowsAsync<CryptographicException>(() =>
            GitHubUpdateService.DownloadVerifiedAsync(release, destination, timeout.Token));
        await responses;
        False(File.Exists(destination), "A checksum-mismatched archive was accepted.");
        False(File.Exists(destination + ".download"), "A rejected temporary download was not removed.");
    }
    finally
    {
        server.Stop();
        if (File.Exists(destination)) File.Delete(destination);
        if (File.Exists(destination + ".download")) File.Delete(destination + ".download");
    }
});

Run("update staging rejects path traversal", () =>
{
    string root = Path.Combine(Path.GetTempPath(), $"oexyz-updater-traversal-{Guid.NewGuid():N}");
    string archive = root + ".zip";
    try
    {
        using (FileStream file = File.Create(archive))
        using (ZipArchive zip = new(file, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("../escape.exe");
            using StreamWriter writer = new(entry.Open());
            writer.Write("unsafe");
        }
        Throws<InvalidDataException>(() => UpdateInstaller.ExtractVerifiedArchive(archive, root));
        False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escape.exe")), "Traversal entry escaped staging.");
    }
    finally
    {
        if (File.Exists(archive)) File.Delete(archive);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("validated update replaces GUI and CLI with rollback backup", () =>
{
    string root = Path.Combine(Path.GetTempPath(), $"oexyz-updater-apply-{Guid.NewGuid():N}");
    string stage = Path.Combine(root, "stage");
    string install = Path.Combine(root, "install");
    try
    {
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(install);
        byte[] oldBytes = Enumerable.Repeat((byte)1, 1024 * 1024 + 1).ToArray();
        byte[] newBytes = Enumerable.Repeat((byte)2, 1024 * 1024 + 1).ToArray();
        foreach (string name in new[] { "OeXYZ Console Client.exe", "oexyz.exe" })
        {
            File.WriteAllBytes(Path.Combine(stage, name), newBytes);
            File.WriteAllBytes(Path.Combine(install, name), oldBytes);
        }
        PreparedUpdate prepared = UpdateInstaller.ValidateStage(stage);
        string backup = UpdateInstaller.ApplyWithRollback(prepared, install);
        foreach (string name in new[] { "OeXYZ Console Client.exe", "oexyz.exe" })
        {
            Equal(newBytes, File.ReadAllBytes(Path.Combine(install, name)));
            Equal(oldBytes, File.ReadAllBytes(Path.Combine(backup, name + ".bak")));
        }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Console.WriteLine($"PASS: {passed.Count} updater tests");
foreach (string name in passed) Console.WriteLine($"  - {name}");
return;

UpdateCheckResult Result(Version current, Version latest) =>
    new(current, latest, "https://example.invalid/release", "OeXYZ-Console-Client-win-x64.zip",
        "https://example.invalid/archive", "https://example.invalid/checksums");

void Run(string name, Action test)
{
    test();
    passed.Add(name);
}

async Task RunAsync(string name, Func<Task> test)
{
    await test();
    passed.Add(name);
}

static async Task ServeAsync(
    TcpListener server,
    byte[] manifest,
    byte[] archive,
    CancellationToken cancellationToken)
{
    for (int requestNumber = 0; requestNumber < 2; requestNumber++)
    {
        using TcpClient client = await server.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        string requestLine = await reader.ReadLineAsync(cancellationToken)
                             ?? throw new InvalidDataException("The local test server received an empty request.");
        string? line;
        do
        {
            line = await reader.ReadLineAsync(cancellationToken);
        } while (!string.IsNullOrEmpty(line));

        byte[] body = requestLine.Contains("/checksums", StringComparison.Ordinal) ? manifest : archive;
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}

static void Equal(byte[] expected, byte[] actual)
{
    if (!expected.AsSpan().SequenceEqual(actual))
        throw new InvalidOperationException("Downloaded bytes differ from the verified source.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void False(bool value, string message) => True(!value, message);

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
