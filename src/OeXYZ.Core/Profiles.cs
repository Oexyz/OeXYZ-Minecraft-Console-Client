using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OeXYZ.Core;

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
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record ServerProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public int CustomPort { get; init; }
    public string Version { get; init; } = "auto";
    public string Group { get; init; } = string.Empty;
    public bool AntiAfk { get; init; } = true;
    public int AntiAfkIntervalSeconds { get; init; } = 45;
    public int AntiAfkJitterSeconds { get; init; } = 5;
    public float AntiAfkYawDegrees { get; init; } = 7.5F;
    public bool AutoReconnect { get; init; } = true;
    public int ReconnectInitialDelaySeconds { get; init; } = 5;
    public int ReconnectMaximumDelaySeconds { get; init; } = 60;
    public int ReconnectMaximumAttempts { get; init; }
    public int ReconnectStableResetSeconds { get; init; } = 120;
    public int StaleConnectionTimeoutSeconds { get; init; } = 120;
    public bool AutoRespawn { get; init; } = true;
    public List<string> QuickCommands { get; init; } = [];
    public bool StartupCommandsEnabled { get; init; }
    public int StartupCommandDelayMilliseconds { get; init; } = 1000;
    public List<string> StartupCommands { get; init; } = [];
    public Guid? ProxyProfileId { get; init; }
    public List<ServerEndpointProfile> Endpoints { get; init; } = [];
    public List<AutomationRuleProfile> Automations { get; init; } = [];
    public bool AllowServerTransfer { get; init; }
    public List<string> MentionPatterns { get; init; } = [];
    public List<string> PrivateMessagePatterns { get; init; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record ApplicationSettings
{
    public bool MinimizeToTray { get; init; }
    public bool KeepRunningOnClose { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public bool NotifyDisconnect { get; init; } = true;
    public bool NotifyReconnect { get; init; } = true;
    public bool NotifyDeath { get; init; } = true;
    public bool NotifyMention { get; init; } = true;
    public bool NotifyPrivateMessage { get; init; } = true;
    public bool RestoreSessionsOnStartup { get; init; }
    public bool ProtocolInspectorEnabled { get; init; }
    public int LogRetentionDays { get; init; } = 90;
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record SessionBookmark
{
    public Guid AccountId { get; init; }
    public Guid ServerId { get; init; }
}

public sealed record ProfileDocument
{
    public const int CurrentFormatVersion = 5;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public long Revision { get; set; }
    public List<AccountProfile> Accounts { get; init; } = [];
    public List<ServerProfile> Servers { get; init; } = [];
    public List<ProxyProfile> ProxyProfiles { get; init; } = [];
    public ApplicationSettings Settings { get; init; } = new();
    public List<SessionBookmark> ManagedSessions { get; init; } = [];
    public List<SessionBookmark> LastSessions { get; init; } = [];
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }

    public ProfileDocument Normalize()
    {
        if (FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException($"Profile format {FormatVersion} is newer than this OeXYZ build supports.");
        if (Revision < 0)
            throw new InvalidDataException("The profile revision cannot be negative.");

        List<AccountProfile> accounts = RequireItems(Accounts, "accounts")
            .Select(NormalizeAccount)
            .ToList();
        List<ServerProfile> servers = RequireItems(Servers, "servers")
            .Select(NormalizeServer)
            .ToList();
        List<ProxyProfile> proxies = RequireItems(ProxyProfiles, "proxy profiles")
            .Select(NormalizeProxy)
            .ToList();
        EnsureUniqueProfiles(
            accounts.Select(account => (account.Id, account.DisplayName)),
            "account");
        EnsureUniqueProfiles(
            servers.Select(server => (server.Id, server.DisplayName)),
            "server");
        EnsureUniqueProfiles(proxies.Select(proxy => (proxy.Id, proxy.DisplayName)), "proxy");
        HashSet<Guid> proxyIds = proxies.Select(proxy => proxy.Id).ToHashSet();
        foreach (ServerProfile server in servers)
            if (server.ProxyProfileId is Guid proxyId && !proxyIds.Contains(proxyId))
                throw new InvalidDataException($"Server profile '{server.DisplayName}' references a missing proxy.");
        HashSet<Guid> accountIds = accounts.Select(account => account.Id).ToHashSet();
        HashSet<Guid> serverIds = servers.Select(server => server.Id).ToHashSet();
        List<SessionBookmark> managedSessions = NormalizeSessions(ManagedSessions, accountIds, serverIds);
        List<SessionBookmark> lastSessions = NormalizeSessions(LastSessions, accountIds, serverIds);

        return this with
        {
            FormatVersion = CurrentFormatVersion,
            Accounts = accounts,
            Servers = servers,
            ProxyProfiles = proxies,
            Settings = NormalizeSettings(Settings ?? new ApplicationSettings()),
            ManagedSessions = managedSessions,
            LastSessions = lastSessions
        };
    }

    private static AccountProfile NormalizeAccount(AccountProfile account)
    {
        if (account.Id == Guid.Empty)
            throw new InvalidDataException("Account profile IDs cannot be empty.");
        if (!Enum.IsDefined(account.Kind))
            throw new InvalidDataException($"Account profile '{account.DisplayName}' has an unknown account kind.");
        return account with
        {
            DisplayName = ProfileRules.NormalizeProfileName(account.DisplayName, "account"),
            LoginHint = account.LoginHint?.Trim() ?? string.Empty
        };
    }

    private static List<SessionBookmark> NormalizeSessions(
        IEnumerable<SessionBookmark>? sessions,
        HashSet<Guid> accountIds,
        HashSet<Guid> serverIds)
    {
        List<SessionBookmark> items = RequireItems(sessions, "session bookmarks");
        return items
            .Where(item => accountIds.Contains(item.AccountId) && serverIds.Contains(item.ServerId))
            .Distinct()
            .ToList();
    }

    private static ServerProfile NormalizeServer(ServerProfile server)
    {
        if (server.Id == Guid.Empty)
            throw new InvalidDataException("Server profile IDs cannot be empty.");
        if (server.CustomPort is < 0 or > 65535)
            throw new InvalidDataException(
                $"Server profile '{server.DisplayName}' has a custom port outside 0-65535.");
        int initial = Math.Clamp(server.ReconnectInitialDelaySeconds, 1, 300);
        int maximum = Math.Clamp(server.ReconnectMaximumDelaySeconds, initial, 3600);
        return server with
        {
            DisplayName = ProfileRules.NormalizeProfileName(server.DisplayName, "server"),
            Address = server.Address?.Trim() ?? string.Empty,
            Version = string.IsNullOrWhiteSpace(server.Version) ? "auto" : server.Version.Trim(),
            Group = server.Group?.Trim() ?? string.Empty,
            AntiAfkIntervalSeconds = Math.Clamp(server.AntiAfkIntervalSeconds, 10, 3600),
            AntiAfkJitterSeconds = Math.Clamp(server.AntiAfkJitterSeconds, 0, 300),
            AntiAfkYawDegrees = Math.Clamp(server.AntiAfkYawDegrees, 0.5F, 45F),
            ReconnectInitialDelaySeconds = initial,
            ReconnectMaximumDelaySeconds = maximum,
            ReconnectMaximumAttempts = Math.Max(0, server.ReconnectMaximumAttempts),
            ReconnectStableResetSeconds = Math.Clamp(server.ReconnectStableResetSeconds, 30, 3600),
            StaleConnectionTimeoutSeconds = Math.Clamp(server.StaleConnectionTimeoutSeconds, 60, 900),
            StartupCommandDelayMilliseconds = Math.Clamp(server.StartupCommandDelayMilliseconds, 500, 30_000),
            QuickCommands = SanitizeCommands(server.QuickCommands, 12),
            StartupCommands = SanitizeCommands(server.StartupCommands, 8)
                .Where(command => !SensitiveDataRedactor.IsSensitiveCommand(command))
                .ToList(),
            Endpoints = NormalizeEndpoints(server),
            Automations = NormalizeAutomations(server.Automations),
            MentionPatterns = NormalizePatterns(server.MentionPatterns),
            PrivateMessagePatterns = NormalizePatterns(server.PrivateMessagePatterns)
        };
    }

    private static ProxyProfile NormalizeProxy(ProxyProfile proxy)
    {
        if (proxy.Id == Guid.Empty || !Enum.IsDefined(proxy.Kind) || !Enum.IsDefined(proxy.DnsMode))
            throw new InvalidDataException("A proxy profile contains an invalid ID or kind.");
        if (proxy.Kind != ProxyKind.Direct && (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535))
            throw new InvalidDataException($"Proxy profile '{proxy.DisplayName}' has an invalid endpoint.");
        return proxy with
        {
            DisplayName = ProfileRules.NormalizeProfileName(proxy.DisplayName, "proxy"),
            Host = proxy.Host.Trim(),
            Username = proxy.Username.Trim(),
            SecretReference = string.IsNullOrWhiteSpace(proxy.SecretReference) ? null : proxy.SecretReference.Trim()
        };
    }

    private static List<ServerEndpointProfile> NormalizeEndpoints(ServerProfile server)
    {
        List<ServerEndpointProfile> endpoints = RequireItems(server.Endpoints, "failover endpoints");
        if (endpoints.Count == 0)
            endpoints.Add(new ServerEndpointProfile { Address = server.Address, CustomPort = server.CustomPort });
        if (endpoints.Count > 8) throw new InvalidDataException("A server profile may contain at most 8 endpoints.");
        foreach (ServerEndpointProfile endpoint in endpoints)
        {
            if (endpoint.Id == Guid.Empty || string.IsNullOrWhiteSpace(endpoint.Address) ||
                endpoint.CustomPort is < 0 or > 65535)
                throw new InvalidDataException("A failover endpoint is invalid.");
        }
        return endpoints.Select(endpoint => endpoint with
        {
            Address = endpoint.Address.Trim(),
            Priority = Math.Clamp(endpoint.Priority, 0, 1000),
            FailureThreshold = Math.Clamp(endpoint.FailureThreshold, 1, 20),
            CooldownSeconds = Math.Clamp(endpoint.CooldownSeconds, 10, 3600)
        }).OrderBy(endpoint => endpoint.Priority).ToList();
    }

    private static List<AutomationRuleProfile> NormalizeAutomations(IEnumerable<AutomationRuleProfile>? source)
    {
        List<AutomationRuleProfile> rules = RequireItems(source, "automation rules");
        if (rules.Count > 32) throw new InvalidDataException("A server profile may contain at most 32 automation rules.");
        return rules.Select(rule =>
        {
            string pattern = rule.Pattern?.Trim() ?? string.Empty;
            string name = ProfileRules.NormalizeProfileName(rule.Name, "automation rule");
            if (rule.Id == Guid.Empty || !Enum.IsDefined(rule.Trigger) || pattern.Length > 256 ||
                ContainsSensitiveAutomationText(name) || ContainsSensitiveAutomationText(pattern))
                throw new InvalidDataException("An automation rule is invalid.");
            if (rule.UseRegex)
            {
                try
                {
                    _ = new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException($"Automation rule '{rule.Name}' contains an invalid regex.", exception);
                }
            }
            List<AutomationActionProfile> actions = RequireItems(rule.Actions, "automation actions");
            if (actions.Count is < 1 or > 4 || actions.Any(action =>
                    !Enum.IsDefined(action.Kind) || (action.Value?.Length ?? 0) > 256 ||
                    SensitiveDataRedactor.IsSensitiveCommand(action.Value ?? string.Empty) ||
                    ContainsSensitiveAutomationText(action.Value ?? string.Empty)))
                throw new InvalidDataException($"Automation rule '{rule.Name}' contains an unsafe action.");
            return rule with
            {
                Name = name,
                Pattern = pattern,
                CooldownSeconds = Math.Clamp(rule.CooldownSeconds,
                    rule.Trigger is AutomationTriggerKind.ChatContains or AutomationTriggerKind.Mention or
                        AutomationTriggerKind.PrivateMessage ? 5 : 1, 3600),
                MaximumRunsPerHour = Math.Clamp(rule.MaximumRunsPerHour, 1, 1000),
                Actions = actions.Select(action => action with { Value = action.Value?.Trim() ?? string.Empty }).ToList()
            };
        }).ToList();
    }

    private static bool ContainsSensitiveAutomationText(string value)
    {
        string redacted = SensitiveDataRedactor.RedactText(value);
        return !string.Equals(value, redacted, StringComparison.Ordinal) &&
               redacted.Contains("[REDACTED]", StringComparison.Ordinal);
    }

    private static List<string> NormalizePatterns(IEnumerable<string>? source) => RequireItems(source, "patterns")
        .Select(value => value.Trim()).Where(value => value.Length is > 0 and <= 256).Take(8).ToList();

    private static ApplicationSettings NormalizeSettings(ApplicationSettings settings) => settings with
    {
        LogRetentionDays = settings.LogRetentionDays is 0 or 30 or 90 ? settings.LogRetentionDays : 90
    };

    private static List<string> SanitizeCommands(IEnumerable<string>? commands, int maximum)
    {
        List<string> items = RequireItems(commands, "commands");
        return items
            .Select(command => command.Trim())
            .Where(command => command.Length is > 0 and <= 256)
            .Take(maximum)
            .ToList();
    }

    private static List<T> RequireItems<T>(IEnumerable<T>? source, string collectionName) where T : class
    {
        List<T> result = [];
        foreach (T? item in source ?? [])
        {
            if (item is null)
                throw new InvalidDataException($"The {collectionName} collection cannot contain null entries.");
            result.Add(item);
        }
        return result;
    }

    private static void EnsureUniqueProfiles(IEnumerable<(Guid Id, string Name)> profiles, string profileKind)
    {
        HashSet<Guid> ids = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach ((Guid id, string name) in profiles)
        {
            if (!ids.Add(id))
                throw new InvalidDataException($"Duplicate {profileKind} profile ID '{id}'.");
            if (!names.Add(name))
                throw new InvalidDataException($"Duplicate {profileKind} profile name '{name}'.");
        }
    }
}
