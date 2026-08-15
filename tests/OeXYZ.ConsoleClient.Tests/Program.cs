using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using OeXYZ.ConsoleClient;
using OeXYZ.Core;
using OeXYZ.Updater;

List<string> passed = [];
List<string> guiPassed = [];

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

Run("release candidates remain lower than the matching stable release", () =>
{
    UpdateCheckResult candidate = Result(
        new Version(1, 3, 0),
        new Version(1, 3, 0),
        currentPrerelease: "rc.1");
    True(candidate.IsUpdateAvailable, "The final 1.3.0 release was hidden from an installed 1.3.0-rc.1.");
    False(candidate.IsCurrentNewer, "A release candidate was treated as newer than its final release.");
    StringEqual("1.3.0-rc.1", candidate.CurrentVersionText);
    StringEqual("1.3.0", candidate.LatestVersionText);

    UpdateCheckResult candidateOrdering = Result(
        new Version(1, 3, 0),
        new Version(1, 3, 0),
        currentPrerelease: "rc.2",
        latestPrerelease: "rc.10");
    True(candidateOrdering.IsUpdateAvailable, "Numeric prerelease identifiers were compared lexically.");

    UpdateCheckResult stableAgainstCandidate = Result(
        new Version(1, 3, 0),
        new Version(1, 3, 0),
        latestPrerelease: "rc.9");
    True(stableAgainstCandidate.IsCurrentNewer, "A stable build was treated as older than a release candidate.");
});

Run("semantic release parser preserves prerelease identity", () =>
{
    ReleaseVersion parsed = GitHubUpdateService.ParseVersionForTesting("v1.3.0-rc.1+build.42");
    True(parsed.Core == new Version(1, 3, 0), "The semantic release core was parsed incorrectly.");
    StringEqual("rc.1", parsed.Prerelease ?? string.Empty);
    Throws<InvalidDataException>(() => GitHubUpdateService.ParseVersionForTesting("v1.3.0-rc.01"));
    Throws<InvalidDataException>(() => GitHubUpdateService.ParseVersionForTesting("v1.3"));
});

Run("release workflow keeps prereleases off latest and marks GitHub releases", () =>
{
    string workflow = File.ReadAllText(FindRepositoryFile(Path.Combine(".github", "workflows", "release.yml")));
    string ciWorkflow = File.ReadAllText(FindRepositoryFile(Path.Combine(".github", "workflows", "ci.yml")));
    const string buildxAction =
        "docker/setup-buildx-action@bb05f3f5519dd87d3ba754cc423b652a5edd6d2c";
    int firstReleaseBuilder = workflow.IndexOf(buildxAction, StringComparison.Ordinal);
    int secondReleaseBuilder = workflow.LastIndexOf(buildxAction, StringComparison.Ordinal);
    True(firstReleaseBuilder >= 0 && secondReleaseBuilder > firstReleaseBuilder
            && ciWorkflow.Contains(buildxAction, StringComparison.Ordinal)
            && workflow.Contains("driver: docker-container", StringComparison.Ordinal)
            && ciWorkflow.Contains("driver: docker-container", StringComparison.Ordinal),
        "A multi-architecture workflow no longer provisions the pinned Buildx builder.");
    True(workflow.Contains("release_flags+=(--prerelease)", StringComparison.Ordinal),
        "The GitHub release workflow no longer marks release candidates as prereleases.");
    True(workflow.Contains("Validate and classify release tag", StringComparison.Ordinal),
        "Release tags are no longer validated and classified before publishing.");
    int containerPush = workflow.IndexOf("--provenance=mode=max --sbom=true --push", StringComparison.Ordinal);
    int publicPullGuard = workflow.IndexOf("Verify the container is publicly pullable", StringComparison.Ordinal);
    int githubRelease = workflow.IndexOf("gh release create", StringComparison.Ordinal);
    int latestPromotion = workflow.IndexOf(
        "--tag \"$image:latest\" \"$image:$version\"",
        StringComparison.Ordinal);
    True(containerPush >= 0 && publicPullGuard > containerPush,
        "The release no longer verifies public access after publishing the container.");
    True(githubRelease > publicPullGuard && latestPromotion > githubRelease,
        "The container latest tag can be promoted before public verification and GitHub release creation.");
    True(workflow.Contains("if: needs.metadata.outputs.prerelease == 'false'", StringComparison.Ordinal)
            && workflow.Contains("needs: [metadata, release]", StringComparison.Ordinal)
            && workflow.Contains("group: oexyz-container-latest", StringComparison.Ordinal)
            && workflow.Contains("queue: max", StringComparison.Ordinal)
            && workflow.Contains("sort -V", StringComparison.Ordinal)
            && workflow.Contains("if [ \"$newest_stable\" != \"$version\" ]", StringComparison.Ordinal),
        "Latest promotion is no longer fully queued, serialized, and restricted to the newest stable release.");
    True(workflow.Contains("DOCKER_CONFIG=\"$anonymous_config\"", StringComparison.Ordinal)
            && workflow.Contains("imagetools inspect \"$image:$version\"", StringComparison.Ordinal)
            && workflow.Contains("Platform:.*linux/amd64", StringComparison.Ordinal)
            && workflow.Contains("Platform:.*linux/arm64", StringComparison.Ordinal)
            && workflow.Contains("linux/amd64 amd64", StringComparison.Ordinal)
            && workflow.Contains("linux/arm64 arm64", StringComparison.Ordinal)
            && workflow.Contains("docker pull --quiet --platform \"$platform\"", StringComparison.Ordinal),
        "The GHCR visibility guard no longer verifies anonymous AMD64 and ARM64 pulls.");
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

Run("release update source ignores repository override", () =>
{
    (string owner, string repository) = GitHubUpdateService.ResolveRepositoryForTesting(
        "Attacker/FakeRepo",
        allowOverride: false);
    StringEqual("Oexyz", owner);
    StringEqual("OeXYZ-Minecraft-Console-Client", repository);
});

Run("debug update source validates repository override", () =>
{
    (string owner, string repository) = GitHubUpdateService.ResolveRepositoryForTesting(
        "https://github.com/Example/OeXYZ-Fork",
        allowOverride: true);
    StringEqual("Example", owner);
    StringEqual("OeXYZ-Fork", repository);
    Throws<InvalidDataException>(() => GitHubUpdateService.ResolveRepositoryForTesting(
        "https://example.invalid/Attacker/FakeRepo",
        allowOverride: true));
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

RunGui("duplicate GUI add and edit operations roll back", () =>
{
    string root = Path.Combine(Path.GetTempPath(), $"oexyz-gui-duplicates-{Guid.NewGuid():N}");
    try
    {
        ProfileRepository repository = new(Path.Combine(root, "profiles.json"));
        AccountProfile primaryAccount = new()
        {
            DisplayName = "Primary account",
            Kind = AccountKind.Offline,
            LoginHint = "Primary"
        };
        AccountProfile secondaryAccount = new()
        {
            DisplayName = "Secondary account",
            Kind = AccountKind.Offline,
            LoginHint = "Secondary"
        };
        ServerProfile primaryServer = new()
        {
            DisplayName = "Primary server",
            Address = "primary.example"
        };
        ServerProfile secondaryServer = new()
        {
            DisplayName = "Secondary server",
            Address = "secondary.example"
        };
        ProfileDocument original = repository.Update(_ => new ProfileDocument
        {
            Accounts = [primaryAccount, secondaryAccount],
            Servers = [primaryServer, secondaryServer]
        });

        AssertRejectedDuplicate(current => ProfileUiOperations.AddAccount(current, new AccountProfile
        {
            DisplayName = " primary ACCOUNT ",
            Kind = AccountKind.Offline,
            LoginHint = "Duplicate"
        }));
        AssertRejectedDuplicate(current => ProfileUiOperations.EditAccount(
            current,
            secondaryAccount with { DisplayName = " PRIMARY account " },
            original.Revision));
        AssertRejectedDuplicate(current => ProfileUiOperations.AddServer(current, new ServerProfile
        {
            DisplayName = " primary SERVER ",
            Address = "duplicate.example"
        }));
        AssertRejectedDuplicate(current => ProfileUiOperations.EditServer(
            current,
            secondaryServer with { DisplayName = " PRIMARY server ", Address = "changed.example" },
            original.Revision));

        void AssertRejectedDuplicate(Func<ProfileDocument, ProfileDocument> mutation)
        {
            ProfileUpdateResult result = ProfileUiOperations.TryUpdate(
                original,
                repository.Update,
                repository.Load,
                mutation);
            False(result.Succeeded, "A duplicate GUI profile mutation was accepted.");
            True(result.Failure is InvalidDataException,
                "A duplicate GUI profile mutation did not report a validation error.");
            AssertProfileState(original, result.Document);
            AssertProfileState(original, repository.Load());
        }
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

RunGui("failed account identifier persistence does not escape the session callback", () =>
{
    AccountProfile storedAccount = new()
    {
        DisplayName = "Microsoft account",
        Kind = AccountKind.Microsoft,
        LoginHint = "player@example.invalid"
    };
    ProfileDocument displayed = new ProfileDocument { Accounts = [storedAccount] }.Normalize();
    AccountProfile sessionAccount = storedAccount with { AccountIdentifier = "authenticated-id" };

    ProfileUpdateResult result = ProfileUiOperations.TryPersistAccountIdentifier(
        displayed,
        sessionAccount,
        _ => throw new IOException("deterministic profile save failure"),
        () => throw new UnauthorizedAccessException("deterministic profile reload failure"));

    True(result.Failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2,
        "The save and reload failures were not contained and aggregated.");
    True(ReferenceEquals(result.Document, displayed),
        "The GUI did not retain its prior profile document after save and reload both failed.");
    True(result.Document.Accounts.Single().AccountIdentifier is null,
        "A failed persistence attempt leaked the authenticated identifier into the displayed profile document.");
    StringEqual("authenticated-id", sessionAccount.AccountIdentifier ?? string.Empty);
});

await RunGuiAsync("close cleanup continues after persistence and session failures", async () =>
{
    AccountProfile account = new()
    {
        DisplayName = "Closing account",
        Kind = AccountKind.Offline,
        LoginHint = "Closing"
    };
    ServerProfile server = new() { DisplayName = "Closing server", Address = "closing.example" };
    ProfileDocument current = new ProfileDocument
    {
        Accounts = [account],
        Servers = [server]
    }.Normalize();
    SessionBookmark bookmark = new() { AccountId = account.Id, ServerId = server.Id };
    List<string> order = [];

    Exception? failure = await ProfileUiOperations.PersistLastSessionsThenCleanupAsync(
        [bookmark],
        persist: update =>
        {
            order.Add("persist");
            ProfileDocument candidate = update(current);
            True(candidate.LastSessions.SequenceEqual([bookmark]),
                "The close path did not pass the session bookmark to persistence.");
            throw new IOException("deterministic close save failure");
        },
        cleanupActions:
        [
            async () =>
            {
                await Task.Yield();
                order.Add("cleanup-1");
                throw new IOException("deterministic first-session cleanup failure");
            },
            async () =>
            {
                await Task.Yield();
                order.Add("cleanup-2");
            }
        ]);

    True(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2,
        "The persistence and cleanup failures were not both returned to the UI.");
    True(order.SequenceEqual(["persist", "cleanup-1", "cleanup-2"]),
        "A failed session cleanup prevented a later session from closing.");
});

RunGui("pending GUI lines are thread-safe and drop the oldest at capacity", () =>
{
    BoundedDropOldestQueue<int> concurrent = new(SessionTab.MaximumPendingLines);
    Parallel.For(0, SessionTab.MaximumPendingLines * 20, concurrent.Enqueue);
    True(concurrent.Count == SessionTab.MaximumPendingLines,
        "Concurrent producers exceeded the GUI pending-line capacity.");
    int concurrentDrained = 0;
    while (concurrent.TryDequeue(out _)) concurrentDrained++;
    True(concurrentDrained == SessionTab.MaximumPendingLines,
        "Concurrent enqueue/dequeue corrupted the bounded GUI queue.");

    BoundedDropOldestQueue<int> ordered = new(4);
    for (int value = 0; value < 10; value++) ordered.Enqueue(value);
    List<int> retained = [];
    while (ordered.TryDequeue(out int value)) retained.Add(value);
    True(retained.SequenceEqual([6, 7, 8, 9]),
        "The bounded GUI queue did not discard the oldest pending lines.");
});

Console.WriteLine($"PASS: {passed.Count} updater tests");
foreach (string name in passed) Console.WriteLine($"  - {name}");
Console.WriteLine($"PASS: {guiPassed.Count} GUI tests");
foreach (string name in guiPassed) Console.WriteLine($"  - {name}");
return;

UpdateCheckResult Result(
    Version current,
    Version latest,
    string? currentPrerelease = null,
    string? latestPrerelease = null) =>
    new(current, latest, "https://example.invalid/release", "OeXYZ-Console-Client-win-x64.zip",
        "https://example.invalid/archive", "https://example.invalid/checksums",
        currentPrerelease, latestPrerelease);

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

void RunGui(string name, Action test)
{
    test();
    guiPassed.Add(name);
}

async Task RunGuiAsync(string name, Func<Task> test)
{
    await test();
    guiPassed.Add(name);
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

static void AssertProfileState(ProfileDocument expected, ProfileDocument actual)
{
    True(expected.Revision == actual.Revision, "A rejected GUI mutation changed the profile revision.");
    True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected.Accounts),
            JsonSerializer.SerializeToElement(actual.Accounts)),
        "A rejected GUI mutation changed the accounts.");
    True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(expected.Servers),
            JsonSerializer.SerializeToElement(actual.Servers)),
        "A rejected GUI mutation changed the servers.");
}

static void StringEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static string FindRepositoryFile(string relativePath)
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, relativePath);
        if (File.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }
    throw new FileNotFoundException(
        $"Could not locate repository file '{relativePath}' from the test output directory.");
}
