using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OeXYZ.Protocol;

public sealed class ProtocolCatalog
{
    private readonly IReadOnlyList<ProtocolDefinition> versions;

    private ProtocolCatalog(IReadOnlyList<ProtocolDefinition> versions)
    {
        this.versions = versions;
    }

    public IReadOnlyList<ProtocolDefinition> Versions => versions;

    public static ProtocolCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(ProtocolCatalog).Assembly;
        string resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("protocol-catalog.json", StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The embedded protocol catalog is missing.");
        CatalogDocument document = JsonSerializer.Deserialize<CatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("The embedded protocol catalog is invalid.");
        return new ProtocolCatalog(document.Versions);
    }

    public ProtocolDefinition Resolve(string minecraftVersion)
    {
        return versions.LastOrDefault(version =>
                   string.Equals(version.MinecraftVersion, minecraftVersion, StringComparison.OrdinalIgnoreCase))
               ?? throw new NotSupportedException($"Minecraft {minecraftVersion} is not in the protocol catalog.");
    }

    public ProtocolDefinition Resolve(int protocolVersion)
    {
        return versions.LastOrDefault(version => version.ProtocolVersion == protocolVersion)
               ?? throw new NotSupportedException($"Minecraft protocol {protocolVersion} is not in the protocol catalog.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    static ProtocolCatalog()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private sealed record CatalogDocument(List<ProtocolDefinition> Versions);
}

public sealed record ProtocolDefinition(
    string MinecraftVersion,
    int ProtocolVersion,
    string SchemaVersion,
    bool HasConfiguration,
    ResourcePackRequestLayout ResourcePackRequestLayout,
    ResourcePackResponseLayout ResourcePackResponseLayout,
    ProtocolCapabilities Capabilities,
    PacketIdGroups PacketIds);

public sealed record ProtocolCapabilities(
    PositionPacketLayout PositionLayout,
    ClientSettingsPacketLayout ClientSettingsLayout,
    ChatPacketLayout ChatLayout,
    PlayerInfoPacketLayout PlayerInfoLayout,
    bool Cookies,
    bool Transfer,
    bool Configuration,
    bool CodeOfConduct);

public enum PositionPacketLayout
{
    None,
    LegacyCoordinates,
    TeleportId,
    TeleportIdWithDismount,
    RelativeVelocity
}

public enum ClientSettingsPacketLayout
{
    None,
    LegacyFiveFields,
    MainHand,
    DisableTextFiltering,
    EnableTextFilteringAndListing,
    ParticleStatus
}

public enum ChatPacketLayout
{
    Legacy,
    Signed,
    SignedSession
}

public enum PlayerInfoPacketLayout
{
    None,
    LegacyAction,
    ModernBitSet
}

public enum ResourcePackRequestLayout
{
    None,
    UrlHash,
    UrlHashForcedPrompt,
    UuidUrlHashForcedPrompt
}

public enum ResourcePackResponseLayout
{
    None,
    HashAndStatus,
    StatusOnly,
    UuidAndStatus
}

public enum ResourcePackResponseStatus
{
    SuccessfullyLoaded = 0,
    Declined = 1,
    FailedDownload = 2,
    Accepted = 3
}

internal sealed record ResourcePackRequest(Guid? PackId, string Hash, bool Forced);

public sealed record PacketIdGroups(
    Dictionary<string, int> LoginClientbound,
    Dictionary<string, int> LoginServerbound,
    Dictionary<string, int> ConfigurationClientbound,
    Dictionary<string, int> ConfigurationServerbound,
    Dictionary<string, int> PlayClientbound,
    Dictionary<string, int> PlayServerbound);
