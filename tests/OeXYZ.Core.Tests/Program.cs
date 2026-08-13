using System.Net.Sockets;
using System.Text.Json;
using OeXYZ.Core;

List<string> passed = [];

Run("reconnect reason classification", () =>
{
    Equal(DisconnectCategory.Transient,
        DisconnectClassifier.Classify(new SocketException((int)SocketError.ConnectionReset)).Category);
    Equal(DisconnectCategory.Transient,
        DisconnectClassifier.Classify(new IOException("Remote host closed connection")).Category);
    Equal(DisconnectCategory.Permanent,
        DisconnectClassifier.Classify(new IOException("You are banned from this server")).Category);
    Equal(DisconnectCategory.Permanent,
        DisconnectClassifier.Classify(new IOException("Invalid session"), false).Category);
    Equal(DisconnectCategory.Transient,
        DisconnectClassifier.Classify(new OperationCanceledException("internal connect timeout")).Category);
    Equal(DisconnectCategory.User,
        DisconnectClassifier.Classify(new IOException("reset"), true).Category);
});

Run("bounded exponential reconnect backoff", () =>
{
    ReconnectBackoff backoff = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60), new Random(42));
    TimeSpan first = backoff.DelayForAttempt(1);
    TimeSpan second = backoff.DelayForAttempt(2);
    TimeSpan fifth = backoff.DelayForAttempt(5);
    True(first >= TimeSpan.FromSeconds(5) && first < TimeSpan.FromSeconds(6), "First delay is outside jitter bounds.");
    True(second >= TimeSpan.FromSeconds(10) && second < TimeSpan.FromSeconds(11), "Second delay is outside jitter bounds.");
    True(fifth <= TimeSpan.FromSeconds(60), "Backoff exceeded its maximum.");
});

Run("command history navigation and secret exclusion", () =>
{
    CommandHistory history = new(3);
    history.Add("/spawn");
    history.Add("hello");
    history.Add("/login never-store-this");
    history.Add("/home");
    Equal("/home", history.Previous());
    Equal("hello", history.Previous());
    Equal("/spawn", history.Previous());
    Equal("hello", history.Next());
    Equal(3, history.Entries.Count);
    True(history.Entries.All(value => !value.Contains("never-store", StringComparison.Ordinal)), "Sensitive command entered history.");
});

Run("central redaction removes command and token secrets", () =>
{
    Equal("/register [REDACTED]", SensitiveDataRedactor.RedactCommand("/register secret secret"));
    Equal("Bearer [REDACTED]", SensitiveDataRedactor.RedactText("Bearer abcdefghijklmnopqrstuvwxyz"));
    True(SensitiveDataRedactor.IsSensitiveCommand("/l password"), "Login alias was not recognized as sensitive.");
});

Run("profile v1 migration preserves unknown data", () =>
{
    const string json = """
        {
          "formatVersion": 1,
          "futureDocumentField": "kept",
          "accounts": [{ "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "displayName": "Test", "kind": "Offline", "loginHint": "Tester", "futureAccountField": 7 }],
          "servers": [{ "id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "displayName": "Local", "address": "127.0.0.1", "antiAfk": true, "autoReconnect": true, "autoRespawn": true, "futureServerField": true }]
        }
        """;
    JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
    ProfileDocument document = JsonSerializer.Deserialize<ProfileDocument>(json, options)!.Normalize();
    Equal(ProfileDocument.CurrentFormatVersion, document.FormatVersion);
    Equal(5, document.Servers[0].ReconnectInitialDelaySeconds);
    True(document.AdditionalData?.ContainsKey("futureDocumentField") == true, "Unknown document field was discarded.");
    True(document.Accounts[0].AdditionalData?.ContainsKey("futureAccountField") == true, "Unknown account field was discarded.");
    True(document.Servers[0].AdditionalData?.ContainsKey("futureServerField") == true, "Unknown server field was discarded.");
});

Run("profile repository creates a migration backup", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-core-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(path, "{\"formatVersion\":1,\"accounts\":[],\"servers\":[]}");
        ProfileRepository repository = new(path);
        ProfileDocument migrated = repository.Load();
        Equal(ProfileDocument.CurrentFormatVersion, migrated.FormatVersion);
        repository.Save(migrated);
        True(File.Exists(repository.BackupPath), "The previous profile file was not backed up.");
        Equal(ProfileDocument.CurrentFormatVersion, repository.Load().FormatVersion);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Console.WriteLine($"PASS: {passed.Count} core tests");
foreach (string name in passed) Console.WriteLine($"  - {name}");
return;

void Run(string name, Action test)
{
    test();
    passed.Add(name);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
