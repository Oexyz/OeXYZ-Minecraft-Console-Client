using System.Text.Json.Serialization;

namespace OeXYZ.Core;

public enum ProxyKind { Direct, Socks5, HttpConnect }
public enum ProxyDnsMode { Local, Proxy }

public sealed record ProxyProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter<ProxyKind>))]
    public ProxyKind Kind { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<ProxyDnsMode>))]
    public ProxyDnsMode DnsMode { get; init; } = ProxyDnsMode.Proxy;
    public string Username { get; init; } = string.Empty;
    public string? SecretReference { get; init; }
}

public sealed record ServerEndpointProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Address { get; init; } = string.Empty;
    public int CustomPort { get; init; }
    public int Priority { get; init; }
    public int FailureThreshold { get; init; } = 2;
    public int CooldownSeconds { get; init; } = 60;
}

public enum AutomationTriggerKind
{
    Connected, Reconnected, Disconnected, Death, ChatContains, Mention, PrivateMessage,
    PlayerJoined, PlayerLeft, Interval
}

public enum AutomationActionKind { SendChat, SendCommand, Respawn, Notify, Stop, Reconnect }

public sealed record AutomationActionProfile
{
    [JsonConverter(typeof(JsonStringEnumConverter<AutomationActionKind>))]
    public AutomationActionKind Kind { get; init; }
    public string Value { get; init; } = string.Empty;
}

public sealed record AutomationRuleProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter<AutomationTriggerKind>))]
    public AutomationTriggerKind Trigger { get; init; }
    public string Pattern { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public int CooldownSeconds { get; init; } = 10;
    public int MaximumRunsPerHour { get; init; } = 60;
    public List<AutomationActionProfile> Actions { get; init; } = [];
}
