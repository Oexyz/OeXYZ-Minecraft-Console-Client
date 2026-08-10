using System.Net.Sockets;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OeXYZ.Protocol;

public sealed class MinecraftConnection : IAsyncDisposable
{
    private readonly ProtocolDefinition protocol;
    private readonly string host;
    private readonly string handshakeHost;
    private readonly ushort port;
    private readonly MinecraftIdentity identity;
    private readonly MinecraftServicesClient servicesClient;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource playReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? tcpClient;
    private MinecraftPacketStream? packets;
    private Task? receiveTask;
    private ConnectionState state;
    private PlayerPosition position = new(0, 0, 0, 0, 0);
    private bool playerLoadedSent;
    private Guid? chatSessionUuid;
    private int chatSessionIndex;

    public MinecraftConnection(string host, ushort port, string username, ProtocolDefinition protocol)
        : this(host, port, MinecraftIdentity.Offline(username), protocol)
    {
    }

    public MinecraftConnection(
        string host,
        ushort port,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient = null)
        : this(host, host, port, identity, protocol, servicesClient)
    {
    }

    public MinecraftConnection(
        ServerAddress address,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient = null)
        : this(address.NetworkHost, address.HandshakeHost, address.Port, identity, protocol, servicesClient)
    {
    }

    private MinecraftConnection(
        string host,
        string handshakeHost,
        ushort port,
        MinecraftIdentity identity,
        ProtocolDefinition protocol,
        MinecraftServicesClient? servicesClient)
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
    }

    public event Action<string>? Log;
    public event Action<ConnectionState>? StateChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action<float, int>? HealthChanged;
    public event Action<PlayerPosition>? PositionChanged;
    public event Action? Died;
    public event Action<ConnectionState, int, int>? PacketObserved;
    public event Action<Exception>? ConnectionFaulted;

    public Func<string, CancellationToken, Task<bool>>? CodeOfConductApproval { get; set; }

    public ConnectionState State => state;
    public string MinecraftVersion => protocol.MinecraftVersion;
    public int ProtocolVersion => protocol.ProtocolVersion;
    public Task Completion => receiveTask ?? Task.CompletedTask;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (state != ConnectionState.Disconnected) throw new InvalidOperationException("This connection has already been started.");
        SetState(ConnectionState.Connecting);
        tcpClient = new TcpClient { NoDelay = true };

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await tcpClient.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
        packets = new MinecraftPacketStream(tcpClient.GetStream());
        Log?.Invoke($"TCP connection established to {host}:{port}.");

        await packets.WriteAsync(0, writer =>
        {
            writer.WriteVarInt(protocol.ProtocolVersion);
            writer.WriteString(handshakeHost, 255);
            writer.WriteUnsignedShort(port);
            writer.WriteVarInt(2);
        }, linked.Token).ConfigureAwait(false);

        SetState(ConnectionState.Login);
        await SendLoginStartAsync(linked.Token).ConfigureAwait(false);
        receiveTask = ReceiveLoopAsync(lifetime.Token);
        await playReady.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task SendChatAsync(string message, CancellationToken cancellationToken = default)
    {
        if (state != ConnectionState.Play || packets is null) throw new InvalidOperationException("The client is not in the play state.");
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > 256) throw new ArgumentOutOfRangeException(nameof(message), "Chat messages are limited to 256 characters.");

        Dictionary<string, int> ids = protocol.PacketIds.PlayServerbound;
        if (message.StartsWith("/", StringComparison.Ordinal) && ids.TryGetValue("chat_command", out int commandPacket))
        {
            await packets.WriteAsync(commandPacket, writer => WriteCommand(writer, message[1..]), cancellationToken).ConfigureAwait(false);
        }
        else if (ids.TryGetValue("chat_message", out int signedChatPacket))
        {
            await packets.WriteAsync(signedChatPacket, writer => WriteChatMessage(writer, message), cancellationToken).ConfigureAwait(false);
        }
        else if (ids.TryGetValue("chat", out int legacyChatPacket))
        {
            await packets.WriteAsync(legacyChatPacket, writer => writer.WriteString(message, 256), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException("Outgoing chat is not mapped for this protocol version.");
        }

        Log?.Invoke($"Chat sent: {message}");
    }

    public async Task RespawnAsync(CancellationToken cancellationToken = default)
    {
        MinecraftPacketStream stream = packets ?? throw new InvalidOperationException("The client is not connected.");
        int packetId = Require(protocol.PacketIds.PlayServerbound, "client_command");
        await stream.WriteAsync(packetId, writer => writer.WriteVarInt(0), cancellationToken).ConfigureAwait(false);
        Log?.Invoke("Respawn request sent.");
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
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task PerformAfkActionAsync(CancellationToken cancellationToken = default)
    {
        PlayerPosition current = position;
        float nextYaw = current.Yaw + 7.5F;
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
                PacketObserved?.Invoke(state, packet.Id, packet.Payload.Length);
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
        catch (Exception exception)
        {
            playReady.TrySetException(exception);
            ConnectionFaulted?.Invoke(exception);
            Log?.Invoke($"Connection ended: {exception.Message}");
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
            Log?.Invoke($"Packet compression enabled at {threshold} bytes.");
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
        Log?.Invoke("Login accepted by server.");
        if (protocol.HasConfiguration)
        {
            int acknowledgement = Require(protocol.PacketIds.LoginServerbound, "login_acknowledged");
            await packets!.WriteAsync(acknowledgement, null, cancellationToken).ConfigureAwait(false);
            SetState(ConnectionState.Configuration);
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
            string contents = reader.ReadString(262144);
            Log?.Invoke("The server requires accepting its code of conduct.");
            bool accepted = CodeOfConductApproval is not null &&
                            await CodeOfConductApproval(contents, cancellationToken).ConfigureAwait(false);
            if (!accepted) throw new IOException("The server code of conduct was not accepted.");
            await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "accept_code_of_conduct"),
                null, cancellationToken).ConfigureAwait(false);
            Log?.Invoke("Server code of conduct accepted by the user.");
            return;
        }

        if (Is(inbound, "add_resource_pack", packet.Id))
        {
            await DeclineResourcePackAsync(packet.Payload, protocol.PacketIds.ConfigurationServerbound, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!Is(inbound, "finish_configuration", packet.Id)) return;
        await packets!.WriteAsync(Require(protocol.PacketIds.ConfigurationServerbound, "finish_configuration"), null, cancellationToken).ConfigureAwait(false);
        SetState(ConnectionState.Play);
        Log?.Invoke($"Joined Minecraft {protocol.MinecraftVersion} (protocol {protocol.ProtocolVersion}).");
        playReady.TrySetResult();
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

        if (Is(inbound, "add_resource_pack", packet.Id))
        {
            await DeclineResourcePackAsync(packet.Payload, protocol.PacketIds.PlayServerbound, cancellationToken)
                .ConfigureAwait(false);
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
            HealthChanged?.Invoke(health, food);
            return;
        }

        if (Is(inbound, "death_combat_event", packet.Id))
        {
            Died?.Invoke();
            Log?.Invoke("The player died; a respawn request can now be sent.");
            return;
        }

        if (Is(inbound, "system_chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string text = protocol.ProtocolVersion >= 765
                ? ChatTextCodec.FromAnonymousNbt(ref reader)
                : ChatTextCodec.FromJson(reader.ReadString(262144));
            bool actionBar = reader.Remaining > 0 && reader.ReadBoolean();
            ChatReceived?.Invoke(new ChatLine(DateTimeOffset.Now, text, actionBar));
            return;
        }

        if (Is(inbound, "player_chat", packet.Id) && protocol.ProtocolVersion >= 759)
        {
            PacketReader reader = new(packet.Payload);
            if (protocol.ProtocolVersion >= 770) _ = reader.ReadVarInt();
            _ = reader.ReadUuid();
            _ = reader.ReadVarInt();
            if (reader.ReadBoolean()) _ = reader.ReadBytes(256);
            string text = reader.ReadString(256);
            ChatReceived?.Invoke(new ChatLine(DateTimeOffset.Now, text));
            return;
        }

        if (Is(inbound, "profileless_chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string text = protocol.ProtocolVersion >= 765
                ? ChatTextCodec.FromAnonymousNbt(ref reader)
                : ChatTextCodec.FromJson(reader.ReadString(262144));
            ChatReceived?.Invoke(new ChatLine(DateTimeOffset.Now, text));
            return;
        }

        if (Is(inbound, "chat", packet.Id))
        {
            PacketReader reader = new(packet.Payload);
            string json = reader.ReadString(262144);
            string text = ChatTextCodec.FromJson(json);
            ChatReceived?.Invoke(new ChatLine(DateTimeOffset.Now, text));
            if (ChatTextCodec.TranslationKeyFromJson(json)?.StartsWith("death.", StringComparison.Ordinal) == true)
            {
                Died?.Invoke();
                Log?.Invoke("The player died; a respawn request can now be sent.");
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

        if (protocol.ProtocolVersion >= 768)
        {
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
        }
        else
        {
            x = reader.ReadDouble();
            y = reader.ReadDouble();
            z = reader.ReadDouble();
            yaw = reader.ReadFloat();
            pitch = reader.ReadFloat();
            flags = reader.ReadByte();
            if (protocol.ProtocolVersion >= 107) teleportId = reader.ReadVarInt();
        }

        PlayerPosition previous = position;
        if ((flags & 0x01) != 0) x += previous.X;
        if ((flags & 0x02) != 0) y += previous.Y;
        if ((flags & 0x04) != 0) z += previous.Z;
        if ((flags & 0x08) != 0) yaw += previous.Yaw;
        if ((flags & 0x10) != 0) pitch += previous.Pitch;
        position = new PlayerPosition(x, y, z, yaw, pitch);
        PositionChanged?.Invoke(position);

        if (teleportId >= 0 && protocol.PacketIds.PlayServerbound.TryGetValue("teleport_confirm", out int confirmId))
            await packets!.WriteAsync(confirmId, writer => writer.WriteVarInt(teleportId), cancellationToken).ConfigureAwait(false);

        if (!playerLoadedSent && protocol.PacketIds.PlayServerbound.TryGetValue("player_loaded", out int loadedId))
        {
            await packets!.WriteAsync(loadedId, null, cancellationToken).ConfigureAwait(false);
            playerLoadedSent = true;
            Log?.Invoke("World loading acknowledged.");
        }
    }

    private async Task SendClientSettingsAsync(Dictionary<string, int> ids, CancellationToken cancellationToken)
    {
        if (!ids.TryGetValue("settings", out int packetId) && !ids.TryGetValue("client_information", out packetId)) return;
        await packets!.WriteAsync(packetId, writer =>
        {
            writer.WriteString("de_de", 16);
            writer.WriteSignedByte(2);
            if (protocol.ProtocolVersion >= 49) writer.WriteVarInt(0); else writer.WriteSignedByte(0);
            writer.WriteBoolean(true);
            writer.WriteByte(0x7F);
            if (protocol.ProtocolVersion >= 49) writer.WriteVarInt(1);
            if (protocol.ProtocolVersion >= 755) writer.WriteBoolean(false);
            if (protocol.ProtocolVersion >= 757) writer.WriteBoolean(true);
            if (protocol.ProtocolVersion >= 768) writer.WriteVarInt(0);
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
        Log?.Invoke("Client brand announced as OeXYZ.");
    }

    private async Task DeclineResourcePackAsync(
        byte[] payload,
        Dictionary<string, int> responseIds,
        CancellationToken cancellationToken)
    {
        PacketReader reader = new(payload);
        Guid packId = reader.ReadUuid();
        _ = reader.ReadString(32767);
        _ = reader.ReadString(64);
        bool forced = reader.ReadBoolean();
        if (!responseIds.TryGetValue("resource_pack_receive", out int responseId)) return;
        await packets!.WriteAsync(responseId, writer =>
        {
            writer.WriteUuid(packId);
            writer.WriteVarInt(1);
        }, cancellationToken).ConfigureAwait(false);
        Log?.Invoke(forced
            ? "The server requires a resource pack. It was declined because this client does not render visual assets."
            : "Optional server resource pack declined; this client does not render visual assets.");
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
            Log?.Invoke("Minecraft session verified.");
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
        Log?.Invoke("Encrypted connection enabled.");
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
        Log?.Invoke("Secure chat session initialized.");
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
            return reader.ReadString();
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

    private void SetState(ConnectionState value)
    {
        if (state == value) return;
        state = value;
        StateChanged?.Invoke(value);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (tcpClient is not null)
        {
            try { tcpClient.Client.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
            tcpClient.Dispose();
        }
        if (receiveTask is not null)
        {
            try { await receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        if (packets is not null) await packets.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }
}
