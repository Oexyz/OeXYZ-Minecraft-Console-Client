using OeXYZ.Core;

namespace OeXYZ.ConsoleClient;

internal sealed record ProfileUpdateResult(ProfileDocument Document, Exception? Failure)
{
    public bool Succeeded => Failure is null;
}

internal static class ProfileUiOperations
{
    public static ProfileDocument AddAccount(ProfileDocument current, AccountProfile account)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(account);
        return current with { Accounts = [.. current.Accounts, account] };
    }

    public static ProfileDocument EditAccount(
        ProfileDocument current,
        AccountProfile replacement,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        EnsureRevision(current, expectedRevision);
        return current with
        {
            Accounts = ReplaceById(current.Accounts, replacement.Id, replacement, "account")
        };
    }

    public static ProfileDocument RemoveAccount(
        ProfileDocument current,
        Guid accountId,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureRevision(current, expectedRevision);
        return current with
        {
            Accounts = RemoveById(current.Accounts, accountId, "account")
        };
    }

    public static ProfileDocument AddServer(ProfileDocument current, ServerProfile server)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(server);
        return current with { Servers = [.. current.Servers, server] };
    }

    public static ProfileDocument EditServer(
        ProfileDocument current,
        ServerProfile replacement,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        EnsureRevision(current, expectedRevision);
        return current with
        {
            Servers = ReplaceById(current.Servers, replacement.Id, replacement, "server")
        };
    }

    public static ProfileDocument RemoveServer(
        ProfileDocument current,
        Guid serverId,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        EnsureRevision(current, expectedRevision);
        return current with
        {
            Servers = RemoveById(current.Servers, serverId, "server")
        };
    }

    public static ProfileDocument EditSettings(
        ProfileDocument current,
        ApplicationSettings settings,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(settings);
        EnsureRevision(current, expectedRevision);
        return current with { Settings = settings };
    }

    public static ProfileUpdateResult TryUpdate(
        ProfileDocument current,
        Func<Func<ProfileDocument, ProfileDocument>, ProfileDocument> persist,
        Func<ProfileDocument> reload,
        Func<ProfileDocument, ProfileDocument> update)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(update);

        try
        {
            return new ProfileUpdateResult(persist(update), null);
        }
        catch (Exception persistenceFailure)
        {
            try
            {
                return new ProfileUpdateResult(reload(), persistenceFailure);
            }
            catch (Exception reloadFailure)
            {
                return new ProfileUpdateResult(
                    current,
                    new AggregateException(persistenceFailure, reloadFailure));
            }
        }
    }

    public static ProfileUpdateResult TryPersistAccountIdentifier(
        ProfileDocument current,
        AccountProfile changedAccount,
        Func<Func<ProfileDocument, ProfileDocument>, ProfileDocument> persist,
        Func<ProfileDocument> reload)
    {
        ArgumentNullException.ThrowIfNull(changedAccount);
        return TryUpdate(current, persist, reload, document =>
        {
            AccountProfile? persisted = document.Accounts.SingleOrDefault(account =>
                account.Id == changedAccount.Id);
            if (persisted is null)
                throw new InvalidDataException("The authenticated account profile no longer exists.");
            if (string.Equals(
                    persisted.AccountIdentifier,
                    changedAccount.AccountIdentifier,
                    StringComparison.Ordinal))
                return document;

            AccountProfile replacement = persisted with
            {
                AccountIdentifier = changedAccount.AccountIdentifier
            };
            return document with
            {
                Accounts = ReplaceById(document.Accounts, replacement.Id, replacement, "account")
            };
        });
    }

    public static async Task<Exception?> PersistLastSessionsThenCleanupAsync(
        IReadOnlyList<SessionBookmark> lastSessions,
        Func<Func<ProfileDocument, ProfileDocument>, ProfileDocument>? persist,
        IReadOnlyList<Func<Task>> cleanupActions)
    {
        ArgumentNullException.ThrowIfNull(lastSessions);
        ArgumentNullException.ThrowIfNull(cleanupActions);

        List<Exception> failures = [];
        if (persist is not null)
        {
            try
            {
                _ = persist(current => current with { LastSessions = [.. lastSessions] });
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (Func<Task> cleanup in cleanupActions)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(cleanup);
                await cleanup();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Multiple profile persistence or session cleanup operations failed.", failures)
        };
    }

    private static List<TProfile> ReplaceById<TProfile>(
        IReadOnlyList<TProfile> source,
        Guid id,
        TProfile replacement,
        string profileKind) where TProfile : notnull
    {
        List<TProfile> result = source.ToList();
        int index = result.FindIndex(item => item switch
        {
            AccountProfile account => account.Id == id,
            ServerProfile server => server.Id == id,
            _ => false
        });
        if (index < 0)
            throw new InvalidDataException($"The {profileKind} profile no longer exists.");
        result[index] = replacement;
        return result;
    }

    private static List<TProfile> RemoveById<TProfile>(
        IReadOnlyList<TProfile> source,
        Guid id,
        string profileKind) where TProfile : notnull
    {
        List<TProfile> result = source.Where(item => item switch
        {
            AccountProfile account => account.Id != id,
            ServerProfile server => server.Id != id,
            _ => true
        }).ToList();
        if (result.Count == source.Count)
            throw new InvalidDataException($"The {profileKind} profile no longer exists.");
        return result;
    }

    private static void EnsureRevision(ProfileDocument current, long expectedRevision)
    {
        if (current.Revision != expectedRevision)
            throw new InvalidDataException(
                "Profiles changed in another OeXYZ process. Repeat the edit after the profile list reloads.");
    }
}
