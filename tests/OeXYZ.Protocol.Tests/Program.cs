using OeXYZ.Protocol;
using System.Net;
using System.Net.Sockets;

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

Run("connected socket disconnect completes", () =>
{
    VerifyConnectedDisconnectAsync().GetAwaiter().GetResult();
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
        await stream.FlushAsync(timeout.Token);
        await releaseServer.Task.WaitAsync(timeout.Token);
    }, timeout.Token);

    try
    {
        ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("1.8");
        await using MinecraftConnection connection = new("127.0.0.1", (ushort)port, "DisconnectTest", protocol);
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
