using OeXYZ.Core;
using OeXYZ.Protocol;
using System.Net.Sockets;
using System.Threading.Channels;

namespace OeXYZ.Session;

public enum SessionLineKind
{
    Information,
    Chat,
    Success,
    Warning,
    Error
}

public enum SessionLineCategory
{
    Chat,
    System,
    Connection,
    Error
}

public enum SessionNotificationKind
{
    Disconnect,
    Reconnect,
    Death,
    Mention,
    PrivateMessage,
    Error
}

public sealed record SessionLine(
    DateTimeOffset Timestamp,
    SessionLineKind Kind,
    SessionLineCategory Category,
    string Text,
    FormattedChatText? Formatting = null);

public sealed record SessionNotification(SessionNotificationKind Kind, string Title, string Message);

public sealed record SessionSnapshot(
    string Status,
    SessionLineKind StatusKind,
    string ServerAddress,
    string? MinecraftVersion,
    int? ProtocolVersion,
    float? Health,
    int? Food,
    PlayerPosition? Position,
    ConnectionMetrics Metrics,
    int ReconnectCount,
    DateTimeOffset? NextReconnectAt,
    IReadOnlyList<PlayerListEntry> Players,
    bool IsConnected,
    long DroppedEvents = 0,
    long DroppedLogLines = 0,
    long SubscriberFailures = 0,
    long OutboundRejections = 0,
    long UnknownPacketOverflow = 0);

public sealed class ConsoleSession : IAsyncDisposable
{
    private const long MaximumSessionLogBytes = 16L * 1024L * 1024L;
    internal const int MaximumPendingLogLines = 1024;
    internal const int MaximumRecentDiagnosticLines = 200;
    internal const int MaximumRecentDiagnosticCharacters = 128 * 1024;
    internal const int MaximumUnknownPacketKeys = 256;
    private readonly AccountProfile account;
    private readonly ServerProfile server;
    private readonly IIdentityProvider authentication;
    private readonly Action profilesChanged;
    private readonly string logBasePath;
    private readonly Func<string, long, RotatingLogWriter> logWriterFactory;
    private readonly ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private readonly object diagnosticsLock = new();
    private readonly object unknownPacketsLock = new();
    private readonly Channel<SessionLine> logLines = Channel.CreateBounded<SessionLine>(
        new BoundedChannelOptions(MaximumPendingLogLines)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Task logTask;
    private readonly Queue<string> recentDiagnostics = new();
    private readonly Dictionary<string, long> unknownPacketStatistics = new(StringComparer.Ordinal);
    private int recentDiagnosticCharacters;
    private MinecraftConnection? connection;
    private MinecraftIdentity? identity;
    private Task? runTask;
    private string currentStatus = "STARTING";
    private SessionLineKind currentStatusKind = SessionLineKind.Information;
    private string? currentVersion;
    private int? currentProtocol;
    private float? health;
    private int? food;
    private PlayerPosition? position;
    private ConnectionMetrics metrics = new(null, null, null, 0, 0, 0, 0, null);
    private IReadOnlyList<PlayerListEntry> players = [];
    private DateTimeOffset? nextReconnectAt;
    private int reconnectCount;
    private int respawnPending;
    private int dead;
    private int stopping;
    private long droppedLogLines;
    private long subscriberFailures;
    private Exception? terminalException;
    private Exception? logException;

    public ConsoleSession(
        AccountProfile account,
        ServerProfile server,
        IIdentityProvider authentication,
        Action profilesChanged,
        string logDirectory,
        bool enablePacketInspection = false)
        : this(
            account,
            server,
            authentication,
            profilesChanged,
            logDirectory,
            enablePacketInspection,
            static (path, maximumBytes) => new RotatingLogWriter(path, maximumBytes))
    {
    }

    internal ConsoleSession(
        AccountProfile account,
        ServerProfile server,
        IIdentityProvider authentication,
        Action profilesChanged,
        string logDirectory,
        bool enablePacketInspection,
        Func<string, long, RotatingLogWriter> logWriterFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        this.account = account ?? throw new ArgumentNullException(nameof(account));
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        this.profilesChanged = profilesChanged ?? throw new ArgumentNullException(nameof(profilesChanged));
        this.logWriterFactory = logWriterFactory ?? throw new ArgumentNullException(nameof(logWriterFactory));
        PrivateFileSystem.EnsurePrivateDirectory(logDirectory);
        string accountName = SafeLogComponent(account.DisplayName);
        string serverName = SafeLogComponent(server.DisplayName);
        string fileStem = $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{accountName}-{account.Id:N}-{serverName}-{server.Id:N}";
        logBasePath = RotatingLogWriter.ReserveUniquePath(logDirectory, fileStem);
        LogPath = logBasePath;
        PacketInspectionEnabled = enablePacketInspection;
        logTask = WriteLogAsync();
    }

    public event Action<SessionLine>? LineAdded;
    public event Action<string, SessionLineKind>? StatusChanged;
    public event Action<bool>? ConnectedChanged;
    public event Action<SessionSnapshot>? SnapshotChanged;
    public event Action<SessionNotification>? NotificationRequested;
    public event Action<PacketTrace>? PacketTraced;

    public Func<string, CancellationToken, Task<bool>>? CodeOfConductApproval { get; set; }

    public string LogPath { get; private set; }
    public bool PacketInspectionEnabled { get; }
    public Exception? TerminalException => Volatile.Read(ref terminalException);
    public Exception? LogException => Volatile.Read(ref logException);
    public Exception? FailureException => LogException ?? TerminalException;
    public Task Completion => runTask ?? Task.CompletedTask;
    public IReadOnlyDictionary<string, long> UnknownPacketStatistics
    {
        get
        {
            lock (unknownPacketsLock) return new Dictionary<string, long>(unknownPacketStatistics);
        }
    }
    public IReadOnlyList<string> RecentDiagnostics
    {
        get
        {
            lock (diagnosticsLock) return recentDiagnostics.ToArray();
        }
    }
    public bool IsConnected => connection?.State == ConnectionState.Play;
    public bool ShouldRestore => Volatile.Read(ref stopping) == 0 && runTask is { IsCompleted: false };
    public string Title => TerminalTextSanitizer.Sanitize($"{account.DisplayName} @ {server.DisplayName}");
    public AccountProfile Account => account;
    public ServerProfile Server => server;
    public SessionSnapshot Snapshot
    {
        get
        {
            lock (stateLock) return CreateSnapshotLocked();
        }
    }

    public void Start()
    {
        if (runTask is not null) throw new InvalidOperationException("The session is already running.");
        runTask = RunAsync(lifetime.Token);
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        MinecraftConnection active = connection ?? throw new InvalidOperationException("The session is not connected.");
        await active.SendChatAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task RespawnAsync(CancellationToken cancellationToken = default)
    {
        MinecraftConnection active = connection ?? throw new InvalidOperationException("The session is not connected.");
        bool claimedKnownDeath = Interlocked.CompareExchange(ref dead, 0, 1) == 1;
        if (!claimedKnownDeath && Volatile.Read(ref respawnPending) != 0)
        {
            Add(SessionLineKind.Information, SessionLineCategory.System, "A respawn request is already in progress.");
            return;
        }
        try
        {
            await active.RespawnAsync(cancellationToken).ConfigureAwait(false);
            if (claimedKnownDeath) SetStatus("CONNECTED", SessionLineKind.Success);
        }
        catch
        {
            if (claimedKnownDeath) Interlocked.Exchange(ref dead, 1);
            throw;
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        lifetime.Cancel();
        connection?.Disconnect();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        ReconnectBackoff backoff = new(
            TimeSpan.FromSeconds(server.ReconnectInitialDelaySeconds),
            TimeSpan.FromSeconds(server.ReconnectMaximumDelaySeconds));
        int consecutiveFailures = 0;
        int connectionAttempts = 0;
        bool connectedBefore = false;

        try
        {
            SetStatus("AUTHENTICATING", SessionLineKind.Information);
            string? previousAccountIdentifier = account.AccountIdentifier;
            identity = await authentication.GetIdentityAsync(account, AddInformation, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(previousAccountIdentifier, account.AccountIdentifier, StringComparison.Ordinal))
                profilesChanged();

            while (!cancellationToken.IsCancellationRequested)
            {
                Exception? failure = null;
                DateTimeOffset? connectedAt = null;
                try
                {
                    if (connectionAttempts > 0 && account.Kind == AccountKind.Microsoft)
                        await RefreshIdentityForReconnectAsync(cancellationToken).ConfigureAwait(false);
                    connectionAttempts++;
                    SetNextReconnect(null);
                    SetStatus("DISCOVERING SERVER", SessionLineKind.Information);
                    (ServerAddress endpoint, ProtocolDefinition protocol, int? statusPingMilliseconds) =
                        await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    SetProtocol(protocol);
                    Add(SessionLineKind.Information, SessionLineCategory.Connection,
                        endpoint.UsedSrv
                            ? $"SRV resolved {endpoint.HandshakeHost} to {endpoint.NetworkHost}:{endpoint.Port}."
                            : $"Using {endpoint.NetworkHost}:{endpoint.Port}.");

                    await using MinecraftConnection active = new(
                        endpoint,
                        identity,
                        protocol,
                        initialPingMilliseconds: statusPingMilliseconds);
                    connection = active;
                    Wire(active, cancellationToken);
                    SetStatus("CONNECTING", SessionLineKind.Information);
                    await active.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    connectedAt = DateTimeOffset.UtcNow;
                    if (connectedBefore)
                    {
                        Interlocked.Increment(ref reconnectCount);
                        Notify(SessionNotificationKind.Reconnect, "Session reconnected", Title);
                    }
                    connectedBefore = true;
                    RecordConnectionEstablished();
                    Raise(ConnectedChanged, true);
                    SetStatus("CONNECTED", SessionLineKind.Success);
                    Add(SessionLineKind.Success, SessionLineCategory.Connection,
                        $"Connected without a game renderer using protocol {protocol.ProtocolVersion}.");

                    using CancellationTokenSource connectedLifetime =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task antiAfk = server.AntiAfk
                        ? RunAntiAfkAsync(active, connectedLifetime.Token)
                        : Task.CompletedTask;
                    Task monitor = MonitorConnectionAsync(active, connectedLifetime.Token);
                    Task startupCommands = server.StartupCommandsEnabled
                        ? RunStartupCommandsAsync(active, connectedLifetime.Token)
                        : Task.CompletedTask;
                    await active.Completion.ConfigureAwait(false);
                    connectedLifetime.Cancel();
                    await ObserveAuxiliaryAsync(antiAfk, connectedLifetime.Token).ConfigureAwait(false);
                    await ObserveAuxiliaryAsync(monitor, connectedLifetime.Token).ConfigureAwait(false);
                    await ObserveAuxiliaryAsync(startupCommands, connectedLifetime.Token).ConfigureAwait(false);
                    failure = active.TerminalException ?? new IOException("The remote host closed the connection.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    connection = null;
                    Raise(ConnectedChanged, false);
                    PublishSnapshot();
                }

                if (cancellationToken.IsCancellationRequested) break;
                failure ??= new IOException("The connection ended unexpectedly.");
                RecordTerminalFailure(failure);
                DisconnectDecision decision = DisconnectClassifier.Classify(failure);
                Add(decision.Category == DisconnectCategory.Permanent ? SessionLineKind.Error : SessionLineKind.Warning,
                    decision.Category == DisconnectCategory.Permanent ? SessionLineCategory.Error : SessionLineCategory.Connection,
                    FriendlyError(failure));
                Notify(SessionNotificationKind.Disconnect, "Session disconnected",
                    $"{Title} · {FriendlyError(failure)}");

                bool stable = connectedAt.HasValue &&
                    DateTimeOffset.UtcNow - connectedAt.Value >= TimeSpan.FromSeconds(server.ReconnectStableResetSeconds);
                if (stable) consecutiveFailures = 0;

                if (!server.AutoReconnect || !decision.MayReconnect)
                {
                    if (decision.Category == DisconnectCategory.Permanent)
                    {
                        SetStatus("RECONNECT STOPPED", SessionLineKind.Error);
                        Add(SessionLineKind.Error, SessionLineCategory.Connection,
                            "Automatic reconnect stopped because repeating this rejection would not help.");
                    }
                    break;
                }

                consecutiveFailures++;
                if (server.ReconnectMaximumAttempts > 0 && consecutiveFailures > server.ReconnectMaximumAttempts)
                {
                    SetStatus("RECONNECT LIMIT REACHED", SessionLineKind.Error);
                    Add(SessionLineKind.Error, SessionLineCategory.Connection,
                        $"Reconnect stopped after {server.ReconnectMaximumAttempts} attempts.");
                    break;
                }

                TimeSpan delay = backoff.DelayForAttempt(consecutiveFailures);
                if (decision.MinimumRetryDelay is TimeSpan minimumDelay)
                {
                    TimeSpan configuredMaximum = TimeSpan.FromSeconds(server.ReconnectMaximumDelaySeconds);
                    delay = TimeSpan.FromMilliseconds(Math.Min(configuredMaximum.TotalMilliseconds,
                        Math.Max(delay.TotalMilliseconds, minimumDelay.TotalMilliseconds)));
                }
                DateTimeOffset retryAt = DateTimeOffset.UtcNow + delay;
                SetNextReconnect(retryAt);
                SetStatus($"RECONNECTING IN {Math.Ceiling(delay.TotalSeconds):0}s", SessionLineKind.Warning);
                Add(SessionLineKind.Warning, SessionLineCategory.Connection,
                    $"Transient disconnect. Reconnect attempt {consecutiveFailures} at {retryAt.ToLocalTime():HH:mm:ss}.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordTerminalFailure(exception);
            Add(SessionLineKind.Error, SessionLineCategory.Error, FriendlyError(exception));
            Notify(SessionNotificationKind.Error, "Session error", $"{Title} · {FriendlyError(exception)}");
        }
        finally
        {
            identity?.Certificate?.Dispose();
            SetNextReconnect(null);
            SetStatus("DISCONNECTED",
                Volatile.Read(ref stopping) != 0 || cancellationToken.IsCancellationRequested
                    ? SessionLineKind.Information
                    : SessionLineKind.Error);
            Raise(ConnectedChanged, false);
        }
    }

    private async Task RefreshIdentityForReconnectAsync(CancellationToken cancellationToken)
    {
        SetStatus("REFRESHING AUTHENTICATION", SessionLineKind.Information);
        string? previousAccountIdentifier = account.AccountIdentifier;
        MinecraftIdentity refreshed = await authentication.GetIdentityAsync(
            account,
            AddInformation,
            cancellationToken,
            AuthenticationInteractionMode.SilentOnly).ConfigureAwait(false);
        if (!string.Equals(previousAccountIdentifier, account.AccountIdentifier, StringComparison.Ordinal))
            profilesChanged();

        MinecraftIdentity? previous = identity;
        if (refreshed.Certificate is null && previous?.Certificate is { } existingCertificate &&
            existingCertificate.ExpiresAt > DateTimeOffset.UtcNow)
        {
            refreshed = refreshed with { Certificate = existingCertificate };
        }
        else if (previous?.Certificate is { } previousCertificate &&
                 !ReferenceEquals(previousCertificate, refreshed.Certificate))
        {
            previousCertificate.Dispose();
        }
        identity = refreshed;
    }

    private async Task<(ServerAddress Address, ProtocolDefinition Protocol, int? StatusPingMilliseconds)> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        MinecraftServerStatus? status = null;
        try
        {
            status = await MinecraftServerDiscovery.QueryAsync(
                server.Address, server.CustomPort, cancellationToken: cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Information, SessionLineCategory.Connection,
                $"Server reports {status.VersionName}, protocol {status.ProtocolVersion}, " +
                $"players {status.PlayersOnline}/{status.PlayersMaximum}, status ping {status.PingMilliseconds} ms.");
        }
        catch when (!string.Equals(server.Version, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Add(SessionLineKind.Warning, SessionLineCategory.Connection,
                "Status ping failed; trying the manually selected version.");
        }

        ProtocolDefinition protocol = string.Equals(server.Version, "auto", StringComparison.OrdinalIgnoreCase)
            ? catalog.Resolve(status?.ProtocolVersion ?? throw new IOException(
                "Automatic version detection failed. Select a version manually or verify that the server is online."))
            : catalog.Resolve(server.Version);
        ServerAddress endpoint = status?.Address ?? await ServerAddress.Parse(server.Address, server.CustomPort)
            .ResolveSrvAsync(cancellationToken).ConfigureAwait(false);
        return (endpoint, protocol, status?.PingMilliseconds);
    }

    private void Wire(MinecraftConnection active, CancellationToken cancellationToken)
    {
        active.PacketInspectionEnabled = PacketInspectionEnabled;
        active.CodeOfConductApproval = CodeOfConductApproval;
        active.UnknownPacketObserved += RecordUnknownPacket;
        active.Log += AddInformation;
        active.ChatReceived += OnChatReceived;
        active.Died += () => OnDeath(active, cancellationToken);
        active.HealthChanged += (currentHealth, currentFood) =>
        {
            lock (stateLock)
            {
                health = currentHealth;
                food = currentFood;
            }
            if (currentHealth <= 0) OnDeath(active, cancellationToken);
            else Interlocked.Exchange(ref dead, 0);
            PublishSnapshot();
        };
        active.PositionChanged += currentPosition =>
        {
            lock (stateLock) position = currentPosition;
            PublishSnapshot();
        };
        active.MetricsChanged += currentMetrics =>
        {
            lock (stateLock) metrics = currentMetrics;
            PublishSnapshot();
        };
        active.PlayerListChanged += currentPlayers =>
        {
            lock (stateLock) players = currentPlayers.ToArray();
            PublishSnapshot();
        };
        if (PacketInspectionEnabled) active.PacketTraced += trace =>
        {
            Raise(PacketTraced, trace);
        };
    }

    private void RecordUnknownPacket(string key)
    {
        lock (unknownPacketsLock)
        {
            if (string.Equals(key, "overflow", StringComparison.Ordinal))
            {
                unknownPacketStatistics["overflow"] = unknownPacketStatistics.GetValueOrDefault("overflow") + 1;
                return;
            }
            if (unknownPacketStatistics.ContainsKey(key) || unknownPacketStatistics.Count < MaximumUnknownPacketKeys)
            {
                unknownPacketStatistics[key] = unknownPacketStatistics.GetValueOrDefault(key) + 1;
                return;
            }
            unknownPacketStatistics["overflow"] = unknownPacketStatistics.GetValueOrDefault("overflow") + 1;
        }
    }

    private async Task RunStartupCommandsAsync(MinecraftConnection active, CancellationToken cancellationToken)
    {
        foreach (string command in server.StartupCommands.Take(8))
        {
            if (SensitiveDataRedactor.IsSensitiveCommand(command))
            {
                Add(SessionLineKind.Warning, SessionLineCategory.System,
                    "A sensitive login/registration startup command was blocked. Send it manually if appropriate.");
                continue;
            }
            await Task.Delay(server.StartupCommandDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            if (active.State != ConnectionState.Play) return;
            await active.SendChatAsync(command, cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Information, SessionLineCategory.System,
                $"Startup command sent: {SensitiveDataRedactor.RedactCommand(command)}");
        }
    }

    private void OnChatReceived(ChatLine line)
    {
        SessionLine? published = Add(
            SessionLineKind.Chat,
            SessionLineCategory.Chat,
            line.Text,
            line.Formatting);
        if (published is null) return;
        string text = published.Text;
        string ownName = identity?.Username ?? account.LoginHint;
        if (string.IsNullOrWhiteSpace(ownName)) return;
        string lower = text.ToLowerInvariant();
        bool privateMessage = lower.Contains("whispers", StringComparison.Ordinal) ||
                              lower.Contains("[pm]", StringComparison.Ordinal) ||
                              lower.Contains("[msg]", StringComparison.Ordinal) ||
                              lower.Contains("-> you", StringComparison.Ordinal);
        if (privateMessage)
            Notify(SessionNotificationKind.PrivateMessage, $"Private message · {server.DisplayName}", text);
        else if (text.Contains(ownName, StringComparison.OrdinalIgnoreCase))
            Notify(SessionNotificationKind.Mention, $"Mention · {server.DisplayName}", text);
    }

    private void OnDeath(MinecraftConnection active, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref dead, 1) != 0) return;
        SetStatus("DEAD", SessionLineKind.Warning);
        Add(SessionLineKind.Warning, SessionLineCategory.System, "The player died.");
        Notify(SessionNotificationKind.Death, "Player died", Title);
        if (!server.AutoRespawn || Interlocked.Exchange(ref respawnPending, 1) != 0) return;
        Observe(AutoRespawnAsync(active, cancellationToken), "Automatic respawn failed");
    }

    private async Task AutoRespawnAsync(MinecraftConnection active, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref dead, 0, 1) != 1) return;
            await active.RespawnAsync(cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Success, SessionLineCategory.System, "Automatic respawn requested.");
            SetStatus("CONNECTED", SessionLineKind.Success);
        }
        catch
        {
            Interlocked.Exchange(ref dead, 1);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref respawnPending, 0);
        }
    }

    private async Task RunAntiAfkAsync(MinecraftConnection active, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && active.State == ConnectionState.Play)
        {
            int jitter = server.AntiAfkJitterSeconds;
            int delaySeconds = server.AntiAfkIntervalSeconds +
                (jitter == 0 ? 0 : Random.Shared.Next(-jitter, jitter + 1));
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, delaySeconds)), cancellationToken).ConfigureAwait(false);
            if (active.State != ConnectionState.Play) return;
            await active.PerformAfkActionAsync(server.AntiAfkYawDegrees, cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Information, SessionLineCategory.System, "Anti-AFK look update sent.");
        }
    }

    private async Task MonitorConnectionAsync(MinecraftConnection active, CancellationToken cancellationToken)
    {
        TimeSpan staleTimeout = TimeSpan.FromSeconds(server.StaleConnectionTimeoutSeconds);
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            ConnectionMetrics current = active.Metrics;
            if (current.LastReceivedAt is null || DateTimeOffset.UtcNow - current.LastReceivedAt.Value <= staleTimeout)
                continue;
            active.Abort(new TimeoutException(
                $"No packet was received for {server.StaleConnectionTimeoutSeconds} seconds; the connection appears stalled."));
            return;
        }
    }

    private static async Task ObserveAuxiliaryAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Observe(Task task, string caption)
    {
        _ = task.ContinueWith(completed =>
        {
            Exception? exception = completed.Exception?.GetBaseException();
            if (exception is not null)
                Add(SessionLineKind.Error, SessionLineCategory.Error, $"{caption}: {FriendlyError(exception)}");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void AddInformation(string text) => Add(SessionLineKind.Information, SessionLineCategory.System, text);

    private SessionLine? Add(
        SessionLineKind kind,
        SessionLineCategory category,
        string text,
        FormattedChatText? formatting = null)
    {
        SessionLine? line = SessionLinePolicy.Create(DateTimeOffset.Now, kind, category, text, formatting);
        if (line is null) return null;
        RecordDiagnostic(line);
        if (!logLines.Writer.TryWrite(line)) Interlocked.Increment(ref droppedLogLines);
        Raise(LineAdded, line);
        return line;
    }

    internal SessionLine? AddForTesting(
        SessionLineKind kind,
        SessionLineCategory category,
        string text,
        FormattedChatText? formatting = null) => Add(kind, category, text, formatting);

    private async Task WriteLogAsync()
    {
        RotatingLogWriter? writer = null;
        try
        {
            await foreach (SessionLine line in logLines.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                writer ??= logWriterFactory(logBasePath, MaximumSessionLogBytes);
                await writer.WriteLineAsync(
                    $"{line.Timestamp:O} [{line.Category}] [{line.Kind}] {SensitiveDataRedactor.RedactText(line.Text)}")
                    .ConfigureAwait(false);
                LogPath = writer.CurrentPath;
            }
        }
        catch (Exception exception)
        {
            ReportLogFailure(exception);
        }
        finally
        {
            if (writer is not null)
            {
                try { await writer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { ReportLogFailure(exception); }
            }
        }
    }

    private void ReportLogFailure(Exception exception)
    {
        if (Interlocked.CompareExchange(ref logException, exception, null) is not null) return;
        SessionLine? line = SessionLinePolicy.Create(
            DateTimeOffset.Now,
            SessionLineKind.Error,
            SessionLineCategory.Error,
            $"Session logging stopped for '{Title}': {FriendlyError(exception)}");
        if (line is null) return;
        RecordDiagnostic(line);
        Raise(LineAdded, line);
        Notify(SessionNotificationKind.Error, "Session logging stopped", line.Text);
    }

    private void RecordDiagnostic(SessionLine line)
    {
        string diagnostic = $"{line.Timestamp:O} [{line.Category}] [{line.Kind}] {line.Text}";
        lock (diagnosticsLock)
        {
            recentDiagnostics.Enqueue(diagnostic);
            recentDiagnosticCharacters += diagnostic.Length;
            while (recentDiagnostics.Count > MaximumRecentDiagnosticLines ||
                   recentDiagnosticCharacters > MaximumRecentDiagnosticCharacters)
            {
                recentDiagnosticCharacters -= recentDiagnostics.Dequeue().Length;
            }
        }
    }

    internal void RecordTerminalFailure(Exception exception) =>
        Volatile.Write(ref terminalException, exception ?? throw new ArgumentNullException(nameof(exception)));

    internal void RecordConnectionEstablished() => Volatile.Write(ref terminalException, null);

    private static string SafeLogComponent(string value)
    {
        string safe = string.Concat((value ?? string.Empty).Select(character =>
            char.IsControl(character) || char.IsSurrogate(character) ||
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "profile";
        return safe.Length <= 32 ? safe : safe[..32];
    }

    private void SetProtocol(ProtocolDefinition protocol)
    {
        lock (stateLock)
        {
            currentVersion = protocol.MinecraftVersion;
            currentProtocol = protocol.ProtocolVersion;
        }
        PublishSnapshot();
    }

    private void SetNextReconnect(DateTimeOffset? value)
    {
        lock (stateLock) nextReconnectAt = value;
        PublishSnapshot();
    }

    private void SetStatus(string text, SessionLineKind kind)
    {
        text = SessionLinePolicy.NormalizeText(text);
        lock (stateLock)
        {
            currentStatus = text;
            currentStatusKind = kind;
        }
        Raise(StatusChanged, text, kind);
        PublishSnapshot();
    }

    private void PublishSnapshot() => Raise(SnapshotChanged, Snapshot);

    private SessionSnapshot CreateSnapshotLocked() => new(
        currentStatus,
        currentStatusKind,
        TerminalTextSanitizer.Sanitize(
            server.Address + (server.CustomPort > 0 ? $":{server.CustomPort}" : string.Empty)),
        currentVersion,
        currentProtocol,
        health,
        food,
        position,
        metrics,
        Volatile.Read(ref reconnectCount),
        nextReconnectAt,
        players,
        connection?.State == ConnectionState.Play,
        metrics.DroppedEvents,
        Interlocked.Read(ref droppedLogLines),
        metrics.SubscriberFailures + Interlocked.Read(ref subscriberFailures),
        metrics.OutboundRejections,
        GetUnknownPacketOverflow());

    private void Notify(SessionNotificationKind kind, string title, string message) =>
        Raise(NotificationRequested, new SessionNotification(
            kind,
            SessionLinePolicy.NormalizeText(title),
            SessionLinePolicy.NormalizeText(message)));

    private long GetUnknownPacketOverflow()
    {
        lock (unknownPacketsLock) return unknownPacketStatistics.GetValueOrDefault("overflow");
    }

    private void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try { ((Action)subscriber)(); }
            catch { Interlocked.Increment(ref subscriberFailures); }
        }
    }

    private void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try { ((Action<T>)subscriber)(value); }
            catch { Interlocked.Increment(ref subscriberFailures); }
        }
    }

    private void Raise<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second)
    {
        if (handlers is null) return;
        foreach (Delegate subscriber in handlers.GetInvocationList())
        {
            try { ((Action<T1, T2>)subscriber)(first, second); }
            catch { Interlocked.Increment(ref subscriberFailures); }
        }
    }

    private static string FriendlyError(Exception exception)
    {
        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        string message = source switch
        {
            SocketException => "The server did not accept the network connection. Check its status, address and custom port.",
            TimeoutException => source.Message,
            OperationCanceledException => "The operation was cancelled.",
            _ => source.Message
        };
        return SessionLinePolicy.NormalizeText(message);
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        logLines.Writer.TryComplete();
        await logTask.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
