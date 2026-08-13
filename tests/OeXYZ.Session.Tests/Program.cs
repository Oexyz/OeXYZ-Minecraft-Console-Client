using System.IO.Compression;
using OeXYZ.Core;
using OeXYZ.Session;

string root = Path.Combine(Path.GetTempPath(), "oexyz-session-tests", Guid.NewGuid().ToString("N"));
string package = Path.Combine(root, "support.zip");
Directory.CreateDirectory(root);
try
{
    ServerProfile server = new()
    {
        DisplayName = "Local test",
        Address = "127.0.0.1",
        CustomPort = 25566,
        Version = "26.2",
        StartupCommandsEnabled = true,
        StartupCommands = ["/register do-not-include-this"]
    };
    await SupportPackageService.CreateAsync(new SupportPackageRequest(
        package,
        "1.2.0-test",
        server,
        "authentication failed access_token=super-secret-token",
        ["normal diagnostic", "/login secret-password", "Bearer abcdefghijklmnopqrstuvwxyz"],
        new Dictionary<string, long> { ["Play:Clientbound:0x7F"] = 3 },
        ResolveDns: false));

    using ZipArchive archive = ZipFile.OpenRead(package);
    string combined = string.Join("\n", archive.Entries.Select(entry =>
    {
        using StreamReader reader = new(entry.Open());
        return entry.FullName + "\n" + reader.ReadToEnd();
    }));
    True(archive.GetEntry("environment.json") is not null, "Environment report is missing.");
    True(archive.GetEntry("server-profile.json") is not null, "Sanitized server profile is missing.");
    True(archive.GetEntry("unknown-packets.json") is not null, "Unknown packet report is missing.");
    True(!combined.Contains("super-secret-token", StringComparison.Ordinal), "Access token leaked into support package.");
    True(!combined.Contains("secret-password", StringComparison.Ordinal), "Login password leaked into support package.");
    True(!combined.Contains("do-not-include-this", StringComparison.Ordinal), "Startup command secret leaked into support package.");
    True(!combined.Contains("accounts.bin", StringComparison.OrdinalIgnoreCase), "Account storage was included.");
    True(combined.Contains("[REDACTED]", StringComparison.Ordinal), "Expected redaction marker is missing.");
    Console.WriteLine("PASS: sanitized support package excludes tokens, passwords, account storage and commands");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
