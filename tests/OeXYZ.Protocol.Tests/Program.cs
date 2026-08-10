using OeXYZ.Protocol;

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
