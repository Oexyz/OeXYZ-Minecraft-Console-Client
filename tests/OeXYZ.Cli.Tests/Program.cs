using OeXYZ.Cli;
using OeXYZ.Core;
using OeXYZ.Session;

List<string> passed = [];

Run("dashboard uses the available height for more than ten events", () =>
{
    string[] frame = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 5),
        Enumerable.Range(1, 40).Select(number => $"event {number:D2}").ToArray(),
        acceptsInput: true,
        currentInput: string.Empty,
        deviceCodePrompt: null,
        width: 120,
        height: 30);

    Equal(30, frame.Length);
    True(frame.All(line => line.Length == 119), "A row did not reserve exactly one wrap-safe terminal cell.");
    True(frame.Count(line => line.Contains("event ", StringComparison.Ordinal)) > 10,
        "The dashboard still exposes ten or fewer event rows.");
    True(frame.Any(line => line.Contains("event 40", StringComparison.Ordinal)), "Newest event is missing.");
    True(!frame.Any(line => line.Contains("event 01", StringComparison.Ordinal)), "Old events were not clipped to viewport height.");
    True(frame[^2].StartsWith('└') && frame[^2].EndsWith('┘'), "The lower chat border is incomplete.");
    True(frame[^1].StartsWith("> ", StringComparison.Ordinal), "The input prompt is not on the final row.");
});

Run("dashboard responds to a taller terminal", () =>
{
    string[] frame = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 5),
        Enumerable.Range(1, 60).Select(number => $"event {number:D2}").ToArray(),
        acceptsInput: true,
        currentInput: string.Empty,
        deviceCodePrompt: null,
        width: 156,
        height: 47);

    Equal(47, frame.Length);
    True(frame.Count(line => line.Contains("event ", StringComparison.Ordinal)) >= 35,
        "The taller viewport was not used for chat history.");
    True(frame[^2].StartsWith('└') && frame[^2].EndsWith('┘'), "Resized lower border is incomplete.");
});

Run("incremental renderer does not clear unchanged rows", () =>
{
    string[] before = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 5), ["hello"], true, string.Empty, null, 120, 30);
    string[] after = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 6), ["hello"], true, string.Empty, null, 120, 30);

    string update = TerminalDashboard.BuildTerminalUpdate(after, before, fullRedraw: false);
    True(!update.Contains("\x1b[2J", StringComparison.Ordinal), "Incremental update clears the full screen.");
    True(!update.Contains("\x1b[2K", StringComparison.Ordinal), "Incremental update clears a row before drawing it.");
    True(update.StartsWith("\x1b[2;1H", StringComparison.Ordinal), "Unexpected row was redrawn.");
    Equal(1, Count(update, "\x1b["));
    Equal(string.Empty, TerminalDashboard.BuildTerminalUpdate(after, after, fullRedraw: false));
});

Run("dashboard redacts sensitive input", () =>
{
    string[] frame = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 5), [], true, "/register secret secret", null, 80, 20);
    True(frame[^1].Contains("/register [REDACTED]", StringComparison.Ordinal), "Sensitive prompt was not redacted.");
    True(!frame[^1].Contains("secret", StringComparison.Ordinal), "Sensitive prompt leaked its value.");
});

Run("dashboard rejects redirected terminals before startup and sanitizes every frame", () =>
{
    Throws<InvalidOperationException>(() =>
        TerminalDashboard.ValidateTerminal(outputRedirected: true, inputRedirected: false, acceptsInput: false));
    Throws<InvalidOperationException>(() =>
        TerminalDashboard.ValidateTerminal(outputRedirected: false, inputRedirected: true, acceptsInput: true));
    string[] frame = TerminalDashboard.ComposeFrame(
        Snapshot(uptimeSeconds: 5, sessionName: "Alpha\x1b]0;forged\a @ Shared"),
        ["message\x1b[2J\a forged"],
        acceptsInput: true,
        currentInput: "hello\u0085world",
        deviceCodePrompt: null,
        width: 100,
        height: 20);
    string rendered = string.Join('\n', frame);
    True(!rendered.Contains('\x1b') && !rendered.Contains('\a') && !rendered.Contains('\u0085'),
        "A control character survived terminal frame composition.");
});

Run("wizard cap, container paths and secret exclusions stay aligned", () =>
{
    string composePath = FindRepositoryFile("docker-compose.yml");
    string buildComposePath = FindRepositoryFile("docker-compose.build.yml");
    string dockerIgnorePath = FindRepositoryFile(".dockerignore");
    string[] compose = File.ReadAllLines(composePath);
    string composeText = File.ReadAllText(composePath);
    string buildComposeText = File.ReadAllText(buildComposePath);
    HashSet<string> dockerIgnore = File.ReadAllLines(dockerIgnorePath)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .ToHashSet(StringComparer.Ordinal);
    int option = Array.FindIndex(compose, line => line.Contains("--max-sessions", StringComparison.Ordinal));
    True(option >= 0, "Compose does not configure the supervisor session limit.");
    string rawLimit = compose[(option + 1)..]
        .First(line => line.TrimStart().StartsWith("-", StringComparison.Ordinal))
        .Trim().TrimStart('-').Trim().Trim('"');
    True(int.TryParse(rawLimit, out int composeLimit), "Compose has a non-numeric supervisor session limit.");
    Equal(composeLimit, SetupWizard.MaximumManagedSessions);
    True(composeText.Contains(
            "image: ${OEXYZ_IMAGE:-ghcr.io/oexyz/oexyz-minecraft-console-client:latest}",
            StringComparison.Ordinal),
        "Compose no longer defaults to the public GHCR latest image.");
    True(composeText.Contains("pull_policy: always", StringComparison.Ordinal),
        "The public-image path no longer always checks GHCR for latest.");
    True(!composeText.Contains("build:", StringComparison.Ordinal),
        "Pull-only Compose can silently fall back to a source build.");
    True(buildComposeText.Contains("context: .", StringComparison.Ordinal)
            && buildComposeText.Contains("pull_policy: never", StringComparison.Ordinal),
        "The source-build override no longer builds locally without a registry pull.");
    string[] sensitiveBuildContextPatterns =
    [
        ".auth", "logs", "SessionCache", "ProfileKeyCache", "runtime", "/docker",
        "MinecraftClient.ini", "*.key", "accounts.bin", "accounts.json", "sessions.json",
        "updates.json", "profiles.json", "appsettings.local.json"
    ];
    foreach (string pattern in sensitiveBuildContextPatterns)
        True(dockerIgnore.Contains(pattern), $"Docker build context no longer excludes '{pattern}'.");
});

Run("file permission failures are operational rather than authentication failures", () =>
{
    Equal(OeXYZExitCode.InternalError, CliApplication.MapFailure(new UnauthorizedAccessException("denied")));
    Equal(OeXYZExitCode.AuthenticationError,
        CliApplication.MapFailure(new FakeAuthenticationException("authentication failed")));

    string root = Path.Combine(Path.GetTempPath(), "oexyz-cli-log-failure-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "combined.log");
    List<string> reported = [];
    try
    {
        Directory.CreateDirectory(root);
        CliLog log = new(path, "information", reported.Add, maximumBytes: 32);
        log.Write("information", new string('x', 64));
        Directory.CreateDirectory(path + ".previous.log");
        log.Write("information", "trigger rotation");
        True(log.FailureException is IOException, "A later CLI log failure was not retained.");
        Equal(1, reported.Count);
        log.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("user-disconnected session failures do not poison the process exit code", () =>
{
    Equal(OeXYZExitCode.Success, CliApplication.AggregateSessionExitCode(
        [(new IOException("obsolete transient failure"), UserStopped: true)],
        allSessionsStoppedByUser: false));
    Equal(OeXYZExitCode.ConnectionFailure, CliApplication.AggregateSessionExitCode(
        [(new IOException("real failure"), UserStopped: false)],
        allSessionsStoppedByUser: false));
});

await RunAsync("guided setup configures two accounts on one server", async () =>
{
    string scriptedInput = string.Join(Environment.NewLine,
    [
        "2", "Alpha", "y",
        "2", "Beta", "n",
        "1", "survival", "play.example.net:25566", "AFK", "auto",
        "y", "2", "1", "n"
    ]) + Environment.NewLine;
    using StringReader input = new(scriptedInput);
    using StringWriter output = new();
    ProfileDocument document = new();
    int saves = 0;
    SetupWizard wizard = new(
        input,
        output,
        (_, _) => throw new InvalidOperationException("Offline setup unexpectedly invoked Microsoft login."),
        _ => saves++,
        container: true);

    await wizard.RunAsync(document, CancellationToken.None);

    Equal(2, document.Accounts.Count);
    Equal(1, document.Servers.Count);
    Equal(2, document.ManagedSessions.Count);
    Equal("Alpha", document.Accounts[0].DisplayName);
    Equal("Beta", document.Accounts[1].DisplayName);
    Equal("play.example.net:25566", document.Servers[0].Address);
    Equal("AFK", document.Servers[0].Group);
    True(document.ManagedSessions.Select(binding => binding.AccountId).Distinct().Count() == 2,
        "Both managed sessions were assigned to the same account.");
    True(saves >= 4, "Setup did not persist its incremental changes.");
    True(output.ToString().Contains("docker compose up -d", StringComparison.Ordinal),
        "Setup did not print the default latest-image Docker start command.");
    True(output.ToString().Contains(
            "docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --no-build",
            StringComparison.Ordinal),
        "Setup did not preserve the direct source-build Compose path.");
});

Run("supervisor resolves managed multi-account sessions and group filters", () =>
{
    AccountProfile first = new() { DisplayName = "First", Kind = AccountKind.Offline, LoginHint = "First" };
    AccountProfile second = new() { DisplayName = "Second", Kind = AccountKind.Offline, LoginHint = "Second" };
    ServerProfile afk = new() { DisplayName = "AFK", Address = "afk.example.net", Group = "AFK" };
    ServerProfile test = new() { DisplayName = "Test", Address = "test.example.net", Group = "Test" };
    ProfileDocument profiles = new()
    {
        Accounts = [first, second],
        Servers = [afk, test],
        ManagedSessions =
        [
            new SessionBookmark { AccountId = first.Id, ServerId = afk.Id },
            new SessionBookmark { AccountId = second.Id, ServerId = afk.Id },
            new SessionBookmark { AccountId = second.Id, ServerId = test.Id }
        ]
    };

    IReadOnlyList<(AccountProfile Account, ServerProfile Server)> all =
        CliApplication.ResolveManagedSessions(profiles, null);
    IReadOnlyList<(AccountProfile Account, ServerProfile Server)> filtered =
        CliApplication.ResolveManagedSessions(profiles, "afk");
    Equal(3, all.Count);
    Equal(2, filtered.Count);
    True(filtered.All(item => item.Server.Id == afk.Id), "Group filtering selected the wrong server.");
    True(filtered.Select(item => item.Account.Id).Distinct().Count() == 2,
        "The supervisor lost a managed account assignment.");
});

await RunAsync("doctor, healthcheck and account-key generation bypass malformed profiles", async () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-cli-early-command-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    string profiles = Path.Combine(root, "profiles.json");
    string validProfiles = Path.Combine(root, "valid-profiles.json");
    string key = Path.Combine(root, "account.key");
    await File.WriteAllTextAsync(profiles, "{ malformed profile JSON");
    try
    {
        OeXYZExitCode generated = await CliApplication.RunAsync(
            ["account-key-generate", key, "--config", profiles]);
        Equal(OeXYZExitCode.Success, generated);
        True(File.Exists(key), "account-key-generate did not create its target before profile loading.");

        SessionRuntimeRegistry registry = new();
        await using LoopbackHealthServer health = new(registry, 0);
        await health.StartAsync();
        OeXYZExitCode checkedHealth = await CliApplication.RunAsync(
            ["healthcheck", $"http://127.0.0.1:{health.Port}/health", "--config", profiles]);
        Equal(OeXYZExitCode.Success, checkedHealth);

        OeXYZExitCode diagnosed = await CliApplication.RunAsync(["doctor", "--config", profiles, "--json"]);
        Equal(OeXYZExitCode.DiagnosticsFailed, diagnosed);

        OeXYZExitCode invalidPort = await CliApplication.RunAsync(
            ["server-add", "BadPort", "--address", "example.org:0", "--config", validProfiles]);
        Equal(OeXYZExitCode.InvalidArguments, invalidPort);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Console.WriteLine($"Passed {passed.Count} CLI dashboard tests:");
foreach (string name in passed) Console.WriteLine($"  - {name}");
return;

RuntimeHealthSnapshot Snapshot(long uptimeSeconds, string sessionName = "PublicAnarchy")
{
    RuntimeSessionStatus session = new(
        sessionName, "CONNECTED", true, false, "26.2", 776, 34, 20F, 20,
        -162.5, 66, 322.5, 0, 0, 1_500_000, 500, 3_000, 40, 0, null);
    return new RuntimeHealthSnapshot(
        DateTimeOffset.UtcNow, uptimeSeconds, true, true, 1, 1, 0,
        80 * 1024 * 1024, 100 * 1024 * 1024, 12, 0.2, [session]);
}

void Run(string name, Action action)
{
    action();
    passed.Add(name);
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

int Count(string value, string needle)
{
    int count = 0;
    int offset = 0;
    while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
    {
        count++;
        offset += needle.Length;
    }
    return count;
}

async Task RunAsync(string name, Func<Task> action)
{
    await action();
    passed.Add(name);
}

void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

string FindRepositoryFile(string name)
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, name);
        if (File.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }
    throw new FileNotFoundException($"Could not locate repository file '{name}' from the test output directory.");
}

sealed class FakeAuthenticationException(string message) : Exception(message);
