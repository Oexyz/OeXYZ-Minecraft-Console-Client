using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OeXYZ.Cli;
using OeXYZ.Authentication;
using OeXYZ.Core;
using OeXYZ.Protocol;
using OeXYZ.Session;

return (int)await CliApplication.RunAsync(args).ConfigureAwait(false);

internal static class CliApplication
{
    private static readonly SemaphoreSlim OutputLock = new(1, 1);

    public static async Task<OeXYZExitCode> RunAsync(string[] args)
    {
        CliArguments options;
        try { options = CliArguments.Parse(args); }
        catch (ArgumentException exception)
        {
            await ErrorAsync(exception.Message).ConfigureAwait(false);
            PrintHelp();
            return OeXYZExitCode.InvalidArguments;
        }

        if (options.ShowHelp || string.IsNullOrEmpty(options.Command))
        {
            PrintHelp();
            return OeXYZExitCode.Success;
        }

        try
        {
            if (options.Command is "install-path" or "uninstall-path")
                return InstallPath(options.Command == "install-path");
            if (options.Command == "healthcheck")
                return await CheckHealthAsync(options.Target).ConfigureAwait(false);
            if (options.Command == "account-key-generate")
                return GenerateAccountKey(options.Target);

            ApplicationPaths paths = ApplicationPaths.Resolve(options.ConfigPath);
            if (options.Command is "control-token-create" or "control-token-check")
                return ManageControlToken(paths, options, options.Command == "control-token-create");
            ProfileRepository repository = new(paths.Profiles);
            if (options.Command == "doctor")
                return await RunDoctorAsync(repository, paths, options).ConfigureAwait(false);
            if (options.Command == "profiles-recover")
                return RecoverProfiles(repository, options.JsonOutput);
            ProfileDocument profiles = repository.Load();
            return options.Command switch
            {
                "list" or "profiles" => PrintProfiles(profiles, paths, options.JsonOutput),
                "setup" => await RunSetupAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "export-profiles" => ExportProfiles(profiles, repository, options.Target),
                "import-profiles" => ImportProfiles(profiles, repository, options.Target),
                "account-add-offline" => AddAccount(profiles, repository, options, AccountKind.Offline),
                "account-add-microsoft" => AddAccount(profiles, repository, options, AccountKind.Microsoft),
                "account-login" => await LoginAccountAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "server-add" => AddServer(profiles, repository, options),
                "proxy-add" => AddProxy(profiles, repository, options),
                "proxy-list" => ListProxies(profiles, options.JsonOutput),
                "proxy-delete" => DeleteProxy(profiles, repository, options.Target),
                "proxy-set-credentials" => await SetProxyCredentialsAsync(
                    profiles, repository, paths, options, clear: false).ConfigureAwait(false),
                "proxy-clear-credentials" => await SetProxyCredentialsAsync(
                    profiles, repository, paths, options, clear: true).ConfigureAwait(false),
                "failover-list" => ListFailoverEndpoints(profiles, options.Target, options.JsonOutput),
                "failover-add" => AddFailoverEndpoint(profiles, repository, options),
                "failover-delete" => DeleteFailoverEndpoint(profiles, repository, options),
                "automation-list" => ListAutomations(profiles, options.Target, options.JsonOutput),
                "automation-validate" => ValidateAutomations(profiles, options.Target, options.JsonOutput),
                "status" => await ShowStatusAsync(profiles, paths, options).ConfigureAwait(false),
                "connect" or "run" => await RunOneAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "run-address" => await RunAddressAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "connect-all" => await RunManyAsync(profiles, repository, paths, options, null).ConfigureAwait(false),
                "connect-group" => await RunManyAsync(profiles, repository, paths, options, options.Target).ConfigureAwait(false),
                "supervise" => await RunManyAsync(profiles, repository, paths, options, options.Target).ConfigureAwait(false),
                _ => UnknownCommand(options.Command)
            };
        }
        catch (ProfileRecoveryAvailableException exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message +
                " Run 'oexyz profiles-recover' to restore it explicitly.")).ConfigureAwait(false);
            return OeXYZExitCode.InvalidArguments;
        }
        catch (FileNotFoundException exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return OeXYZExitCode.ProfileNotFound;
        }
        catch (InvalidDataException exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return OeXYZExitCode.InvalidArguments;
        }
        catch (ArgumentException exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return OeXYZExitCode.InvalidArguments;
        }
        catch (Exception exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return MapFailure(exception);
        }
    }

    private static OeXYZExitCode PrintProfiles(ProfileDocument profiles, ApplicationPaths paths, bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                formatVersion = profiles.FormatVersion,
                accounts = profiles.Accounts.Select(account => new { account.DisplayName, kind = account.Kind.ToString() }),
                servers = profiles.Servers.Select(server => new
                {
                    server.DisplayName,
                    server.Address,
                    server.CustomPort,
                    server.Version,
                    server.Group
                }),
                managedSessions = profiles.ManagedSessions.Select(session => new
                {
                    account = profiles.Accounts.FirstOrDefault(account => account.Id == session.AccountId)?.DisplayName,
                    server = profiles.Servers.FirstOrDefault(server => server.Id == session.ServerId)?.DisplayName
                })
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return OeXYZExitCode.Success;
        }
        WriteOutput($"OeXYZ profiles · {paths.Profiles}");
        WriteOutput("Accounts:");
        foreach (AccountProfile account in profiles.Accounts)
            WriteOutput($"  {account.DisplayName}  [{account.Kind}]");
        WriteOutput("Servers:");
        foreach (ServerProfile server in profiles.Servers)
            WriteOutput($"  {server.DisplayName}  {server.Address}{(server.CustomPort > 0 ? $":{server.CustomPort}" : string.Empty)}{(server.Group.Length > 0 ? $"  group={server.Group}" : string.Empty)}");
        WriteOutput("Managed sessions:");
        foreach (SessionBookmark session in profiles.ManagedSessions)
        {
            string? account = profiles.Accounts.FirstOrDefault(item => item.Id == session.AccountId)?.DisplayName;
            string? server = profiles.Servers.FirstOrDefault(item => item.Id == session.ServerId)?.DisplayName;
            if (account is not null && server is not null) WriteOutput($"  {account} -> {server}");
        }
        return OeXYZExitCode.Success;
    }

    private static async Task<OeXYZExitCode> RunSetupAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        if (options.NoInput) throw new ArgumentException("setup is interactive and cannot be combined with --no-input.");
        paths.EnsureDirectories();
        string? accountKeyFile = OperatingSystem.IsWindows()
            ? null
            : ResolveSetupAccountKeyFile(paths, options.AccountKeyFile);
        AuthenticationService? authentication = null;
        try
        {
            async Task<string> LoginAsync(AccountProfile account, CancellationToken cancellationToken)
            {
                if (!OperatingSystem.IsWindows() && accountKeyFile is not null && !File.Exists(accountKeyFile))
                {
                    if (File.Exists(paths.ProtectedAccounts))
                        throw new InvalidDataException(
                            $"The encrypted account store exists, but its key is missing at {accountKeyFile}. " +
                            "Restore the original key; generating a replacement would not unlock existing sessions.");
                    AccountKeyFile.Generate(accountKeyFile);
                    WriteOutput($"Created a private account-store key at {accountKeyFile}.");
                }

                AccountSecretProvider? secretProvider = OperatingSystem.IsWindows()
                    ? null
                    : CreateAccountSecretProvider(paths.ProtectedAccounts, accountKeyFile);
                authentication ??= new AuthenticationService(
                    paths.ProtectedAccounts,
                    secretProvider,
                    prompt => PresentDeviceCode(prompt, null));
                MinecraftIdentity identity = await authentication.GetIdentityAsync(
                    account,
                    WriteError,
                    cancellationToken).ConfigureAwait(false);
                return identity.Username;
            }

            SetupWizard wizard = new(Console.In, Console.Out, LoginAsync, repository.Save, IsContainer());
            await wizard.RunAsync(profiles, CancellationToken.None).ConfigureAwait(false);
            return OeXYZExitCode.Success;
        }
        finally
        {
            if (authentication is not null) await authentication.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string ResolveSetupAccountKeyFile(ApplicationPaths paths, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        return IsContainer() && Directory.Exists("/keys")
            ? "/keys/account.key"
            : Path.Combine(paths.Root, "account.key");
    }

    private static bool IsContainer() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase) || File.Exists("/.dockerenv");

    private static OeXYZExitCode ExportProfiles(
        ProfileDocument profiles,
        ProfileRepository repository,
        string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("export-profiles requires a destination JSON path.");
        string path = Path.GetFullPath(destination);
        if (string.Equals(path, repository.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The portable export must not overwrite the active profile file.");
        ProfileTransferService.Export(profiles, path);
        WriteOutput($"Exported {profiles.Accounts.Count} accounts and {profiles.Servers.Count} servers without tokens or Microsoft identifiers to {path}");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode RecoverProfiles(ProfileRepository repository, bool jsonOutput)
    {
        ProfileRecoveryState state = repository.InspectRecovery();
        if (!state.CanRestore)
            throw new InvalidDataException("A valid profiles.json.bak is not available for recovery.");
        ProfileRecoveryResult result = repository.RestoreBackup();
        if (jsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                recovered = true,
                revision = result.Document.Revision,
                corruptFilePreserved = result.PreservedCorruptPath is not null,
                preservedCorruptPath = result.PreservedCorruptPath
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        }
        else
        {
            WriteOutput(result.PreservedCorruptPath is null
                ? "Restored profiles.json from its validated backup."
                : $"Restored profiles.json from its validated backup; preserved the corrupt file at {result.PreservedCorruptPath}");
        }
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ManageControlToken(
        ApplicationPaths paths,
        CliArguments options,
        bool create)
    {
        string target = Path.GetFullPath(options.ControlTokenFile ?? options.Target ?? paths.ControlToken);
        if (create)
        {
            ControlTokenFile.Create(target);
            WriteOutput($"Created a private 256-bit control token at {target}. Its value was not displayed.");
        }
        else
        {
            byte[] token = ControlTokenFile.Read(target);
            CryptographicOperations.ZeroMemory(token);
            WriteOutput($"The control-token file at {target} is valid and private.");
        }
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ImportProfiles(
        ProfileDocument profiles,
        ProfileRepository repository,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("import-profiles requires a portable JSON path.");
        if (string.Equals(Path.GetFullPath(source), repository.Path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Import from a separate portable export, not the active profile file.");
        ProfileImportResult? result = null;
        _ = repository.Update(current =>
        {
            result = ProfileTransferService.Import(current, source);
            return result.Document;
        });
        WriteOutput(
            $"Imported {result!.AccountsAdded} accounts and {result.ServersAdded} servers; " +
            $"skipped {result.DuplicatesSkipped} duplicates. Backup: {repository.BackupPath}");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode AddAccount(
        ProfileDocument profiles,
        ProfileRepository repository,
        CliArguments options,
        AccountKind kind)
    {
        string name = ValidateProfileName(options.Target, "account");
        string loginHint = kind == AccountKind.Offline ? name : options.LoginHint?.Trim() ?? string.Empty;
        if (kind == AccountKind.Offline) ProfileRules.EnsureValidOfflineName(loginHint);
        if (loginHint.Length > 256 || loginHint.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The Microsoft login hint is invalid or exceeds 256 characters.");
        AccountProfile candidate = new()
        {
            DisplayName = name,
            Kind = kind,
            LoginHint = loginHint
        };
        _ = repository.Update(current =>
        {
            if (current.Accounts.Any(account =>
                    string.Equals(account.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"An account profile named '{name}' already exists.");
            current.Accounts.Add(candidate);
            return current;
        });
        WriteOutput(kind == AccountKind.Offline
            ? $"Added offline account '{name}'. It is valid only on servers that explicitly allow offline mode."
            : $"Added Microsoft account profile '{name}'. Device/browser authentication starts on first connection.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode AddServer(
        ProfileDocument profiles,
        ProfileRepository repository,
        CliArguments options)
    {
        string name = ValidateProfileName(options.Target, "server");
        if (string.IsNullOrWhiteSpace(options.Address))
            throw new ArgumentException("server-add requires --address <host[:port]>.");
        string address = options.Address.Trim();
        _ = ParseServerAddressArgument(address, options.Port);
        string version = string.IsNullOrWhiteSpace(options.MinecraftVersion) ? "auto" : options.MinecraftVersion.Trim();
        if (!string.Equals(version, "auto", StringComparison.OrdinalIgnoreCase))
            _ = ProtocolCatalog.LoadEmbedded().Resolve(version);
        string group = options.Group?.Trim() ?? string.Empty;
        if (group.Length > 64 || group.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidDataException("The session group is invalid or exceeds 64 characters.");
        ServerProfile candidate = new()
        {
            DisplayName = name,
            Address = address,
            CustomPort = options.Port,
            Version = version,
            Group = group,
            ProxyProfileId = string.IsNullOrWhiteSpace(options.Proxy)
                ? null
                : FindProxy(profiles, options.Proxy).Id
        };
        _ = repository.Update(current =>
        {
            if (current.Servers.Any(server =>
                    string.Equals(server.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"A server profile named '{name}' already exists.");
            current.Servers.Add(candidate);
            return current;
        });
        WriteOutput($"Added server profile '{name}' for {address}{(options.Port > 0 ? $" (custom port {options.Port})" : string.Empty)}.");
        return OeXYZExitCode.Success;
    }

    private static async Task<OeXYZExitCode> LoginAccountAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        AccountProfile account = FindAccount(profiles, options.Target ?? options.Account);
        if (account.Kind != AccountKind.Microsoft)
            throw new ArgumentException("account-login requires a Microsoft account profile.");
        paths.EnsureDirectories();
        AccountSecretProvider? secretProvider = OperatingSystem.IsWindows()
            ? null
            : CreateAccountSecretProvider(paths.ProtectedAccounts, options.AccountKeyFile);
        await using AuthenticationService authentication = new(
            paths.ProtectedAccounts,
            secretProvider,
            prompt => PresentDeviceCode(prompt, null));
        MinecraftIdentity identity = await authentication.GetIdentityAsync(
            account,
            WriteError,
            CancellationToken.None).ConfigureAwait(false);
        PersistAccountIdentifier(repository, account);
        WriteOutput($"Microsoft account '{account.DisplayName}' is ready as {identity.Username}.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode GenerateAccountKey(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("account-key-generate requires a destination path.");
        string path = Path.GetFullPath(target);
        AccountKeyFile.Generate(path);
        WriteOutput($"Created a private account-store key at {path}. Its value was not displayed.");
        return OeXYZExitCode.Success;
    }

    private static void PersistAccountIdentifier(ProfileRepository repository, AccountProfile account)
    {
        string? identifier = account.AccountIdentifier;
        _ = repository.Update(current =>
        {
            int index = current.Accounts.FindIndex(candidate => candidate.Id == account.Id);
            if (index < 0)
                throw new InvalidDataException(
                    $"Account profile '{account.DisplayName}' was removed while authentication was in progress.");
            current.Accounts[index] = current.Accounts[index] with { AccountIdentifier = identifier };
            return current;
        });
    }

    private static string ValidateProfileName(string? value, string kind) =>
        ProfileRules.NormalizeProfileName(value, kind);

    private static async Task<OeXYZExitCode> ShowStatusAsync(
        ProfileDocument profiles,
        ApplicationPaths paths,
        CliArguments options)
    {
        ServerProfile server = FindServer(profiles, options.Target);
        AccountSecretProvider? secretProvider = OperatingSystem.IsWindows()
            ? null
            : CreateAccountSecretProvider(paths.ProtectedAccounts, options.AccountKeyFile);
        IConnectionDialer? dialer = null;
        try
        {
            dialer = await CreateConnectionDialerAsync(
                profiles, server, paths, secretProvider, CancellationToken.None).ConfigureAwait(false);
            MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(
                    server.Address, server.CustomPort, dialer: dialer)
                .ConfigureAwait(false);
            if (options.JsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    online = true,
                    status.VersionName,
                    status.ProtocolVersion,
                    status.PingMilliseconds,
                    status.PlayersOnline,
                    status.PlayersMaximum,
                    motd = status.Description,
                    endpoint = $"{status.Address.NetworkHost}:{status.Address.Port}",
                    status.Address.UsedSrv
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
                return OeXYZExitCode.Success;
            }
            WriteOutput($"ONLINE | {status.VersionName} | Protocol {status.ProtocolVersion} | " +
                        $"Ping {status.PingMilliseconds} ms | Players {status.PlayersOnline}/{status.PlayersMaximum}");
            WriteOutput(status.Description);
            WriteOutput($"Endpoint: {status.Address.NetworkHost}:{status.Address.Port}");
            return OeXYZExitCode.Success;
        }
        catch (Exception exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return OeXYZExitCode.ConnectionFailure;
        }
        finally
        {
            if (dialer is IDisposable disposable) disposable.Dispose();
        }
    }

    private static async Task<OeXYZExitCode> RunDoctorAsync(
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        ProfileDocument profiles;
        string? configurationError = null;
        try
        {
            profiles = repository.Load();
        }
        catch (Exception exception)
        {
            profiles = new ProfileDocument();
            configurationError = SensitiveDataRedactor.RedactText(exception.Message);
        }
        ServerProfile? server = configurationError is null && !string.IsNullOrWhiteSpace(options.Target)
            ? FindServer(profiles, options.Target)
            : null;
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        DoctorReport report = await DoctorService.RunAsync(
            paths,
            profiles,
            version,
            server,
            options.AccountKeyFile,
            options.ControlTokenFile,
            options.AllowRemoteControl,
            configurationError: configurationError).ConfigureAwait(false);
        if (options.JsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true
            }));
        }
        else
        {
            WriteOutput($"OeXYZ doctor {report.OeXYZVersion} · {report.OperatingSystem} · {report.Architecture}");
            WriteOutput($"Runtime: {report.Framework} · Container: {report.Container} · WSL: {report.Wsl}");
            foreach (DoctorCheck check in report.Checks)
                WriteOutput($"[{check.Status.ToString().ToUpperInvariant(),-7}] {check.Name}: {check.Message}");
        }
        return report.Successful ? OeXYZExitCode.Success : OeXYZExitCode.DiagnosticsFailed;
    }

    private static async Task<OeXYZExitCode> CheckHealthAsync(string? target)
    {
        string value = string.IsNullOrWhiteSpace(target) ? "http://127.0.0.1:8765/health" : target;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttp ||
            !(uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("healthcheck accepts only an http:// loopback URL.");
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is > 32 * 1024)
                throw new InvalidDataException("The local health response exceeds 32 KiB.");
            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using MemoryStream bounded = new(32 * 1024);
            byte[] buffer = new byte[4096];
            while (true)
            {
                int read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (bounded.Length + read > 32 * 1024)
                    throw new InvalidDataException("The local health response exceeds 32 KiB.");
                bounded.Write(buffer, 0, read);
            }
            string text = Encoding.UTF8.GetString(bounded.GetBuffer(), 0, checked((int)bounded.Length));
            WriteOutput(text);
            return response.IsSuccessStatusCode ? OeXYZExitCode.Success : OeXYZExitCode.ConnectionFailure;
        }
        catch (HttpRequestException exception)
        {
            await ErrorAsync("OeXYZ health endpoint is unavailable: " + exception.Message).ConfigureAwait(false);
            return OeXYZExitCode.ConnectionFailure;
        }
        catch (TaskCanceledException)
        {
            await ErrorAsync("OeXYZ health endpoint timed out.").ConfigureAwait(false);
            return OeXYZExitCode.ConnectionFailure;
        }
    }

    private static async Task<OeXYZExitCode> RunOneAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        ServerProfile server = FindServer(profiles, options.Target);
        AccountProfile account = FindAccount(profiles, options.Account);
        return await RunSessionsAsync([(account, server)], profiles, repository, paths, options).ConfigureAwait(false);
    }

    private static async Task<OeXYZExitCode> RunAddressAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        if (string.IsNullOrWhiteSpace(options.Target))
            throw new ArgumentException("run-address requires host[:port].");
        ServerAddress parsed = ParseServerAddressArgument(options.Target);
        ServerProfile server = new()
        {
            DisplayName = options.Target,
            Address = options.Target,
            Version = "auto",
            AntiAfk = false,
            AutoReconnect = false,
            AutoRespawn = true
        };
        _ = parsed;
        AccountProfile account = FindAccount(profiles, options.Account);
        return await RunSessionsAsync([(account, server)], profiles, repository, paths, options).ConfigureAwait(false);
    }

    private static ServerAddress ParseServerAddressArgument(string address, int customPort = 0)
    {
        try
        {
            return ServerAddress.Parse(address, customPort);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(exception.Message, exception);
        }
    }

    private static async Task<OeXYZExitCode> RunManyAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options,
        string? group)
    {
        string? effectiveGroup = string.IsNullOrWhiteSpace(group) && options.Command == "supervise"
            ? ReadNonSecretEnvironmentOption("OEXYZ_GROUP")
            : group;
        if (options.Command == "connect-group" && string.IsNullOrWhiteSpace(effectiveGroup))
            throw new ArgumentException("connect-group requires a group name.");
        string? selectedAccount = string.IsNullOrWhiteSpace(options.Account)
            ? ReadNonSecretEnvironmentOption("OEXYZ_ACCOUNT")
            : options.Account;
        if (options.Command == "supervise" && string.IsNullOrWhiteSpace(selectedAccount) &&
            profiles.ManagedSessions.Count > 0)
        {
            IReadOnlyList<(AccountProfile Account, ServerProfile Server)> managed =
                ResolveManagedSessions(profiles, effectiveGroup);
            if (managed.Count == 0) throw new FileNotFoundException(string.IsNullOrWhiteSpace(effectiveGroup)
                ? "No valid managed sessions were found. Run 'oexyz setup' to configure one."
                : $"No managed sessions were found in group '{effectiveGroup}'.");
            return await RunSessionsAsync(managed, profiles, repository, paths, options).ConfigureAwait(false);
        }

        AccountProfile account = FindAccount(profiles, selectedAccount);
        List<ServerProfile> servers = profiles.Servers
            .Where(server => string.IsNullOrWhiteSpace(effectiveGroup) ||
                             string.Equals(server.Group, effectiveGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (servers.Count == 0) throw new FileNotFoundException(string.IsNullOrWhiteSpace(effectiveGroup)
            ? "No server profiles were found."
            : $"No profiles were found in group '{effectiveGroup}'.");
        return await RunSessionsAsync(servers.Select(server => (account, server)).ToList(),
            profiles, repository, paths, options).ConfigureAwait(false);
    }

    internal static IReadOnlyList<(AccountProfile Account, ServerProfile Server)> ResolveManagedSessions(
        ProfileDocument profiles,
        string? group)
    {
        Dictionary<Guid, AccountProfile> accounts = profiles.Accounts.ToDictionary(account => account.Id);
        Dictionary<Guid, ServerProfile> servers = profiles.Servers.ToDictionary(server => server.Id);
        List<(AccountProfile Account, ServerProfile Server)> result = [];
        foreach (SessionBookmark binding in profiles.ManagedSessions.Distinct())
        {
            if (!accounts.TryGetValue(binding.AccountId, out AccountProfile? account) ||
                !servers.TryGetValue(binding.ServerId, out ServerProfile? server))
                continue;
            if (!string.IsNullOrWhiteSpace(group) &&
                !string.Equals(server.Group, group, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add((account, server));
        }
        return result;
    }

    private static async Task<OeXYZExitCode> RunSessionsAsync(
        IReadOnlyList<(AccountProfile Account, ServerProfile Server)> requested,
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        if (requested.Count > options.MaximumSessions)
            throw new ArgumentException(
                $"The request contains {requested.Count} sessions, exceeding --max-sessions {options.MaximumSessions}.");
        foreach ((AccountProfile account, ServerProfile server) in requested)
        {
            if (account.Kind == AccountKind.Offline) ProfileRules.EnsureValidOfflineName(account.LoginHint);
            if (string.IsNullOrWhiteSpace(server.Address))
                throw new InvalidDataException($"Server profile '{server.DisplayName}' has no address.");
        }
        paths.EnsureDirectories();
        AccountSecretProvider? secretProvider = OperatingSystem.IsWindows()
            ? null
            : CreateAccountSecretProvider(paths.ProtectedAccounts, options.AccountKeyFile);
        SessionRuntimeRegistry runtime = new();
        await using SessionControlManager controls = new();
        TerminalDashboard? dashboard = options.Dashboard
            ? new TerminalDashboard(runtime, acceptsInput: !options.NoInput)
            : null;
        await using AuthenticationService authentication = new(
            paths.ProtectedAccounts,
            secretProvider,
            prompt => PresentDeviceCode(prompt, dashboard));
        using CancellationTokenSource lifetime = new();
        int userStopRequested = 0;
        ConcurrentDictionary<ConsoleSession, byte> userStoppedSessions = new();
        void RequestUserStop()
        {
            Interlocked.Exchange(ref userStopRequested, 1);
            lifetime.Cancel();
        }
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestUserStop();
        };
        Console.CancelKeyPress += cancelHandler;
        PosixSignalRegistration? terminateSignal = null;
        if (!OperatingSystem.IsWindows())
        {
            terminateSignal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                RequestUserStop();
            });
        }
        await using CliLog log = new(options.LogFile, options.LogLevel, WriteError);
        List<ConsoleSession> sessions = [];
        List<IDisposable> connectionDialers = [];
        LoopbackHealthServer? healthServer = null;
        SystemdNotifier? systemd = null;
        Task? logRetentionTask = null;
        try
        {
            // A hidden Linux passphrase must be consumed before the dashboard
            // starts its own Console.ReadKey loop. Device-code interaction can
            // then safely continue in the dashboard because it uses a browser.
            if (!OperatingSystem.IsWindows() && dashboard is not null &&
                requested.Any(item => item.Account.Kind == AccountKind.Microsoft))
            {
                await authentication.PrepareAsync(
                    WriteError,
                    lifetime.Token).ConfigureAwait(false);
            }

            // Enter and validate dashboard terminal mode before any network
            // session is started, so a TTY failure cannot leave sessions behind.
            dashboard?.Start();

            foreach ((AccountProfile account, ServerProfile server) in requested)
            {
                IConnectionDialer sessionDialer = await CreateConnectionDialerAsync(
                    profiles, server, paths, secretProvider, lifetime.Token).ConfigureAwait(false);
                if (sessionDialer is IDisposable disposableDialer) connectionDialers.Add(disposableDialer);
                ConsoleSession session = new(account, server, authentication,
                    () => PersistAccountIdentifier(repository, account), paths.Logs, options.InspectPackets,
                    sessionDialer);
                string prefix = requested.Count == 1 ? string.Empty : $"[{account.DisplayName} @ {server.DisplayName}] ";
                session.LineAdded += line => WriteLine(log, prefix, line, dashboard);
                session.PacketTraced += trace => WriteTrace(log, prefix, trace, dashboard);
                string sessionId = controls.Register(session);
                SessionControlResult started = await controls.StartAsync(sessionId, lifetime.Token).ConfigureAwait(false);
                if (!started.Success) throw new InvalidOperationException(started.Message);
                runtime.Register(session);
                sessions.Add(session);
            }

            ApplyLogRetention(paths, profiles, sessions);
            logRetentionTask = MaintainLogRetentionAsync(paths, profiles, sessions, lifetime.Token);

            if (options.HealthPort > 0)
            {
                string controlTokenPath = Path.GetFullPath(options.ControlTokenFile ?? paths.ControlToken);
                byte[]? controlToken = File.Exists(controlTokenPath) ? ControlTokenFile.Read(controlTokenPath) : null;
                if (options.AllowRemoteControl && controlToken is null)
                    throw new InvalidOperationException("--allow-remote-control requires a private control-token file.");
                try
                {
                    healthServer = new LoopbackHealthServer(
                        runtime,
                        options.HealthPort,
                        controls,
                        controlToken,
                        options.AllowRemoteControl);
                }
                finally
                {
                    if (controlToken is not null) CryptographicOperations.ZeroMemory(controlToken);
                }
                await healthServer.StartAsync(lifetime.Token).ConfigureAwait(false);
                string bindHost = options.AllowRemoteControl ? "0.0.0.0" : "127.0.0.1";
                string healthMessage = $"OeXYZ management endpoint: http://{bindHost}:{healthServer.Port}/health";
                if (dashboard is null) WriteOutput(healthMessage);
                else dashboard.AddEvent(healthMessage);
                log.Write("information", healthMessage);
            }

            systemd = await SystemdNotifier.TryStartAsync(sessions.Count, lifetime.Token).ConfigureAwait(false);
            if (systemd is not null)
            {
                const string message = "systemd readiness and watchdog notifications enabled.";
                log.Write("information", message);
                if (dashboard is not null) dashboard.AddEvent(message);
                else WriteOutput(message);
            }

            Task allCompleted = Task.WhenAll(sessions.Select(session => session.Completion));
            Task<bool> input = options.NoInput
                ? WaitForCancellationResultAsync(lifetime.Token)
                : dashboard is null
                    ? ReadInputAsync(sessions, userStoppedSessions, lifetime.Token)
                    : ReadDashboardInputAsync(sessions, userStoppedSessions, dashboard, lifetime.Token);
            Task cancellation = WaitForCancellationAsync(lifetime.Token);
            Task winner = await Task.WhenAny(allCompleted, input, cancellation).ConfigureAwait(false);
            if (winner == input && await input.ConfigureAwait(false))
                Interlocked.Exchange(ref userStopRequested, 1);
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
            foreach (ConsoleSession session in sessions) session.Stop();
            try { await input.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await allCompleted.ConfigureAwait(false);

            bool stoppedByUser = Volatile.Read(ref userStopRequested) != 0;
            OeXYZExitCode sessionResult = AggregateSessionExitCode(
                sessions.Select(session => (
                    session.FailureException,
                    UserStopped: userStoppedSessions.ContainsKey(session))),
                stoppedByUser);
            return !stoppedByUser && log.FailureException is not null
                ? OeXYZExitCode.InternalError
                : sessionResult;
        }
        finally
        {
            lifetime.Cancel();
            if (logRetentionTask is not null)
            {
                try { await logRetentionTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            }
            if (systemd is not null) await systemd.DisposeAsync().ConfigureAwait(false);
            if (dashboard is not null) await dashboard.DisposeAsync().ConfigureAwait(false);
            if (healthServer is not null) await healthServer.DisposeAsync().ConfigureAwait(false);
            foreach (ConsoleSession session in sessions) await session.DisposeAsync().ConfigureAwait(false);
            foreach (IDisposable connectionDialer in connectionDialers) connectionDialer.Dispose();
            terminateSignal?.Dispose();
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static OeXYZExitCode AddProxy(
        ProfileDocument profiles,
        ProfileRepository repository,
        CliArguments options)
    {
        string name = ValidateProfileName(options.Target, "proxy");
        ProxyKind kind = options.ProxyKind switch
        {
            "direct" => ProxyKind.Direct,
            "socks5" => ProxyKind.Socks5,
            "http-connect" => ProxyKind.HttpConnect,
            _ => throw new ArgumentException("proxy-add requires --proxy-kind direct, socks5, or http-connect.")
        };
        if (profiles.ProxyProfiles.Any(proxy => string.Equals(proxy.DisplayName, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"A proxy profile named '{name}' already exists.");
        ProxyProfile proxy = new()
        {
            DisplayName = name,
            Kind = kind,
            Host = kind == ProxyKind.Direct ? string.Empty : options.Address?.Trim() ?? string.Empty,
            Port = kind == ProxyKind.Direct ? 0 : options.Port,
            DnsMode = options.ProxyDns ? ProxyDnsMode.Proxy : ProxyDnsMode.Local
        };
        profiles.ProxyProfiles.Add(proxy);
        repository.Save(profiles);
        WriteOutput($"Added proxy profile '{name}' ({kind}); no credentials were stored.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ListProxies(ProfileDocument profiles, bool json)
    {
        var safe = profiles.ProxyProfiles.Select(proxy => new
        {
            id = proxy.Id,
            proxy.DisplayName,
            kind = proxy.Kind.ToString(),
            proxy.Host,
            proxy.Port,
            dns = proxy.DnsMode.ToString(),
            hasCredentials = proxy.SecretReference is not null
        }).ToArray();
        if (json) Console.WriteLine(JsonSerializer.Serialize(safe, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true }));
        else foreach (var proxy in safe) WriteOutput($"{proxy.DisplayName} | {proxy.kind} | {proxy.Host}:{proxy.Port} | credentials: {proxy.hasCredentials}");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ListFailoverEndpoints(
        ProfileDocument profiles,
        string? target,
        bool json)
    {
        ServerProfile server = FindServer(profiles, target);
        var endpoints = server.Endpoints.Select(endpoint => new
        {
            id = endpoint.Id,
            endpoint.Address,
            port = endpoint.CustomPort,
            endpoint.Priority,
            endpoint.FailureThreshold,
            endpoint.CooldownSeconds
        }).ToArray();
        if (json)
            WriteOutputBlock(JsonSerializer.Serialize(endpoints,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else
            foreach (var endpoint in endpoints)
                WriteOutput($"{endpoint.id} | {endpoint.Address}:{(endpoint.port > 0 ? endpoint.port : 25565)} | " +
                            $"priority {endpoint.Priority} | failure threshold {endpoint.FailureThreshold} | " +
                            $"cooldown {endpoint.CooldownSeconds}s");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode AddFailoverEndpoint(
        ProfileDocument profiles,
        ProfileRepository repository,
        CliArguments options)
    {
        _ = FindServer(profiles, options.Target);
        if (string.IsNullOrWhiteSpace(options.Address))
            throw new ArgumentException("failover-add requires --address <host[:port]>.");
        ServerAddress address = ParseServerAddressArgument(options.Address.Trim(), options.Port);
        _ = repository.Update(current =>
        {
            ServerProfile server = FindServer(current, options.Target);
            if (server.Endpoints.Count >= 8)
                throw new InvalidDataException("A server profile may contain at most 8 endpoints.");
            int customPort = options.Port > 0 || address.HasExplicitPort ? address.Port : 0;
            if (server.Endpoints.Any(endpoint =>
                    string.Equals(endpoint.Address, address.HandshakeHost, StringComparison.OrdinalIgnoreCase) &&
                    endpoint.CustomPort == customPort))
                throw new InvalidDataException("That failover endpoint already exists.");
            int priority = server.Endpoints.Count == 0 ? 0 : server.Endpoints.Max(endpoint => endpoint.Priority) + 1;
            server.Endpoints.Add(new ServerEndpointProfile
            {
                Address = address.HandshakeHost,
                CustomPort = customPort,
                Priority = Math.Min(priority, 1000)
            });
            return current;
        });
        WriteOutput($"Added failover endpoint to '{options.Target}'.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode DeleteFailoverEndpoint(
        ProfileDocument profiles,
        ProfileRepository repository,
        CliArguments options)
    {
        _ = FindServer(profiles, options.Target);
        if (string.IsNullOrWhiteSpace(options.Address))
            throw new ArgumentException("failover-delete requires --address <host[:port]>.");
        ServerAddress address = ParseServerAddressArgument(options.Address.Trim(), options.Port);
        int customPort = options.Port > 0 || address.HasExplicitPort ? address.Port : 0;
        _ = repository.Update(current =>
        {
            ServerProfile server = FindServer(current, options.Target);
            int removed = server.Endpoints.RemoveAll(endpoint =>
                string.Equals(endpoint.Address, address.HandshakeHost, StringComparison.OrdinalIgnoreCase) &&
                endpoint.CustomPort == customPort);
            if (removed == 0) throw new FileNotFoundException("The failover endpoint was not found.");
            return current;
        });
        WriteOutput($"Deleted failover endpoint from '{options.Target}'.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ListAutomations(ProfileDocument profiles, string? target, bool json)
    {
        ServerProfile server = FindServer(profiles, target);
        var rules = server.Automations.Select(rule => new
        {
            id = rule.Id,
            rule.Name,
            rule.Enabled,
            trigger = rule.Trigger.ToString(),
            rule.Pattern,
            rule.UseRegex,
            rule.CooldownSeconds,
            rule.MaximumRunsPerHour,
            actions = rule.Actions.Select(action => new { kind = action.Kind.ToString(), action.Value })
        }).ToArray();
        if (json)
            WriteOutputBlock(JsonSerializer.Serialize(rules,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else
            foreach (var rule in rules)
                WriteOutput($"{rule.id} | {rule.Name} | {(rule.Enabled ? "enabled" : "disabled")} | " +
                            $"{rule.trigger} | {rule.actions.Count()} action(s)");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode ValidateAutomations(ProfileDocument profiles, string? target, bool json)
    {
        ServerProfile server = FindServer(profiles, target);
        ServerProfile validated = new ProfileDocument
        {
            Servers = [server],
            ProxyProfiles = profiles.ProxyProfiles.ToList()
        }.Normalize().Servers.Single();
        var result = new
        {
            valid = true,
            rules = validated.Automations.Count,
            enabled = validated.Automations.Count(rule => rule.Enabled)
        };
        if (json)
            WriteOutputBlock(JsonSerializer.Serialize(result,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else
            WriteOutput($"Validated {result.rules} bounded automation rule(s); {result.enabled} enabled.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode DeleteProxy(
        ProfileDocument profiles,
        ProfileRepository repository,
        string? target)
    {
        ProxyProfile proxy = FindProxy(profiles, target);
        if (profiles.Servers.Any(server => server.ProxyProfileId == proxy.Id))
            throw new InvalidDataException("The proxy is still referenced by a server profile.");
        profiles.ProxyProfiles.Remove(proxy);
        repository.Save(profiles);
        WriteOutput($"Deleted proxy profile '{proxy.DisplayName}'. Its protected secret, if any, must be cleared first.");
        return OeXYZExitCode.Success;
    }

    private static async Task<OeXYZExitCode> SetProxyCredentialsAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options,
        bool clear)
    {
        ProxyProfile proxy = FindProxy(profiles, options.Target);
        string reference = proxy.SecretReference ?? $"proxy.{proxy.Id:N}";
        byte[]? master = null;
        byte[]? password = null;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                AccountSecretProvider provider = CreateAccountSecretProvider(paths.ProtectedAccounts, options.AccountKeyFile);
                master = await provider(WriteError, CancellationToken.None).ConfigureAwait(false);
            }
            using LocalSecretStore store = new(paths.Secrets, master ?? []);
            if (clear)
            {
                await store.DeleteAsync(reference).ConfigureAwait(false);
                proxy = proxy with { SecretReference = null };
            }
            else
            {
                password = options.ControlTokenFile is not null
                    ? await File.ReadAllBytesAsync(Path.GetFullPath(options.ControlTokenFile)).ConfigureAwait(false)
                    : ReadSecretFromTerminal("Proxy password: ");
                if (password.Length is < 1 or > 4096) throw new InvalidDataException("The proxy password length is invalid.");
                await store.SetAsync(reference, password).ConfigureAwait(false);
                proxy = proxy with
                {
                    Username = options.ProxyUsername?.Trim() ?? proxy.Username,
                    SecretReference = reference
                };
            }
            int index = profiles.ProxyProfiles.FindIndex(item => item.Id == proxy.Id);
            profiles.ProxyProfiles[index] = proxy;
            repository.Save(profiles);
            WriteOutput(clear
                ? $"Cleared protected credentials for proxy '{proxy.DisplayName}'."
                : $"Stored protected credentials for proxy '{proxy.DisplayName}' without displaying the password.");
            return OeXYZExitCode.Success;
        }
        finally
        {
            if (master is not null) CryptographicOperations.ZeroMemory(master);
            if (password is not null) CryptographicOperations.ZeroMemory(password);
        }
    }

    private static ProxyProfile FindProxy(ProfileDocument profiles, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("A proxy profile name is required.");
        return profiles.ProxyProfiles.SingleOrDefault(proxy =>
                   string.Equals(proxy.DisplayName, target, StringComparison.OrdinalIgnoreCase))
               ?? throw new FileNotFoundException($"Proxy profile '{target}' was not found.");
    }

    private static async Task<IConnectionDialer> CreateConnectionDialerAsync(
        ProfileDocument profiles,
        ServerProfile server,
        ApplicationPaths paths,
        AccountSecretProvider? secretProvider,
        CancellationToken cancellationToken)
    {
        if (server.ProxyProfileId is not Guid proxyId) return DirectConnectionDialer.Instance;
        ProxyProfile proxy = profiles.ProxyProfiles.Single(item => item.Id == proxyId);
        if (proxy.Kind == ProxyKind.Direct) return DirectConnectionDialer.Instance;
        byte[]? masterSecret = null;
        byte[]? proxyPassword = null;
        try
        {
            if (proxy.SecretReference is not null)
            {
                if (!OperatingSystem.IsWindows())
                {
                    if (secretProvider is null)
                        throw new InvalidOperationException("Linux proxy credentials require an account/secret key file.");
                    masterSecret = await secretProvider(WriteError, cancellationToken).ConfigureAwait(false);
                }
                using LocalSecretStore store = new(paths.Secrets, masterSecret ?? []);
                proxyPassword = await store.GetAsync(proxy.SecretReference, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"Proxy profile '{proxy.DisplayName}' has no stored credentials.");
            }
            return new ProxyConnectionDialer(proxy, proxyPassword ?? []);
        }
        finally
        {
            if (masterSecret is not null) CryptographicOperations.ZeroMemory(masterSecret);
            if (proxyPassword is not null) CryptographicOperations.ZeroMemory(proxyPassword);
        }
    }

    private static async Task MaintainLogRetentionAsync(
        ApplicationPaths paths,
        ProfileDocument profiles,
        IReadOnlyList<ConsoleSession> sessions,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            ApplyLogRetention(paths, profiles, sessions);
    }

    private static void ApplyLogRetention(
        ApplicationPaths paths,
        ProfileDocument profiles,
        IReadOnlyList<ConsoleSession> sessions)
    {
        try
        {
            LogRetentionService.Apply(
                paths.Logs,
                profiles.Settings.LogRetentionDays,
                maximumBytes: LogRetentionService.DefaultMaximumBytes,
                protectedPaths: sessions.Select(session => session.LogPath));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<bool> ReadInputAsync(
        IReadOnlyList<ConsoleSession> sessions,
        ConcurrentDictionary<ConsoleSession, byte> userStoppedSessions,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return true;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (await HandleInputAsync(sessions, userStoppedSessions, line, null, cancellationToken).ConfigureAwait(false))
                return true;
        }
        return true;
    }

    private static async Task<bool> ReadDashboardInputAsync(
        IReadOnlyList<ConsoleSession> sessions,
        ConcurrentDictionary<ConsoleSession, byte> userStoppedSessions,
        TerminalDashboard dashboard,
        CancellationToken cancellationToken)
    {
        StringBuilder input = new();
        CommandHistory history = new(200);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    string line = input.ToString();
                    input.Clear();
                    dashboard.SetInput(string.Empty);
                    if (string.IsNullOrWhiteSpace(line)) break;
                    history.Add(line);
                    if (await HandleInputAsync(
                            sessions,
                            userStoppedSessions,
                            line,
                            dashboard,
                            cancellationToken).ConfigureAwait(false))
                        return true;
                    break;
                }
                case ConsoleKey.Backspace:
                    if (input.Length > 0) input.Length--;
                    dashboard.SetInput(input.ToString());
                    break;
                case ConsoleKey.UpArrow:
                    input.Clear().Append(history.Previous());
                    dashboard.SetInput(input.ToString());
                    break;
                case ConsoleKey.DownArrow:
                    input.Clear().Append(history.Next());
                    dashboard.SetInput(input.ToString());
                    break;
                case ConsoleKey.Escape:
                    input.Clear();
                    dashboard.SetInput(string.Empty);
                    break;
                default:
                    if (!char.IsControl(key.KeyChar) && input.Length < 256)
                    {
                        input.Append(key.KeyChar);
                        dashboard.SetInput(input.ToString());
                    }
                    break;
            }
        }
        return false;
    }

    private static async Task<bool> HandleInputAsync(
        IReadOnlyList<ConsoleSession> sessions,
        ConcurrentDictionary<ConsoleSession, byte> userStoppedSessions,
        string line,
        TerminalDashboard? dashboard,
        CancellationToken cancellationToken)
    {
        LocalSessionCommand localCommand = SessionInput.Classify(line);
        if (localCommand == LocalSessionCommand.Quit) return true;
        ConsoleSession? target = sessions.FirstOrDefault(session => session.IsConnected);
        if (target is null)
        {
            const string message = "No session is connected yet.";
            if (dashboard is null) await ErrorAsync(message).ConfigureAwait(false);
            else dashboard.AddEvent(message);
            return false;
        }
        try
        {
            if (localCommand == LocalSessionCommand.Respawn)
                await target.RespawnAsync(cancellationToken).ConfigureAwait(false);
            else if (localCommand == LocalSessionCommand.Disconnect)
            {
                userStoppedSessions.TryAdd(target, 0);
                target.Stop();
            }
            else
                await target.SendAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            string message = SensitiveDataRedactor.RedactText(exception.Message);
            if (dashboard is null) await ErrorAsync(message).ConfigureAwait(false);
            else dashboard.AddEvent(message);
        }
        return false;
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<bool> WaitForCancellationResultAsync(CancellationToken cancellationToken)
    {
        await WaitForCancellationAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    private static AccountSecretProvider CreateAccountSecretProvider(
        string accountStorePath,
        string? keyFile)
    {
        return async (status, cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(keyFile))
                return await ReadAccountKeyFileAsync(keyFile, cancellationToken).ConfigureAwait(false);
            if (Console.IsInputRedirected)
                throw new InvalidOperationException(
                    "A Microsoft account needs an encrypted account-store passphrase. " +
                    "Provide --account-key-file <path> when stdin is not interactive.");

            bool existing = File.Exists(accountStorePath);
            status(existing
                ? "Unlock the encrypted Linux account store. The passphrase is not saved or echoed."
                : "Create a passphrase for the encrypted Linux account store. It is not saved or echoed.");
            byte[] first = ReadSecretFromTerminal("Account-store passphrase: ");
            try
            {
                if (!existing)
                {
                    byte[] confirmation = ReadSecretFromTerminal("Confirm passphrase: ");
                    try
                    {
                        if (!CryptographicOperations.FixedTimeEquals(first, confirmation))
                            throw new InvalidOperationException("The account-store passphrases did not match.");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(confirmation);
                    }
                }
                if (first.Length < 12)
                    throw new InvalidOperationException("The account-store passphrase must contain at least 12 UTF-8 bytes.");
                byte[] result = first.ToArray();
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(first);
            }
        };
    }

    private static void PresentDeviceCode(
        MicrosoftDeviceCodePrompt? prompt,
        TerminalDashboard? dashboard)
    {
        if (dashboard is not null)
        {
            dashboard.SetDeviceCodePrompt(prompt);
            return;
        }
        if (prompt is null) return;
        WriteUnredactedError(string.Empty);
        WriteUnredactedError("Microsoft device sign-in (temporary code; not written to OeXYZ logs)");
        WriteUnredactedError($"Open: {prompt.VerificationUrl}");
        WriteUnredactedError($"Code: {prompt.UserCode}");
        WriteUnredactedError($"Expires: {prompt.ExpiresOn.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        WriteUnredactedError(string.Empty);
    }

    private static async Task<byte[]> ReadAccountKeyFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        FileInfo info = new(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The account-key file was not found.", fullPath);
        if (info.Length is < 12 or > 4096)
            throw new InvalidDataException("The account-key file must contain between 12 and 4096 bytes.");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = File.GetUnixFileMode(fullPath);
            UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                   UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & exposed) != 0)
                throw new UnauthorizedAccessException(
                    "The account-key file is accessible to other users. Run: chmod 600 " + fullPath);
        }

        byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        int length = bytes.Length;
        while (length > 0 && bytes[length - 1] is (byte)'\r' or (byte)'\n') length--;
        if (length < 12)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidDataException("The account-key file must contain at least 12 bytes excluding line endings.");
        }
        if (length == bytes.Length) return bytes;
        byte[] trimmed = bytes.AsSpan(0, length).ToArray();
        CryptographicOperations.ZeroMemory(bytes);
        return trimmed;
    }

    private static byte[] ReadSecretFromTerminal(string prompt)
    {
        WriteUnredactedErrorInline(prompt);
        char[] characters = new char[1024];
        int length = 0;
        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Passphrase entry was cancelled.");
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0) characters[--length] = '\0';
                    continue;
                }
                if (!char.IsControl(key.KeyChar) && length < characters.Length) characters[length++] = key.KeyChar;
            }
            WriteUnredactedError(string.Empty);
            ReadOnlySpan<char> value = characters.AsSpan(0, length);
            byte[] encoded = new byte[Encoding.UTF8.GetByteCount(value)];
            _ = Encoding.UTF8.GetBytes(value, encoded);
            return encoded;
        }
        finally
        {
            Array.Clear(characters);
        }
    }

    private static ServerProfile FindServer(ProfileDocument profiles, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new FileNotFoundException("A server profile name is required.");
        ServerProfile? server = profiles.Servers.FirstOrDefault(item =>
            string.Equals(item.DisplayName, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Address, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Id.ToString(), target, StringComparison.OrdinalIgnoreCase));
        return server ?? throw new FileNotFoundException($"Server profile '{target}' was not found.");
    }

    private static AccountProfile FindAccount(ProfileDocument profiles, string? target)
    {
        if (profiles.Accounts.Count == 0) throw new FileNotFoundException("No account profile was found.");
        if (string.IsNullOrWhiteSpace(target)) target = ReadNonSecretEnvironmentOption("OEXYZ_ACCOUNT");
        if (string.IsNullOrWhiteSpace(target))
        {
            if (profiles.Accounts.Count == 1) return profiles.Accounts[0];
            throw new FileNotFoundException("Multiple accounts exist; choose one with --account <name>.");
        }
        AccountProfile? account = profiles.Accounts.FirstOrDefault(item =>
            string.Equals(item.DisplayName, target, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.Id.ToString(), target, StringComparison.OrdinalIgnoreCase));
        return account ?? throw new FileNotFoundException($"Account profile '{target}' was not found.");
    }

    private static string? ReadNonSecretEnvironmentOption(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 64 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InvalidDataException($"{name} is invalid or exceeds 64 characters.");
        return value;
    }

    private static OeXYZExitCode InstallPath(bool install)
    {
        if (OperatingSystem.IsWindows())
        {
            string directory = AppContext.BaseDirectory;
            string current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
            string updated = PathRegistration.Update(current, directory, install);
            Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
            WriteOutput(install
                ? $"Added {directory} to your user PATH. Open a new terminal and run: oexyz --help"
                : $"Removed {directory} from your user PATH. Open a new terminal to apply the change.");
            return OeXYZExitCode.Success;
        }

        string home = UserDirectories.GetHomeDirectory();
        string executable = Environment.ProcessPath
                            ?? throw new InvalidOperationException("The current OeXYZ executable path is unavailable.");
        string binDirectory = PathRegistration.GetUnixUserBin(home);
        bool changed = PathRegistration.UpdateUnixLink(executable, binDirectory, install);
        if (install)
        {
            WriteOutput(changed
                ? $"Installed symbolic link: {Path.Combine(binDirectory, "oexyz")} -> {executable}"
                : "The OeXYZ symbolic link is already installed.");
            string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            bool visible = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(Path.GetFullPath)
                .Contains(binDirectory, StringComparer.Ordinal);
            if (!visible)
                WriteOutput($"Add {binDirectory} to PATH in your shell profile, then open a new terminal.");
        }
        else
        {
            WriteOutput(changed ? "Removed the OeXYZ symbolic link." : "No OeXYZ symbolic link was installed.");
        }
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode UnknownCommand(string command)
    {
        WriteError($"Unknown command: {command}");
        PrintHelp();
        return OeXYZExitCode.InvalidArguments;
    }

    internal static OeXYZExitCode AggregateSessionExitCode(
        IEnumerable<(Exception? Failure, bool UserStopped)> sessions,
        bool allSessionsStoppedByUser)
    {
        if (allSessionsStoppedByUser) return OeXYZExitCode.Success;
        Exception[] failures = sessions
            .Where(session => !session.UserStopped && session.Failure is not null)
            .Select(session => session.Failure!)
            .ToArray();
        return failures.Length == 0
            ? OeXYZExitCode.Success
            : (OeXYZExitCode)failures.Select(failure => (int)MapFailure(failure)).Max();
    }

    internal static OeXYZExitCode MapFailure(Exception exception)
    {
        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        if (source is UnauthorizedAccessException) return OeXYZExitCode.InternalError;
        if (source.GetType().Namespace?.StartsWith("Microsoft.Identity.Client", StringComparison.Ordinal) == true ||
            source.GetType().Name.Contains("Auth", StringComparison.OrdinalIgnoreCase))
            return OeXYZExitCode.AuthenticationError;
        DisconnectDecision decision = DisconnectClassifier.Classify(source);
        if (decision.Category == DisconnectCategory.Permanent)
        {
            string message = source.Message.ToLowerInvariant();
            if (message.Contains("auth", StringComparison.Ordinal) || message.Contains("session", StringComparison.Ordinal) ||
                message.Contains("microsoft", StringComparison.Ordinal)) return OeXYZExitCode.AuthenticationError;
            if (message.Contains("protocol", StringComparison.Ordinal) || message.Contains("unsupported", StringComparison.Ordinal))
                return OeXYZExitCode.ProtocolUnsupported;
            return OeXYZExitCode.PermanentServerRejection;
        }
        if (source is NotSupportedException) return OeXYZExitCode.ProtocolUnsupported;
        if (source is SocketException or IOException or TimeoutException) return OeXYZExitCode.ConnectionFailure;
        return OeXYZExitCode.InternalError;
    }

    private static void WriteLine(CliLog log, string prefix, SessionLine line, TerminalDashboard? dashboard)
    {
        string safePrefix = TerminalTextSanitizer.Sanitize(prefix);
        string safeLine = SensitiveDataRedactor.RedactText(TerminalTextSanitizer.Sanitize(line.Text));
        string text = $"[{line.Timestamp:HH:mm:ss}] {safePrefix}{safeLine}";
        log.Write(line.Kind.ToString(), text);
        if (dashboard is not null) dashboard.AddEvent(text);
        else if (line.Kind == SessionLineKind.Error) WriteError(text);
        else WriteOutput(text);
    }

    private static void WriteTrace(CliLog log, string prefix, PacketTrace trace, TerminalDashboard? dashboard)
    {
        string arrow = trace.Direction == PacketDirection.Clientbound ? "<-" : "->";
        string safePrefix = TerminalTextSanitizer.Sanitize(prefix);
        string safeName = TerminalTextSanitizer.Sanitize(trace.Name);
        string text = $"[{trace.Timestamp:HH:mm:ss.fff}] {safePrefix}{arrow} {safeName} 0x{trace.PacketId:X2} {trace.PayloadBytes} bytes";
        log.Write("trace", text);
        if (dashboard is not null) dashboard.AddEvent(text);
        else WriteOutput(text);
    }

    private static void WriteOutput(string text) =>
        Console.WriteLine(SensitiveDataRedactor.RedactText(TerminalTextSanitizer.Sanitize(text)));

    private static void WriteError(string text) =>
        Console.Error.WriteLine(SensitiveDataRedactor.RedactText(TerminalTextSanitizer.Sanitize(text)));

    private static void WriteUnredactedError(string text) =>
        Console.Error.WriteLine(TerminalTextSanitizer.Sanitize(text));

    private static void WriteUnredactedErrorInline(string text) =>
        Console.Error.Write(TerminalTextSanitizer.Sanitize(text));

    private static void WriteOutputBlock(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            WriteOutput(line);
    }

    private static async Task ErrorAsync(string text)
    {
        text = SensitiveDataRedactor.RedactText(TerminalTextSanitizer.Sanitize(text));
        await OutputLock.WaitAsync().ConfigureAwait(false);
        try { await Console.Error.WriteLineAsync(text).ConfigureAwait(false); }
        finally { OutputLock.Release(); }
    }

    private static void PrintHelp()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        WriteOutputBlock($$"""
            OeXYZ headless Minecraft Java client {{version}}

            Usage:
              oexyz list
              oexyz profiles
              oexyz setup
              oexyz status <profile>
              oexyz doctor [profile] [--json]
              oexyz run <profile> [--account <name>]
              oexyz connect <profile> [--account <name>]
              oexyz run-address <host[:port]> [--account <name>]
              oexyz connect-all [--account <name>]
              oexyz connect-group <group> [--account <name>]
              oexyz supervise [group] [--no-input] [--health-port 8765]
              oexyz healthcheck [http://127.0.0.1:8765/health]
              oexyz export-profiles <portable.json>
              oexyz import-profiles <portable.json>
              oexyz profiles-recover [--json]
              oexyz account-add-offline <player-name>
              oexyz account-add-microsoft <profile-name> [--login-hint <email>]
              oexyz account-login <profile-name> [--account-key-file <path>]
              oexyz account-key-generate <path>
              oexyz control-token-create [--file <path>]
              oexyz control-token-check [--file <path>]
              oexyz proxy-add <name> --proxy-kind <kind> --address <host> --port <port> [--proxy-dns]
              oexyz proxy-list [--json]
              oexyz proxy-set-credentials <name> [--proxy-username <name>] [--file <password-file>]
              oexyz proxy-clear-credentials <name>
              oexyz proxy-delete <name>
              oexyz failover-list <server> [--json]
              oexyz failover-add <server> --address <host[:port]>
              oexyz failover-delete <server> --address <host[:port]>
              oexyz automation-list <server> [--json]
              oexyz automation-validate <server> [--json]
              oexyz server-add <profile-name> --address <host[:port]>
              oexyz install-path | uninstall-path

            Options:
              --config <profiles.json>    Use an explicit profile file
              --address <host[:port]>     Address for server-add
              --port <1-65535>            Optional custom port for server-add
              --minecraft-version <name>  auto or a supported Minecraft version
              --group <name>              Optional session group for server-add
              --login-hint <email>        Optional Microsoft account hint
              --log-file <path>           Also write sanitized CLI output to a file
              --log-level <level>         trace, debug, information, warning, error
              --inspect-packets           Show safe packet metadata (no payload dumps)
              --account-key-file <path>   Unlock encrypted Microsoft sessions on Linux
              --health-port <port>        Loopback health/status endpoint for services
              --control-token-file <path> Private token file for /v1 management actions
              --allow-remote-control      Explicitly bind management beyond loopback (token required)
              --proxy-kind <kind>         direct, socks5, or http-connect
              --proxy <name>              Assign a proxy profile when adding a server
              --proxy-dns                 Let the proxy resolve destination hostnames
              --proxy-username <name>     Proxy username (password remains in the secret store)
              --dashboard                 Interactive terminal session dashboard
              --no-input                  Service mode; do not read stdin
              --max-sessions <1-128>      Resource safety limit (default: 16)
              --json                      Machine-readable output where supported
              --help                      Show this help

            While connected, type chat/commands on stdin. Use /quit or Ctrl+C to stop.
            """);
    }
}

internal sealed class CliLog : IAsyncDisposable
{
    private const long DefaultMaximumBytes = 32L * 1024L * 1024L;
    private StreamWriter? writer;
    private readonly string? path;
    private readonly int threshold;
    private readonly Action<string>? reportFailure;
    private readonly long maximumBytes;
    private readonly object gate = new();
    private Exception? failureException;

    public CliLog(
        string? path,
        string level,
        Action<string>? reportFailure = null,
        long maximumBytes = DefaultMaximumBytes)
    {
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        threshold = Level(level);
        this.reportFailure = reportFailure;
        this.maximumBytes = maximumBytes;
        if (string.IsNullOrWhiteSpace(path)) return;
        this.path = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(this.path);
        if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
        writer = Open(this.path);
    }

    public Exception? FailureException => Volatile.Read(ref failureException);

    public void Write(string level, string text)
    {
        if (Level(level) < threshold) return;
        string sanitized = SensitiveDataRedactor.RedactText(TerminalTextSanitizer.Sanitize(text));
        lock (gate)
        {
            if (writer is null || path is null) return;
            try
            {
                if (writer.BaseStream.Length > 0 &&
                    writer.BaseStream.Length + Encoding.UTF8.GetByteCount(sanitized) + 2L > maximumBytes)
                {
                    writer.Dispose();
                    string previous = path + ".previous.log";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                    PrivateFileSystem.ProtectFile(previous);
                    writer = Open(path);
                }
                writer.WriteLine(sanitized);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writer?.Dispose();
                writer = null;
                if (Interlocked.CompareExchange(ref failureException, exception, null) is null)
                {
                    reportFailure?.Invoke(
                        "CLI file logging stopped: " + SensitiveDataRedactor.RedactText(exception.Message));
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            writer?.Dispose();
            writer = null;
        }
        return ValueTask.CompletedTask;
    }

    private static StreamWriter Open(string path)
    {
        StreamWriter result = new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.SequentialScan), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        { AutoFlush = true };
        PrivateFileSystem.ProtectFile(path);
        return result;
    }

    private static int Level(string value) => value.ToLowerInvariant() switch
    {
        "trace" => 0,
        "debug" => 1,
        "information" or "success" or "chat" => 2,
        "warning" => 3,
        "error" => 4,
        _ => 2
    };
}
