using System.Text.Json.Serialization;

namespace OeXYZ.ConsoleClient;

public enum AccountKind
{
    Offline,
    Microsoft
}

public sealed record AccountProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter<AccountKind>))]
    public AccountKind Kind { get; init; } = AccountKind.Microsoft;
    public string LoginHint { get; init; } = string.Empty;
    public string? AccountIdentifier { get; set; }
}

public sealed record ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int CustomPort { get; init; }
    public string Version { get; init; } = "auto";
    public bool AntiAfk { get; init; } = true;
    public bool AutoReconnect { get; init; } = true;
    public bool AutoRespawn { get; init; } = true;
}

public sealed record ProfileDocument
{
    public int FormatVersion { get; init; } = 1;
    public List<AccountProfile> Accounts { get; init; } = [];
    public List<ServerProfile> Servers { get; init; } = [];
}
