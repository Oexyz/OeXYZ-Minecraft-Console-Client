using OeXYZ.Protocol;
using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

List<string> passed = [];

Run("protocol catalog endpoints", () =>
{
    ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    Equal(47, catalog.Resolve("1.8").ProtocolVersion);
    Equal(47, catalog.Resolve("1.8.8").ProtocolVersion);
    Equal(776, catalog.Resolve("26.2").ProtocolVersion);
    True(catalog.Versions.Count >= 70, "Expected release mappings across the supported range.");
});

Run("required packet maps", () =>
{
    ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    ProtocolDefinition legacy = catalog.Resolve("1.8.8");
    ProtocolDefinition latest = catalog.Resolve("26.2");
    True(legacy.PacketIds.PlayServerbound.ContainsKey("custom_payload"), "Legacy brand channel is missing.");
    True(legacy.PacketIds.PlayServerbound.ContainsKey("client_command"), "Legacy respawn packet is missing.");
    True(latest.PacketIds.ConfigurationServerbound.ContainsKey("custom_payload"), "Configuration brand channel is missing.");
    True(latest.PacketIds.ConfigurationClientbound.ContainsKey("code_of_conduct"), "Code-of-conduct packet is missing.");
    True(latest.PacketIds.ConfigurationServerbound.ContainsKey("accept_code_of_conduct"), "Code-of-conduct response is missing.");
    True(latest.PacketIds.PlayClientbound.ContainsKey("ping"), "Current play ping packet is missing.");
    True(latest.PacketIds.PlayServerbound.ContainsKey("pong"), "Current play pong response is missing.");
    True(legacy.PacketIds.PlayClientbound.ContainsKey("player_info"), "Legacy player-list packet is missing.");
    True(latest.PacketIds.PlayClientbound.ContainsKey("player_info") || latest.PacketIds.PlayClientbound.ContainsKey("player_info_update"), "Current player-list packet is missing.");
});

Run("server address parsing", () =>
{
    ServerAddress standard = ServerAddress.Parse("example.org");
    Equal("example.org", standard.HandshakeHost);
    Equal((ushort)25565, standard.Port);
    True(!standard.HasExplicitPort, "The default port must permit SRV discovery.");

    ServerAddress embedded = ServerAddress.Parse("example.org:25570");
    Equal((ushort)25570, embedded.Port);
    True(embedded.HasExplicitPort, "An embedded port must skip SRV discovery.");

    ServerAddress custom = ServerAddress.Parse("example.org:25570", 25571);
    Equal((ushort)25571, custom.Port);

    ServerAddress ipv6 = ServerAddress.Parse("[::1]:25567");
    Equal("::1", ipv6.HandshakeHost);
    Equal((ushort)25567, ipv6.Port);
    Throws<FormatException>(() => ServerAddress.Parse("https://example.org"));
});

Run("offline UUID compatibility", () =>
{
    Equal(Guid.Parse("b50ad385-829d-3141-a216-7e7d7539ba7f"), OfflineIdentity.CreateUuid("Notch"));
});

Run("packet primitive round trip", () =>
{
    PacketWriter writer = new();
    writer.WriteVarInt(776);
    writer.WriteString("OeXYZ");
    writer.WriteLong(1234567890123456789);
    writer.WriteBoolean(true);
    PacketReader reader = new(writer.ToArray());
    Equal(776, reader.ReadVarInt());
    Equal("OeXYZ", reader.ReadString());
    Equal(1234567890123456789L, reader.ReadLong());
    True(reader.ReadBoolean(), "Boolean packet value was not preserved.");
    Equal(0, reader.Remaining);
});

Run("chat formatting preserves styles without click actions", () =>
{
    FormattedChatText json = ChatTextCodec.ParseJson("{\"text\":\"Hello \u00a7aSteve\",\"bold\":true,\"extra\":[{\"text\":\"!\",\"italic\":true,\"clickEvent\":{\"action\":\"run_command\",\"value\":\"/op me\"}}]}");
    Equal("Hello Steve!", json.Text);
    True(json.Runs.Any(run => run.Text.Contains("Hello", StringComparison.Ordinal) && run.Style.Bold), "Bold JSON style was lost.");
    True(json.Runs.Any(run => run.Text.Contains("Steve", StringComparison.Ordinal) && run.Style.Color == "green"), "Legacy color was lost.");
    True(json.Runs.Any(run => run.Text == "!" && run.Style.Italic), "Italic JSON style was lost.");
    True(json.Text.IndexOf("/op me", StringComparison.Ordinal) < 0, "A click command leaked into rendered text.");

    FormattedChatText legacy = ChatTextCodec.ParseLegacy("\u00a7cRed \u00a7nunder\u00a7r plain");
    Equal("Red under plain", legacy.Text);
    True(legacy.Runs.Any(run => run.Style.Color == "red"), "Legacy red color was not parsed.");
    True(legacy.Runs.Any(run => run.Style.Underlined), "Legacy underline was not parsed.");
});

Run("current NBT chat supports literal translations and modified UTF-8", () =>
{
    Equal("Slime", ChatTextCodec.ParseJson("{\"translate\":\"entity.minecraft.slime\"}").Text);
    Equal("OeXYZTest was slain by Slime", ChatTextCodec.ParseJson(
        "{\"translate\":\"death.attack.mob\",\"with\":[\"OeXYZTest\",{\"translate\":\"entity.minecraft.slime\"}]}").Text);
    Equal("Successfully filled 121 block(s)", ChatTextCodec.ParseJson(
        "{\"translate\":\"commands.fill.success\",\"with\":[121]}").Text);

    byte[] proxyComponent = Convert.FromHexString(
        "0A090004776974680A0000000109000565787472610A0000000208000474657874000B" +
        "5465737455736572203E2000080000001868656C6C6F2066726F6D20612032362E3220" +
        "73657276657200080004746578740000000800097472616E736C6174650002257300");
    PacketReader proxyReader = new(proxyComponent);
    Equal("TestUser > hello from a 26.2 server", ChatTextCodec.FromAnonymousNbt(ref proxyReader));
    Equal(0, proxyReader.Remaining);

    // Java modified UTF-8 encodes U+1F600 as the CESU-8 surrogate pair below.
    byte[] supplementaryText = Convert.FromHexString(
        "0A080004746578740006EDA0BDEDB88000");
    PacketReader supplementaryReader = new(supplementaryText);
    Equal("😀", ChatTextCodec.FromAnonymousNbt(ref supplementaryReader));
    Equal(0, supplementaryReader.Remaining);

    FormattedChatText literal = ChatTextCodec.ParseJson(
        "{\"translate\":\"[%1$s] %2$s %%\",\"with\":[\"Alex\",\"hello\"]}");
    Equal("[Alex] hello %", literal.Text);

    byte[] styledComponent = Convert.FromHexString(
        "0A09000565787472610A00000002080005636F6C6F720003726564010004626F6C64010800047465787400045265642000" +
        "0100066974616C69630101000A756E6465726C696E65640101000D737472696B657468726F756768010A000B636C69636B" +
        "5F6576656E74080006616374696F6E000B72756E5F636F6D6D616E64080007636F6D6D616E6400062F6F70206D650008" +
        "00047465787400067374796C65640008000474657874000000");
    PacketReader styledReader = new(styledComponent);
    FormattedChatText styled = ChatTextCodec.ParseAnonymousNbt(ref styledReader);
    Equal("Red styled", styled.Text);
    True(styled.Runs.Any(run => run.Text == "Red " && run.Style.Color == "red" && run.Style.Bold),
        "NBT color or bold formatting was lost.");
    True(styled.Runs.Any(run => run.Text == "styled" && run.Style.Italic && run.Style.Underlined && run.Style.Strikethrough),
        "NBT text decorations were lost.");
    True(styled.Text.IndexOf("/op me", StringComparison.Ordinal) < 0,
        "An NBT click command leaked into rendered text.");
});

Run("player chat supports native and proxy-forwarded layouts", () =>
{
    Guid sender = Guid.Parse("52fdfc07-2182-454f-963f-5f0f9a621d72");
    PacketWriter current = new();
    current.WriteVarInt(42);
    current.WriteUuid(sender);
    current.WriteVarInt(0);
    current.WriteBoolean(false);
    current.WriteString("hello from current");
    Equal("<Steve> hello from current",
        PlayerChatDecoder.Decode(current.ToArray(), 773, id => id == sender ? "Steve" : null).Text);

    PacketWriter forwardedLegacy = new();
    forwardedLegacy.WriteUuid(sender);
    forwardedLegacy.WriteVarInt(0);
    forwardedLegacy.WriteBoolean(false);
    forwardedLegacy.WriteString("hello through proxy");
    Equal("<Steve> hello through proxy",
        PlayerChatDecoder.Decode(forwardedLegacy.ToArray(), 773, id => id == sender ? "Steve" : null).Text);

    PacketWriter unsigned = new();
    unsigned.WriteVarInt(43);
    unsigned.WriteUuid(sender);
    unsigned.WriteVarInt(1);
    unsigned.WriteBoolean(false);
    unsigned.WriteString("<Steve>");
    unsigned.WriteLong(0);
    unsigned.WriteLong(0);
    unsigned.WriteVarInt(0);
    unsigned.WriteBoolean(true);
    byte[] unsignedText = Encoding.UTF8.GetBytes("<Steve> visible unsigned content");
    unsigned.WriteByte(8);
    unsigned.WriteUnsignedShort((ushort)unsignedText.Length);
    unsigned.WriteBytes(unsignedText);
    Equal("<Steve> visible unsigned content",
        PlayerChatDecoder.Decode(unsigned.ToArray(), 773, id => id == sender ? "Steve" : null).Text);
});

Run("connected socket disconnect completes", () =>
{
    VerifyConnectedDisconnectAsync().GetAwaiter().GetResult();
});

Run("invalid protocol state transitions are rejected", () =>
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("1.8.8");
    MinecraftConnection connection = new("127.0.0.1", 25565, "StateTest", protocol);
    try
    {
        Throws<InvalidOperationException>(() => connection.SendChatAsync("too early").GetAwaiter().GetResult());
        Throws<InvalidOperationException>(() => connection.RespawnAsync().GetAwaiter().GetResult());
    }
    finally { connection.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
});

Run("malformed primitives fail within protocol limits", () =>
{
    Throws<InvalidDataException>(() => { PacketReader reader = new(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 }); _ = reader.ReadVarInt(); });
    Throws<EndOfStreamException>(() => { PacketReader reader = new(new byte[] { 0, 1, 2 }); _ = reader.ReadUuid(); });
    Throws<InvalidDataException>(() => { PacketReader reader = new(new byte[] { 2, 0xC3, 0x28 }); _ = reader.ReadString(); });
    PacketReader malformedNbt = new(new byte[] { 99 });
    Equal("[Unreadable server message]", ChatTextCodec.FromAnonymousNbt(ref malformedNbt));
    byte[] deepNbt = new byte[] { 10 }.Concat(Enumerable.Repeat(new byte[] { 10, 0, 0 }, 66).SelectMany(value => value))
        .Concat(Enumerable.Repeat((byte)0, 66)).ToArray();
    PacketReader deepReader = new(deepNbt);
    Equal("[Unreadable server message]", ChatTextCodec.FromAnonymousNbt(ref deepReader));
    FormattedChatText malformed = ChatTextCodec.ParseJson("{not-json");
    Equal("{not-json", malformed.Text);
});

Run("framing rejects invalid lengths truncation and compression bombs", () =>
{
    VerifyFramingGuardsAsync().GetAwaiter().GetResult();
});

Run("fragmented one-byte transport is reassembled", () =>
{
    VerifyFragmentedFrameAsync().GetAwaiter().GetResult();
});

Run("AES-CFB8 short packets flush immediately and round trip", () =>
{
    VerifyCfb8StreamAsync().GetAwaiter().GetResult();
});

Run("anonymized protocol replays cover legacy through current", () =>
{
    VerifyReplayFixturesAsync().GetAwaiter().GetResult();
});

Console.WriteLine($"PASS: {passed.Count} protocol tests");
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
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task VerifyConnectedDisconnectAsync()
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    TaskCompletionSource releaseServer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    Task server = Task.Run(async () =>
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        NetworkStream stream = peer.GetStream();

        // Minimal 1.8 login-success and play-login frames. Their payload is not
        // needed by the client, which lets this test keep the socket open and
        // prove that Disconnect(), rather than a server close, ends the session.
        await stream.WriteAsync(new byte[] { 1, 2, 1, 1 }, timeout.Token);
        PacketWriter playerInfo = new();
        playerInfo.WriteVarInt(0x38);
        playerInfo.WriteVarInt(0);
        playerInfo.WriteVarInt(1);
        playerInfo.WriteUuid(OfflineIdentity.CreateUuid("DisconnectTest"));
        playerInfo.WriteString("DisconnectTest", 16);
        playerInfo.WriteVarInt(0);
        playerInfo.WriteVarInt(1);
        playerInfo.WriteVarInt(42);
        playerInfo.WriteBoolean(false);
        byte[] playerInfoBody = playerInfo.ToArray();
        PacketWriter playerInfoFrame = new();
        playerInfoFrame.WriteVarInt(playerInfoBody.Length);
        playerInfoFrame.WriteBytes(playerInfoBody);
        await stream.WriteAsync(playerInfoFrame.ToArray(), timeout.Token);
        await stream.WriteAsync(playerInfoFrame.ToArray(), timeout.Token);
        await stream.WriteAsync(new byte[] { 1, 0x7F }, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        await releaseServer.Task.WaitAsync(timeout.Token);
    }, timeout.Token);

    try
    {
        ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("1.8");
        await using MinecraftConnection connection = new("127.0.0.1", (ushort)port, "DisconnectTest", protocol)
        {
            PacketInspectionEnabled = true
        };
        await connection.ConnectAsync(timeout.Token);
        Equal(ConnectionState.Play, connection.State);
        await WaitUntilAsync(() => connection.Players.Count == 1, TimeSpan.FromSeconds(2));
        PlayerListEntry player = connection.Players.Single();
        Equal("DisconnectTest", player.Name);
        Equal(42, player.PingMilliseconds);
        True(connection.Metrics.PacketsReceived >= 3, "Received packet metrics were not incremented.");
        True(connection.Metrics.PacketsSent >= 3, "Sent packet metrics were not incremented.");
        True(connection.Metrics.BytesReceived > 0 && connection.Metrics.BytesSent > 0, "Wire-byte metrics were not incremented.");
        True(connection.Metrics.LastReceivedAt is not null, "Last receive activity was not recorded.");
        True(connection.UnknownPacketStatistics.Any(item => item.Key.EndsWith("0x7F", StringComparison.Ordinal) && item.Value == 1),
            "The unexpected play packet was not recorded exactly once.");

        connection.Disconnect();
        connection.Disconnect();
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(ConnectionState.Disconnected, connection.State);
    }
    finally
    {
        releaseServer.TrySetResult();
        await server.WaitAsync(TimeSpan.FromSeconds(2));
        listener.Stop();
    }
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
    while (!condition())
    {
        if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("The test condition was not reached.");
        await Task.Delay(10);
    }
}

static async Task VerifyFramingGuardsAsync()
{
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 }));
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }));
    PacketWriter hugeLength = new();
    hugeLength.WriteVarInt(MinecraftPacketStream.MaximumPacketLength + 1);
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(hugeLength.ToArray()));
    await ThrowsAsync<EndOfStreamException>(() => ReadPacketAsync(new byte[] { 2, 0 }));

    byte[] expanding = Compress(Enumerable.Repeat((byte)0x41, 64).ToArray());
    PacketWriter body = new();
    body.WriteVarInt(1);
    body.WriteBytes(expanding);
    PacketWriter frame = new();
    frame.WriteVarInt(body.Length);
    frame.WriteBytes(body.ToArray());
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(frame.ToArray(), compressionThreshold: 1));

    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(new byte[] { 2, 1, 0xFF }, compressionThreshold: 2));

    await using MemoryStream encryptedSource = new();
    await using MinecraftPacketStream encryptedPackets = new(encryptedSource);
    encryptedPackets.EnableEncryption(new byte[16]);
    await ThrowsAsync<EndOfStreamException>(async () => { _ = await encryptedPackets.ReadAsync(CancellationToken.None); });
}

static async Task VerifyFragmentedFrameAsync()
{
    await using OneByteReadStream stream = new(new byte[] { 3, 0x2A, 0x01, 0x02 });
    await using MinecraftPacketStream packets = new(stream);
    InboundPacket packet = await packets.ReadAsync(CancellationToken.None);
    Equal(0x2A, packet.Id);
    True(packet.Payload.AsSpan().SequenceEqual(new byte[] { 1, 2 }), "Fragmented payload changed.");
}

static async Task VerifyCfb8StreamAsync()
{
    byte[] key = Enumerable.Range(0, 16).Select(index => (byte)index).ToArray();
    byte[] clear = [0x01, 0x02, 0x03];
    await using MemoryStream transport = new();
    await using (MinecraftCfb8Stream writer = new(transport, key, decrypt: false))
    {
        await writer.WriteAsync(clear);
        await writer.FlushAsync();
        Equal((long)clear.Length, transport.Length);
    }
    byte[] cipher = transport.ToArray();
    True(!cipher.AsSpan().SequenceEqual(clear), "Encrypted bytes equal plaintext.");
    await using MemoryStream input = new(cipher, writable: false);
    await using MinecraftCfb8Stream reader = new(input, key, decrypt: true);
    byte[] decoded = new byte[clear.Length];
    await reader.ReadExactlyAsync(decoded);
    True(decoded.AsSpan().SequenceEqual(clear), "CFB8 round trip changed a short packet.");
}

static async Task VerifyReplayFixturesAsync()
{
    string directory = Path.Combine(AppContext.BaseDirectory, "fixtures");
    string[] files = Directory.GetFiles(directory, "*.json").Order().ToArray();
    True(files.Length >= 6, "Expected replay fixtures for legacy and current versions.");
    ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    foreach (string path in files)
    {
        ReplayFixture fixture = JsonSerializer.Deserialize<ReplayFixture>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Equal(fixture.ProtocolVersion, catalog.Resolve(fixture.MinecraftVersion).ProtocolVersion);
        foreach (string raw in fixture.Frames)
        {
            InboundPacket packet = await ReadPacketAsync(Convert.FromHexString(raw));
            True(packet.Id >= 0, $"Fixture {Path.GetFileName(path)} contains an invalid packet ID.");
        }
    }
}

static async Task<InboundPacket> ReadPacketAsync(byte[] bytes, int? compressionThreshold = null)
{
    await using MemoryStream stream = new(bytes, writable: false);
    await using MinecraftPacketStream packets = new(stream);
    if (compressionThreshold.HasValue) packets.EnableCompression(compressionThreshold.Value);
    return await packets.ReadAsync(CancellationToken.None);
}

static byte[] Compress(byte[] bytes)
{
    using MemoryStream output = new();
    using (ZLibStream zlib = new(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(bytes);
    return output.ToArray();
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed record ReplayFixture(string MinecraftVersion, int ProtocolVersion, List<string> Frames);

sealed class OneByteReadStream : Stream
{
    private readonly MemoryStream inner;
    public OneByteReadStream(byte[] bytes) => inner = new MemoryStream(bytes, writable: false);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
}
