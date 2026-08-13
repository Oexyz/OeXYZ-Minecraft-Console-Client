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
    DisconnectDecision throttled = DisconnectClassifier.Classify(
        new IOException("You were kicked. Please wait before reconnecting."));
    Equal(DisconnectCategory.Transient, throttled.Category);
    True(throttled.MinimumRetryDelay == TimeSpan.FromSeconds(60),
        "Server throttle cooldown was not preserved.");
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

Run("session restore drops stale bookmarks and keeps valid ones", () =>
{
    AccountProfile account = new() { Id = Guid.NewGuid(), DisplayName = "Account", Kind = AccountKind.Offline, LoginHint = "Tester" };
    ServerProfile server = new() { Id = Guid.NewGuid(), DisplayName = "Server", Address = "localhost" };
    ProfileDocument normalized = new ProfileDocument
    {
        Accounts = [account],
        Servers = [server],
        LastSessions =
        [
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = Guid.NewGuid(), ServerId = server.Id }
        ]
    }.Normalize();
    Equal(1, normalized.LastSessions.Count);
});

Run("headless argument parsing and documented exit codes", () =>
{
    CliArguments parsed = CliArguments.Parse([
        "run", "survival", "--account", "Main", "--config", "C:\\config\\profiles.json",
        "--log-file", "oexyz.log", "--log-level", "debug", "--inspect-packets"
    ]);
    Equal("run", parsed.Command);
    Equal("survival", parsed.Target!);
    Equal("Main", parsed.Account!);
    Equal("debug", parsed.LogLevel);
    True(parsed.InspectPackets, "Packet inspection option was not parsed.");
    Equal(0, (int)OeXYZExitCode.Success);
    Equal(2, (int)OeXYZExitCode.ProfileNotFound);
    Equal(3, (int)OeXYZExitCode.AuthenticationError);
    Equal(4, (int)OeXYZExitCode.ProtocolUnsupported);
    Equal(5, (int)OeXYZExitCode.ConnectionFailure);
    Equal(6, (int)OeXYZExitCode.PermanentServerRejection);
    Equal(LocalSessionCommand.Respawn, SessionInput.Classify(" /RESPAWN "));
    Equal(LocalSessionCommand.Disconnect, SessionInput.Classify("/disconnect"));
    Equal(LocalSessionCommand.Quit, SessionInput.Classify("/quit"));
    Equal(LocalSessionCommand.None, SessionInput.Classify("/spawn"));
    Throws<ArgumentException>(() => CliArguments.Parse(["run", "test", "--unknown"]));
});

Run("PATH helper is idempotent and reversible", () =>
{
    string separator = Path.PathSeparator.ToString();
    string target = Path.Combine(Path.GetTempPath(), "oexyz-path");
    string original = string.Join(separator, Path.GetTempPath(), AppContext.BaseDirectory);
    string installed = PathRegistration.Update(original, target, install: true);
    string installedTwice = PathRegistration.Update(installed, target + Path.DirectorySeparatorChar, install: true);
    Equal(installed, installedTwice);
    string removed = PathRegistration.Update(installedTwice, target, install: false);
    True(!removed.Contains(target, StringComparison.OrdinalIgnoreCase), "PATH entry was not removed.");
});

Run("log retention selects only expired logs", () =>
{
    DateTimeOffset now = new(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);
    IReadOnlyList<string> expired = LogRetentionService.FindExpiredFiles(
        [("old.log", now.AddDays(-31)), ("new.log", now.AddDays(-29))], 30, now);
    Equal(1, expired.Count);
    Equal("old.log", expired[0]);
    Equal(0, LogRetentionService.FindExpiredFiles([("old.log", now.AddYears(-1))], 0, now).Count);
});

Run("log retention removes oldest files above 300 MB while protecting active logs", () =>
{
    DateTimeOffset now = new(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
    IReadOnlyList<string> removed = LogRetentionService.FindFilesOverLimit(
        [
            ("active.log", now.AddHours(-3), 140L * 1024 * 1024),
            ("old.log", now.AddHours(-2), 120L * 1024 * 1024),
            ("new.log", now.AddHours(-1), 100L * 1024 * 1024)
        ],
        LogRetentionService.DefaultMaximumBytes,
        ["active.log"]);
    Equal(1, removed.Count);
    Equal("old.log", removed[0]);
});

Run("log retention enforces its cap on disk and preserves active logs", () =>
{
    string directory = Path.Combine(Path.GetTempPath(), "oexyz-log-retention-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        string active = Path.Combine(directory, "active.log");
        string oldest = Path.Combine(directory, "oldest.log");
        string newest = Path.Combine(directory, "newest.log");
        File.WriteAllBytes(active, new byte[6]);
        File.WriteAllBytes(oldest, new byte[6]);
        File.WriteAllBytes(newest, new byte[4]);
        File.SetLastWriteTimeUtc(active, new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(oldest, new DateTime(2026, 8, 11, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2026, 8, 12, 0, 0, 0, DateTimeKind.Utc));

        int removedCount = LogRetentionService.Apply(directory, 0, maximumBytes: 10, protectedPaths: [active]);
        Equal(1, removedCount);
        True(File.Exists(active), "The active log was deleted.");
        True(!File.Exists(oldest), "The oldest closed log was not deleted.");
        True(File.Exists(newest), "A newer log was deleted unnecessarily.");
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
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

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
