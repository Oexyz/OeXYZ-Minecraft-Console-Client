using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
