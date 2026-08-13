using System.Net.Sockets;
using System.Reflection;
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

            ApplicationPaths paths = ApplicationPaths.Resolve(options.ConfigPath);
            ProfileRepository repository = new(paths.Profiles);
            ProfileDocument profiles = repository.Load();
            return options.Command switch
            {
                "list" or "profiles" => PrintProfiles(profiles, paths),
                "status" => await ShowStatusAsync(profiles, options).ConfigureAwait(false),
                "connect" or "run" => await RunOneAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "run-address" => await RunAddressAsync(profiles, repository, paths, options).ConfigureAwait(false),
                "connect-all" => await RunManyAsync(profiles, repository, paths, options, null).ConfigureAwait(false),
                "connect-group" => await RunManyAsync(profiles, repository, paths, options, options.Target).ConfigureAwait(false),
                _ => UnknownCommand(options.Command)
            };
        }
        catch (FileNotFoundException exception)
        {
            await ErrorAsync(exception.Message).ConfigureAwait(false);
            return OeXYZExitCode.ProfileNotFound;
        }
        catch (InvalidDataException exception)
        {
            await ErrorAsync(exception.Message).ConfigureAwait(false);
            return OeXYZExitCode.InvalidArguments;
        }
        catch (ArgumentException exception)
        {
            await ErrorAsync(exception.Message).ConfigureAwait(false);
            return OeXYZExitCode.InvalidArguments;
        }
        catch (Exception exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
            return MapFailure(exception);
        }
    }

    private static OeXYZExitCode PrintProfiles(ProfileDocument profiles, ApplicationPaths paths)
    {
        Console.WriteLine($"OeXYZ profiles · {paths.Profiles}");
        Console.WriteLine("Accounts:");
        foreach (AccountProfile account in profiles.Accounts)
            Console.WriteLine($"  {account.DisplayName}  [{account.Kind}]");
        Console.WriteLine("Servers:");
        foreach (ServerProfile server in profiles.Servers)
            Console.WriteLine($"  {server.DisplayName}  {server.Address}{(server.CustomPort > 0 ? $":{server.CustomPort}" : string.Empty)}{(server.Group.Length > 0 ? $"  group={server.Group}" : string.Empty)}");
        return OeXYZExitCode.Success;
    }

    private static async Task<OeXYZExitCode> ShowStatusAsync(ProfileDocument profiles, CliArguments options)
    {
        ServerProfile server = FindServer(profiles, options.Target);
        try
        {
            MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(server.Address, server.CustomPort)
                .ConfigureAwait(false);
            Console.WriteLine($"ONLINE | {status.VersionName} | Protocol {status.ProtocolVersion} | " +
                              $"Ping {status.PingMilliseconds} ms | Players {status.PlayersOnline}/{status.PlayersMaximum}");
            Console.WriteLine(status.Description);
            Console.WriteLine($"Endpoint: {status.Address.NetworkHost}:{status.Address.Port}");
            return OeXYZExitCode.Success;
        }
        catch (Exception exception)
        {
            await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false);
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
        ServerAddress parsed = ServerAddress.Parse(options.Target);
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

    private static async Task<OeXYZExitCode> RunManyAsync(
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options,
        string? group)
    {
        if (options.Command == "connect-group" && string.IsNullOrWhiteSpace(group))
            throw new ArgumentException("connect-group requires a group name.");
        AccountProfile account = FindAccount(profiles, options.Account);
        List<ServerProfile> servers = profiles.Servers
            .Where(server => group is null || string.Equals(server.Group, group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (servers.Count == 0) throw new FileNotFoundException(group is null
            ? "No server profiles were found."
            : $"No profiles were found in group '{group}'.");
        return await RunSessionsAsync(servers.Select(server => (account, server)).ToList(),
            profiles, repository, paths, options).ConfigureAwait(false);
    }

    private static async Task<OeXYZExitCode> RunSessionsAsync(
        IReadOnlyList<(AccountProfile Account, ServerProfile Server)> requested,
        ProfileDocument profiles,
        ProfileRepository repository,
        ApplicationPaths paths,
        CliArguments options)
    {
        paths.EnsureDirectories();
        AuthenticationService authentication = new(paths.ProtectedAccounts);
        using CancellationTokenSource lifetime = new();
        int userStopRequested = 0;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Interlocked.Exchange(ref userStopRequested, 1);
            lifetime.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        await using CliLog log = new(options.LogFile, options.LogLevel);
        List<ConsoleSession> sessions = [];
        try
        {
            foreach ((AccountProfile account, ServerProfile server) in requested)
            {
                ConsoleSession session = new(account, server, authentication,
                    () => repository.Save(profiles), paths.Logs, options.InspectPackets);
                string prefix = requested.Count == 1 ? string.Empty : $"[{server.DisplayName}] ";
                session.LineAdded += line => WriteLine(log, prefix, line);
                session.PacketTraced += trace => WriteTrace(log, prefix, trace);
                session.Start();
                sessions.Add(session);
            }

            Task allCompleted = Task.WhenAll(sessions.Select(session => session.Completion));
            Task<bool> input = ReadInputAsync(sessions, lifetime.Token);
            Task cancellation = WaitForCancellationAsync(lifetime.Token);
            Task winner = await Task.WhenAny(allCompleted, input, cancellation).ConfigureAwait(false);
            if (winner == input && await input.ConfigureAwait(false))
                Interlocked.Exchange(ref userStopRequested, 1);
            if (!lifetime.IsCancellationRequested) lifetime.Cancel();
            foreach (ConsoleSession session in sessions) session.Stop();
            try { await input.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await allCompleted.ConfigureAwait(false);

            if (sessions.All(session => session.TerminalException is null) || Volatile.Read(ref userStopRequested) != 0)
                return OeXYZExitCode.Success;
            return (OeXYZExitCode)sessions.Select(session => (int)MapFailure(session.TerminalException!)).Max();
        }
        finally
        {
            foreach (ConsoleSession session in sessions) await session.DisposeAsync().ConfigureAwait(false);
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<bool> ReadInputAsync(IReadOnlyList<ConsoleSession> sessions, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return true;
            if (string.IsNullOrWhiteSpace(line)) continue;
            LocalSessionCommand localCommand = SessionInput.Classify(line);
            if (localCommand == LocalSessionCommand.Quit) return true;
            ConsoleSession? target = sessions.FirstOrDefault(session => session.IsConnected);
            if (target is null)
            {
                await ErrorAsync("No session is connected yet.").ConfigureAwait(false);
                continue;
            }
            try
            {
                if (localCommand == LocalSessionCommand.Respawn)
                    await target.RespawnAsync(cancellationToken).ConfigureAwait(false);
                else if (localCommand == LocalSessionCommand.Disconnect)
                    target.Stop();
                else
                    await target.SendAsync(line, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) { await ErrorAsync(SensitiveDataRedactor.RedactText(exception.Message)).ConfigureAwait(false); }
        }
        return true;
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
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

    private static OeXYZExitCode InstallPath(bool install)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("PATH installation is currently supported on Windows only.");
            return OeXYZExitCode.InvalidArguments;
        }
        string directory = AppContext.BaseDirectory;
        string current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        string updated = PathRegistration.Update(current, directory, install);
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.User);
        Console.WriteLine(install
            ? $"Added {directory} to your user PATH. Open a new terminal and run: oexyz --help"
            : $"Removed {directory} from your user PATH. Open a new terminal to apply the change.");
        return OeXYZExitCode.Success;
    }

    private static OeXYZExitCode UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return OeXYZExitCode.InvalidArguments;
    }

    private static OeXYZExitCode MapFailure(Exception exception)
    {
        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
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

    private static void WriteLine(CliLog log, string prefix, SessionLine line)
    {
        string text = $"[{line.Timestamp:HH:mm:ss}] {prefix}{SensitiveDataRedactor.RedactText(line.Text)}";
        if (line.Kind == SessionLineKind.Error) Console.Error.WriteLine(text);
        else Console.WriteLine(text);
        log.Write(line.Kind.ToString(), text);
    }

    private static void WriteTrace(CliLog log, string prefix, PacketTrace trace)
    {
        string arrow = trace.Direction == PacketDirection.Clientbound ? "<-" : "->";
        string text = $"[{trace.Timestamp:HH:mm:ss.fff}] {prefix}{arrow} {trace.Name} 0x{trace.PacketId:X2} {trace.PayloadBytes} bytes";
        Console.WriteLine(text);
        log.Write("trace", text);
    }

    private static async Task ErrorAsync(string text)
    {
        await OutputLock.WaitAsync().ConfigureAwait(false);
        try { await Console.Error.WriteLineAsync(text).ConfigureAwait(false); }
        finally { OutputLock.Release(); }
    }

    private static void PrintHelp()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "dev";
        Console.WriteLine($$"""
            OeXYZ headless Minecraft Java client {{version}}

            Usage:
              oexyz list
              oexyz profiles
              oexyz status <profile>
              oexyz run <profile> [--account <name>]
              oexyz connect <profile> [--account <name>]
              oexyz run-address <host[:port]> [--account <name>]
              oexyz connect-all [--account <name>]
              oexyz connect-group <group> [--account <name>]
              oexyz install-path | uninstall-path

            Options:
              --config <profiles.json>    Use an explicit profile file
              --log-file <path>           Also write sanitized CLI output to a file
              --log-level <level>         trace, debug, information, warning, error
              --inspect-packets           Show safe packet metadata (no payload dumps)
              --help                      Show this help

            While connected, type chat/commands on stdin. Use /quit or Ctrl+C to stop.
            """);
    }
}

internal sealed class CliLog : IAsyncDisposable
{
    private readonly StreamWriter? writer;
    private readonly int threshold;
    private readonly object gate = new();

    public CliLog(string? path, string level)
    {
        threshold = Level(level);
        if (string.IsNullOrWhiteSpace(path)) return;
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        writer = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    public void Write(string level, string text)
    {
        if (writer is null || Level(level) < threshold) return;
        lock (gate) writer.WriteLine(SensitiveDataRedactor.RedactText(text));
    }

    public ValueTask DisposeAsync()
    {
        writer?.Dispose();
        return ValueTask.CompletedTask;
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
