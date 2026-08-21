using System.Net.Sockets;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using OeXYZ.Core;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OeXYZ.Protocol;

public sealed class MinecraftConnection : IAsyncDisposable
{
    private const int MaximumUnknownPacketKeys = 256;
    private const int MaximumResourcePackUrlCharacters = 8192;
    private const int MaximumResourcePackHashCharacters = 128;
    private const int MaximumResourcePackPromptCharacters = 4096;
    private readonly ProtocolDefinition protocol;
    private readonly string host;
    private readonly string handshakeHost;
    private readonly ushort port;
    private readonly MinecraftIdentity identity;
    private readonly MinecraftServicesClient servicesClient;
    private readonly ConnectionDeadlinePolicy deadlines;
    private readonly ProtocolEventDispatcher eventDispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource metricsLifetime = new();
    private readonly TaskCompletionSource playReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource loginComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource codeOfConductStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object playersLock = new();
    private readonly Dictionary<Guid, PlayerListEntry> players = [];
    private readonly object unknownPacketsLock = new();
    private readonly Dictionary<string, long> unknownPackets = new(StringComparer.Ordinal);
    private readonly object codeOfConductLock = new();
    private TcpClient? tcpClient;
    private MinecraftPacketStream? packets;
    private Task? receiveTask;
    private readonly Task metricsPublisherTask;
    private int stopping;
    private ConnectionState state;
    private PlayerPosition position = new(0, 0, 0, 0, 0);
    private bool playerLoadedSent;
    private Guid? chatSessionUuid;
    private int chatSessionIndex;
    private long connectedAtUtcTicks;
    private long lastReceivedUtcTicks;
    private long lastSentUtcTicks;
    private long bytesReceived;
    private long bytesSent;
    private long packetsReceived;
    private long packetsSent;
    private int pingMilliseconds = -1;
    private Exception? terminalException;
    private long unknownPacketOverflow;
    private long subscriberFailures;
    private int metricsDirty;
    private Task? codeOfConductTask;
    private bool finishConfigurationPending;

    public MinecraftConnection(
        string host,
        ushort port,
        string username,
        ProtocolDefinition protocol,
        int? initialPingMilliseconds = null)
        : this(host, port, MinecraftIdentity.Offline(username), protocol, initialPingMilliseconds: initialPingMilliseconds)
    {
    }

    public MinecraftConnection(
        string host,
        ushort port,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient = null,
        int? initialPingMilliseconds = null)
        : this(host, host, port, identity, protocol, servicesClient, initialPingMilliseconds,
            ConnectionDeadlinePolicy.Default)
    {
    }

    public MinecraftConnection(
        ServerAddress address,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient = null,
        int? initialPingMilliseconds = null)
        : this(address.NetworkHost, address.HandshakeHost, address.Port, identity, protocol, servicesClient,
            initialPingMilliseconds, ConnectionDeadlinePolicy.Default)
    {
    }

    internal MinecraftConnection(
        ServerAddress address,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        ConnectionDeadlinePolicy deadlines,
        MinecraftServicesClient? servicesClient = null,
        int? initialPingMilliseconds = null)
        : this(address.NetworkHost, address.HandshakeHost, address.Port, identity, protocol, servicesClient,
            initialPingMilliseconds, deadlines)
    {
    }

    private MinecraftConnection(
        string host,
        string handshakeHost,
        ushort port,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient,
        int? initialPingMilliseconds,
        ConnectionDeadlinePolicy deadlines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(handshakeHost);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Username);
        if (identity.Username.Length > 16) throw new ArgumentOutOfRangeException(nameof(identity), "Minecraft usernames are limited to 16 characters.");

        this.host = host;
        this.handshakeHost = handshakeHost;
        this.port = port;
        this.identity = identity;
        this.protocol = protocol;
        this.servicesClient = servicesClient ?? new MinecraftServicesClient();
        this.deadlines = deadlines ?? throw new ArgumentNullException(nameof(deadlines));
        deadlines.Validate();
        eventDispatcher = new ProtocolEventDispatcher(_ => Interlocked.Increment(ref subscriberFailures));
        metricsPublisherTask = RunMetricsPublisherAsync();
        if (initialPingMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(initialPingMilliseconds));
        pingMilliseconds = initialPingMilliseconds ?? -1;
    }

    public event Action<string>? Log;
    public event Action<ConnectionState>? StateChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action<float, int>? HealthChanged;
    public event Action<PlayerPosition>? PositionChanged;
    public event Action? Died;
    public event Action<ConnectionState, int, int>? PacketObserved;
    public event Action<Exception>? ConnectionFaulted;
    public event Action<ConnectionMetrics>? MetricsChanged;
    public event Action<IReadOnlyList<PlayerListEntry>>? PlayerListChanged;
    public event Action<PacketTrace>? PacketTraced;
    public event Action<string>? UnknownPacketObserved;
    public bool PacketInspectionEnabled { get; set; }

    public Func<string, CancellationToken, Task<bool>>? CodeOfConductApproval { get; set; }

    public ConnectionState State => state;
    public string MinecraftVersion => protocol.MinecraftVersion;
    public int ProtocolVersion => protocol.ProtocolVersion;
    public Task Completion => receiveTask ?? Task.CompletedTask;
    public Exception? TerminalException => Volatile.Read(ref terminalException);

    public ConnectionMetrics Metrics => new(
        ReadTimestamp(ref connectedAtUtcTicks),
        ReadTimestamp(ref lastReceivedUtcTicks),
        ReadTimestamp(ref lastSentUtcTicks),
        Interlocked.Read(ref bytesReceived),
        Interlocked.Read(ref bytesSent),
        Interlocked.Read(ref packetsReceived),
        Interlocked.Read(ref packetsSent),
        Volatile.Read(ref pingMilliseconds) < 0 ? null : Volatile.Read(ref pingMilliseconds),
        eventDispatcher.Dropped,
        Interlocked.Read(ref subscriberFailures),
        0);

    public IReadOnlyList<PlayerListEntry> Players
    {
        get
        {
            lock (playersLock)
                return players.Values.OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public IReadOnlyDictionary<string, long> UnknownPacketStatistics
    {
        get
        {
            lock (unknownPacketsLock)
                return new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(unknownPackets));
        }
    }

    public long UnknownPacketOverflowCount => Interlocked.Read(ref unknownPacketOverflow);

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref stopping, 1) == 0)
            lifetime.Cancel();

        TcpClient? activeClient = Interlocked.Exchange(ref tcpClient, null);
        if (activeClient is null)
            return;

        try
        {
            activeClient.Client.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        activeClient.Dispose();
    }

    public void Abort(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Interlocked.CompareExchange(ref terminalException, reason, null);
        loginComplete.TrySetException(reason);
        playReady.TrySetException(reason);
        Disconnect();
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (state != ConnectionState.Disconnected) throw new InvalidOperationException("This connection has already been started.");
        SetState(ConnectionState.Connecting);
        TcpClient client = new() { NoDelay = true };
        tcpClient = client;

        await RunWithDeadlineAsync(
            token => client.ConnectAsync(host, port, token).AsTask(),
            ConnectionPhase.TcpConnect,
            deadlines.TcpConnect,
            cancellationToken).ConfigureAwait(false);
        packets = new MinecraftPacketStream(client.GetStream());
        packets.PacketWritten += OnPacketWritten;
        Raise(Log, $"TCP connection established to {host}:{port}.");

        await packets.WriteAsync(0, writer =>
        {
            writer.WriteVarInt(protocol.ProtocolVersion);
            writer.WriteString(handshakeHost, 255);
            writer.WriteUnsignedShort(port);
            writer.WriteVarInt(2);
        }, cancellationToken).ConfigureAwait(false);

        SetState(ConnectionState.Login);
        await SendLoginStartAsync(cancellationToken).ConfigureAwait(false);
        receiveTask = ReceiveLoopAsync(lifetime.Token);
        if (protocol.HasConfiguration)
        {
            await WaitWithDeadlineAsync(loginComplete.Task, ConnectionPhase.Login, deadlines.Login, cancellationToken)
                .ConfigureAwait(false);
            await WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WaitWithDeadlineAsync(playReady.Task, ConnectionPhase.Login, deadlines.Login, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunWithDeadlineAsync(
        Func<CancellationToken, Task> operation,
        ConnectionPhase phase,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            ConnectionPhaseTimeoutException exception = new(phase, timeout);
            Abort(exception);
            throw exception;
        }
    }

    private Task WaitWithDeadlineAsync(
        Task task,
        ConnectionPhase phase,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(token => task.WaitAsync(token), phase, timeout, cancellationToken);

    private async Task WaitForConfigurationAsync(CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        Task? completed = null;
        await RunWithDeadlineAsync(async token =>
        {
            completed = await Task.WhenAny(playReady.Task, codeOfConductStarted.Task)
                .WaitAsync(token).ConfigureAwait(false);
        }, ConnectionPhase.Configuration, deadlines.Configuration, cancellationToken).ConfigureAwait(false);
        elapsed.Stop();

        if (ReferenceEquals(completed, playReady.Task))
        {
            await playReady.Task.ConfigureAwait(false);
            return;
        }

        Task? decision;
        lock (codeOfConductLock) decision = codeOfConductTask;
        if (decision is null)
            throw new InvalidOperationException("The code-of-conduct decision task was not initialized.");
        TimeSpan remaining = deadlines.Configuration - elapsed.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            ConnectionPhaseTimeoutException exception = new(ConnectionPhase.Configuration, deadlines.Configuration);
            Abort(exception);
            throw exception;
        }
        await decision.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (playReady.Task.IsCompleted)
        {
            await playReady.Task.ConfigureAwait(false);
            return;
        }
        await WaitWithDeadlineAsync(
            playReady.Task, ConnectionPhase.Configuration, remaining, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendChatAsync(string message, CancellationToken cancellationToken = default)
    {
        if (state != ConnectionState.Play || packets is null) throw new InvalidOperationException("The client is not in the play state.");
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 256) throw new ArgumentOutOfRangeException(nameof(message), "Chat messages are limited to 256 characters.");

        Dictionary<string, int> ids = protocol.PacketIds.PlayServerbound;
        if (message.StartsWith("/", StringComparison.Ordinal) && ids.TryGetValue("chat_command", out int commandPacket))
        {
            await packets.WriteAsync(commandPacket, writer => WriteCommand(writer, message[1..]), cancellationToken,
                OutboundPacketPriority.Normal).ConfigureAwait(false);
        }
        else if (ids.TryGetValue("chat_message", out int signedChatPacket))
        {
            await packets.WriteAsync(signedChatPacket, writer => WriteChatMessage(writer, message), cancellationToken,
                OutboundPacketPriority.Normal).ConfigureAwait(false);
        }
        else if (ids.TryGetValue("chat", out int legacyChatPacket))
        {
            await packets.WriteAsync(legacyChatPacket, writer => writer.WriteString(message, 256), cancellationToken,
                OutboundPacketPriority.Normal).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException("Outgoing chat is not mapped for this protocol version.");
        }

        Raise(Log, FormatOutgoingChatLog(message));
    }

    internal static string FormatOutgoingChatLog(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string commandSafe = SensitiveDataRedactor.RedactCommand(message);
        return $"Chat sent: {SensitiveDataRedactor.RedactText(commandSafe)}";
    }

    public async Task RespawnAsync(CancellationToken cancellationToken = default)
    {
        MinecraftPacketStream stream = packets ?? throw new InvalidOperationException("The client is not connected.");
        int packetId = Require(protocol.PacketIds.PlayServerbound, "client_command");
        await stream.WriteAsync(packetId, writer => writer.WriteVarInt(0), cancellationToken,
            OutboundPacketPriority.Normal).ConfigureAwait(false);
        Raise(Log, "Respawn request sent.");
    }

    public async Task SendPositionAsync(CancellationToken cancellationToken = default)
    {
        MinecraftPacketStream stream = packets ?? throw new InvalidOperationException("The client is not connected.");
        int packetId = Require(protocol.PacketIds.PlayServerbound, "position_look");
        PlayerPosition current = position;
        await stream.WriteAsync(packetId, writer =>
        {
            writer.WriteDouble(current.X);
            writer.WriteDouble(current.Y);
            writer.WriteDouble(current.Z);
            writer.WriteFloat(current.Yaw);
            writer.WriteFloat(current.Pitch);
            writer.WriteByte(1);
        }, cancellationToken, OutboundPacketPriority.Normal).ConfigureAwait(false);
    }

    public Task PerformAfkActionAsync(float yawDegrees = 7.5F, CancellationToken cancellationToken = default)
    {
        PlayerPosition current = position;
        float safeYawDegrees = Math.Clamp(yawDegrees, 0.5F, 45F);
        float nextYaw = current.Yaw + safeYawDegrees;
        if (nextYaw >= 180F) nextYaw -= 360F;
        position = current with { Yaw = nextYaw };
        return SendPositionAsync(cancellationToken);
    }

    private async Task SendLoginStartAsync(CancellationToken cancellationToken)
    {
        MinecraftPacketStream stream = packets ?? throw new InvalidOperationException();
        int packetId = Require(protocol.PacketIds.LoginServerbound, "login_start");
        await stream.WriteAsync(packetId, writer =>
        {
            writer.WriteString(identity.Username, 16);
            PlayerCertificate? certificate = identity.Certificate;
            if (protocol.ProtocolVersion is 759 or 760)
            {
                writer.WriteBoolean(certificate is not null);
                if (certificate is not null)
                {
                    writer.WriteLong(certificate.ExpiresAt.ToUnixTimeMilliseconds());
                    writer.WriteVarIntPrefixedBytes(certificate.PublicKeyDer);
                    writer.WriteVarIntPrefixedBytes(protocol.ProtocolVersion == 759
                        ? certificate.PublicKeySignature
                        : certificate.PublicKeySignatureV2);
                }
            }
            if (protocol.ProtocolVersion >= 764)
            {
                writer.WriteUuid(identity.PlayerUuid);
            }
            else if (protocol.ProtocolVersion >= 761)
            {
                writer.WriteBoolean(true);
                writer.WriteUuid(identity.PlayerUuid);
            }
            else if (protocol.ProtocolVersion == 760)
            {
                writer.WriteBoolean(true);
                writer.WriteUuid(identity.PlayerUuid);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            MinecraftPacketStream stream = packets ?? throw new InvalidOperationException();
            while (!cancellationToken.IsCancellationRequested)
            {
                InboundPacket packet = await stream.ReadAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref bytesReceived, packet.WireLength);
                Interlocked.Increment(ref packetsReceived);
                Interlocked.Exchange(ref lastReceivedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
                TracePacket(PacketDirection.Clientbound, state, packet.Id, packet.Payload.Length, packet.WireLength);
                Raise(PacketObserved, state, packet.Id, packet.Payload.Length);
                RequestMetricsUpdate();
                switch (state)
                {
                    case ConnectionState.Login:
                        await HandleLoginAsync(packet, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConnectionState.Configuration:
                        await HandleConfigurationAsync(packet, cancellationToken).ConfigureAwait(false);
                        break;
                    case ConnectionState.Play:
                        await HandlePlayAsync(packet, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // A socket shutdown may surface as IOException/ObjectDisposedException
            // instead of OperationCanceledException. It is still a clean user disconnect.
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref terminalException, exception, null);
            loginComplete.TrySetException(exception);
            playReady.TrySetException(exception);
            Raise(ConnectionFaulted, exception, isCritical: true);
            Raise(Log, $"Connection ended: {exception.Message}", isCritical: true);
        }
        finally
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    private async Task HandleLoginAsync(InboundPacket packet, CancellationToken cancellationToken)
    {
        Dictionary<string, int> inbound = protocol.PacketIds.LoginClientbound;
        if (Is(inbound, "disconnect", packet.Id))
            throw new IOException("Login rejected: " + ReadJsonText(packet.Payload));

        if (Is(inbound, "compress", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            int threshold = reader.ReadVarInt();
            packets!.EnableCompression(threshold);
            Raise(Log, $"Packet compression enabled at {threshold} bytes.");
            return;
        }

        if (Is(inbound, "encryption_begin", packet.Id))
        {
            await HandleEncryptionRequestAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "login_plugin_request", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            int messageId = reader.ReadVarInt();
            int responseId = Require(protocol.PacketIds.LoginServerbound, "login_plugin_response");
            await packets!.WriteAsync(responseId, writer =>
            {
                writer.WriteVarInt(messageId);
                writer.WriteBoolean(false);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "cookie_request", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string key = reader.ReadString(32767);
            int responseId = Require(protocol.PacketIds.LoginServerbound, "cookie_response");
            await packets!.WriteAsync(responseId, writer =>
            {
                writer.WriteString(key);
                writer.WriteBoolean(false);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!Is(inbound, "success", packet.Id)) return;
        Raise(Log, "Login accepted by server.");
        if (protocol.HasConfiguration)
        {
            int acknowledgement = Require(protocol.PacketIds.LoginServerbound, "login_acknowledged");
            await packets!.WriteAsync(acknowledgement, null, cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Configuration);
            loginComplete.TrySetResult();
            await SendClientSettingsAsync(protocol.PacketIds.ConfigurationServerbound, cancellationToken).ConfigureAwait(false);
            await SendBrandAsync(protocol.PacketIds.ConfigurationServerbound, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            SetState(ConnectionState.Play);
        }
    }

    private async Task HandleConfigurationAsync(InboundPacket packet, CancellationToken cancellationToken)
    {
        Dictionary<string, int> inbound = protocol.PacketIds.ConfigurationClientbound;
        if (Is(inbound, "disconnect", packet.Id) || Is(inbound, "kick_disconnect", packet.Id))
            throw new IOException("Configuration rejected by server: " + ReadDisconnectText(packet.Payload));

        if (Is(inbound, "keep_alive", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            long value = reader.ReadLong();
            await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "keep_alive"),
                writer => writer.WriteLong(value), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "ping", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            int value = reader.ReadInt();
            await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "pong"),
                writer => writer.WriteInt(value), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "select_known_packs", packet.Id))
        {
            await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "select_known_packs"),
                writer => writer.WriteVarInt(0), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "cookie_request", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string key = reader.ReadString();
            await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "cookie_response"), writer =>
            {
                writer.WriteString(key);
                writer.WriteBoolean(false);
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "code_of_conduct", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string contents = TerminalTextSanitizer.Sanitize(reader.ReadString(16_384));
            if (contents.Length > 16_384 || reader.Remaining != 0)
                throw new InvalidDataException("The server code of conduct is outside safety limits.");
            Raise(Log, "The server requires accepting its code of conduct.", isCritical: true);
            BeginCodeOfConductDecision(contents, cancellationToken);
            return;
        }

        if (Is(inbound, "resource_pack_send", packet.Id) || Is(inbound, "add_resource_pack", packet.Id))
        {
            await DeclineResourcePackAsync(packet.Payload, protocol.PacketIds.ConfigurationServerbound, cancellationToken)
                .ConfigureAwait(false);
            return;
        }


        if (Is(inbound, "remove_resource_pack", packet.Id))
        {
            HandleRemoveResourcePack(packet.Payload);
            return;
        }

        if (!Is(inbound, "finish_configuration", packet.Id)) return;
        lock (codeOfConductLock)
        {
            if (codeOfConductTask is { IsCompleted: false })
            {
                finishConfigurationPending = true;
                return;
            }
        }
        await CompleteConfigurationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlayAsync(InboundPacket packet, CancellationToken cancellationToken)
    {
        Dictionary<string, int> inbound = protocol.PacketIds.PlayClientbound;
        if (Is(inbound, "kick_disconnect", packet.Id))
            throw new IOException("Disconnected by server: " + ReadDisconnectText(packet.Payload));

        if (Is(inbound, "keep_alive", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            int responseId = Require(protocol.PacketIds.PlayServerbound, "keep_alive");
            if (protocol.ProtocolVersion >= 339)
            {
                long value = reader.ReadLong();
                await packets!.WriteAsync(responseId, writer => writer.WriteLong(value), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                int value = reader.ReadVarInt();
                await packets!.WriteAsync(responseId, writer => writer.WriteVarInt(value), cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // Modern servers can use a separate play-state ping packet in addition
        // to keep-alives. Velocity uses this to verify that the client is still
        // responsive and closes the connection after roughly one minute when
        // no pong is returned.
        if (Is(inbound, "ping", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            int value = reader.ReadInt();
            await packets!.WriteAsync(Require(protocol.PacketIds.PlayServerbound, "pong"),
                writer => writer.WriteInt(value), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "login", packet.Id))
        {
            if (!protocol.HasConfiguration)
            {
                await SendClientSettingsAsync(protocol.PacketIds.PlayServerbound, cancellationToken).ConfigureAwait(false);
                await SendBrandAsync(protocol.PacketIds.PlayServerbound, cancellationToken).ConfigureAwait(false);
            }
            await SendChatSessionAsync(cancellationToken).ConfigureAwait(false);
            playReady.TrySetResult();
            return;
        }

        if (Is(inbound, "resource_pack_send", packet.Id) || Is(inbound, "add_resource_pack", packet.Id))
        {
            await DeclineResourcePackAsync(packet.Payload, protocol.PacketIds.PlayServerbound, cancellationToken)
                .ConfigureAwait(false);
            return;
        }


        if (Is(inbound, "remove_resource_pack", packet.Id))
        {
            HandleRemoveResourcePack(packet.Payload);
            return;
        }

        if (Is(inbound, "position", packet.Id))
        {
            await HandlePositionAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Is(inbound, "update_health", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            float health = reader.ReadFloat();
            int food = reader.ReadVarInt();
            Raise(HealthChanged, health, food, isCritical: true);
            return;
        }

        if (Is(inbound, "player_info", packet.Id) || Is(inbound, "player_info_update", packet.Id))
        {
            TryHandlePlayerInfo(packet.Payload,
                protocol.Capabilities.PlayerInfoLayout == PlayerInfoPacketLayout.ModernBitSet);
            return;
        }

        if (Is(inbound, "player_info_remove", packet.Id) || Is(inbound, "player_remove", packet.Id))
        {
            TryHandlePlayerInfoRemove(packet.Payload);
            return;
        }

        if (Is(inbound, "death_combat_event", packet.Id))
        {
            Raise(Died, isCritical: true);
            Raise(Log, "The player died; a respawn request can now be sent.", isCritical: true);
            return;
        }

        if (Is(inbound, "system_chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            FormattedChatText formatting;
            if (protocol.ProtocolVersion >= 765)
            {
                formatting = ChatTextCodec.ParseAnonymousNbt(ref reader);
            }
            else
            {
                formatting = ChatTextCodec.ParseJson(reader.ReadString(262144));
            }
            bool actionBar = reader.Remaining > 0 && reader.ReadBoolean();
            Raise(ChatReceived, new ChatLine(DateTimeOffset.Now, formatting.Text, actionBar, formatting));
            return;
        }

        if (Is(inbound, "player_chat", packet.Id) && protocol.ProtocolVersion >= 759)
        {
            DecodedPlayerChat decoded = PlayerChatDecoder.Decode(packet.Payload, protocol.ProtocolVersion, uuid =>
            {
                lock (playersLock) return players.TryGetValue(uuid, out PlayerListEntry? player) ? player.Name : null;
            });
            FormattedChatText formatting = ChatTextCodec.ParseLegacy(decoded.Text);
            Raise(ChatReceived, new ChatLine(DateTimeOffset.Now, formatting.Text, Formatting: formatting));
            return;
        }

        if (Is(inbound, "profileless_chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            FormattedChatText formatting;
            if (protocol.ProtocolVersion >= 765)
            {
                formatting = ChatTextCodec.ParseAnonymousNbt(ref reader);
            }
            else
            {
                formatting = ChatTextCodec.ParseJson(reader.ReadString(262144));
            }
            Raise(ChatReceived, new ChatLine(DateTimeOffset.Now, formatting.Text, Formatting: formatting));
            return;
        }

        if (Is(inbound, "chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string json = reader.ReadString(262144);
            FormattedChatText formatting = ChatTextCodec.ParseJson(json);
            Raise(ChatReceived, new ChatLine(DateTimeOffset.Now, formatting.Text, Formatting: formatting));
            if (ChatTextCodec.TranslationKeyFromJson(json)?.StartsWith("death.", StringComparison.Ordinal) == true)
            {
                Raise(Died, isCritical: true);
                Raise(Log, "The player died; a respawn request can now be sent.", isCritical: true);
            }
            return;
        }

        if (Is(inbound, "start_configuration", packet.Id))
        {
            await packets!.WriteAsync(Require(protocol.PacketIds.PlayServerbound, "configuration_acknowledged"), null, cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Configuration);
        }
    }

    private async Task HandlePositionAsync(byte[] payload, CancellationToken cancellationToken)
    {
        PacketReader reader = new(payload);
        int teleportId = -1;
        double x;
        double y;
        double z;
        float yaw;
        float pitch;
        int flags;

        switch (protocol.Capabilities.PositionLayout)
        {
            case PositionPacketLayout.RelativeVelocity:
                teleportId = reader.ReadVarInt();
                x = reader.ReadDouble();
                y = reader.ReadDouble();
                z = reader.ReadDouble();
                _ = reader.ReadDouble();
                _ = reader.ReadDouble();
                _ = reader.ReadDouble();
                yaw = reader.ReadFloat();
                pitch = reader.ReadFloat();
                flags = reader.ReadInt();
                break;
            case PositionPacketLayout.TeleportIdWithDismount:
            case PositionPacketLayout.TeleportId:
            case PositionPacketLayout.LegacyCoordinates:
                x = reader.ReadDouble();
                y = reader.ReadDouble();
                z = reader.ReadDouble();
                yaw = reader.ReadFloat();
                pitch = reader.ReadFloat();
                flags = reader.ReadByte();
                if (protocol.Capabilities.PositionLayout is PositionPacketLayout.TeleportId or
                    PositionPacketLayout.TeleportIdWithDismount)
                    teleportId = reader.ReadVarInt();
                if (protocol.Capabilities.PositionLayout == PositionPacketLayout.TeleportIdWithDismount)
                    _ = reader.ReadBoolean();
                break;
            case PositionPacketLayout.None:
            default:
                throw new NotSupportedException("The position packet layout is not supported.");
        }
        if (reader.Remaining != 0)
            throw new InvalidDataException("The position packet contains unexpected trailing data.");

        PlayerPosition previous = position;
        if ((flags & 0x01) != 0) x += previous.X;
        if ((flags & 0x02) != 0) y += previous.Y;
        if ((flags & 0x04) != 0) z += previous.Z;
        if ((flags & 0x08) != 0) yaw += previous.Yaw;
        if ((flags & 0x10) != 0) pitch += previous.Pitch;
        position = new PlayerPosition(x, y, z, yaw, pitch);
        Raise(PositionChanged, position, isCritical: true);

        if (teleportId >= 0 && protocol.PacketIds.PlayServerbound.TryGetValue("teleport_confirm", out int confirmId))
            await packets!.WriteAsync(confirmId, writer => writer.WriteVarInt(teleportId), cancellationToken).ConfigureAwait(false);

        if (!playerLoadedSent && protocol.PacketIds.PlayServerbound.TryGetValue("player_loaded", out int loadedId))
        {
            await packets!.WriteAsync(loadedId, null, cancellationToken).ConfigureAwait(false);
            playerLoadedSent = true;
            Raise(Log, "World loading acknowledged.");
        }
    }

    private async Task SendClientSettingsAsync(Dictionary<string, int> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue("settings", out int packetId) && !ids.TryGetValue("client_information", out packetId)) return;
        await packets!.WriteAsync(packetId, writer =>
        {
            writer.WriteString("de_de", 16);
            writer.WriteSignedByte(2);
            if (protocol.Capabilities.ClientSettingsLayout == ClientSettingsPacketLayout.LegacyFiveFields)
                writer.WriteSignedByte(0);
            else
                writer.WriteVarInt(0);
            writer.WriteBoolean(true);
            writer.WriteByte(0x7F);
            switch (protocol.Capabilities.ClientSettingsLayout)
            {
                case ClientSettingsPacketLayout.LegacyFiveFields:
                    break;
                case ClientSettingsPacketLayout.MainHand:
                    writer.WriteVarInt(1);
                    break;
                case ClientSettingsPacketLayout.DisableTextFiltering:
                    writer.WriteVarInt(1);
                    writer.WriteBoolean(false);
                    break;
                case ClientSettingsPacketLayout.EnableTextFilteringAndListing:
                    writer.WriteVarInt(1);
                    writer.WriteBoolean(false);
                    writer.WriteBoolean(true);
                    break;
                case ClientSettingsPacketLayout.ParticleStatus:
                    writer.WriteVarInt(1);
                    writer.WriteBoolean(false);
                    writer.WriteBoolean(true);
                    writer.WriteVarInt(0);
                    break;
                case ClientSettingsPacketLayout.None:
                default:
                    throw new NotSupportedException("The client-settings packet layout is not supported.");
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendBrandAsync(Dictionary<string, int> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue("custom_payload", out int packetId)) return;
        await packets!.WriteAsync(packetId, writer =>
        {
            writer.WriteString(protocol.ProtocolVersion >= 393 ? "minecraft:brand" : "MC|Brand");
            writer.WriteString("OeXYZ");
        }, cancellationToken).ConfigureAwait(false);
        Raise(Log, "Client brand announced as OeXYZ.");
    }

    private async Task DeclineResourcePackAsync(
        byte[] payload,
        Dictionary<string, int> responseIds,
        CancellationToken cancellationToken)
    {
        ResourcePackRequest request = ParseResourcePackRequest(payload, protocol.ResourcePackRequestLayout);
        int responseId = RequireResourcePackResponseId(responseIds);
        await packets!.WriteAsync(responseId,
            writer => WriteResourcePackDecline(writer, request, protocol.ResourcePackResponseLayout),
            cancellationToken).ConfigureAwait(false);
        Raise(Log, request.Forced
            ? "The server requires a resource pack. It was declined because this client does not render visual assets; the server may disconnect this session."
            : "Optional server resource pack declined; this client does not render visual assets.",
            isCritical: request.Forced);
    }

    internal static ResourcePackRequest ParseResourcePackRequest(
        ReadOnlySpan<byte> payload,
        ResourcePackRequestLayout layout)
    {
        PacketReader reader = new(payload);
        Guid? packId = null;
        string hash;
        bool forced = false;
        switch (layout)
        {
            case ResourcePackRequestLayout.UrlHash:
                _ = reader.ReadString(MaximumResourcePackUrlCharacters);
                hash = reader.ReadString(MaximumResourcePackHashCharacters);
                break;
            case ResourcePackRequestLayout.UrlHashForcedPrompt:
                _ = reader.ReadString(MaximumResourcePackUrlCharacters);
                hash = reader.ReadString(MaximumResourcePackHashCharacters);
                forced = reader.ReadBoolean();
                if (reader.ReadBoolean()) _ = reader.ReadString(MaximumResourcePackPromptCharacters);
                break;
            case ResourcePackRequestLayout.UuidUrlHashForcedPrompt:
                packId = reader.ReadUuid();
                _ = reader.ReadString(MaximumResourcePackUrlCharacters);
                hash = reader.ReadString(MaximumResourcePackHashCharacters);
                forced = reader.ReadBoolean();
                if (reader.ReadBoolean())
                {
                    string prompt = ChatTextCodec.FromAnonymousNbt(ref reader);
                    if (prompt.Length > MaximumResourcePackPromptCharacters)
                        throw new InvalidDataException("The resource-pack prompt is too long.");
                }
                break;
            case ResourcePackRequestLayout.None:
            default:
                throw new NotSupportedException("The resource-pack request layout is not supported.");
        }
        if (reader.Remaining != 0)
            throw new InvalidDataException("The resource-pack request contains unexpected trailing data.");
        return new ResourcePackRequest(packId, hash, forced);
    }

    internal static void WriteResourcePackDecline(
        PacketWriter writer,
        ResourcePackRequest request,
        ResourcePackResponseLayout layout)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);
        switch (layout)
        {
            case ResourcePackResponseLayout.HashAndStatus:
                writer.WriteString(request.Hash, MaximumResourcePackHashCharacters);
                break;
            case ResourcePackResponseLayout.StatusOnly:
                break;
            case ResourcePackResponseLayout.UuidAndStatus:
                writer.WriteUuid(request.PackId ?? throw new InvalidDataException(
                    "The resource-pack response requires a UUID that was not present."));
                break;
            case ResourcePackResponseLayout.None:
            default:
                throw new NotSupportedException("The resource-pack response layout is not supported.");
        }
        writer.WriteVarInt((int)ResourcePackResponseStatus.Declined);
    }

    internal static int RequireResourcePackResponseId(Dictionary<string, int> responseIds) =>
        Require(responseIds, "resource_pack_receive");

    private void HandleRemoveResourcePack(byte[] payload)
    {
        if (protocol.ResourcePackRequestLayout != ResourcePackRequestLayout.UuidUrlHashForcedPrompt)
            throw new NotSupportedException("The remove-resource-pack layout is not supported for this protocol.");
        PacketReader reader = new(payload);
        if (reader.ReadBoolean()) _ = reader.ReadUuid();
        if (reader.Remaining != 0)
            throw new InvalidDataException("The remove-resource-pack packet contains unexpected trailing data.");
        Raise(Log, "The server removed a resource-pack reference; this client had not downloaded any assets.");
    }

    private void BeginCodeOfConductDecision(string contents, CancellationToken cancellationToken)
    {
        lock (codeOfConductLock)
        {
            if (codeOfConductTask is { IsCompleted: false })
                throw new InvalidDataException("The server sent more than one active code-of-conduct request.");
            codeOfConductTask = ProcessCodeOfConductDecisionAsync(contents, cancellationToken);
            codeOfConductStarted.TrySetResult();
        }
    }

    private async Task ProcessCodeOfConductDecisionAsync(string contents, CancellationToken cancellationToken)
    {
        try
        {
            bool accepted = false;
            if (CodeOfConductApproval is not null)
            {
                using CancellationTokenSource deadline =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
                deadline.CancelAfter(deadlines.CodeOfConductDecision);
                try
                {
                    Task<bool> approval = CodeOfConductApproval(contents, deadline.Token);
                    accepted = await approval.WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    throw new ConnectionPhaseTimeoutException(
                        ConnectionPhase.CodeOfConductDecision, deadlines.CodeOfConductDecision);
                }
            }

            if (!accepted) throw new IOException("The server code of conduct was not accepted.");
            await packets!.WriteAsync(
                Require(protocol.PacketIds.ConfigurationServerbound, "accept_code_of_conduct"),
                null,
                cancellationToken).ConfigureAwait(false);
            Raise(Log, "Server code of conduct accepted by the user.", isCritical: true);

            bool finishPending;
            lock (codeOfConductLock)
            {
                finishPending = finishConfigurationPending;
                finishConfigurationPending = false;
            }
            if (finishPending) await CompleteConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Abort(exception);
            Raise(ConnectionFaulted, exception, isCritical: true);
            Raise(Log, $"Connection ended: {exception.Message}", isCritical: true);
        }
    }

    private async Task CompleteConfigurationAsync(CancellationToken cancellationToken)
    {
        if (state != ConnectionState.Configuration) return;
        await packets!.WriteAsync(
            Require(protocol.PacketIds.ConfigurationServerbound, "finish_configuration"),
            null,
            cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Play);
        Raise(Log, $"Joined Minecraft {protocol.MinecraftVersion} (protocol {protocol.ProtocolVersion}).", isCritical: true);
        playReady.TrySetResult();
    }

    private async Task HandleEncryptionRequestAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (!identity.IsOnline)
            throw new NotSupportedException("This is an online-mode server. Select a signed-in Microsoft account and connect again.");

        PacketReader reader = new(payload);
        string serverId = reader.ReadString(20);
        byte[] publicKeyBytes = reader.ReadBytes(reader.ReadVarInt());
        byte[] verifyToken = reader.ReadBytes(reader.ReadVarInt());
        bool shouldAuthenticate = protocol.ProtocolVersion < 770 || reader.Remaining == 0 || reader.ReadBoolean();
        byte[] sharedSecret = RandomNumberGenerator.GetBytes(16);

        if (shouldAuthenticate)
        {
            string serverHash = ComputeServerHash(serverId, sharedSecret, publicKeyBytes);
            await servicesClient.JoinServerAsync(identity, serverHash, cancellationToken).ConfigureAwait(false);
            Raise(Log, "Minecraft session verified.");
        }

        using RSA rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
        byte[] encryptedSecret = rsa.Encrypt(sharedSecret, RSAEncryptionPadding.Pkcs1);
        byte[] encryptedToken = rsa.Encrypt(verifyToken, RSAEncryptionPadding.Pkcs1);
        int responseId = Require(protocol.PacketIds.LoginServerbound, "encryption_begin");
        await packets!.WriteAsync(responseId, writer =>
        {
            writer.WriteVarIntPrefixedBytes(encryptedSecret);
            if (protocol.ProtocolVersion is 759 or 760) writer.WriteBoolean(true);
            writer.WriteVarIntPrefixedBytes(encryptedToken);
        }, cancellationToken).ConfigureAwait(false);
        packets.EnableEncryption(sharedSecret);
        Raise(Log, "Encrypted connection enabled.");
    }

    private async Task SendChatSessionAsync(CancellationToken cancellationToken)
    {
        PlayerCertificate? certificate = identity.Certificate;
        if (certificate is null || protocol.ProtocolVersion < 761 ||
            !protocol.PacketIds.PlayServerbound.TryGetValue("chat_session_update", out int packetId)) return;
        chatSessionUuid = Guid.NewGuid();
        await packets!.WriteAsync(packetId, writer =>
        {
            writer.WriteUuid(chatSessionUuid.Value);
            writer.WriteLong(certificate.ExpiresAt.ToUnixTimeMilliseconds());
            writer.WriteVarIntPrefixedBytes(certificate.PublicKeyDer);
            writer.WriteVarIntPrefixedBytes(certificate.PublicKeySignatureV2);
        }, cancellationToken).ConfigureAwait(false);
        Raise(Log, "Secure chat session initialized.");
    }

    private void WriteChatMessage(PacketWriter writer, string message)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long salt = BinaryPrimitives.ReadInt64BigEndian(RandomNumberGenerator.GetBytes(sizeof(long)));
        writer.WriteString(message, 256);
        writer.WriteLong(timestamp);
        writer.WriteLong(salt);

        if (protocol.ProtocolVersion == 759)
        {
            writer.WriteVarInt(0);
            writer.WriteBoolean(false);
            return;
        }

        if (protocol.ProtocolVersion == 760)
        {
            writer.WriteVarInt(0);
            writer.WriteBoolean(false);
            writer.WriteVarInt(0);
            writer.WriteBoolean(false);
            return;
        }

        byte[]? signature = CreateChatSignature(message, timestamp, salt);
        writer.WriteBoolean(signature is not null);
        if (signature is not null) writer.WriteBytes(signature);
        writer.WriteVarInt(0);
        writer.WriteBytes(stackalloc byte[3]);
        if (protocol.ProtocolVersion >= 770) writer.WriteByte(0);
    }

    private void WriteCommand(PacketWriter writer, string command)
    {
        writer.WriteString(command, 256);
        if (protocol.ProtocolVersion < 759 || protocol.ProtocolVersion >= 766) return;
        writer.WriteLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        writer.WriteLong(0);
        writer.WriteVarInt(0);
        if (protocol.ProtocolVersion <= 760)
        {
            writer.WriteBoolean(false);
            if (protocol.ProtocolVersion == 760)
            {
                writer.WriteVarInt(0);
                writer.WriteBoolean(false);
            }
        }
        else
        {
            writer.WriteVarInt(0);
            writer.WriteBytes(stackalloc byte[3]);
        }
    }

    private byte[]? CreateChatSignature(string message, long timestampMilliseconds, long salt)
    {
        PlayerCertificate? certificate = identity.Certificate;
        if (certificate is null || chatSessionUuid is null || protocol.ProtocolVersion < 761) return null;
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);
        PacketWriter signable = new();
        signable.WriteInt(1);
        signable.WriteUuid(identity.PlayerUuid);
        signable.WriteUuid(chatSessionUuid.Value);
        signable.WriteInt(chatSessionIndex++);
        signable.WriteLong(salt);
        signable.WriteLong(timestampMilliseconds / 1000);
        signable.WriteInt(messageBytes.Length);
        signable.WriteBytes(messageBytes);
        signable.WriteInt(0);
        return certificate.PrivateKey.SignData(signable.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static string ComputeServerHash(string serverId, byte[] sharedSecret, byte[] publicKey)
    {
        using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(Encoding.UTF8.GetBytes(serverId));
        sha1.AppendData(sharedSecret);
        sha1.AppendData(publicKey);
        byte[] digest = sha1.GetHashAndReset();
        BigInteger number = new(digest, isUnsigned: false, isBigEndian: true);
        bool negative = number.Sign < 0;
        byte[] magnitude = BigInteger.Abs(number).ToByteArray(isUnsigned: true, isBigEndian: true);
        string hexadecimal = Convert.ToHexString(magnitude).TrimStart('0').ToLowerInvariant();
        if (hexadecimal.Length == 0) hexadecimal = "0";
        return negative ? "-" + hexadecimal : hexadecimal;
    }

    private static string ReadJsonText(byte[] payload)
    {
        try
        {
            PacketReader reader = new(payload);
            return ChatTextCodec.FromJson(reader.ReadString());
        }
        catch
        {
            return "The server did not provide a readable reason.";
        }
    }

    private string ReadDisconnectText(byte[] payload)
    {
        if (protocol.ProtocolVersion < 765) return ReadJsonText(payload);
        PacketReader reader = new(payload);
        return ChatTextCodec.FromAnonymousNbt(ref reader);
    }

    private static bool Is(Dictionary<string, int> ids, string name, int packetId) =>
        ids.TryGetValue(name, out int expected) && expected == packetId;

    private static int Require(Dictionary<string, int> ids, string name) =>
        ids.TryGetValue(name, out int packetId)
            ? packetId
            : throw new NotSupportedException($"Packet '{name}' is not mapped for this protocol.");

    private void OnPacketWritten(int packetId, int payloadBytes, int wireBytes)
    {
        Interlocked.Add(ref bytesSent, wireBytes);
        Interlocked.Increment(ref packetsSent);
        Interlocked.Exchange(ref lastSentUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        TracePacket(PacketDirection.Serverbound, state, packetId, payloadBytes, wireBytes);
        RequestMetricsUpdate();
    }

    private void RequestMetricsUpdate(bool immediate = false)
    {
        if (immediate)
        {
            Interlocked.Exchange(ref metricsDirty, 0);
            Raise(MetricsChanged, Metrics, isCritical: true);
        }
        else
        {
            Interlocked.Exchange(ref metricsDirty, 1);
        }
    }

    private async Task RunMetricsPublisherAsync()
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(metricsLifetime.Token).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref metricsDirty, 0) != 0)
                    Raise(MetricsChanged, Metrics);
            }
        }
        catch (OperationCanceledException) when (metricsLifetime.IsCancellationRequested)
        {
        }
    }

    private void Raise(Action? handlers, bool isCritical = false)
    {
        if (handlers is null) return;
        eventDispatcher.Publish(() =>
        {
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch { Interlocked.Increment(ref subscriberFailures); }
            }
        }, isCritical);
    }

    private void Raise<T>(Action<T>? handlers, T value, bool isCritical = false)
    {
        if (handlers is null) return;
        eventDispatcher.Publish(() =>
        {
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                try { ((Action<T>)subscriber)(value); }
                catch { Interlocked.Increment(ref subscriberFailures); }
            }
        }, isCritical);
    }

    private void Raise<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second, bool isCritical = false)
    {
        if (handlers is null) return;
        eventDispatcher.Publish(() =>
        {
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                try { ((Action<T1, T2>)subscriber)(first, second); }
                catch { Interlocked.Increment(ref subscriberFailures); }
            }
        }, isCritical);
    }

    private void Raise<T1, T2, T3>(
        Action<T1, T2, T3>? handlers,
        T1 first,
        T2 second,
        T3 third,
        bool isCritical = false)
    {
        if (handlers is null) return;
        eventDispatcher.Publish(() =>
        {
            foreach (Delegate subscriber in handlers.GetInvocationList())
            {
                try { ((Action<T1, T2, T3>)subscriber)(first, second, third); }
                catch { Interlocked.Increment(ref subscriberFailures); }
            }
        }, isCritical);
    }

    private void TryHandlePlayerInfo(byte[] payload, bool modern)
    {
        try
        {
            PacketReader reader = new(payload);
            if (modern) HandleModernPlayerInfo(ref reader);
            else HandleLegacyPlayerInfo(ref reader);
            RaisePlayerListChanged();
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            Raise(Log, $"Ignored malformed player-list update: {exception.Message}");
        }
    }

    private void HandleLegacyPlayerInfo(ref PacketReader reader)
    {
        int action = reader.ReadVarInt();
        int count = ReadBoundedCount(ref reader, 10_000, "player-list entries");
        lock (playersLock)
        {
            for (int index = 0; index < count; index++)
            {
                Guid uuid = reader.ReadUuid();
                PlayerListEntry entry = players.GetValueOrDefault(uuid) ?? new PlayerListEntry(uuid, uuid.ToString("N"), -1, -1);
                switch (action)
                {
                    case 0:
                        string name = TerminalTextSanitizer.Sanitize(reader.ReadString(16));
                        SkipProperties(ref reader);
                        int gameMode = reader.ReadVarInt();
                        int latency = reader.ReadVarInt();
                        SkipOptionalChat(ref reader);
                        entry = new PlayerListEntry(uuid, name, latency, gameMode);
                        break;
                    case 1:
                        entry = entry with { GameMode = reader.ReadVarInt() };
                        break;
                    case 2:
                        entry = entry with { PingMilliseconds = reader.ReadVarInt() };
                        break;
                    case 3:
                        SkipOptionalChat(ref reader);
                        break;
                    case 4:
                        players.Remove(uuid);
                        continue;
                    default:
                        throw new InvalidDataException($"Unknown legacy player-list action {action}.");
                }
                players[uuid] = entry;
                UpdateOwnPing(entry);
            }
        }
    }

    private void HandleModernPlayerInfo(ref PacketReader reader)
    {
        int actions = reader.ReadByte();
        int count = ReadBoundedCount(ref reader, 10_000, "player-list entries");
        lock (playersLock)
        {
            for (int index = 0; index < count; index++)
            {
                Guid uuid = reader.ReadUuid();
                PlayerListEntry entry = players.GetValueOrDefault(uuid) ?? new PlayerListEntry(uuid, uuid.ToString("N"), -1, -1);
                if ((actions & 0x01) != 0)
                {
                    string name = TerminalTextSanitizer.Sanitize(reader.ReadString(16));
                    SkipProperties(ref reader);
                    entry = entry with { Name = name };
                }
                if ((actions & 0x02) != 0) SkipChatSession(ref reader);
                if ((actions & 0x04) != 0) entry = entry with { GameMode = reader.ReadVarInt() };
                if ((actions & 0x08) != 0) entry = entry with { Listed = reader.ReadBoolean() };
                if ((actions & 0x10) != 0) entry = entry with { PingMilliseconds = reader.ReadVarInt() };
                if ((actions & 0x20) != 0) SkipOptionalChat(ref reader);
                if ((actions & 0x40) != 0) _ = reader.ReadBoolean();
                if ((actions & 0x80) != 0) _ = reader.ReadVarInt();
                players[uuid] = entry;
                UpdateOwnPing(entry);
            }
        }
    }

    private void TryHandlePlayerInfoRemove(byte[] payload)
    {
        try
        {
            PacketReader reader = new(payload);
            int count = ReadBoundedCount(ref reader, 10_000, "removed player-list entries");
            lock (playersLock)
            {
                for (int index = 0; index < count; index++) players.Remove(reader.ReadUuid());
            }
            RaisePlayerListChanged();
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            Raise(Log, $"Ignored malformed player-list removal: {exception.Message}");
        }
    }

    private static void SkipProperties(ref PacketReader reader)
    {
        int properties = ReadBoundedCount(ref reader, 1024, "player properties");
        for (int property = 0; property < properties; property++)
        {
            _ = reader.ReadString(32767);
            _ = reader.ReadString(32767);
            if (reader.ReadBoolean()) _ = reader.ReadString(32767);
        }
    }

    private static void SkipChatSession(ref PacketReader reader)
    {
        if (!reader.ReadBoolean()) return;
        _ = reader.ReadUuid();
        _ = reader.ReadLong();
        SkipByteArray(ref reader, 1_048_576);
        SkipByteArray(ref reader, 1_048_576);
    }

    private void SkipOptionalChat(ref PacketReader reader)
    {
        if (!reader.ReadBoolean()) return;
        if (protocol.ProtocolVersion >= 765) _ = ChatTextCodec.FromAnonymousNbt(ref reader);
        else _ = ChatTextCodec.FromJson(reader.ReadString(262144));
    }

    private static void SkipByteArray(ref PacketReader reader, int maximum)
    {
        int length = reader.ReadVarInt();
        if (length < 0 || length > maximum) throw new InvalidDataException("Byte array is outside safety limits.");
        _ = reader.ReadBytes(length);
    }

    private static int ReadBoundedCount(ref PacketReader reader, int maximum, string description)
    {
        int count = reader.ReadVarInt();
        if (count < 0 || count > maximum) throw new InvalidDataException($"The number of {description} is outside safety limits.");
        return count;
    }

    private void UpdateOwnPing(PlayerListEntry entry)
    {
        // Some proxies publish a placeholder latency of zero for the local
        // player. Keep the measured status-handshake RTT in that case, then
        // replace it as soon as the server supplies a positive live latency.
        if (entry.Uuid == identity.PlayerUuid &&
            (entry.PingMilliseconds > 0 || Volatile.Read(ref pingMilliseconds) < 0))
            Volatile.Write(ref pingMilliseconds, entry.PingMilliseconds);
    }

    private void RaisePlayerListChanged()
    {
        IReadOnlyList<PlayerListEntry> snapshot = Players;
        Raise(PlayerListChanged, snapshot, isCritical: true);
        RequestMetricsUpdate();
    }

    private void TracePacket(
        PacketDirection direction,
        ConnectionState packetState,
        int packetId,
        int payloadBytes,
        int wireBytes)
    {
        Dictionary<string, int> mappings = (packetState, direction) switch
        {
            (ConnectionState.Login, PacketDirection.Clientbound) => protocol.PacketIds.LoginClientbound,
            (ConnectionState.Login, PacketDirection.Serverbound) => protocol.PacketIds.LoginServerbound,
            (ConnectionState.Configuration, PacketDirection.Clientbound) => protocol.PacketIds.ConfigurationClientbound,
            (ConnectionState.Configuration, PacketDirection.Serverbound) => protocol.PacketIds.ConfigurationServerbound,
            (ConnectionState.Play, PacketDirection.Clientbound) => protocol.PacketIds.PlayClientbound,
            (ConnectionState.Play, PacketDirection.Serverbound) => protocol.PacketIds.PlayServerbound,
            _ => EmptyPacketMappings.Value
        };
        string? name = packetState == ConnectionState.Connecting &&
                       direction == PacketDirection.Serverbound && packetId == 0
            ? "handshake"
            : mappings.FirstOrDefault(pair => pair.Value == packetId).Key;
        bool known = !string.IsNullOrEmpty(name);
        name ??= $"unknown_0x{packetId:X2}";
        if (!known)
        {
            string key = $"{packetState}:{direction}:0x{packetId:X2}";
            lock (unknownPacketsLock)
            {
                if (unknownPackets.TryGetValue(key, out long count)) unknownPackets[key] = count + 1;
                else if (unknownPackets.Count < MaximumUnknownPacketKeys) unknownPackets[key] = 1;
                else
                {
                    Interlocked.Increment(ref unknownPacketOverflow);
                    key = "overflow";
                }
            }
            Raise(UnknownPacketObserved, key);
        }
        if (!PacketInspectionEnabled) return;
        Raise(PacketTraced, new PacketTrace(DateTimeOffset.Now, direction, packetState, packetId, name,
            payloadBytes, wireBytes, known));
    }

    private static DateTimeOffset? ReadTimestamp(ref long value)
    {
        long ticks = Interlocked.Read(ref value);
        return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static class EmptyPacketMappings
    {
        public static readonly Dictionary<string, int> Value = [];
    }

    private void SetState(ConnectionState value)
    {
        if (state == value) return;
        state = value;
        if (value == ConnectionState.Play && Interlocked.Read(ref connectedAtUtcTicks) == 0)
            Interlocked.Exchange(ref connectedAtUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Raise(StateChanged, value, isCritical: true);
        RequestMetricsUpdate(immediate: true);
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (receiveTask is not null)
        {
            try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        Task? decisionTask;
        lock (codeOfConductLock) decisionTask = codeOfConductTask;
        if (decisionTask is not null)
        {
            try { await decisionTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        RequestMetricsUpdate(immediate: true);
        metricsLifetime.Cancel();
        try { await metricsPublisherTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        if (packets is not null) await packets.DisposeAsync().ConfigureAwait(false);
        await eventDispatcher.DisposeAsync().ConfigureAwait(false);
        metricsLifetime.Dispose();
        lifetime.Dispose();
    }
}
