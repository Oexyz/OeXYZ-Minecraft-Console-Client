using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OeXYZ.Core;
using OeXYZ.Protocol;
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
    SupportPackageRequest request = new(
        package,
        "1.2.0-test",
        server,
        "authentication failed access_token=super-secret-token",
        ["normal diagnostic", "/login secret-password", "Bearer abcdefghijklmnopqrstuvwxyz"],
        new Dictionary<string, long> { ["Play:Clientbound:0x7F"] = 3 },
        ResolveDns: false);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => SupportPackageService.CreateAsync(request)));
    True(!Directory.EnumerateFiles(root, "support.zip.*.tmp").Any(),
        "A concurrent support-package export left a temporary file behind.");

    using ZipArchive archive = ZipFile.OpenRead(package);
    string combined = string.Join("\n", archive.Entries.Select(entry =>
    {
        using StreamReader reader = new(entry.Open());
        return entry.FullName + "\n" + reader.ReadToEnd();
    }));
    True(archive.GetEntry("environment.json") is not null, "Environment report is missing.");
    True(archive.GetEntry("server-profile.json") is not null, "Sanitized server profile is missing.");
    True(archive.GetEntry("unknown-packets.json") is not null, "Unknown packet report is missing.");
    True(archive.GetEntry("diagnostic-counters.json") is not null, "Diagnostic counter report is missing.");
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

SessionRuntimeRegistry registry = new();
SessionSnapshot disconnected = Snapshot(connected: false);
registry.Update("test-session", "Local test", disconnected, completed: false);
await using (LoopbackHealthServer health = new(registry, 0))
{
    await health.StartAsync();
    using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{health.Port}") };
    using HttpResponseMessage healthy = await client.GetAsync("/health");
    True(healthy.StatusCode == HttpStatusCode.OK, "An active disconnected session was reported as unhealthy.");
    using HttpResponseMessage notReady = await client.GetAsync("/ready");
    True(notReady.StatusCode == HttpStatusCode.ServiceUnavailable, "A disconnected runtime was reported ready.");

    registry.Update("test-session", "Local test", Snapshot(connected: true), completed: false);
    using HttpResponseMessage ready = await client.GetAsync("/ready");
    True(ready.StatusCode == HttpStatusCode.OK, "A connected runtime was not reported ready.");
    string status = await client.GetStringAsync("/status");
    True(status.Contains("\"connectedSessions\":1", StringComparison.Ordinal), "Status JSON omitted the connected session.");
    True(!status.Contains("secret.example", StringComparison.Ordinal), "The loopback status endpoint exposed a server address.");

    registry.MarkCompleted("test-session");
    using HttpResponseMessage stopped = await client.GetAsync("/health");
    True(stopped.StatusCode == HttpStatusCode.ServiceUnavailable, "A fully stopped runtime remained healthy.");
}
Console.WriteLine("PASS: bounded loopback health and readiness endpoint");

string rotationRoot = Path.Combine(Path.GetTempPath(), "oexyz-log-rotation-tests", Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(rotationRoot);
    string baseLog = Path.Combine(rotationRoot, "session.log");
    await using (RotatingLogWriter rotating = new(baseLog, 100))
    {
        await rotating.WriteLineAsync(new string('a', 60));
        await rotating.WriteLineAsync(new string('b', 60));
        True(rotating.CurrentPath.EndsWith("-part2.log", StringComparison.Ordinal), "The active log did not rotate.");
    }
    FileInfo[] parts = new DirectoryInfo(rotationRoot).GetFiles("*.log");
    True(parts.Length == 2, "Log rotation did not create exactly two bounded parts.");
    True(parts.All(part => part.Length <= 100), "A rotated log part exceeded its configured limit.");
    Console.WriteLine("PASS: active session logs rotate before exceeding their bounded part size");
}
finally
{
    if (Directory.Exists(rotationRoot)) Directory.Delete(rotationRoot, recursive: true);
}

string multiAccountLogRoot = Path.Combine(
    Path.GetTempPath(), "oexyz-multi-account-log-tests", Guid.NewGuid().ToString("N"));
try
{
    AccountProfile alpha = new()
    {
        DisplayName = "Alpha",
        Kind = AccountKind.Offline,
        LoginHint = "Alpha"
    };
    AccountProfile beta = new()
    {
        DisplayName = "Beta",
        Kind = AccountKind.Offline,
        LoginHint = "Beta"
    };
    ServerProfile sharedServer = new()
    {
        DisplayName = "Shared",
        Address = "127.0.0.1",
        CustomPort = 29997,
        Version = "26.2",
        AntiAfk = false,
        AutoReconnect = false
    };
    ConsoleSession alphaSession = new(alpha, sharedServer, new OfflineIdentityProvider(), () => { }, multiAccountLogRoot);
    ConsoleSession betaSession = new(beta, sharedServer, new OfflineIdentityProvider(), () => { }, multiAccountLogRoot);
    try
    {
        True(!string.Equals(alphaSession.LogPath, betaSession.LogPath, StringComparison.Ordinal),
            "Two accounts on one server reserved the same log path.");
        True(File.Exists(alphaSession.LogPath) && File.Exists(betaSession.LogPath),
            "Session log paths were not reserved atomically at construction time.");
        True(Path.GetFileName(alphaSession.LogPath).Contains(alpha.Id.ToString("N"), StringComparison.Ordinal) &&
             Path.GetFileName(alphaSession.LogPath).Contains(sharedServer.Id.ToString("N"), StringComparison.Ordinal),
            "The session log name does not contain both stable account and server identifiers.");

        SessionRuntimeRegistry multiAccountRegistry = new();
        multiAccountRegistry.Register(alphaSession);
        multiAccountRegistry.Register(betaSession);
        string[] names = multiAccountRegistry.Snapshot().Sessions.Select(session => session.Name).ToArray();
        True(names.Contains("Alpha @ Shared", StringComparer.Ordinal) &&
             names.Contains("Beta @ Shared", StringComparer.Ordinal),
            "Runtime status does not distinguish accounts on the same server.");
        Console.WriteLine("PASS: multi-account sessions reserve distinct identified logs and display both identities");
    }
    finally
    {
        await alphaSession.DisposeAsync();
        await betaSession.DisposeAsync();
    }
}
finally
{
    if (Directory.Exists(multiAccountLogRoot)) Directory.Delete(multiAccountLogRoot, recursive: true);
}

string logFailureRoot = Path.Combine(Path.GetTempPath(), "oexyz-log-failure-tests", Guid.NewGuid().ToString("N"));
try
{
    AccountProfile account = new()
    {
        DisplayName = "Logger",
        Kind = AccountKind.Offline,
        LoginHint = "Logger"
    };
    ServerProfile unreachable = new()
    {
        DisplayName = "Log failure",
        Address = "127.0.0.1",
        CustomPort = 29996,
        Version = "26.2",
        AntiAfk = false,
        AutoReconnect = false
    };
    ConsoleSession session = new(
        account,
        unreachable,
        new OfflineIdentityProvider(),
        () => { },
        logFailureRoot,
        enablePacketInspection: false,
        logWriterFactory: (_, _) => throw new IOException("simulated log writer failure"));
    List<SessionLine> visibleLines = [];
    session.LineAdded += visibleLines.Add;
    session.Start();
    await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    await session.DisposeAsync();
    True(session.LogException is IOException, "The asynchronous log writer failure was not retained.");
    True(visibleLines.Any(line => line.Kind == SessionLineKind.Error &&
                                line.Text.Contains("Session logging stopped", StringComparison.Ordinal)),
        "The asynchronous log writer failure was not exposed to the session UI/CLI.");
    Console.WriteLine("PASS: asynchronous session log failures remain visible and affect failure state");
}
finally
{
    if (Directory.Exists(logFailureRoot)) Directory.Delete(logFailureRoot, recursive: true);
}

string reconnectStateRoot = Path.Combine(Path.GetTempPath(), "oexyz-reconnect-state-tests", Guid.NewGuid().ToString("N"));
try
{
    ConsoleSession stateSession = new(
        new AccountProfile { DisplayName = "Reconnect", Kind = AccountKind.Offline, LoginHint = "Reconnect" },
        new ServerProfile { DisplayName = "Reconnect", Address = "127.0.0.1", Version = "26.2" },
        new OfflineIdentityProvider(),
        () => { },
        reconnectStateRoot);
    try
    {
        stateSession.RecordTerminalFailure(new IOException("transient disconnect"));
        True(stateSession.TerminalException is IOException, "Transient failure state was not recorded.");
        stateSession.RecordConnectionEstablished();
        True(stateSession.TerminalException is null,
            "A successful reconnect retained an obsolete terminal failure.");
        Console.WriteLine("PASS: successful reconnect clears obsolete terminal failure state");
    }
    finally
    {
        await stateSession.DisposeAsync();
    }
}
finally
{
    if (Directory.Exists(reconnectStateRoot)) Directory.Delete(reconnectStateRoot, recursive: true);
}

string offlineRoot = Path.Combine(Path.GetTempPath(), "oexyz-offline-save-tests", Guid.NewGuid().ToString("N"));
try
{
    int saves = 0;
    AccountProfile offlineAccount = new()
    {
        DisplayName = "Offline Test",
        Kind = AccountKind.Offline,
        LoginHint = "OfflineTest"
    };
    ServerProfile unreachable = new()
    {
        DisplayName = "Unreachable",
        Address = "127.0.0.1",
        CustomPort = 29998,
        Version = "26.2",
        AntiAfk = false,
        AutoReconnect = false
    };
    await using ConsoleSession offlineSession = new(
        offlineAccount,
        unreachable,
        new OfflineIdentityProvider(),
        () => saves++,
        offlineRoot);
    offlineSession.Start();
    await offlineSession.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    True(saves == 0, "An unchanged offline account triggered a profile rewrite.");
    Console.WriteLine("PASS: offline sessions do not rewrite unchanged profile configuration");
}
finally
{
    if (Directory.Exists(offlineRoot)) Directory.Delete(offlineRoot, recursive: true);
}

string floodRoot = Path.Combine(Path.GetTempPath(), "oexyz-session-flood-tests", Guid.NewGuid().ToString("N"));
try
{
    ConsoleSession floodSession = new(
        new AccountProfile { DisplayName = "Flood", Kind = AccountKind.Offline, LoginHint = "Flood" },
        new ServerProfile { DisplayName = "Flood", Address = "127.0.0.1", Version = "26.2" },
        new OfflineIdentityProvider(),
        () => { },
        floodRoot,
        enablePacketInspection: false,
        logWriterFactory: (_, _) => throw new IOException("Injected stalled log sink."));
    bool emittedUnsafeText = false;
    int longestEmittedText = 0;
    int healthySubscriberCalls = 0;
    object observedLock = new();
    floodSession.LineAdded += line =>
    {
        lock (observedLock)
        {
            longestEmittedText = Math.Max(longestEmittedText, line.Text.Length);
            emittedUnsafeText |= ContainsUnsafeControl(line.Text) ||
                                 line.Text.Contains("super-secret-token", StringComparison.Ordinal);
        }
    };
    floodSession.LineAdded += _ => throw new InvalidOperationException("Injected session subscriber failure.");
    floodSession.LineAdded += _ => Interlocked.Increment(ref healthySubscriberCalls);

    string hostile = "access_token=super-secret-token \u001b[31m\u0007" + new string('X', 20_000);
    FormattedChatText hugeFormatting = new(
        hostile,
        [new ChatRun(hostile, new ChatStyle(Color: "red", Bold: true))]);
    string runFloodText = new('r', SessionLinePolicy.MaximumFormattingRuns + 1);
    FormattedChatText excessiveRuns = new(
        runFloodText,
        Enumerable.Range(0, runFloodText.Length)
            .Select(_ => new ChatRun("r", new ChatStyle(Color: "green")))
            .ToArray());
    FormattedChatText validFormatting = new(
        "safe chat",
        [new ChatRun("safe ", new ChatStyle(Bold: true)), new ChatRun("chat", new ChatStyle(Color: "aqua"))]);

    SessionLine truncated = floodSession.AddForTesting(
        SessionLineKind.Chat,
        SessionLineCategory.Chat,
        hostile,
        hugeFormatting) ?? throw new InvalidOperationException("The hostile flood line was unexpectedly discarded.");
    True(truncated.Text.Length <= SessionLinePolicy.MaximumTextCharacters &&
         truncated.Text.EndsWith(SessionLinePolicy.TruncationMarker, StringComparison.Ordinal),
        "A remote session line was not truncated with the bounded marker.");
    True(truncated.Formatting is null, "Formatting survived text truncation.");
    SessionLine runLimited = floodSession.AddForTesting(
        SessionLineKind.Chat,
        SessionLineCategory.Chat,
        runFloodText,
        excessiveRuns) ?? throw new InvalidOperationException("The run-limit line was unexpectedly discarded.");
    True(runLimited.Formatting is null, "Excessive formatted-chat runs remained retained by the session.");
    SessionLine valid = floodSession.AddForTesting(
        SessionLineKind.Chat,
        SessionLineCategory.Chat,
        validFormatting.Text,
        validFormatting) ?? throw new InvalidOperationException("The valid formatted line was unexpectedly discarded.");
    True(ReferenceEquals(valid.Formatting, validFormatting), "Safe bounded formatting was unnecessarily discarded.");

    for (int index = 0; index < ConsoleSession.MaximumPendingLogLines * 3; index++)
        _ = floodSession.AddForTesting(SessionLineKind.Chat, SessionLineCategory.Chat, hostile, hugeFormatting);

    IReadOnlyList<string> diagnostics = floodSession.RecentDiagnostics;
    True(diagnostics.Count <= ConsoleSession.MaximumRecentDiagnosticLines,
        "The recent-diagnostics line bound was exceeded during a flood.");
    True(diagnostics.Sum(line => line.Length) <= ConsoleSession.MaximumRecentDiagnosticCharacters,
        "The recent-diagnostics character budget was exceeded during a flood.");
    True(diagnostics.All(line => !ContainsUnsafeControl(line) &&
                                 !line.Contains("super-secret-token", StringComparison.Ordinal)),
        "Recent diagnostics retained terminal controls or remote secrets.");
    lock (observedLock)
    {
        True(longestEmittedText <= SessionLinePolicy.MaximumTextCharacters,
            "A ConsoleSession event emitted an oversized line.");
        True(!emittedUnsafeText, "A ConsoleSession event emitted terminal controls or remote secrets.");
    }
    True(Volatile.Read(ref healthySubscriberCalls) > 0,
        "A failing session subscriber prevented later subscribers from running.");
    True(floodSession.Snapshot.SubscriberFailures > 0,
        "Session subscriber failures were not exposed in diagnostics.");
    True(floodSession.Snapshot.DroppedLogLines > 0,
        "A saturated bounded log queue did not expose dropped-line diagnostics.");

    await floodSession.DisposeAsync();
    string[] persistedLogLines = Directory.EnumerateFiles(floodRoot, "*.log")
        .SelectMany(File.ReadLines)
        .ToArray();
    True(persistedLogLines.All(line =>
             !line.Contains("super-secret-token", StringComparison.Ordinal) &&
             !ContainsUnsafeControl(line)),
        "The bounded session log retained terminal controls or remote secrets.");
    Console.WriteLine("PASS: remote session floods keep text, formatting, logs and diagnostics bounded and sanitized");
}
finally
{
    if (Directory.Exists(floodRoot)) Directory.Delete(floodRoot, recursive: true);
}

string doctorRoot = Path.Combine(Path.GetTempPath(), "oexyz-doctor-tests", Guid.NewGuid().ToString("N"));
try
{
    ApplicationPaths doctorPaths = new(
        doctorRoot,
        Path.Combine(doctorRoot, "profiles.json"),
        Path.Combine(doctorRoot, "accounts.bin"),
        Path.Combine(doctorRoot, "logs"),
        Path.Combine(doctorRoot, "diagnostics"));
    DoctorReport doctor = await DoctorService.RunAsync(doctorPaths, new ProfileDocument(), "1.3.0-test");
    True(doctor.Successful, "Local-only doctor checks failed unexpectedly.");
    True(doctor.Checks.Any(check => check.Name == "State directories" && check.Status == DoctorCheckStatus.Pass),
        "Doctor did not verify writable state directories.");
    True(doctor.Checks.All(check => !check.Message.Contains("token", StringComparison.OrdinalIgnoreCase)),
        "Doctor output unexpectedly contained token material.");
    DoctorReport invalidProfile = await DoctorService.RunAsync(doctorPaths, new ProfileDocument
    {
        Accounts = [new AccountProfile { DisplayName = "Broken", Kind = AccountKind.Offline, LoginHint = "bad name" }]
    }, "1.3.0-test");
    True(!invalidProfile.Successful && invalidProfile.Checks.Any(check =>
            check.Name == "Profile integrity" && check.Status == DoctorCheckStatus.Failure),
        "Doctor did not reject an invalid offline profile before networking.");
    DoctorReport corruptProfile = await DoctorService.RunAsync(
        doctorPaths,
        new ProfileDocument(),
        "1.3.0-test",
        configurationError: "Malformed JSON near access_token=do-not-leak");
    True(!corruptProfile.Successful && corruptProfile.Checks.Any(check =>
            check.Name == "Configuration" && check.Status == DoctorCheckStatus.Failure),
        "Doctor did not report an unreadable profile as a configuration failure.");
    True(corruptProfile.Checks.All(check => !check.Message.Contains("do-not-leak", StringComparison.Ordinal)),
        "Doctor leaked sensitive content from the profile parse error.");
    Console.WriteLine("PASS: local doctor report is sanitized and writable");
}
finally
{
    if (Directory.Exists(doctorRoot)) Directory.Delete(doctorRoot, recursive: true);
}

string refreshRoot = Path.Combine(Path.GetTempPath(), "oexyz-reconnect-auth-tests", Guid.NewGuid().ToString("N"));
try
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using CancellationTokenSource testTimeout = new(TimeSpan.FromSeconds(10));
    Task serverTask = Task.Run(async () =>
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using (TcpClient statusPeer = await listener.AcceptTcpClientAsync(testTimeout.Token))
            {
            }
            using TcpClient loginPeer = await listener.AcceptTcpClientAsync(testTimeout.Token);
            NetworkStream stream = loginPeer.GetStream();
            await stream.WriteAsync(new byte[] { 1, 2, 1, 1 }, testTimeout.Token);
            await stream.FlushAsync(testTimeout.Token);
            await Task.Delay(100, testTimeout.Token);
        }
    }, testTimeout.Token);

    AccountProfile account = new()
    {
        DisplayName = "Microsoft refresh test",
        Kind = AccountKind.Microsoft,
        LoginHint = "refresh@example.invalid"
    };
    ServerProfile server = new()
    {
        DisplayName = "Reconnect authentication",
        Address = "127.0.0.1",
        CustomPort = port,
        Version = "1.8.8",
        AntiAfk = false,
        AutoReconnect = true,
        ReconnectInitialDelaySeconds = 1,
        ReconnectMaximumDelaySeconds = 1,
        ReconnectMaximumAttempts = 2
    };
    RecordingIdentityProvider identityProvider = new();
    await using ConsoleSession session = new(account, server, identityProvider, () => { }, refreshRoot);
    session.Start();
    await WaitUntilAsync(() => identityProvider.Modes.Count >= 2, TimeSpan.FromSeconds(7));
    True(identityProvider.Modes[0] == AuthenticationInteractionMode.InteractiveAllowed,
        "The initial user-started connection did not allow interaction.");
    True(identityProvider.Modes[1] == AuthenticationInteractionMode.SilentOnly,
        "Automatic reconnect did not require silent-only authentication.");
    True(identityProvider.FirstCertificateKeyWasDisposed,
        "The old secure-chat certificate was not disposed after successful replacement.");
    await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
    session.Stop();
    await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    Console.WriteLine("PASS: reconnect silently refreshes Microsoft identity and replaces certificates");
}
finally
{
    if (Directory.Exists(refreshRoot)) Directory.Delete(refreshRoot, recursive: true);
}

static SessionSnapshot Snapshot(bool connected) => new(
    connected ? "CONNECTED" : "CONNECTING",
    connected ? SessionLineKind.Success : SessionLineKind.Information,
    "secret.example:25565",
    "26.2",
    776,
    connected ? 20F : null,
    connected ? 20 : null,
    null,
    new ConnectionMetrics(
        connected ? DateTimeOffset.UtcNow : null,
        connected ? DateTimeOffset.UtcNow : null,
        connected ? DateTimeOffset.UtcNow : null,
        connected ? 1024 : 0,
        connected ? 256 : 0,
        connected ? 10 : 0,
        connected ? 4 : 0,
        connected ? 34 : null),
    0,
    null,
    [],
    connected);

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static bool ContainsUnsafeControl(string value) => value.Any(character =>
    char.IsControl(character) || character is '\u2028' or '\u2029');

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (!condition())
    {
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The test condition was not reached.");
        await Task.Delay(20);
    }
}

sealed class OfflineIdentityProvider : IIdentityProvider
{
    public Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken,
        AuthenticationInteractionMode interactionMode = AuthenticationInteractionMode.InteractiveAllowed) =>
        Task.FromResult(MinecraftIdentity.Offline(profile.LoginHint));
}

sealed class RecordingIdentityProvider : IIdentityProvider
{
    private readonly object sync = new();
    private readonly List<AuthenticationInteractionMode> modes = [];
    private readonly RSA firstKey = RSA.Create(1024);
    private int calls;

    public IReadOnlyList<AuthenticationInteractionMode> Modes
    {
        get { lock (sync) return modes.ToArray(); }
    }

    public bool FirstCertificateKeyWasDisposed
    {
        get
        {
            try
            {
                _ = firstKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }

    public Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken,
        AuthenticationInteractionMode interactionMode = AuthenticationInteractionMode.InteractiveAllowed)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int call = Interlocked.Increment(ref calls);
        lock (sync) modes.Add(interactionMode);
        RSA key = call == 1 ? firstKey : RSA.Create(1024);
        PlayerCertificate certificate = new(
            key, [1], [2], [3], DateTimeOffset.UtcNow.AddHours(1));
        profile.AccountIdentifier = "protected-account-reference";
        return Task.FromResult(new MinecraftIdentity(
            "RefreshTest", Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            "not-a-real-token", certificate));
    }
}
