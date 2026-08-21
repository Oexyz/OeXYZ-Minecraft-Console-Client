using OeXYZ.Protocol;
using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Diagnostics;
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
    Equal(PositionPacketLayout.LegacyCoordinates, legacy.Capabilities.PositionLayout);
    Equal(ClientSettingsPacketLayout.LegacyFiveFields, legacy.Capabilities.ClientSettingsLayout);
    Equal(ChatPacketLayout.Legacy, legacy.Capabilities.ChatLayout);
    Equal(PlayerInfoPacketLayout.LegacyAction, legacy.Capabilities.PlayerInfoLayout);
    Equal(PositionPacketLayout.RelativeVelocity, latest.Capabilities.PositionLayout);
    Equal(ClientSettingsPacketLayout.ParticleStatus, latest.Capabilities.ClientSettingsLayout);
    Equal(ChatPacketLayout.SignedSession, latest.Capabilities.ChatLayout);
    Equal(PlayerInfoPacketLayout.ModernBitSet, latest.Capabilities.PlayerInfoLayout);
    True(latest.Capabilities.Configuration && latest.Capabilities.Cookies &&
         latest.Capabilities.Transfer && latest.Capabilities.CodeOfConduct,
        "Current protocol capabilities are incomplete.");
    True(catalog.Versions.All(version =>
            version.Capabilities.PositionLayout != PositionPacketLayout.None &&
            version.Capabilities.ClientSettingsLayout != ClientSettingsPacketLayout.None &&
            version.Capabilities.PlayerInfoLayout != PlayerInfoPacketLayout.None),
        "A supported protocol lacks a headless-session capability layout.");
});

Run("resource-pack capabilities and bounded decline codecs", () =>
{
    ProtocolCatalog catalog = ProtocolCatalog.LoadEmbedded();
    Equal(ResourcePackRequestLayout.UrlHash, catalog.Resolve("1.8.8").ResourcePackRequestLayout);
    Equal(ResourcePackResponseLayout.HashAndStatus, catalog.Resolve("1.8.8").ResourcePackResponseLayout);
    Equal(ResourcePackRequestLayout.UrlHash, catalog.Resolve("1.12.2").ResourcePackRequestLayout);
    Equal(ResourcePackResponseLayout.StatusOnly, catalog.Resolve("1.12.2").ResourcePackResponseLayout);
    Equal(ResourcePackRequestLayout.UrlHashForcedPrompt, catalog.Resolve("1.19").ResourcePackRequestLayout);
    Equal(ResourcePackRequestLayout.UrlHashForcedPrompt, catalog.Resolve("1.20.2").ResourcePackRequestLayout);
    Equal(ResourcePackRequestLayout.UuidUrlHashForcedPrompt,
        catalog.Resolve("1.20.3").ResourcePackRequestLayout);
    Equal(ResourcePackResponseLayout.UuidAndStatus, catalog.Resolve("26.2").ResourcePackResponseLayout);
    True(catalog.Versions.All(version =>
            version.ResourcePackRequestLayout == ResourcePackRequestLayout.None ||
            version.PacketIds.PlayServerbound.ContainsKey("resource_pack_receive") ||
            version.PacketIds.ConfigurationServerbound.ContainsKey("resource_pack_receive")),
        "A supported resource-pack request has no response packet ID.");

    PacketWriter legacyPayload = new();
    legacyPayload.WriteString("https://example.invalid/pack.zip");
    legacyPayload.WriteString("0123456789abcdef");
    ResourcePackRequest legacy = MinecraftConnection.ParseResourcePackRequest(
        legacyPayload.ToArray(), ResourcePackRequestLayout.UrlHash);
    True(!legacy.Forced && legacy.PackId is null, "Legacy resource-pack fields were misclassified.");
    PacketWriter legacyResponse = new();
    MinecraftConnection.WriteResourcePackDecline(
        legacyResponse, legacy, ResourcePackResponseLayout.HashAndStatus);
    PacketReader legacyReader = new(legacyResponse.ToArray());
    Equal("0123456789abcdef", legacyReader.ReadString(128));
    Equal((int)ResourcePackResponseStatus.Declined, legacyReader.ReadVarInt());

    PacketWriter forcedPayload = new();
    forcedPayload.WriteString("https://example.invalid/required.zip");
    forcedPayload.WriteString("hash");
    forcedPayload.WriteBoolean(true);
    forcedPayload.WriteBoolean(true);
    forcedPayload.WriteString("Required pack prompt");
    ResourcePackRequest forced = MinecraftConnection.ParseResourcePackRequest(
        forcedPayload.ToArray(), ResourcePackRequestLayout.UrlHashForcedPrompt);
    True(forced.Forced, "A required resource pack was not recognized as forced.");
    PacketWriter statusResponse = new();
    MinecraftConnection.WriteResourcePackDecline(
        statusResponse, forced, ResourcePackResponseLayout.StatusOnly);
    PacketReader statusReader = new(statusResponse.ToArray());
    Equal((int)ResourcePackResponseStatus.Declined, statusReader.ReadVarInt());

    Guid packId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    PacketWriter uuidPayload = new();
    uuidPayload.WriteUuid(packId);
    uuidPayload.WriteString("https://example.invalid/current.zip");
    uuidPayload.WriteString("hash");
    uuidPayload.WriteBoolean(false);
    uuidPayload.WriteBoolean(false);
    ResourcePackRequest current = MinecraftConnection.ParseResourcePackRequest(
        uuidPayload.ToArray(), ResourcePackRequestLayout.UuidUrlHashForcedPrompt);
    PacketWriter uuidResponse = new();
    MinecraftConnection.WriteResourcePackDecline(
        uuidResponse, current, ResourcePackResponseLayout.UuidAndStatus);
    PacketReader uuidReader = new(uuidResponse.ToArray());
    Equal(packId, uuidReader.ReadUuid());
    Equal((int)ResourcePackResponseStatus.Declined, uuidReader.ReadVarInt());

    Throws<EndOfStreamException>(() => MinecraftConnection.ParseResourcePackRequest(
        [0, 1, 2], ResourcePackRequestLayout.UuidUrlHashForcedPrompt));
    PacketWriter longUrl = new();
    longUrl.WriteString(new string('u', 8193));
    longUrl.WriteString("hash");
    Throws<InvalidDataException>(() => MinecraftConnection.ParseResourcePackRequest(
        longUrl.ToArray(), ResourcePackRequestLayout.UrlHash));
    PacketWriter longHash = new();
    longHash.WriteString("https://example.invalid/pack.zip");
    longHash.WriteString(new string('h', 129));
    Throws<InvalidDataException>(() => MinecraftConnection.ParseResourcePackRequest(
        longHash.ToArray(), ResourcePackRequestLayout.UrlHash));
    PacketWriter longPrompt = new();
    longPrompt.WriteString("https://example.invalid/pack.zip");
    longPrompt.WriteString("hash");
    longPrompt.WriteBoolean(false);
    longPrompt.WriteBoolean(true);
    longPrompt.WriteString(new string('p', 4097));
    Throws<InvalidDataException>(() => MinecraftConnection.ParseResourcePackRequest(
        longPrompt.ToArray(), ResourcePackRequestLayout.UrlHashForcedPrompt));
    Throws<NotSupportedException>(() => MinecraftConnection.ParseResourcePackRequest(
        [], ResourcePackRequestLayout.None));
    Throws<NotSupportedException>(() => MinecraftConnection.RequireResourcePackResponseId([]));
    Throws<NotSupportedException>(() => MinecraftConnection.WriteResourcePackDecline(
        new PacketWriter(), current, ResourcePackResponseLayout.None));
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
    Throws<FormatException>(() => ServerAddress.Parse("example.org:0"));
    Throws<FormatException>(() => ServerAddress.Parse("example.org:0", 25570));
    Throws<FormatException>(() => ServerAddress.Parse("[::1]:0"));
    Throws<FormatException>(() => ServerAddress.Parse("evil\u001b[2J.example.org"));
    Throws<FormatException>(() => ServerAddress.Parse("https://example.org"));
});

Run("server status text is terminal-safe and field-bounded", () =>
{
    string versionName = "Version\u001b]2;forged\u0007\r\n" + new string('V', 128 * 1024);
    string description = "MOTD\u009b2J\u2028" + new string('M', 128 * 1024);
    string json = JsonSerializer.Serialize(new
    {
        version = new { name = versionName, protocol = 776 },
        players = new { online = 1, max = 20 },
        description = new { text = description }
    });

    MinecraftServerStatus status = MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"), json, 42);
    Equal(MinecraftServerDiscovery.MaximumVersionNameCharacters, status.VersionName.Length);
    Equal(MinecraftServerDiscovery.MaximumDescriptionCharacters, status.Description.Length);
    True(IsTerminalSafe(status.VersionName), "The status version still contains terminal controls.");
    True(IsTerminalSafe(status.Description), "The status MOTD still contains terminal controls.");
    Equal(42, status.PingMilliseconds);

    MinecraftServerStatus minimal = MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"), "{\"version\":{\"protocol\":776}}", 7);
    Equal("Unknown", minimal.VersionName);
    Equal(0, minimal.PlayersOnline);
    Equal(0, minimal.PlayersMaximum);
    Equal(string.Empty, minimal.Description);

    MinecraftServerStatus malformedOptional = MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"),
        "{\"version\":{\"protocol\":776,\"name\":7},\"players\":\"unknown\",\"description\":5,\"favicon\":false}",
        8);
    Equal("Unknown", malformedOptional.VersionName);
    Equal(0, malformedOptional.PlayersOnline);
    True(malformedOptional.ServerIconPng is null, "A non-string favicon was accepted.");

    MinecraftServerStatus negativePlayers = MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"),
        "{\"version\":{\"protocol\":776},\"players\":{\"online\":-5,\"max\":-1}}",
        9);
    Equal(0, negativePlayers.PlayersOnline);
    Equal(0, negativePlayers.PlayersMaximum);
    Throws<InvalidDataException>(() => MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"), "{\"version\":{}}", 1));
    string deepJson = "{\"version\":{\"protocol\":776},\"description\":" +
                      string.Concat(Enumerable.Repeat("{\"extra\":", 40)) + "\"deep\"" +
                      new string('}', 40) + "}";
    Throws<JsonException>(() => MinecraftServerDiscovery.ParseResponse(
        ServerAddress.Parse("example.org"), deepJson, 1));
});

Run("server status performs ping-pong with close-after-response fallback", () =>
{
    VerifyStatusPingAsync().GetAwaiter().GetResult();
});

Run("portable DNS SRV parser is bounded and preserves target ports", () =>
{
    const ushort transaction = 0x4F58;
    const string question = "_minecraft._tcp.example.org";
    byte[] response = CreateSrvResponse(transaction, "srv.example.org", 25570);
    IReadOnlyList<PortableSrvEndpoint> records = PortableSrvResolver.ParseResponse(response, transaction, question);
    Equal(1, records.Count);
    Equal("srv.example.org", records[0].Target);
    Equal((ushort)25570, records[0].Port);

    byte[] pointerLoop = response.ToArray();
    int targetOffset = PortableSrvResolver.BuildQuery("_minecraft._tcp.example.org", transaction).Length + 18;
    pointerLoop[targetOffset] = (byte)(0xC0 | (targetOffset >> 8));
    pointerLoop[targetOffset + 1] = (byte)targetOffset;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(pointerLoop, transaction));
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(response.AsSpan(0, 10), transaction));
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(response, (ushort)(transaction + 1)));
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(response, transaction, "_minecraft._tcp.wrong.example"));

    byte[] notResponse = response.ToArray();
    notResponse[2] &= 0x7F;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(notResponse, transaction));
    byte[] wrongOpcode = response.ToArray();
    wrongOpcode[2] |= 0x08;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(wrongOpcode, transaction));
    byte[] wrongType = response.ToArray();
    int questionTypeOffset = PortableSrvResolver.BuildQuery(question, transaction).Length - 4;
    wrongType[questionTypeOffset + 1] = 1;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(wrongType, transaction));
    byte[] wrongClass = response.ToArray();
    wrongClass[questionTypeOffset + 3] = 2;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(wrongClass, transaction));
    byte[] nxdomain = response.ToArray();
    nxdomain[3] = (byte)((nxdomain[3] & 0xF0) | 3);
    Equal(0, PortableSrvResolver.ParseResponse(nxdomain, transaction).Count);
    byte[] servfail = response.ToArray();
    servfail[3] = (byte)((servfail[3] & 0xF0) | 2);
    Equal(0, PortableSrvResolver.ParseResponse(servfail, transaction).Count);
    byte[] truncated = response.ToArray();
    truncated[2] |= 0x02;
    Throws<IOException>(() => PortableSrvResolver.ParseResponse(truncated, transaction));
    byte[] pointerOutside = response.ToArray();
    pointerOutside[targetOffset] = 0xFF;
    pointerOutside[targetOffset + 1] = 0xFF;
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(pointerOutside, transaction));
    Equal(0, PortableSrvResolver.ParseResponse(
        CreateSrvResponse(transaction, ".", 25570), transaction, question).Count);
    Throws<InvalidDataException>(() => PortableSrvResolver.ParseResponse(new byte[4097], transaction));

    PortableSrvEndpoint zero = new("zero.example", 25565, 0, 0);
    PortableSrvEndpoint weighted = new("weighted.example", 25566, 0, 10);
    PortableSrvEndpoint lowerPriority = new("later.example", 25567, 1, 100);
    Equal(zero, PortableSrvResolver.Select([zero, weighted, lowerPriority], _ => 0));
    Equal(weighted, PortableSrvResolver.Select([zero, weighted, lowerPriority], maximum => maximum - 1));
    Equal(zero, PortableSrvResolver.Select([zero], _ => 0));

    string resolvPath = Path.Combine(Path.GetTempPath(), "oexyz-resolv-" + Guid.NewGuid().ToString("N"));
    try
    {
        File.WriteAllText(resolvPath, "nameserver 127.0.0.1\nnameserver ::1\n");
        IReadOnlyList<IPAddress> resolvers = PortableSrvResolver.ReadNameServers(resolvPath);
        True(resolvers.Contains(IPAddress.Loopback), "IPv4 DNS resolver was not parsed.");
        True(resolvers.Contains(IPAddress.IPv6Loopback), "IPv6 DNS resolver was not parsed.");
    }
    finally { File.Delete(resolvPath); }
});

Run("portable DNS transport validates its source and TCP fallback", () =>
{
    VerifyDnsTransportAsync().GetAwaiter().GetResult();
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

Run("terminal sanitizer neutralizes controls in chat and plain fields", () =>
{
    const string unsafeText = "safe\u001b]2;title\u0007\r\nnext\t\u009b2J\u2028done";
    const string expected = "safe ]2;title next 2J done";
    Equal(expected, TerminalTextSanitizer.Sanitize(unsafeText));
    Equal(string.Empty, TerminalTextSanitizer.Sanitize(null));

    FormattedChatText json = ChatTextCodec.ParseJson(JsonSerializer.Serialize(new { text = unsafeText }));
    Equal(expected, json.Text);
    True(json.Runs.All(run => IsTerminalSafe(run.Text)), "A formatted JSON run contains terminal controls.");

    FormattedChatText legacy = ChatTextCodec.ParseLegacy(unsafeText);
    Equal(expected, legacy.Text);
    True(legacy.Runs.All(run => IsTerminalSafe(run.Text)), "A legacy chat run contains terminal controls.");
});

Run("outgoing chat logs redact commands and structured secrets at the source", () =>
{
    Equal("Chat sent: /authme:login [REDACTED]",
        MinecraftConnection.FormatOutgoingChatLog("/authme:login two word password"));
    Equal("Chat sent: {\"password\":\"[REDACTED]\",\"safe\":\"kept\"}",
        MinecraftConnection.FormatOutgoingChatLog("{\"password\":\"two word password\",\"safe\":\"kept\"}"));
    Equal("Chat sent: {\"password\":\"[REDACTED]\",\"safe\":\"kept\"}",
        MinecraftConnection.FormatOutgoingChatLog(
            "{\"pass\\u0077ord\":[\"array-secret\",{\"value\":\"object-secret\"}],\"safe\":\"kept\"}"));
    Equal("Chat sent: password=[REDACTED] safe=yes",
        MinecraftConnection.FormatOutgoingChatLog("password=\"two word password\" safe=yes"));
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

Run("NBT chat budgets reject allocation bombs and allow nested components", () =>
{
    PacketWriter nestedEndList = new();
    nestedEndList.WriteByte(9);
    nestedEndList.WriteByte(9);
    nestedEndList.WriteInt(1);
    nestedEndList.WriteByte(0);
    nestedEndList.WriteInt(1_000_000);
    Throws<InvalidDataException>(() =>
    {
        PacketReader reader = new(nestedEndList.ToArray());
        _ = ChatTextCodec.ReadAnonymousNbtFormatting(ref reader);
    });

    PacketWriter oversizedList = new();
    oversizedList.WriteByte(9);
    oversizedList.WriteByte(1);
    oversizedList.WriteInt(ChatTextCodec.MaximumNbtListElements + 1);
    Throws<InvalidDataException>(() =>
    {
        PacketReader reader = new(oversizedList.ToArray());
        _ = ChatTextCodec.ReadAnonymousNbtFormatting(ref reader);
    });

    PacketWriter cumulativeLists = new();
    cumulativeLists.WriteByte(9);
    cumulativeLists.WriteByte(9);
    cumulativeLists.WriteInt(2);
    cumulativeLists.WriteByte(1);
    cumulativeLists.WriteInt(ChatTextCodec.MaximumNbtListElements);
    cumulativeLists.WriteBytes(new byte[ChatTextCodec.MaximumNbtListElements]);
    cumulativeLists.WriteByte(1);
    cumulativeLists.WriteInt(ChatTextCodec.MaximumNbtListElements);
    True(2 + (2 * ChatTextCodec.MaximumNbtListElements) > ChatTextCodec.MaximumNbtCollectionElements,
        "The cumulative-list fixture no longer exceeds the global collection budget.");
    Throws<InvalidDataException>(() =>
    {
        PacketReader reader = new(cumulativeLists.ToArray());
        _ = ChatTextCodec.ReadAnonymousNbtFormatting(ref reader);
    });

    byte[] nestedChat = Convert.FromHexString(
        "0A080004746578740005526F6F742009000565787472610A00000002" +
        "0800047465787400066E65737465640009000565787472610A00000001" +
        "0800047465787400052063686174000000");
    PacketReader nestedReader = new(nestedChat);
    Equal("Root nested chat", ChatTextCodec.FromAnonymousNbt(ref nestedReader));
    Equal(0, nestedReader.Remaining);
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

    PacketWriter controlled = new();
    controlled.WriteVarInt(44);
    controlled.WriteUuid(sender);
    controlled.WriteVarInt(0);
    controlled.WriteBoolean(false);
    controlled.WriteString("hello\u001b[2J\u0007");
    Equal("<Steve> hello [2J ",
        PlayerChatDecoder.Decode(controlled.ToArray(), 773, id => id == sender ? "Steve" : null).Text);

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

Run("connection phase deadlines and nonblocking code-of-conduct", () =>
{
    VerifyConnectionPhasesAsync().GetAwaiter().GetResult();
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

Run("outbound dispatcher serializes payload state and prioritizes control", () =>
{
    VerifyOutboundDispatcherAsync().GetAwaiter().GetResult();
});

Run("event dispatcher isolates subscribers and bounds slow-consumer floods", () =>
{
    VerifyEventDispatcherAsync().GetAwaiter().GetResult();
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

static bool IsTerminalSafe(string text) =>
    !text.Any(value => char.IsControl(value) || value is '\u2028' or '\u2029');

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
    TaskCompletionSource releaseLatencyUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        playerInfo.WriteString("Disconnect\u001b[2J", 16);
        playerInfo.WriteVarInt(0);
        playerInfo.WriteVarInt(1);
        playerInfo.WriteVarInt(0);
        playerInfo.WriteBoolean(false);
        byte[] playerInfoBody = playerInfo.ToArray();
        PacketWriter playerInfoFrame = new();
        playerInfoFrame.WriteVarInt(playerInfoBody.Length);
        playerInfoFrame.WriteBytes(playerInfoBody);
        await stream.WriteAsync(playerInfoFrame.ToArray(), timeout.Token);

        await stream.FlushAsync(timeout.Token);
        await releaseLatencyUpdate.Task.WaitAsync(timeout.Token);
        PacketWriter latencyUpdate = new();
        latencyUpdate.WriteVarInt(0x38);
        latencyUpdate.WriteVarInt(2);
        latencyUpdate.WriteVarInt(1);
        latencyUpdate.WriteUuid(OfflineIdentity.CreateUuid("DisconnectTest"));
        latencyUpdate.WriteVarInt(42);
        PacketWriter latencyFrame = new();
        byte[] latencyBody = latencyUpdate.ToArray();
        latencyFrame.WriteVarInt(latencyBody.Length);
        latencyFrame.WriteBytes(latencyBody);
        await stream.WriteAsync(latencyFrame.ToArray(), timeout.Token);
        await stream.WriteAsync(new byte[] { 1, 0x7F }, timeout.Token);
        for (int packetId = 1000; packetId < 1300; packetId++)
            await WriteMinecraftPacketAsync(stream, packetId, null, timeout.Token);
        await stream.FlushAsync(timeout.Token);
        await releaseServer.Task.WaitAsync(timeout.Token);
    }, timeout.Token);

    try
    {
        ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("1.8");
        await using MinecraftConnection connection = new(
            "127.0.0.1",
            (ushort)port,
            "DisconnectTest",
            protocol,
            initialPingMilliseconds: 321);
        int metricNotifications = 0;
        connection.Log += _ => throw new InvalidOperationException("Injected log subscriber failure.");
        connection.StateChanged += _ =>
        {
            Thread.Sleep(50);
            throw new InvalidOperationException("Injected slow state subscriber failure.");
        };
        connection.MetricsChanged += _ => Interlocked.Increment(ref metricNotifications);
        await connection.ConnectAsync(timeout.Token);
        Equal(ConnectionState.Play, connection.State);
        await WaitUntilAsync(() => connection.Players.Count == 1, TimeSpan.FromSeconds(2));
        PlayerListEntry player = connection.Players.Single();
        Equal("Disconnect [2J", player.Name);
        Equal(0, player.PingMilliseconds);
        True(connection.Metrics.PingMilliseconds == 321,
            "The measured status RTT was not retained when the proxy reported zero latency.");
        releaseLatencyUpdate.TrySetResult();
        await WaitUntilAsync(() => connection.Metrics.PingMilliseconds == 42, TimeSpan.FromSeconds(2));
        True(connection.Metrics.PacketsReceived >= 3, "Received packet metrics were not incremented.");
        True(connection.Metrics.PacketsSent >= 3, "Sent packet metrics were not incremented.");
        True(connection.Metrics.BytesReceived > 0 && connection.Metrics.BytesSent > 0, "Wire-byte metrics were not incremented.");
        True(connection.Metrics.LastReceivedAt is not null, "Last receive activity was not recorded.");
        await WaitUntilAsync(
            () => connection.UnknownPacketStatistics.Any(item =>
                item.Key.EndsWith("0x7F", StringComparison.Ordinal) && item.Value == 1),
            TimeSpan.FromSeconds(2));
        True(connection.UnknownPacketStatistics.Any(item => item.Key.EndsWith("0x7F", StringComparison.Ordinal) && item.Value == 1),
            "The unexpected play packet was not recorded exactly once.");
        await WaitUntilAsync(() => connection.UnknownPacketOverflowCount > 0, TimeSpan.FromSeconds(2));
        True(connection.UnknownPacketStatistics.Count <= 256,
            "Unknown packet keys exceeded their hard bound while inspection was disabled.");
        True(connection.UnknownPacketOverflowCount > 0,
            "Discarded unknown packet IDs were not represented by an overflow counter.");
        await WaitUntilAsync(() => connection.Metrics.SubscriberFailures > 0, TimeSpan.FromSeconds(2));
        True(connection.TerminalException is null,
            "A slow or throwing event subscriber terminated the network connection.");
        await Task.Delay(300, timeout.Token);
        True(Volatile.Read(ref metricNotifications) <= 10,
            "Metrics notifications were not coalesced to a bounded update rate.");

        connection.Disconnect();
        connection.Disconnect();
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(ConnectionState.Disconnected, connection.State);
    }
    finally
    {
        releaseLatencyUpdate.TrySetResult();
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

    byte[] belowThreshold = CreateCompressionFrame(0, [0x01]);
    InboundPacket below = await ReadPacketAsync(belowThreshold, compressionThreshold: 2);
    Equal(1, below.Id);
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(
        CreateCompressionFrame(0, [0x01, 0x00]), compressionThreshold: 2));
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(
        CreateCompressionFrame(0, [0x01]), compressionThreshold: 0));

    byte[] exactClear = [0x01, 0x02, 0x03, 0x04];
    InboundPacket exact = await ReadPacketAsync(
        CreateCompressionFrame(exactClear.Length, Compress(exactClear)), compressionThreshold: 1);
    Equal(1, exact.Id);
    True(exact.Payload.AsSpan().SequenceEqual(new byte[] { 2, 3, 4 }),
        "Exactly sized decompression changed the packet.");
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(
        CreateCompressionFrame(exactClear.Length + 1, Compress(exactClear)), compressionThreshold: 1));
    byte[] truncatedDeflate = Compress(exactClear);
    Array.Resize(ref truncatedDeflate, Math.Max(1, truncatedDeflate.Length / 2));
    await ThrowsAsync<InvalidDataException>(() => ReadPacketAsync(
        CreateCompressionFrame(exactClear.Length, truncatedDeflate), compressionThreshold: 1));

    await using MemoryStream encryptedSource = new();
    await using MinecraftPacketStream encryptedPackets = new(encryptedSource);
    encryptedPackets.EnableEncryption(new byte[16]);
    await ThrowsAsync<EndOfStreamException>(async () => { _ = await encryptedPackets.ReadAsync(CancellationToken.None); });
}

static async Task VerifyStatusPingAsync()
{
    await VerifyStatusServerAsync(closeBeforePong: false);
    await VerifyStatusServerAsync(closeBeforePong: true);
}

static async Task VerifyStatusServerAsync(bool closeBeforePong)
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
    Task server = Task.Run(async () =>
    {
        using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
        await using MinecraftPacketStream packets = new(peer.GetStream());
        _ = await packets.ReadAsync(timeout.Token);
        _ = await packets.ReadAsync(timeout.Token);
        await packets.WriteAsync(0, writer => writer.WriteString(
            "{\"version\":{\"name\":\"Local\",\"protocol\":776}}"), timeout.Token);
        if (closeBeforePong) return;
        InboundPacket ping = await packets.ReadAsync(timeout.Token);
        Equal(1, ping.Id);
        PacketReader pingReader = new(ping.Payload);
        long nonce = pingReader.ReadLong();
        await packets.WriteAsync(1, writer => writer.WriteLong(nonce), timeout.Token);
    }, timeout.Token);
    MinecraftServerStatus status = await MinecraftServerDiscovery.QueryAsync(
        "127.0.0.1", port, TimeSpan.FromSeconds(3), timeout.Token);
    Equal(776, status.ProtocolVersion);
    True(status.PingMilliseconds >= 0, "Status latency was negative.");
    await server.WaitAsync(TimeSpan.FromSeconds(2));
}

static async Task VerifyDnsTransportAsync()
{
    const ushort transaction = 0x4F59;
    const string question = "_minecraft._tcp.example.org";
    byte[] query = PortableSrvResolver.BuildQuery(question, transaction);

    using (TcpListener tcp = new(IPAddress.Loopback, 0))
    {
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        using UdpClient udp = new(port, AddressFamily.InterNetwork);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        Task udpServer = Task.Run(async () =>
        {
            UdpReceiveResult request = await udp.ReceiveAsync(timeout.Token);
            byte[] truncated = CreateSrvResponse(transaction, "srv.example.org", 25570);
            truncated[2] |= 0x02;
            await udp.SendAsync(truncated, request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        Task tcpServer = Task.Run(async () =>
        {
            using TcpClient peer = await tcp.AcceptTcpClientAsync(timeout.Token);
            NetworkStream stream = peer.GetStream();
            byte[] lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, timeout.Token);
            int queryLength = (lengthBytes[0] << 8) | lengthBytes[1];
            byte[] receivedQuery = new byte[queryLength];
            await stream.ReadExactlyAsync(receivedQuery, timeout.Token);
            True(receivedQuery.AsSpan().SequenceEqual(query), "DNS-over-TCP changed the original query.");
            byte[] response = CreateSrvResponse(transaction, "srv.example.org", 25570);
            await stream.WriteAsync(new byte[] { (byte)(response.Length >> 8), (byte)response.Length }, timeout.Token);
            await stream.WriteAsync(response, timeout.Token);
        }, timeout.Token);
        IReadOnlyList<PortableSrvEndpoint> records = await PortableSrvResolver.QueryResolverAsync(
            IPAddress.Loopback, port, question, transaction, query, timeout.Token);
        Equal(1, records.Count);
        Equal("srv.example.org", records[0].Target);
        await Task.WhenAll(udpServer, tcpServer);
    }

    using (UdpClient expectedSource = new(0, AddressFamily.InterNetwork))
    using (UdpClient wrongSource = new(0, AddressFamily.InterNetwork))
    using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3)))
    {
        int expectedPort = ((IPEndPoint)expectedSource.Client.LocalEndPoint!).Port;
        Task responder = Task.Run(async () =>
        {
            UdpReceiveResult request = await expectedSource.ReceiveAsync(timeout.Token);
            byte[] response = CreateSrvResponse(transaction, "srv.example.org", 25570);
            await wrongSource.SendAsync(response, request.RemoteEndPoint, timeout.Token);
        }, timeout.Token);
        await ThrowsAsync<InvalidDataException>(() => PortableSrvResolver.QueryResolverAsync(
            IPAddress.Loopback, expectedPort, question, transaction, query, timeout.Token));
        await responder;
    }

    using (UdpClient silent = new(0, AddressFamily.InterNetwork))
    using (CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150)))
    {
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
        await ThrowsAsync<OperationCanceledException>(() => PortableSrvResolver.QueryResolverAsync(
            IPAddress.Loopback, port, question, transaction, query, cancellation.Token));
    }
}

static async Task VerifyConnectionPhasesAsync()
{
    await VerifyLoginDeadlineAsync(sendCompression: false);
    await VerifyLoginDeadlineAsync(sendCompression: true);
    await VerifyConfigurationDeadlineAsync(sendKeepAlives: false);
    await VerifyConfigurationDeadlineAsync(sendKeepAlives: true);
    await VerifyUserCancellationAsync(configuration: false);
    await VerifyUserCancellationAsync(configuration: true);
    await VerifyConfigurationCompletesAsync();
    await VerifyCodeOfConductDoesNotBlockAsync();
    await VerifyCodeOfConductTimeoutAsync();
    await VerifyCodeOfConductDuplicateAsync();
    await VerifyCodeOfConductCancellationAsync();
    await VerifyCodeOfConductOversizeAsync();
}

static async Task VerifyLoginDeadlineAsync(bool sendCompression)
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("1.8.8");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        if (sendCompression)
        {
            int compressionId = protocol.PacketIds.LoginClientbound["compress"];
            await WriteMinecraftPacketAsync(stream, compressionId,
                writer => writer.WriteVarInt(32), cancellationToken);
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("DeadlineTest"), protocol, policy);
        ConnectionPhaseTimeoutException exception = await CaptureThrowsAsync<ConnectionPhaseTimeoutException>(
            () => connection.ConnectAsync(cancellationToken));
        Equal(ConnectionPhase.Login, exception.Phase);
        True(exception.Message.Contains("login", StringComparison.OrdinalIgnoreCase),
            "The login timeout did not identify its phase.");
    });
}

static async Task VerifyConfigurationDeadlineAsync(bool sendKeepAlives)
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        if (sendKeepAlives)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(40, cancellationToken);
                await WriteMinecraftPacketAsync(stream,
                    protocol.PacketIds.ConfigurationClientbound["keep_alive"],
                    writer => writer.WriteLong(42), cancellationToken);
            }
        }
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConfigTimeout"), protocol, policy);
        ConnectionPhaseTimeoutException exception = await CaptureThrowsAsync<ConnectionPhaseTimeoutException>(
            () => connection.ConnectAsync(cancellationToken));
        Equal(ConnectionPhase.Configuration, exception.Phase);
        True(exception.Message.Contains("configuration", StringComparison.OrdinalIgnoreCase),
            "The configuration timeout did not identify its phase.");
    });
}

static async Task VerifyUserCancellationAsync(bool configuration)
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve(configuration ? "26.2" : "1.8.8");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        if (configuration)
            await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
                cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("CancelTest"), protocol, policy);
        using CancellationTokenSource userCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task connect = connection.ConnectAsync(userCancellation.Token);
        await WaitUntilAsync(() => connection.State ==
            (configuration ? ConnectionState.Configuration : ConnectionState.Login), TimeSpan.FromSeconds(2));
        userCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => connect);
        True(connection.TerminalException is not ConnectionPhaseTimeoutException,
            "User cancellation was misclassified as a timeout.");
    });
}

static async Task VerifyConfigurationCompletesAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await Task.Delay(40, cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["finish_configuration"], null, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("PlayOnTime"), protocol, policy);
        await connection.ConnectAsync(cancellationToken);
        Equal(ConnectionState.Play, connection.State);
        await Task.Delay(350, cancellationToken);
        Equal(ConnectionState.Play, connection.State);
        True(connection.TerminalException is null, "A completed phase left a timeout watchdog active.");
        connection.Disconnect();
    });
}

static async Task VerifyCodeOfConductDoesNotBlockAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    TaskCompletionSource sendConfigurationPackets = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<bool> approval = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await sendConfigurationPackets.Task.WaitAsync(cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["code_of_conduct"],
            writer => writer.WriteString("Rules\u001b[2J\u2028safe"), cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["keep_alive"],
            writer => writer.WriteLong(123456), cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["finish_configuration"], null, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        string? displayed = null;
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConductTest"), protocol, policy);
        connection.CodeOfConductApproval = (contents, _) =>
        {
            displayed = contents;
            return approval.Task;
        };
        Task connect = connection.ConnectAsync(cancellationToken);
        await WaitUntilAsync(() => connection.State == ConnectionState.Configuration, TimeSpan.FromSeconds(2));
        await Task.Delay(50, cancellationToken);
        long sentBefore = connection.Metrics.PacketsSent;
        sendConfigurationPackets.TrySetResult();
        await WaitUntilAsync(() => displayed is not null, TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => connection.Metrics.PacketsSent > sentBefore, TimeSpan.FromSeconds(2));
        True(displayed is not null && IsTerminalSafe(displayed),
            "The displayed code of conduct retained unsafe terminal controls.");
        True(!connect.IsCompleted, "Configuration finished before the pending conduct decision was coordinated.");
        approval.TrySetResult(true);
        await connect.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(ConnectionState.Play, connection.State);
        connection.Disconnect();
    });
}

static async Task VerifyCodeOfConductTimeoutAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await Task.Delay(40, cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["code_of_conduct"],
            writer => writer.WriteString("Bounded decision"), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConductLimit"), protocol, policy);
        connection.CodeOfConductApproval = (_, _) => new TaskCompletionSource<bool>().Task;
        ConnectionPhaseTimeoutException exception = await CaptureThrowsAsync<ConnectionPhaseTimeoutException>(
            () => connection.ConnectAsync(cancellationToken));
        Equal(ConnectionPhase.CodeOfConductDecision, exception.Phase);
    });
}

static async Task VerifyCodeOfConductDuplicateAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await Task.Delay(40, cancellationToken);
        for (int index = 0; index < 2; index++)
            await WriteMinecraftPacketAsync(stream,
                protocol.PacketIds.ConfigurationClientbound["code_of_conduct"],
                writer => writer.WriteString("One active request only"), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConductDupe"), protocol, policy);
        connection.CodeOfConductApproval = (_, _) => new TaskCompletionSource<bool>().Task;
        await CaptureThrowsAsync<InvalidDataException>(() => connection.ConnectAsync(cancellationToken));
    });
}

static async Task VerifyCodeOfConductCancellationAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    TaskCompletionSource promptOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await Task.Delay(40, cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["code_of_conduct"],
            writer => writer.WriteString("Cancel this decision"), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConductCancel"), protocol, policy);
        connection.CodeOfConductApproval = (_, _) =>
        {
            promptOpened.TrySetResult();
            return new TaskCompletionSource<bool>().Task;
        };
        using CancellationTokenSource userCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task connect = connection.ConnectAsync(userCancellation.Token);
        await promptOpened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        userCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => connect);
        True(connection.TerminalException is not ConnectionPhaseTimeoutException,
            "Cancelling an open conduct dialog was misclassified as a timeout.");
    });
}

static async Task VerifyCodeOfConductOversizeAsync()
{
    ProtocolDefinition protocol = ProtocolCatalog.LoadEmbedded().Resolve("26.2");
    await RunFakeServerAsync(async (stream, cancellationToken) =>
    {
        await WriteMinecraftPacketAsync(stream, protocol.PacketIds.LoginClientbound["success"], null,
            cancellationToken);
        await Task.Delay(40, cancellationToken);
        await WriteMinecraftPacketAsync(stream,
            protocol.PacketIds.ConfigurationClientbound["code_of_conduct"],
            writer => writer.WriteString(new string('R', 16_385)), cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }, async (port, cancellationToken) =>
    {
        ConnectionDeadlinePolicy policy = new(
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        await using MinecraftConnection connection = new(
            ServerAddress.Parse($"127.0.0.1:{port}"), MinecraftIdentity.Offline("ConductSize"), protocol, policy);
        await CaptureThrowsAsync<InvalidDataException>(() => connection.ConnectAsync(cancellationToken));
    });
}

static async Task RunFakeServerAsync(
    Func<NetworkStream, CancellationToken, Task> serverHandler,
    Func<int, CancellationToken, Task> clientHandler)
{
    using TcpListener listener = new(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
    Task server = Task.Run(async () =>
    {
        try
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(timeout.Token);
            await serverHandler(peer.GetStream(), timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
        }
    }, timeout.Token);
    try
    {
        await clientHandler(port, timeout.Token);
    }
    finally
    {
        timeout.Cancel();
        listener.Stop();
        await server.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

static async Task WriteMinecraftPacketAsync(
    NetworkStream stream,
    int packetId,
    Action<PacketWriter>? payload,
    CancellationToken cancellationToken)
{
    PacketWriter body = new();
    body.WriteVarInt(packetId);
    payload?.Invoke(body);
    PacketWriter frame = new();
    frame.WriteVarInt(body.Length);
    frame.WriteBytes(body.ToArray());
    await stream.WriteAsync(frame.ToArray(), cancellationToken);
    await stream.FlushAsync(cancellationToken);
}

static async Task<TException> CaptureThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException exception) { return exception; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static byte[] CreateCompressionFrame(int uncompressedLength, byte[] bodyBytes)
{
    PacketWriter body = new();
    body.WriteVarInt(uncompressedLength);
    body.WriteBytes(bodyBytes);
    PacketWriter frame = new();
    frame.WriteVarInt(body.Length);
    frame.WriteBytes(body.ToArray());
    return frame.ToArray();
}

static async Task VerifyFragmentedFrameAsync()
{
    await using OneByteReadStream stream = new(new byte[] { 3, 0x2A, 0x01, 0x02 });
    await using MinecraftPacketStream packets = new(stream);
    InboundPacket packet = await packets.ReadAsync(CancellationToken.None);
    Equal(0x2A, packet.Id);
    True(packet.Payload.AsSpan().SequenceEqual(new byte[] { 1, 2 }), "Fragmented payload changed.");
}

static async Task VerifyOutboundDispatcherAsync()
{
    await using MemoryStream transport = new();
    await using MinecraftPacketStream packets = new(transport);
    int nextSequence = 0;
    int activeBuilders = 0;
    int maximumBuilders = 0;
    Task[] sends = Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
    {
        await packets.WriteAsync(0x55, writer =>
        {
            int active = Interlocked.Increment(ref activeBuilders);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumBuilders);
                if (observed >= active) break;
            } while (Interlocked.CompareExchange(ref maximumBuilders, active, observed) != observed);
            int sequence = Interlocked.Increment(ref nextSequence);
            Thread.SpinWait(10_000);
            writer.WriteVarInt(sequence);
            Interlocked.Decrement(ref activeBuilders);
        }, CancellationToken.None, OutboundPacketPriority.Normal);
    })).ToArray();
    await Task.WhenAll(sends).WaitAsync(TimeSpan.FromSeconds(5));
    Equal(1, maximumBuilders);
    IReadOnlyList<(int Id, byte[] Payload)> frames = DecodeFrames(transport.ToArray());
    Equal(100, frames.Count);
    for (int index = 0; index < frames.Count; index++)
    {
        Equal(0x55, frames[index].Id);
        PacketReader payload = new(frames[index].Payload);
        Equal(index + 1, payload.ReadVarInt());
    }

    await using MemoryStream priorityTransport = new();
    await using MinecraftPacketStream priorityPackets = new(priorityTransport);
    using ManualResetEventSlim firstBuilderStarted = new();
    using ManualResetEventSlim releaseFirstBuilder = new();
    Task first = Task.Run(async () => await priorityPackets.WriteAsync(0x10, _ =>
    {
        firstBuilderStarted.Set();
        releaseFirstBuilder.Wait(TimeSpan.FromSeconds(2));
    }, CancellationToken.None, OutboundPacketPriority.Normal));
    True(firstBuilderStarted.Wait(TimeSpan.FromSeconds(2)), "The first normal packet did not enter the writer.");
    Task[] backlog = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        await priorityPackets.WriteAsync(0x11, null, CancellationToken.None, OutboundPacketPriority.Normal))).ToArray();
    Task critical = Task.Run(async () =>
        await priorityPackets.WriteAsync(0x7E, null, CancellationToken.None, OutboundPacketPriority.Critical));
    releaseFirstBuilder.Set();
    await Task.WhenAll([first, critical, .. backlog]).WaitAsync(TimeSpan.FromSeconds(5));
    IReadOnlyList<(int Id, byte[] Payload)> priorityFrames = DecodeFrames(priorityTransport.ToArray());
    Equal(0x10, priorityFrames[0].Id);
    True(priorityFrames.Take(3).Any(frame => frame.Id == 0x7E),
        "A critical control packet remained behind the normal backlog.");
}

static IReadOnlyList<(int Id, byte[] Payload)> DecodeFrames(byte[] bytes)
{
    List<(int Id, byte[] Payload)> frames = [];
    PacketReader input = new(bytes);
    while (input.Remaining > 0)
    {
        int frameLength = input.ReadVarInt();
        if (frameLength <= 0 || frameLength > input.Remaining)
            throw new InvalidDataException("The outbound test frame length is invalid.");
        PacketReader frame = new(input.ReadBytes(frameLength));
        int packetId = frame.ReadVarInt();
        frames.Add((packetId, frame.ReadRemaining().ToArray()));
    }
    return frames;
}

static async Task VerifyEventDispatcherAsync()
{
    int failures = 0;
    await using ProtocolEventDispatcher dispatcher = new(_ => Interlocked.Increment(ref failures));
    using ManualResetEventSlim blocked = new();
    using ManualResetEventSlim release = new();
    dispatcher.Publish(() =>
    {
        blocked.Set();
        release.Wait(TimeSpan.FromSeconds(2));
    });
    True(blocked.Wait(TimeSpan.FromSeconds(2)), "The event worker did not start its blocked subscriber.");
    Stopwatch publishTime = Stopwatch.StartNew();
    for (int index = 0; index < ProtocolEventDispatcher.NormalCapacity * 3; index++)
        dispatcher.Publish(static () => { });
    TaskCompletionSource criticalDelivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    dispatcher.Publish(() => criticalDelivered.TrySetResult(), isCritical: true);
    dispatcher.Publish(() => throw new InvalidOperationException("Injected event subscriber failure."),
        isCritical: true);
    publishTime.Stop();
    True(publishTime.Elapsed < TimeSpan.FromSeconds(1),
        "A slow subscriber applied backpressure to the network-facing publisher.");
    True(dispatcher.Dropped > 0, "A saturated event queue did not report drops.");
    release.Set();
    await criticalDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await WaitUntilAsync(() => Volatile.Read(ref failures) > 0, TimeSpan.FromSeconds(2));
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

static byte[] CreateSrvResponse(ushort transaction, string target, ushort port)
{
    byte[] question = PortableSrvResolver.BuildQuery("_minecraft._tcp.example.org", transaction);
    List<byte> response = question.ToList();
    response[2] = 0x81;
    response[3] = 0x80;
    response[6] = 0;
    response[7] = 1;
    response.AddRange([0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C]);
    List<byte> record = [];
    WriteUInt16(record, 0);
    WriteUInt16(record, 10);
    WriteUInt16(record, port);
    if (target == ".")
    {
        record.Add(0);
    }
    else foreach (string label in target.Split('.'))
    {
        byte[] bytes = Encoding.ASCII.GetBytes(label);
        record.Add((byte)bytes.Length);
        record.AddRange(bytes);
    }
    if (target != ".") record.Add(0);
    WriteUInt16(response, checked((ushort)record.Count));
    response.AddRange(record);
    return response.ToArray();
}

static void WriteUInt16(List<byte> output, ushort value)
{
    output.Add((byte)(value >> 8));
    output.Add((byte)value);
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
