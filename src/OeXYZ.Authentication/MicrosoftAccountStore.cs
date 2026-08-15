using OeXYZ.Core;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.SessionStorages;

namespace OeXYZ.Authentication;

/// <summary>
/// Owns the process-safe account-store transaction boundary and the durable
/// association between an OeXYZ profile and its Microsoft account.
/// </summary>
internal static class MicrosoftAccountStore
{
    private const string ProfileBindingPrefix = "OeXYZ.ProfileId.";

    public static async ValueTask<TResult> ExecuteAsync<TResult>(
        string accountStorePath,
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using FileStream transaction = await AccountStoreLock.AcquireAsync(
            accountStorePath, cancellationToken).ConfigureAwait(false);
        return await operation(cancellationToken).ConfigureAwait(false);
    }

    public static IXboxGameAccount? FindAccount(
        IXboxGameAccountManager accountManager,
        AccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(accountManager);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Id == Guid.Empty)
            throw new ArgumentException("The OeXYZ account profile ID cannot be empty.", nameof(profile));

        List<IXboxGameAccount> accounts = accountManager.GetAccounts().ToList();
        if (!string.IsNullOrWhiteSpace(profile.AccountIdentifier))
        {
            IXboxGameAccount? identified = accounts.FirstOrDefault(candidate => string.Equals(
                candidate.Identifier,
                profile.AccountIdentifier,
                StringComparison.OrdinalIgnoreCase));
            if (identified is not null) return identified;
        }

        string bindingKey = GetProfileBindingKey(profile.Id);
        return accounts.FirstOrDefault(account => account.SessionStorage.Keys.Contains(
            bindingKey, StringComparer.Ordinal));
    }

    public static void BindAccount(
        IXboxGameAccountManager accountManager,
        IXboxGameAccount account,
        Guid profileId)
    {
        ArgumentNullException.ThrowIfNull(accountManager);
        ArgumentNullException.ThrowIfNull(account);
        if (profileId == Guid.Empty)
            throw new ArgumentException("The OeXYZ account profile ID cannot be empty.", nameof(profileId));
        string bindingKey = GetProfileBindingKey(profileId);
        foreach (IXboxGameAccount other in accountManager.GetAccounts())
        {
            if (!ReferenceEquals(other, account)) other.SessionStorage.Remove(bindingKey);
        }
        account.SessionStorage.Set(bindingKey, profileId.ToString("N"));
        account.SessionStorage.SetKeyMode(bindingKey, SessionStorageKeyMode.Default);
    }

    internal static string GetProfileBindingKey(Guid profileId) =>
        ProfileBindingPrefix + profileId.ToString("N");
}
