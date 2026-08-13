using OeXYZ.Core;
using OeXYZ.Protocol;
using System.Net.Sockets;
using System.Threading.Channels;

namespace OeXYZ.ConsoleClient;

internal enum SessionLineKind
{
    Information,
    Chat,
    Success,
    Warning,
    Error
}

internal enum SessionLineCategory
{
    Chat,
    System,
    Connection,
    Error
}

internal enum SessionNotificationKind
{
    Disconnect,
    Reconnect,
    Death,
    Mention,
    PrivateMessage,
    Error
}

internal sealed record SessionLine(
    DateTimeOffset Timestamp,
    SessionLineKind Kind,
    SessionLineCategory Category,
    string Text,
    FormattedChatText? Formatting = null);

internal sealed record SessionNotification(SessionNotificationKind Kind, string Title, string Message);

internal sealed record SessionSnapshot(
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
    bool IsConnected);

internal sealed class ConsoleSession : IAsyncDisposable
{
    private readonly AccountProfile account;
    private readonly ServerProfile server;
    private readonly AuthenticationService authentication;
    private readonly Action profilesChanged;
    private readonly ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object stateLock = new();
    private readonly Channel<SessionLine> logLines = Channel.CreateUnbounded<SessionLine>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Task logTask;
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

    public ConsoleSession(
        AccountProfile account,
        ServerProfile server,
        AuthenticationService authentication,
        Action profilesChanged)
    {
        this.account = account;
        this.server = server;
        this.authentication = authentication;
        this.profilesChanged = profilesChanged;
        Directory.CreateDirectory(AppPaths.Logs);
        string safeName = string.Concat(server.DisplayName.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        LogPath = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.log");
        logTask = WriteLogAsync();
    }

    public event Action<SessionLine>? LineAdded;
    public event Action<string, SessionLineKind>? StatusChanged;
    public event Action<bool>? ConnectedChanged;
    public event Action<SessionSnapshot>? SnapshotChanged;
    public event Action<SessionNotification>? NotificationRequested;

    public Func<string, CancellationToken, Task<bool>>? CodeOfConductApproval { get; set; }

    public string LogPath { get; }
    public bool IsConnected => connection?.State == ConnectionState.Play;
    public string Title => $"{account.DisplayName} @ {server.DisplayName}";
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
        await active.RespawnAsync(cancellationToken).ConfigureAwait(false);
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
        bool connectedBefore = false;

        try
        {
            SetStatus("AUTHENTICATING", SessionLineKind.Information);
            identity = await authentication.GetIdentityAsync(account, AddInformation, cancellationToken).ConfigureAwait(false);
            profilesChanged();

            while (!cancellationToken.IsCancellationRequested)
            {
                Exception? failure = null;
                DateTimeOffset? connectedAt = null;
                try
                {
                    SetNextReconnect(null);
                    SetStatus("DISCOVERING SERVER", SessionLineKind.Information);
                    (ServerAddress endpoint, ProtocolDefinition protocol) = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    SetProtocol(protocol);
                    Add(SessionLineKind.Information, SessionLineCategory.Connection,
                        endpoint.UsedSrv
                            ? $"SRV resolved {endpoint.HandshakeHost} to {endpoint.NetworkHost}:{endpoint.Port}."
                            : $"Using {endpoint.NetworkHost}:{endpoint.Port}.");

                    await using MinecraftConnection active = new(endpoint, identity, protocol);
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
                    ConnectedChanged?.Invoke(true);
                    SetStatus("CONNECTED", SessionLineKind.Success);
                    Add(SessionLineKind.Success, SessionLineCategory.Connection,
                        $"Connected without a game renderer using protocol {protocol.ProtocolVersion}.");

                    using CancellationTokenSource connectedLifetime =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task antiAfk = server.AntiAfk
                        ? RunAntiAfkAsync(active, connectedLifetime.Token)
                        : Task.CompletedTask;
                    Task monitor = MonitorConnectionAsync(active, connectedLifetime.Token);
                    await active.Completion.ConfigureAwait(false);
                    connectedLifetime.Cancel();
                    await ObserveAuxiliaryAsync(antiAfk, connectedLifetime.Token).ConfigureAwait(false);
                    await ObserveAuxiliaryAsync(monitor, connectedLifetime.Token).ConfigureAwait(false);
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
                    ConnectedChanged?.Invoke(false);
                    PublishSnapshot();
                }

                if (cancellationToken.IsCancellationRequested) break;
                failure ??= new IOException("The connection ended unexpectedly.");
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
            Add(SessionLineKind.Error, SessionLineCategory.Error, FriendlyError(exception));
            Notify(SessionNotificationKind.Error, "Session error", $"{Title} · {FriendlyError(exception)}");
        }
        finally
        {
            identity?.Certificate?.Dispose();
            SetNextReconnect(null);
            SetStatus("DISCONNECTED", SessionLineKind.Error);
            ConnectedChanged?.Invoke(false);
        }
    }

    private async Task<(ServerAddress Address, ProtocolDefinition Protocol)> DiscoverAsync(CancellationToken cancellationToken)
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
        ServerAddress endpoint = status?.Address ?? ServerAddress.Parse(server.Address, server.CustomPort).ResolveSrv();
        return (endpoint, protocol);
    }

    private void Wire(MinecraftConnection active, CancellationToken cancellationToken)
    {
        active.CodeOfConductApproval = CodeOfConductApproval;
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
    }

    private void OnChatReceived(ChatLine line)
    {
        Add(SessionLineKind.Chat, SessionLineCategory.Chat, line.Text, line.Formatting);
        string ownName = identity?.Username ?? account.LoginHint;
        if (string.IsNullOrWhiteSpace(ownName)) return;
        string lower = line.Text.ToLowerInvariant();
        bool privateMessage = lower.Contains("whispers", StringComparison.Ordinal) ||
                              lower.Contains("[pm]", StringComparison.Ordinal) ||
                              lower.Contains("[msg]", StringComparison.Ordinal) ||
                              lower.Contains("-> you", StringComparison.Ordinal);
        if (privateMessage)
            Notify(SessionNotificationKind.PrivateMessage, $"Private message · {server.DisplayName}", line.Text);
        else if (line.Text.Contains(ownName, StringComparison.OrdinalIgnoreCase))
            Notify(SessionNotificationKind.Mention, $"Mention · {server.DisplayName}", line.Text);
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
            await active.RespawnAsync(cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Success, SessionLineCategory.System, "Automatic respawn requested.");
            SetStatus("CONNECTED", SessionLineKind.Success);
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

    private void Add(
        SessionLineKind kind,
        SessionLineCategory category,
        string text,
        FormattedChatText? formatting = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SessionLine line = new(DateTimeOffset.Now, kind, category, text.Trim(), formatting);
        logLines.Writer.TryWrite(line);
        LineAdded?.Invoke(line);
    }

    private async Task WriteLogAsync()
    {
        await using FileStream file = new(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using StreamWriter writer = new(file);
        await foreach (SessionLine line in logLines.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await writer.WriteLineAsync($"{line.Timestamp:O} [{line.Category}] [{line.Kind}] {SensitiveDataRedactor.RedactText(line.Text)}")
                .ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
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
        lock (stateLock)
        {
            currentStatus = text;
            currentStatusKind = kind;
        }
        StatusChanged?.Invoke(text, kind);
        PublishSnapshot();
    }

    private void PublishSnapshot() => SnapshotChanged?.Invoke(Snapshot);

    private SessionSnapshot CreateSnapshotLocked() => new(
        currentStatus,
        currentStatusKind,
        server.Address + (server.CustomPort > 0 ? $":{server.CustomPort}" : string.Empty),
        currentVersion,
        currentProtocol,
        health,
        food,
        position,
        metrics,
        Volatile.Read(ref reconnectCount),
        nextReconnectAt,
        players,
        connection?.State == ConnectionState.Play);

    private void Notify(SessionNotificationKind kind, string title, string message) =>
        NotificationRequested?.Invoke(new SessionNotification(kind, title, message));

    private static string FriendlyError(Exception exception)
    {
        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        return source switch
        {
            SocketException => "The server did not accept the network connection. Check its status, address and custom port.",
            TimeoutException => source.Message,
            OperationCanceledException => "The operation was cancelled.",
            _ => source.Message
        };
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
        try { await logTask.ConfigureAwait(false); }
        catch (IOException) { }
        lifetime.Dispose();
    }
}
