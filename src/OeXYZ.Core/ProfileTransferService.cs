using System.Text.Json;

namespace OeXYZ.Core;

public sealed record ProfileImportResult(
    ProfileDocument Document,
    int AccountsAdded,
    int ServersAdded,
    int DuplicatesSkipped,
    int ProxiesAdded = 0);

public static class ProfileTransferService
{
    private const long MaximumTransferBytes = 2L * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 64
    };

    public static void Export(ProfileDocument source, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string path = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) PrivateFileSystem.EnsurePrivateDirectory(directory);
        ProfileDocument safe = CreatePortableDocument(source);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(safe, Options);
        if (json.LongLength > MaximumTransferBytes)
            throw new InvalidDataException("The exported profile document exceeds the 2 MiB safety limit.");
        PrivateFileSystem.WriteAllBytesAtomically(path, json);
    }

    public static ProfileImportResult Import(ProfileDocument existing, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string path = Path.GetFullPath(sourcePath);
        FileInfo info = new(path);
        if (!info.Exists) throw new FileNotFoundException("The profile import file was not found.", path);
        if (info.Length > MaximumTransferBytes)
            throw new InvalidDataException("The profile import exceeds the 2 MiB safety limit.");
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ProfileDocument imported = JsonSerializer.Deserialize<ProfileDocument>(stream, Options)?.Normalize()
                                       ?? throw new InvalidDataException("The profile import is empty or invalid.");
            return Merge(existing.Normalize(), CreatePortableDocument(imported));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The profile import contains invalid JSON or profile values.", exception);
        }
    }

    public static ProfileDocument CreatePortableDocument(ProfileDocument source)
    {
        ProfileDocument normalized = source.Normalize();
        List<AccountProfile> accounts = normalized.Accounts.Select(account => account with
        {
            AccountIdentifier = null,
            LoginHint = account.Kind == AccountKind.Microsoft ? string.Empty : account.LoginHint,
            AdditionalData = null
        }).ToList();
        List<ServerProfile> servers = normalized.Servers.Select(server => server with
        {
            QuickCommands = server.QuickCommands
                .Where(command => !SensitiveDataRedactor.IsSensitiveCommand(command))
                .ToList(),
            StartupCommands = server.StartupCommands
                .Where(command => !SensitiveDataRedactor.IsSensitiveCommand(command))
                .ToList(),
            AdditionalData = null
        }).ToList();
        List<ProxyProfile> proxies = normalized.ProxyProfiles.Select(proxy => proxy with
        {
            Username = string.Empty,
            SecretReference = null
        }).ToList();
        return new ProfileDocument
        {
            FormatVersion = ProfileDocument.CurrentFormatVersion,
            Revision = 0,
            Accounts = accounts,
            Servers = servers,
            ProxyProfiles = proxies,
            Settings = normalized.Settings with { AdditionalData = null },
            ManagedSessions = normalized.ManagedSessions.ToList(),
            LastSessions = [],
            AdditionalData = null
        }.Normalize();
    }

    private static ProfileImportResult Merge(ProfileDocument existing, ProfileDocument imported)
    {
        List<AccountProfile> accounts = existing.Accounts.ToList();
        List<ServerProfile> servers = existing.Servers.ToList();
        List<ProxyProfile> proxies = existing.ProxyProfiles.ToList();
        HashSet<Guid> usedAccountIds = accounts.Select(account => account.Id).ToHashSet();
        HashSet<Guid> usedServerIds = servers.Select(server => server.Id).ToHashSet();
        HashSet<Guid> usedProxyIds = proxies.Select(proxy => proxy.Id).ToHashSet();
        Dictionary<Guid, Guid> accountIds = [];
        Dictionary<Guid, Guid> serverIds = [];
        Dictionary<Guid, Guid> proxyIds = [];
        int accountsAdded = 0;
        int serversAdded = 0;
        int proxiesAdded = 0;
        int skipped = 0;

        foreach (AccountProfile candidate in imported.Accounts)
        {
            AccountProfile? duplicate = accounts.FirstOrDefault(existingAccount =>
                existingAccount.Id == candidate.Id && existingAccount.Kind == candidate.Kind);
            if (duplicate is not null)
            {
                accountIds[candidate.Id] = duplicate.Id;
                skipped++;
                continue;
            }
            Guid id = usedAccountIds.Add(candidate.Id) ? candidate.Id : NewId(usedAccountIds);
            accountIds[candidate.Id] = id;
            string displayName = UniqueName(candidate.DisplayName, accounts.Select(account => account.DisplayName));
            accounts.Add(candidate with { Id = id, DisplayName = displayName, AccountIdentifier = null, AdditionalData = null });
            accountsAdded++;
        }

        foreach (ProxyProfile candidate in imported.ProxyProfiles)
        {
            ProxyProfile? duplicate = proxies.FirstOrDefault(existingProxy => existingProxy.Id == candidate.Id);
            if (duplicate is not null)
            {
                proxyIds[candidate.Id] = duplicate.Id;
                skipped++;
                continue;
            }
            Guid id = usedProxyIds.Add(candidate.Id) ? candidate.Id : NewId(usedProxyIds);
            proxyIds[candidate.Id] = id;
            string displayName = UniqueName(candidate.DisplayName, proxies.Select(proxy => proxy.DisplayName));
            proxies.Add(candidate with
            {
                Id = id, DisplayName = displayName, Username = string.Empty, SecretReference = null
            });
            proxiesAdded++;
        }

        foreach (ServerProfile candidate in imported.Servers)
        {
            ServerProfile? duplicate = servers.FirstOrDefault(existingServer => existingServer.Id == candidate.Id);
            if (duplicate is not null)
            {
                serverIds[candidate.Id] = duplicate.Id;
                skipped++;
                continue;
            }
            Guid id = usedServerIds.Add(candidate.Id) ? candidate.Id : NewId(usedServerIds);
            serverIds[candidate.Id] = id;
            string displayName = UniqueName(candidate.DisplayName, servers.Select(server => server.DisplayName));
            Guid? proxyId = candidate.ProxyProfileId is Guid importedProxyId && proxyIds.TryGetValue(importedProxyId, out Guid mapped)
                ? mapped
                : null;
            servers.Add(candidate with
            {
                Id = id, DisplayName = displayName, ProxyProfileId = proxyId, AdditionalData = null
            });
            serversAdded++;
        }

        List<SessionBookmark> managedSessions = existing.ManagedSessions.ToList();
        managedSessions.AddRange(imported.ManagedSessions
            .Where(session => accountIds.ContainsKey(session.AccountId) && serverIds.ContainsKey(session.ServerId))
            .Select(session => new SessionBookmark
            {
                AccountId = accountIds[session.AccountId],
                ServerId = serverIds[session.ServerId]
            }));
        ProfileDocument merged = existing with
        {
            Accounts = accounts,
            Servers = servers,
            ProxyProfiles = proxies,
            ManagedSessions = managedSessions
        };
        return new ProfileImportResult(merged.Normalize(), accountsAdded, serversAdded, skipped, proxiesAdded);
    }

    private static Guid NewId(HashSet<Guid> used)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (!used.Add(id));
        return id;
    }

    private static string UniqueName(string requested, IEnumerable<string> existing)
    {
        string basis = string.IsNullOrWhiteSpace(requested) ? "Imported" : requested.Trim();
        HashSet<string> names = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(basis)) return basis;
        for (int suffix = 2; suffix < 10_000; suffix++)
        {
            string suffixText = $" (imported {suffix})";
            int maximumStemLength = ProfileRules.MaximumProfileNameLength - suffixText.Length;
            string stem = basis.Length <= maximumStemLength
                ? basis
                : TruncateWithoutSplittingSurrogate(basis, maximumStemLength).TrimEnd();
            string candidate = stem + suffixText;
            if (!names.Contains(candidate)) return candidate;
        }
        throw new InvalidDataException("Too many imported profiles share the same display name.");
    }

    private static string TruncateWithoutSplittingSurrogate(string value, int maximumLength)
    {
        int length = Math.Min(value.Length, maximumLength);
        if (length > 0 && length < value.Length &&
            char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length]))
            length--;
        return value[..length];
    }
}
