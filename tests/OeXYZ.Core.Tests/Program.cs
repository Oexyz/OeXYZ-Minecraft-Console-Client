using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using OeXYZ.Core;

if (args is ["--profile-update-worker", string workerPath, string workerName])
{
    ProfileRepository workerRepository = new(workerPath);
    _ = workerRepository.Update(current =>
    {
        current.Accounts.Add(new AccountProfile
        {
            DisplayName = workerName,
            Kind = AccountKind.Offline,
            LoginHint = workerName.Replace(" ", string.Empty, StringComparison.Ordinal)
        });
        return current;
    });
    return;
}

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
    Equal("/authme:login [REDACTED]", SensitiveDataRedactor.RedactCommand("/authme:login \"two word password\""));
    Equal("Bearer [REDACTED]", SensitiveDataRedactor.RedactText("Bearer abcdefghijklmnopqrstuvwxyz"));
    Equal("password=[REDACTED] safe=yes",
        SensitiveDataRedactor.RedactText("password=\"two word password\" safe=yes"));
    Equal("{\"password\":\"[REDACTED]\",\"client_secret\":\"[REDACTED]\",\"safe\":\"kept\"}",
        SensitiveDataRedactor.RedactText(
            "{\"password\":\"two word password\",\"client_secret\":\"escaped\\\\\\\"value\",\"safe\":\"kept\"}"));
    Equal("{\"password\":\"[REDACTED]\",\"safe\":\"kept\"}",
        SensitiveDataRedactor.RedactText(
            "{\"pass\\u0077ord\":\"escaped-key-secret\",\"safe\":\"kept\"}"));
    Equal("{\"access_token\":\"[REDACTED]\",\"client_secret\":\"[REDACTED]\",\"nested\":{\"password\":\"[REDACTED]\",\"safe\":1}}",
        SensitiveDataRedactor.RedactText(
            "{\"access_token\":[\"array-secret\",{\"still\":\"secret\"}],\"client_secret\":{\"value\":\"object-secret\"},\"nested\":{\"password\":[1,2,3],\"safe\":1}}"));
    string oversizedJson = "{\"pass\\u0077ord\":\"" +
                           new string('x', SensitiveDataRedactor.MaximumStructuredJsonCharacters) + "\"}";
    Equal("\"[REDACTED]\"", SensitiveDataRedactor.RedactText(oversizedJson));
    Equal("Chat sent: /authme:login [REDACTED]",
        SensitiveDataRedactor.RedactText("Chat sent: /authme:login one two"));
    True(SensitiveDataRedactor.IsSensitiveCommand("/l password"), "Login alias was not recognized as sensitive.");
    True(SensitiveDataRedactor.IsSensitiveCommand(" /authme:register first second"),
        "Namespaced registration command was not recognized as sensitive.");
});

Run("offline identity names are validated before networking", () =>
{
    True(ProfileRules.IsValidOfflineName("OeXYZ_Test123"), "A valid offline player name was rejected.");
    True(!ProfileRules.IsValidOfflineName("name with spaces"), "Spaces were accepted in an offline player name.");
    True(!ProfileRules.IsValidOfflineName(new string('x', 17)), "An oversized offline player name was accepted.");
    Throws<InvalidDataException>(() => ProfileRules.EnsureValidOfflineName(""));
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

Run("profile v5 normalizes proxy failover automation and transfer policy", () =>
{
    ProxyProfile proxy = new()
    {
        DisplayName = "Local SOCKS",
        Kind = ProxyKind.Socks5,
        Host = "127.0.0.1",
        Port = 1080,
        SecretReference = "proxy.local"
    };
    ServerProfile server = new()
    {
        DisplayName = "Advanced",
        Address = "primary.example",
        ProxyProfileId = proxy.Id,
        AllowServerTransfer = true,
        Endpoints =
        [
            new ServerEndpointProfile { Address = "primary.example", Priority = 0 },
            new ServerEndpointProfile { Address = "secondary.example", Priority = 1 }
        ],
        Automations =
        [
            new AutomationRuleProfile
            {
                Name = "Welcome",
                Trigger = AutomationTriggerKind.Connected,
                CooldownSeconds = 1,
                Actions = [new AutomationActionProfile { Kind = AutomationActionKind.SendChat, Value = "hello" }]
            }
        ]
    };
    ProfileDocument document = new ProfileDocument
    {
        FormatVersion = 4,
        ProxyProfiles = [proxy],
        Servers = [server]
    }.Normalize();
    Equal(5, document.FormatVersion);
    Equal(2, document.Servers[0].Endpoints.Count);
    True(document.Servers[0].AllowServerTransfer, "Transfer policy was not preserved.");
    Equal(1, document.Servers[0].Automations.Count);
    Throws<InvalidDataException>(() => (document with
    {
        Servers = [server with { ProxyProfileId = Guid.NewGuid() }]
    }).Normalize());
    Throws<InvalidDataException>(() => (document with
    {
        Servers = [server with
        {
            Automations = [server.Automations[0] with
            {
                Actions = [new AutomationActionProfile { Kind = AutomationActionKind.SendCommand, Value = "/login secret" }]
            }]
        }]
    }).Normalize());
    Throws<InvalidDataException>(() => (document with
    {
        Servers = [server with
        {
            Automations = [server.Automations[0] with { UseRegex = true, Pattern = "(" }]
        }]
    }).Normalize());
    Throws<InvalidDataException>(() => (document with
    {
        Servers = [server with
        {
            Automations = [server.Automations[0] with { Pattern = "password=must-not-be-stored" }]
        }]
    }).Normalize());
    Throws<InvalidDataException>(() => (document with
    {
        Servers = [server with
        {
            Automations = [server.Automations[0] with
            {
                Actions = [new AutomationActionProfile
                {
                    Kind = AutomationActionKind.Notify,
                    Value = "access_token=must-not-be-stored"
                }]
            }]
        }]
    }).Normalize());
});

Run("profile backup recovery is explicit atomic and preserves corrupt input", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-profile-recovery-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    try
    {
        ProfileRepository repository = new(path);
        ProfileDocument first = new()
        {
            Accounts = [new AccountProfile
            {
                DisplayName = "Backup account",
                Kind = AccountKind.Offline,
                LoginHint = "BackupUser"
            }]
        };
        repository.Save(first);
        ProfileDocument second = repository.Load();
        second.Accounts[0] = second.Accounts[0] with { DisplayName = "Current account" };
        repository.Save(second);
        byte[] validatedBackup = File.ReadAllBytes(repository.BackupPath);

        File.WriteAllText(path, "{ corrupt primary");
        ProfileRecoveryState state = repository.InspectRecovery();
        True(state.PrimaryExists && !state.PrimaryValid && state.BackupExists && state.BackupValid && state.CanRestore,
            "A valid backup was not offered for explicit recovery.");
        Throws<ProfileRecoveryAvailableException>(() => repository.Load());

        ProfileRecoveryResult recovered = repository.RestoreBackup();
        Equal("Backup account", recovered.Document.Accounts.Single().DisplayName);
        True(recovered.PreservedCorruptPath is not null && File.Exists(recovered.PreservedCorruptPath),
            "The corrupt primary profile was not preserved.");
        True(File.ReadAllText(recovered.PreservedCorruptPath!).Contains("corrupt primary", StringComparison.Ordinal),
            "The preserved corrupt profile did not retain the original bytes.");
        True(validatedBackup.AsSpan().SequenceEqual(File.ReadAllBytes(repository.BackupPath)),
            "Recovery modified the validated backup.");
        Equal("Backup account", repository.Load().Accounts.Single().DisplayName);
        if (!OperatingSystem.IsWindows())
        {
            True(PrivateFileSystem.HasPrivateUnixPermissions(path), "The recovered profile is not private.");
            True(PrivateFileSystem.HasPrivateUnixPermissions(recovered.PreservedCorruptPath!),
                "The preserved corrupt profile is not private.");
        }

        File.Delete(path);
        Throws<ProfileRecoveryAvailableException>(() => repository.Load());
        ProfileRecoveryResult missingPrimary = repository.RestoreBackup();
        True(missingPrimary.PreservedCorruptPath is null,
            "Recovery invented a corrupt-file copy when the primary was missing.");

        File.WriteAllText(path, "{ bad primary");
        File.WriteAllText(repository.BackupPath, "{ bad backup");
        ProfileRecoveryState invalid = repository.InspectRecovery();
        True(!invalid.CanRestore && !invalid.PrimaryValid && !invalid.BackupValid,
            "An invalid backup was offered for recovery.");
        Throws<InvalidDataException>(() => repository.Load());
        Throws<InvalidOperationException>(() => repository.RestoreBackup());
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("profile repository rejects oversized local configuration", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-profile-limit-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    try
    {
        Directory.CreateDirectory(root);
        using (FileStream oversized = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            oversized.SetLength(ProfileRepository.MaximumProfileBytes + 1);
        Throws<InvalidDataException>(() => new ProfileRepository(path).Load());
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("private directories never chmod an existing caller-owned directory", () =>
{
    if (OperatingSystem.IsWindows()) return;
    string root = Path.Combine(Path.GetTempPath(), "oexyz-permission-tests", Guid.NewGuid().ToString("N"));
    string owned = Path.Combine(root, "created-by-oexyz");
    try
    {
        Directory.CreateDirectory(root);
        UnixFileMode publicMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                  UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                  UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(root, publicMode);
        PrivateFileSystem.EnsurePrivateDirectory(root);
        Equal(publicMode, File.GetUnixFileMode(root));

        PrivateFileSystem.EnsurePrivateDirectory(owned);
        True(PrivateFileSystem.HasPrivateUnixPermissions(owned),
            "A directory created by OeXYZ did not receive private permissions.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("control tokens are private 256-bit files and never printed", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-control-token-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "control.token");
    try
    {
        ControlTokenFile.Create(path);
        byte[] token = ControlTokenFile.Read(path);
        try { Equal(ControlTokenFile.TokenBytes, token.Length); }
        finally { CryptographicOperations.ZeroMemory(token); }
        Throws<IOException>(() => ControlTokenFile.Create(path));
        True(PrivateFileSystem.HasPrivateUnixPermissions(path), "The control token is not private.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("malformed profile entries are rejected with data errors", () =>
{
    AccountProfile account = new()
    {
        Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        DisplayName = "Account",
        Kind = AccountKind.Offline,
        LoginHint = "Player"
    };
    ServerProfile server = new()
    {
        Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
        DisplayName = "Server",
        Address = "localhost"
    };

    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Accounts = [account, account with { DisplayName = "Other" }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Accounts = [account, account with { Id = Guid.NewGuid(), DisplayName = " account " }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Accounts = [account with { Id = Guid.Empty }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Accounts = [account with { Kind = (AccountKind)99 }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Servers = [server with { CustomPort = -1 }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Servers = [server with { CustomPort = 65536 }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Servers = [server, server with { DisplayName = "Other" }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Servers = [server, server with { Id = Guid.NewGuid(), DisplayName = "SERVER" }]
    }.Normalize());
    Throws<InvalidDataException>(() => new ProfileDocument
    {
        Servers = [server with { DisplayName = " " }]
    }.Normalize());

    JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true };
    ProfileDocument nullCommand = JsonSerializer.Deserialize<ProfileDocument>(
        "{\"servers\":[{\"displayName\":\"Server\",\"address\":\"localhost\",\"quickCommands\":[null]}]}",
        options)!;
    ProfileDocument nullBookmark = JsonSerializer.Deserialize<ProfileDocument>(
        "{\"managedSessions\":[null]}", options)!;
    Throws<InvalidDataException>(() => nullCommand.Normalize());
    Throws<InvalidDataException>(() => nullBookmark.Normalize());

    string root = Path.Combine(Path.GetTempPath(), "oexyz-malformed-profile-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    try
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(path,
            "{\"accounts\":[{\"displayName\":\"Account\",\"kind\":\"Bogus\"}]}");
        Throws<InvalidDataException>(() => new ProfileRepository(path).Load());
        File.WriteAllText(path, "{ malformed json");
        Throws<InvalidDataException>(() => new ProfileRepository(path).Load());
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("parallel profile saves preserve independent changes", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-parallel-profile-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    const int writers = 20;
    try
    {
        ProfileRepository seed = new(path);
        seed.Save(new ProfileDocument());
        ProfileRepository[] repositories = new ProfileRepository[writers];
        ProfileDocument[] documents = new ProfileDocument[writers];
        for (int index = 0; index < writers; index++)
        {
            repositories[index] = new ProfileRepository(path);
            documents[index] = repositories[index].Load();
            documents[index].Accounts.Add(new AccountProfile
            {
                DisplayName = $"Account {index:D2}",
                Kind = AccountKind.Offline,
                LoginHint = $"Player{index:D2}"
            });
        }

        ConcurrentQueue<Exception> errors = [];
        Parallel.For(0, writers, index =>
        {
            try { repositories[index].Save(documents[index]); }
            catch (Exception exception) { errors.Enqueue(exception); }
        });
        if (!errors.IsEmpty) throw new AggregateException(errors);

        const int processes = 8;
        Process[] workers = Enumerable.Range(0, processes)
            .Select(index => StartTestProcess("--profile-update-worker", path, $"External {index:D2}"))
            .ToArray();
        foreach (Process worker in workers)
        {
            string error = worker.StandardError.ReadToEnd();
            worker.WaitForExit();
            if (worker.ExitCode != 0)
                throw new InvalidOperationException($"Profile update worker failed ({worker.ExitCode}): {error}");
            worker.Dispose();
        }

        ProfileDocument result = new ProfileRepository(path).Load();
        Equal(writers + processes, result.Accounts.Count);
        Equal((long)writers + processes + 1, result.Revision);
        Equal(0, Directory.GetFiles(root, "*.tmp").Length);
        True(File.Exists(path + ".lock"), "The repository lock file was not retained.");
        True(File.Exists(path + ".bak"), "The previous revision was not backed up.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("profile repository detects same-entity conflicts and rolls back failed updates", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-profile-conflict-tests", Guid.NewGuid().ToString("N"));
    string path = Path.Combine(root, "profiles.json");
    try
    {
        AccountProfile account = new()
        {
            DisplayName = "Original",
            Kind = AccountKind.Offline,
            LoginHint = "Player"
        };
        ProfileRepository seed = new(path);
        seed.Save(new ProfileDocument { Accounts = [account] });

        ProfileRepository first = new(path);
        ProfileRepository second = new(path);
        ProfileDocument firstView = first.Load();
        ProfileDocument secondView = second.Load();
        firstView.Accounts[0] = firstView.Accounts[0] with { DisplayName = "First edit" };
        secondView.Accounts[0] = secondView.Accounts[0] with { DisplayName = "Second edit" };
        first.Save(firstView);
        Throws<ProfileConcurrencyException>(() => second.Save(secondView));
        Equal("First edit", new ProfileRepository(path).Load().Accounts.Single().DisplayName);

        ProfileRepository updater = new(path);
        long beforeRevision = updater.Load().Revision;
        Throws<InvalidOperationException>(() => updater.Update(current =>
        {
            current.Accounts.Add(new AccountProfile
            {
                DisplayName = "Must not persist",
                Kind = AccountKind.Offline,
                LoginHint = "NoPersist"
            });
            throw new InvalidOperationException("abort update");
        }));
        ProfileDocument after = new ProfileRepository(path).Load();
        Equal(beforeRevision, after.Revision);
        Equal(1, after.Accounts.Count);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("Linux defaults follow XDG config and state roots", () =>
{
    string basePath = Path.Combine(Path.GetTempPath(), "oexyz-xdg-tests");
    string home = Path.Combine(basePath, "home");
    ApplicationPaths defaults = ApplicationPaths.ResolveUnixDefaults(home, null, null);
    Equal(Path.GetFullPath(Path.Combine(home, ".config", "oexyz")), defaults.Root);
    Equal(Path.GetFullPath(Path.Combine(home, ".config", "oexyz", "profiles.json")), defaults.Profiles);
    Equal(Path.GetFullPath(Path.Combine(home, ".local", "state", "oexyz", "logs")), defaults.Logs);

    string config = Path.Combine(basePath, "configuration");
    string state = Path.Combine(basePath, "state");
    ApplicationPaths custom = ApplicationPaths.ResolveUnixDefaults(home, config, state);
    Equal(Path.GetFullPath(Path.Combine(config, "oexyz", "accounts.bin")), custom.ProtectedAccounts);
    Equal(Path.GetFullPath(Path.Combine(state, "oexyz", "diagnostics")), custom.Diagnostics);
});

Run("explicit Linux config keeps XDG state separate", () =>
{
    string home = Path.Combine(Path.GetTempPath(), "oexyz-linux-explicit-home");
    string config = Path.Combine(home, "portable", "profiles.json");
    string state = Path.Combine(home, "service-state");
    ApplicationPaths paths = ApplicationPaths.ResolveUnixExplicitConfig(config, home, state);
    Equal(Path.GetFullPath(config), paths.Profiles);
    Equal(Path.GetDirectoryName(Path.GetFullPath(config))!, paths.Root);
    Equal(Path.Combine(Path.GetFullPath(state), "oexyz", "logs"), paths.Logs);
    Equal(Path.Combine(Path.GetFullPath(state), "oexyz", "diagnostics"), paths.Diagnostics);
    Equal(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config))!, "accounts.bin"), paths.ProtectedAccounts);
});

Run("session restore drops stale bookmarks and keeps valid ones", () =>
{
    AccountProfile account = new() { Id = Guid.NewGuid(), DisplayName = "Account", Kind = AccountKind.Offline, LoginHint = "Tester" };
    ServerProfile server = new() { Id = Guid.NewGuid(), DisplayName = "Server", Address = "localhost" };
    ProfileDocument normalized = new ProfileDocument
    {
        Accounts = [account],
        Servers = [server],
        ManagedSessions =
        [
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = Guid.NewGuid(), ServerId = server.Id }
        ],
        LastSessions =
        [
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = account.Id, ServerId = server.Id },
            new SessionBookmark { AccountId = Guid.NewGuid(), ServerId = server.Id }
        ]
    }.Normalize();
    Equal(ProfileDocument.CurrentFormatVersion, normalized.FormatVersion);
    Equal(1, normalized.ManagedSessions.Count);
    Equal(1, normalized.LastSessions.Count);
});

Run("portable profile transfer excludes identity secrets and merges safely", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-transfer-tests", Guid.NewGuid().ToString("N"));
    string exportPath = Path.Combine(root, "portable.json");
    try
    {
        AccountProfile premium = new()
        {
            DisplayName = "Premium",
            Kind = AccountKind.Microsoft,
            LoginHint = "private@example.invalid",
            AccountIdentifier = "private-account-id"
        };
        AccountProfile offline = new()
        {
            DisplayName = "Offline",
            Kind = AccountKind.Offline,
            LoginHint = "LocalPlayer"
        };
        ServerProfile survival = new()
        {
            DisplayName = "Survival",
            Address = "localhost",
            QuickCommands = ["/home", "/login do-not-export", "/authme:login namespaced-secret"],
            StartupCommandsEnabled = true,
            StartupCommands = ["/spawn", "/register do-not-export"]
        };
        ProfileDocument source = new()
        {
            Accounts = [premium, offline],
            Servers = [survival],
            ManagedSessions =
            [
                new SessionBookmark { AccountId = premium.Id, ServerId = survival.Id }
            ]
        };
        ProfileTransferService.Export(source, exportPath);
        string json = File.ReadAllText(exportPath);
        True(!json.Contains("private@example.invalid", StringComparison.Ordinal), "Microsoft login hint leaked into export.");
        True(!json.Contains("private-account-id", StringComparison.Ordinal), "Microsoft account identifier leaked into export.");
        True(!json.Contains("do-not-export", StringComparison.Ordinal), "Sensitive command leaked into export.");

        ProfileImportResult first = ProfileTransferService.Import(new ProfileDocument(), exportPath);
        Equal(2, first.AccountsAdded);
        Equal(1, first.ServersAdded);
        Equal(1, first.Document.ManagedSessions.Count);
        Equal("LocalPlayer", first.Document.Accounts.Single(account => account.Kind == AccountKind.Offline).LoginHint);
        ProfileImportResult duplicate = ProfileTransferService.Import(first.Document, exportPath);
        Equal(0, duplicate.AccountsAdded);
        Equal(0, duplicate.ServersAdded);
        Equal(3, duplicate.DuplicatesSkipped);
        Equal(1, duplicate.Document.ManagedSessions.Count);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("profile import never equates Microsoft identities by display name", () =>
{
    string root = Path.Combine(Path.GetTempPath(), "oexyz-transfer-identity-tests", Guid.NewGuid().ToString("N"));
    string exportPath = Path.Combine(root, "portable.json");
    try
    {
        string maximumAccountName = new('A', ProfileRules.MaximumProfileNameLength);
        string maximumServerName = new('S', ProfileRules.MaximumProfileNameLength);
        AccountProfile existingAccount = new()
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            DisplayName = maximumAccountName,
            Kind = AccountKind.Microsoft,
            AccountIdentifier = "existing-real-identity"
        };
        AccountProfile importedAccount = new()
        {
            Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            DisplayName = maximumAccountName,
            Kind = AccountKind.Microsoft,
            AccountIdentifier = "different-real-identity"
        };
        ServerProfile existingServer = new()
        {
            Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            DisplayName = maximumServerName,
            Address = "existing.example"
        };
        ServerProfile importedServer = new()
        {
            Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            DisplayName = maximumServerName,
            Address = "localhost"
        };
        ProfileTransferService.Export(new ProfileDocument
        {
            Accounts = [importedAccount],
            Servers = [importedServer],
            ManagedSessions =
            [
                new SessionBookmark { AccountId = importedAccount.Id, ServerId = importedServer.Id }
            ]
        }, exportPath);

        ProfileImportResult result = ProfileTransferService.Import(
            new ProfileDocument { Accounts = [existingAccount], Servers = [existingServer] }, exportPath);
        Equal(1, result.AccountsAdded);
        Equal(2, result.Document.Accounts.Count);
        AccountProfile added = result.Document.Accounts.Single(account => account.Id == importedAccount.Id);
        Equal(ProfileRules.MaximumProfileNameLength, added.DisplayName.Length);
        True(added.DisplayName.EndsWith(" (imported 2)", StringComparison.Ordinal),
            "A maximum-length colliding account name was not safely shortened.");
        ServerProfile addedServer = result.Document.Servers.Single(server => server.Id == importedServer.Id);
        Equal(ProfileRules.MaximumProfileNameLength, addedServer.DisplayName.Length);
        True(addedServer.DisplayName.EndsWith(" (imported 2)", StringComparison.Ordinal),
            "A maximum-length colliding server name was not safely shortened.");
        True(added.AccountIdentifier is null, "A portable Microsoft identity unexpectedly retained an identifier.");
        Equal(importedAccount.Id, result.Document.ManagedSessions.Single().AccountId);
        True(result.Document.ManagedSessions.Single().AccountId != existingAccount.Id,
            "The imported session was rebound to a same-named Microsoft account.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
});

Run("headless argument parsing and documented exit codes", () =>
{
    CliArguments parsed = CliArguments.Parse([
        "run", "survival", "--account", "Main", "--config", "C:\\config\\profiles.json",
        "--log-file", "oexyz.log", "--log-level", "debug", "--inspect-packets",
        "--account-key-file", "account.key", "--health-port", "8765", "--dashboard",
        "--no-input", "--max-sessions", "8", "--json", "--address", "play.example.net",
        "--port", "25570", "--minecraft-version", "26.2", "--group", "AFK", "--login-hint", "user@example.net"
    ]);
    Equal("run", parsed.Command);
    Equal("survival", parsed.Target!);
    Equal("Main", parsed.Account!);
    Equal("debug", parsed.LogLevel);
    True(parsed.InspectPackets, "Packet inspection option was not parsed.");
    Equal("account.key", parsed.AccountKeyFile!);
    Equal(8765, parsed.HealthPort);
    Equal(8, parsed.MaximumSessions);
    Equal("play.example.net", parsed.Address!);
    Equal(25570, parsed.Port);
    Equal("26.2", parsed.MinecraftVersion!);
    Equal("AFK", parsed.Group!);
    Equal("user@example.net", parsed.LoginHint!);
    True(parsed.Dashboard && parsed.NoInput && parsed.JsonOutput, "v1.3 CLI switches were not parsed.");
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
    Throws<ArgumentException>(() => CliArguments.Parse(["list", "--config", "--json"]));
    Throws<ArgumentException>(() => CliArguments.Parse(["run", "test", "--health-port", "70000"]));
    Throws<ArgumentException>(() => CliArguments.Parse(["run", "test", "--max-sessions", "0"]));
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
    string unixHome = Path.Combine(Path.GetTempPath(), "oexyz-unix-home");
    Equal(Path.GetFullPath(Path.Combine(unixHome, ".local", "bin")), PathRegistration.GetUnixUserBin(unixHome));
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

static Process StartTestProcess(params string[] arguments)
{
    string processPath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test process path is unavailable.");
    ProcessStartInfo start = new(processPath)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start a profile update worker.");
}
