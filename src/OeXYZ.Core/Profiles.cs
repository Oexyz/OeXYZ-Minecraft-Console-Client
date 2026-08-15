using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const int CurrentFormatVersion = 4;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public long Revision { get; set; }
    public List<AccountProfile> Accounts { get; init; } = [];
    public List<ServerProfile> Servers { get; init; } = [];
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
        EnsureUniqueProfiles(
            accounts.Select(account => (account.Id, account.DisplayName)),
            "account");
        EnsureUniqueProfiles(
            servers.Select(server => (server.Id, server.DisplayName)),
            "server");
        HashSet<Guid> accountIds = accounts.Select(account => account.Id).ToHashSet();
        HashSet<Guid> serverIds = servers.Select(server => server.Id).ToHashSet();
        List<SessionBookmark> managedSessions = NormalizeSessions(ManagedSessions, accountIds, serverIds);
        List<SessionBookmark> lastSessions = NormalizeSessions(LastSessions, accountIds, serverIds);

        return this with
        {
            FormatVersion = CurrentFormatVersion,
            Accounts = accounts,
            Servers = servers,
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
                .ToList()
        };
    }

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
