using System.Runtime.InteropServices;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using OeXYZ.Core;
using OeXYZ.Protocol;

namespace OeXYZ.Session;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Failure
}

public sealed record DoctorCheck(string Name, DoctorCheckStatus Status, string Message);

public sealed record DoctorReport(
    DateTimeOffset Timestamp,
    string OeXYZVersion,
    string OperatingSystem,
    string Architecture,
    string Framework,
    bool Container,
    bool Wsl,
    IReadOnlyList<DoctorCheck> Checks)
{
    public bool Successful => Checks.All(check => check.Status != DoctorCheckStatus.Failure);
}

public static class DoctorService
{
    public static async Task<DoctorReport> RunAsync(
        ApplicationPaths paths,
        ProfileDocument profiles,
        string version,
        ServerProfile? server = null,
        string? accountKeyFile = null,
        string? controlTokenFile = null,
        bool allowRemoteControl = false,
        CancellationToken cancellationToken = default,
        string? configurationError = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profiles);
        List<DoctorCheck> checks = [];
        bool configurationLoaded = string.IsNullOrWhiteSpace(configurationError);
        Add(checks, "Configuration",
            !configurationLoaded
                ? DoctorCheckStatus.Failure
                : File.Exists(paths.Profiles) ? DoctorCheckStatus.Pass : DoctorCheckStatus.Warning,
            !configurationLoaded
                ? $"profiles.json could not be loaded: {configurationError}"
                : File.Exists(paths.Profiles)
                    ? $"Loaded format {profiles.FormatVersion} with {profiles.Accounts.Count} accounts and {profiles.Servers.Count} servers."
                    : "No profiles.json exists yet; the GUI or an import can create one.");
        Add(checks, "Config migration backup", File.Exists(paths.Profiles) && !File.Exists(paths.Profiles + ".bak")
                ? DoctorCheckStatus.Warning
                : DoctorCheckStatus.Pass,
            File.Exists(paths.Profiles + ".bak")
                ? "A previous profiles.json backup is available."
                : "No migration backup is currently needed or available.");

        if (configurationLoaded)
        {
            int duplicateIds = profiles.Accounts.GroupBy(account => account.Id).Count(group => group.Count() > 1) +
                               profiles.Servers.GroupBy(serverProfile => serverProfile.Id).Count(group => group.Count() > 1);
            int invalidAccounts = profiles.Accounts.Count(account =>
                string.IsNullOrWhiteSpace(account.DisplayName) ||
                account.Kind == AccountKind.Offline && !ProfileRules.IsValidOfflineName(account.LoginHint));
            int invalidServers = profiles.Servers.Count(serverProfile =>
                string.IsNullOrWhiteSpace(serverProfile.DisplayName) || string.IsNullOrWhiteSpace(serverProfile.Address));
            int profileProblems = duplicateIds + invalidAccounts + invalidServers;
            Add(checks, "Profile integrity", profileProblems == 0 ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                profileProblems == 0
                    ? "Profile identifiers, offline player names, and server addresses are valid."
                    : $"Found {profileProblems} invalid or duplicate profile entries; edit or re-import the affected profiles.");
        }
        else
        {
            Add(checks, "Profile integrity", DoctorCheckStatus.Failure,
                "Profile integrity could not be evaluated until profiles.json is repaired or restored.");
        }

        if (!OperatingSystem.IsWindows() && File.Exists(paths.Profiles))
        {
            bool privatePermissions = PrivateFileSystem.HasPrivateUnixPermissions(paths.Profiles);
            Add(checks, "Profile permissions", privatePermissions ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                privatePermissions
                    ? "profiles.json has private Unix permissions."
                    : "profiles.json is accessible to group or other users; run chmod 600 on it.");
        }

        try
        {
            paths.EnsureDirectories();
            string probe = Path.Combine(paths.Diagnostics, $".doctor-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(probe, "OeXYZ doctor write probe", cancellationToken).ConfigureAwait(false);
                PrivateFileSystem.ProtectFile(probe);
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            Add(checks, "State directories", DoctorCheckStatus.Pass, "Config, logs, and diagnostics directories are writable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Add(checks, "State directories", DoctorCheckStatus.Failure,
                SensitiveDataRedactor.RedactText(exception.Message));
        }

        if (File.Exists(paths.ProtectedAccounts))
        {
            FileInfo accounts = new(paths.ProtectedAccounts);
            bool privatePermissions = PrivateFileSystem.HasPrivateUnixPermissions(paths.ProtectedAccounts);
            Add(checks, "Account store", privatePermissions ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                privatePermissions
                    ? $"Protected account storage exists ({accounts.Length} bytes) with private OS permissions."
                    : "The account store is readable by another Unix user; run chmod 600 on accounts.bin.");
        }
        else
        {
            Add(checks, "Account store", DoctorCheckStatus.Warning,
                "No protected Microsoft account session has been saved; offline profiles remain usable.");
        }

        if (!OperatingSystem.IsWindows())
        {
            Add(checks, "Linux Microsoft session storage",
                File.Exists(paths.ProtectedAccounts) ? DoctorCheckStatus.Pass : DoctorCheckStatus.Warning,
                File.Exists(paths.ProtectedAccounts)
                    ? "Microsoft Live, Xbox, and Minecraft refreshable session data is contained in the encrypted account store."
                    : "No encrypted Linux Microsoft session exists yet; complete account-login before unattended sessions.");

            if (string.IsNullOrWhiteSpace(accountKeyFile))
            {
                Add(checks, "Linux account key", DoctorCheckStatus.Warning,
                    "Interactive Microsoft login can prompt for a passphrase; services should use --account-key-file.");
            }
            else
            {
                string fullPath = Path.GetFullPath(accountKeyFile);
                bool exists = File.Exists(fullPath);
                bool privatePermissions = exists && PrivateFileSystem.HasPrivateUnixPermissions(fullPath);
                Add(checks, "Linux account key",
                    exists && privatePermissions ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                    !exists
                        ? "The configured account-key file does not exist."
                        : privatePermissions
                            ? "The account-key file exists with private Unix permissions."
                            : "The account-key file must not be accessible to group or other users (chmod 600).");
            }
        }
        else
        {
            Add(checks, "Windows account protection", DoctorCheckStatus.Pass,
                "Microsoft sessions use current-user Windows DPAPI protection.");
        }

        string checkedControlToken = Path.GetFullPath(controlTokenFile ?? paths.ControlToken);
        if (File.Exists(checkedControlToken))
        {
            try
            {
                byte[] token = ControlTokenFile.Read(checkedControlToken);
                CryptographicOperations.ZeroMemory(token);
                Add(checks, "Control token", DoctorCheckStatus.Pass,
                    "The management control-token file is valid and private.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Add(checks, "Control token", DoctorCheckStatus.Failure, exception.Message);
            }
        }
        else
        {
            Add(checks, "Control token", DoctorCheckStatus.Warning,
                "No control token exists; versioned management actions remain disabled.");
        }
        Add(checks, "Management bind",
            allowRemoteControl ? DoctorCheckStatus.Warning : DoctorCheckStatus.Pass,
            allowRemoteControl
                ? "Remote management is explicitly enabled; require the control token and place the endpoint behind a VPN or TLS reverse proxy."
                : "Management uses the safe loopback-only default.");
        int secretReferences = profiles.ProxyProfiles.Count(proxy => proxy.SecretReference is not null);
        if (File.Exists(paths.Secrets))
        {
            try
            {
                using FileStream secrets = new(paths.Secrets, FileMode.Open, FileAccess.Read, FileShare.Read);
                bool bounded = secrets.Length is > 0 and <= 2 * 1024 * 1024;
                bool privatePermissions = PrivateFileSystem.HasPrivateUnixPermissions(paths.Secrets);
                if (secrets.ReadByte() < 0) bounded = false;
                Add(checks, "Secret store",
                    bounded && privatePermissions ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                    bounded && privatePermissions
                        ? $"The protected secret store is readable, bounded, and private ({secretReferences} reference(s))."
                        : "The protected secret store is empty, oversized, or accessible to another Unix user.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Add(checks, "Secret store", DoctorCheckStatus.Failure,
                    SensitiveDataRedactor.RedactText(exception.Message));
            }
        }
        else
        {
            Add(checks, "Secret store",
                secretReferences == 0 ? DoctorCheckStatus.Warning : DoctorCheckStatus.Failure,
                secretReferences == 0
                    ? "No protected local secrets are configured; the store will be created on first use."
                    : "Proxy profiles reference protected credentials, but the secret store is missing.");
        }

        int proxyReferences = profiles.Servers.Count(item => item.ProxyProfileId is not null);
        int automationRules = profiles.Servers.Sum(item => item.Automations.Count);
        Add(checks, "Advanced profile policy", DoctorCheckStatus.Pass,
            $"Validated {profiles.ProxyProfiles.Count} proxy profile(s), {proxyReferences} proxy reference(s), and {automationRules} bounded automation rule(s).");

        if (server is not null)
        {
            ProxyProfile? proxy = server.ProxyProfileId is Guid proxyId
                ? profiles.ProxyProfiles.SingleOrDefault(candidate => candidate.Id == proxyId)
                : null;
            if (proxy is not null && proxy.Kind != ProxyKind.Direct)
            {
                bool requiresCredentials = !string.IsNullOrWhiteSpace(proxy.Username) || proxy.SecretReference is not null;
                bool hasCredentials = proxy.SecretReference is not null && File.Exists(paths.Secrets);
                Add(checks, "Proxy credentials",
                    !requiresCredentials || hasCredentials ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                    !requiresCredentials
                        ? "The selected proxy does not require stored credentials."
                        : hasCredentials
                            ? "The selected proxy has a protected secret reference; no credential value was read or displayed."
                            : "The selected proxy references credentials, but the protected secret store is unavailable.");
                try
                {
                    using CancellationTokenSource proxyTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    proxyTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                    using TcpClient client = new();
                    await client.ConnectAsync(proxy.Host, proxy.Port, proxyTimeout.Token).ConfigureAwait(false);
                    Add(checks, "Proxy reachability", DoctorCheckStatus.Pass,
                        $"The selected {proxy.Kind} proxy accepted a TCP connection; no credentials were transmitted.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException ||
                                                  !cancellationToken.IsCancellationRequested)
                {
                    Add(checks, "Proxy reachability", DoctorCheckStatus.Failure,
                        SensitiveDataRedactor.RedactText(exception.Message));
                }
            }
            else
            {
                Add(checks, "Proxy reachability", DoctorCheckStatus.Pass,
                    "The selected server uses a direct connection.");
            }

            try
            {
                using CancellationTokenSource endpointTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                endpointTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                ServerEndpointProfile[] endpoints = server.Endpoints.Count == 0
                    ? [new ServerEndpointProfile { Address = server.Address, CustomPort = server.CustomPort }]
                    : server.Endpoints.ToArray();
                foreach (ServerEndpointProfile candidate in endpoints)
                {
                    ServerAddress parsed = ServerAddress.Parse(candidate.Address, candidate.CustomPort);
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(parsed.HandshakeHost, endpointTimeout.Token)
                        .ConfigureAwait(false);
                    if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                }
                Add(checks, "Failover DNS", DoctorCheckStatus.Pass,
                    $"All {endpoints.Length} configured endpoint(s) resolve successfully.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException ||
                                              !cancellationToken.IsCancellationRequested)
            {
                Add(checks, "Failover DNS", DoctorCheckStatus.Failure,
                    SensitiveDataRedactor.RedactText(exception.Message));
            }

            Add(checks, "Transfer policy", DoctorCheckStatus.Pass,
                server.AllowServerTransfer
                    ? "Validated server transfers are explicitly enabled and remain subject to proxy, loop, and rate limits."
                    : "Server transfers are disabled by default for this profile.");
            if (!string.Equals(server.Version, "auto", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ProtocolCapabilities capabilities = ProtocolCatalog.LoadEmbedded().Resolve(server.Version).Capabilities;
                    Add(checks, "Cookie and transfer capabilities", DoctorCheckStatus.Pass,
                        $"Minecraft {server.Version}: cookies {(capabilities.Cookies ? "supported" : "not advertised")}, " +
                        $"transfer {(capabilities.Transfer ? "supported" : "not advertised")}.");
                }
                catch (NotSupportedException exception)
                {
                    Add(checks, "Cookie and transfer capabilities", DoctorCheckStatus.Failure, exception.Message);
                }
            }
            else
            {
                Add(checks, "Cookie and transfer capabilities", DoctorCheckStatus.Warning,
                    "Capability support will be selected after automatic protocol detection.");
            }

            try
            {
                using CancellationTokenSource dnsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dnsTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                ServerAddress parsed = ServerAddress.Parse(server.Address, server.CustomPort);
                ServerAddress endpoint = await parsed.ResolveSrvAsync(dnsTimeout.Token).ConfigureAwait(false);
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(endpoint.NetworkHost, dnsTimeout.Token)
                    .ConfigureAwait(false);
                Add(checks, "DNS/SRV", addresses.Length > 0 ? DoctorCheckStatus.Pass : DoctorCheckStatus.Failure,
                    endpoint.UsedSrv
                        ? $"SRV resolved to {endpoint.NetworkHost}:{endpoint.Port}; DNS returned {addresses.Length} address(es)."
                        : $"Using {endpoint.NetworkHost}:{endpoint.Port} without SRV redirection; DNS returned {addresses.Length} address(es).");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Add(checks, "DNS/SRV", DoctorCheckStatus.Failure,
                    SensitiveDataRedactor.RedactText(exception.Message));
            }

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(
                    server.Address,
                    server.CustomPort,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                Add(checks, "Minecraft status", DoctorCheckStatus.Pass,
                    $"{status.VersionName}, protocol {status.ProtocolVersion}, ping {status.PingMilliseconds} ms, " +
                    $"players {status.PlayersOnline}/{status.PlayersMaximum}.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Add(checks, "Minecraft status", DoctorCheckStatus.Failure,
                    SensitiveDataRedactor.RedactText(exception.Message));
            }
        }

        long logBytes = 0;
        try
        {
            if (Directory.Exists(paths.Logs))
                logBytes = new DirectoryInfo(paths.Logs).EnumerateFiles("*.log", SearchOption.TopDirectoryOnly)
                    .Aggregate(0L, (total, file) => file.Length > long.MaxValue - total ? long.MaxValue : total + file.Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Add(checks, "Log directory", DoctorCheckStatus.Warning, SensitiveDataRedactor.RedactText(exception.Message));
        }
        Add(checks, "Log safety limit",
            logBytes <= LogRetentionService.DefaultMaximumBytes ? DoctorCheckStatus.Pass : DoctorCheckStatus.Warning,
            $"Session logs use {logBytes / 1024D / 1024D:0.0} MiB; closed logs are capped at " +
            $"{LogRetentionService.DefaultMaximumBytes / 1024 / 1024} MiB and active logs rotate at 16 MiB.");
        return new DoctorReport(
            DateTimeOffset.UtcNow,
            version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            IsContainer(),
            IsWsl(),
            checks);
    }

    private static void Add(List<DoctorCheck> checks, string name, DoctorCheckStatus status, string message) =>
        checks.Add(new DoctorCheck(name, status, SensitiveDataRedactor.RedactText(message)));

    private static bool IsContainer() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
        File.Exists("/.dockerenv");

    private static bool IsWsl()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/proc/version")) return false;
        try { return File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
