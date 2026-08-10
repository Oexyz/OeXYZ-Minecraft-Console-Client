using OeXYZ.Protocol;
using System.Net.Sockets;

namespace OeXYZ.ConsoleClient;

internal enum SessionLineKind
{
    Information,
    Chat,
    Success,
    Warning,
    Error
}

internal sealed record SessionLine(DateTimeOffset Timestamp, SessionLineKind Kind, string Text);

internal sealed class ConsoleSession : IAsyncDisposable
{
    private readonly AccountProfile account;
    private readonly ServerProfile server;
    private readonly AuthenticationService authentication;
    private readonly Action profilesChanged;
    private readonly ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    private readonly CancellationTokenSource lifetime = new();
    private readonly object logLock = new();
    private MinecraftConnection? connection;
    private MinecraftIdentity? identity;
    private Task? runTask;
    private int respawnPending;

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
        string safeName = string.Concat(server.DisplayName.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        LogPath = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safeName}.log");
    }

    public event Action<SessionLine>? LineAdded;
    public event Action<string, SessionLineKind>? StatusChanged;
    public event Action<bool>? ConnectedChanged;

    public Func<string, CancellationToken, Task<bool>>? CodeOfConductApproval { get; set; }

    public string LogPath { get; }
    public bool IsConnected => connection?.State == ConnectionState.Play;
    public string Title => $"{account.DisplayName} @ {server.DisplayName}";

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

    public void Stop() => lifetime.Cancel();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetStatus("AUTHENTICATING", SessionLineKind.Information);
            identity = await authentication.GetIdentityAsync(account, AddInformation, cancellationToken).ConfigureAwait(false);
            profilesChanged();

            int attempt = 0;
            do
            {
                attempt++;
                try
                {
                    SetStatus("DISCOVERING SERVER", SessionLineKind.Information);
                    (ServerAddress endpoint, ProtocolDefinition protocol) = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
                    Add(SessionLineKind.Information,
                        endpoint.UsedSrv
                            ? $"SRV resolved {endpoint.HandshakeHost} to {endpoint.NetworkHost}:{endpoint.Port}."
                            : $"Using {endpoint.NetworkHost}:{endpoint.Port}.");

                    await using MinecraftConnection active = new(endpoint, identity, protocol);
                    connection = active;
                    Wire(active, cancellationToken);
                    SetStatus("CONNECTING", SessionLineKind.Information);
                    await active.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    attempt = 0;
                    ConnectedChanged?.Invoke(true);
                    SetStatus($"CONNECTED · {protocol.MinecraftVersion}", SessionLineKind.Success);
                    Add(SessionLineKind.Success, $"Connected without a game renderer using protocol {protocol.ProtocolVersion}.");

                    using CancellationTokenSource connectedLifetime =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Task antiAfk = server.AntiAfk
                        ? RunAntiAfkAsync(active, connectedLifetime.Token)
                        : Task.CompletedTask;
                    await active.Completion.ConfigureAwait(false);
                    connectedLifetime.Cancel();
                    try
                    {
                        await antiAfk.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (connectedLifetime.IsCancellationRequested)
                    {
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Add(SessionLineKind.Error, FriendlyError(exception));
                }
                finally
                {
                    connection = null;
                    ConnectedChanged?.Invoke(false);
                }

                if (!server.AutoReconnect || cancellationToken.IsCancellationRequested) break;
                int delaySeconds = Math.Min(60, 4 * Math.Max(1, attempt)) + Random.Shared.Next(0, 4);
                SetStatus($"RECONNECTING IN {delaySeconds}s", SessionLineKind.Warning);
                Add(SessionLineKind.Warning, $"Connection lost. Reconnecting in {delaySeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken).ConfigureAwait(false);
            }
            while (!cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Add(SessionLineKind.Error, FriendlyError(exception));
        }
        finally
        {
            identity?.Certificate?.Dispose();
            SetStatus("DISCONNECTED", SessionLineKind.Error);
            ConnectedChanged?.Invoke(false);
        }
    }

    private async Task<(ServerAddress Address, ProtocolDefinition Protocol)> DiscoverAsync(CancellationToken cancellationToken)
    {
        MinecraftServerStatus? status = null;
        try
        {
            status = await MinecraftServerDiscovery.QueryAsync(server.Address, server.CustomPort, cancellationToken: cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Information,
                $"Server reports {status.VersionName}, protocol {status.ProtocolVersion}, players {status.PlayersOnline}/{status.PlayersMaximum}.");
        }
        catch when (!string.Equals(server.Version, "auto", StringComparison.OrdinalIgnoreCase))
        {
            Add(SessionLineKind.Warning, "Status ping failed; trying the manually selected version.");
        }

        ProtocolDefinition protocol = string.Equals(server.Version, "auto", StringComparison.OrdinalIgnoreCase)
            ? catalog.Resolve(status?.ProtocolVersion ?? throw new IOException("Automatic version detection failed. Select a version manually or verify that the server is online."))
            : catalog.Resolve(server.Version);
        ServerAddress endpoint = status?.Address ?? ServerAddress.Parse(server.Address, server.CustomPort).ResolveSrv();
        return (endpoint, protocol);
    }

    private void Wire(MinecraftConnection active, CancellationToken cancellationToken)
    {
        active.CodeOfConductApproval = CodeOfConductApproval;
        active.Log += AddInformation;
        active.ChatReceived += line => Add(SessionLineKind.Chat, line.Text);
        active.ConnectionFaulted += exception => Add(SessionLineKind.Error, FriendlyError(exception));
        active.Died += () => OnDeath(active, cancellationToken);
        active.HealthChanged += (health, _) =>
        {
            if (health <= 0) OnDeath(active, cancellationToken);
        };
    }

    private void OnDeath(MinecraftConnection active, CancellationToken cancellationToken)
    {
        SetStatus("DEAD", SessionLineKind.Warning);
        if (!server.AutoRespawn || Interlocked.Exchange(ref respawnPending, 1) != 0) return;
        _ = AutoRespawnAsync(active, cancellationToken);
    }

    private async Task AutoRespawnAsync(MinecraftConnection active, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            await active.RespawnAsync(cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Success, "Automatic respawn requested.");
            SetStatus("CONNECTED", SessionLineKind.Success);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Add(SessionLineKind.Error, "Automatic respawn failed: " + exception.Message);
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
            await Task.Delay(TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
            if (active.State != ConnectionState.Play) return;
            await active.PerformAfkActionAsync(cancellationToken).ConfigureAwait(false);
            Add(SessionLineKind.Information, "Anti-AFK movement sent.");
        }
    }

    private void AddInformation(string text) => Add(SessionLineKind.Information, text);

    private void Add(SessionLineKind kind, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SessionLine line = new(DateTimeOffset.Now, kind, text.Trim());
        lock (logLock)
        {
            File.AppendAllText(LogPath, $"{line.Timestamp:O} [{kind}] {line.Text}{Environment.NewLine}");
        }
        LineAdded?.Invoke(line);
    }

    private void SetStatus(string text, SessionLineKind kind) => StatusChanged?.Invoke(text, kind);

    private static string FriendlyError(Exception exception)
    {
        Exception source = exception is AggregateException aggregate ? aggregate.GetBaseException() : exception;
        return source switch
        {
            SocketException => "The server did not accept the network connection. Check its status, address and custom port.",
            TimeoutException => "The server did not respond before the connection timed out.",
            OperationCanceledException => "The operation was cancelled.",
            _ => source.Message
        };
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (runTask is not null)
        {
            try { await runTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        lifetime.Dispose();
    }
}
