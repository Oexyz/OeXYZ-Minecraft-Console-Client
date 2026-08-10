using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using OeXYZ.Protocol;
using XboxAuthNet.Game.Accounts;

namespace OeXYZ.ConsoleClient;

internal sealed class AuthenticationService
{
    private readonly JELoginHandler loginHandler;
    private readonly MinecraftServicesClient services = new();
    private readonly SemaphoreSlim authenticationLock = new(1, 1);

    public AuthenticationService()
    {
        JsonXboxGameAccountManager accountManager = new(
            new ProtectedJsonStorage(AppPaths.ProtectedAccounts),
            JEGameAccount.FromSessionStorage,
            JsonXboxGameAccountManager.DefaultSerializerOption);
        loginHandler = new JELoginHandlerBuilder()
            .WithAccountManager(accountManager)
            .Build();
    }

    public async Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        if (profile.Kind == AccountKind.Offline)
        {
            status("Using offline identity. This only works on offline-mode servers.");
            return MinecraftIdentity.Offline(profile.LoginHint);
        }

        await authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IXboxGameAccount? account = string.IsNullOrWhiteSpace(profile.AccountIdentifier)
                ? null
                : loginHandler.AccountManager.GetAccounts()
                    .FirstOrDefault(candidate => string.Equals(candidate.Identifier, profile.AccountIdentifier, StringComparison.OrdinalIgnoreCase));

            MSession session;
            if (account is not null)
            {
                status("Refreshing the protected Microsoft session...");
                try
                {
                    session = await loginHandler.AuthenticateSilently(account, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    status("Microsoft needs confirmation. Your browser will open securely.");
                    session = await loginHandler.AuthenticateInteractively(account, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                status("Opening Microsoft sign-in in your default browser...");
                account = loginHandler.AccountManager.NewAccount();
                session = await loginHandler.AuthenticateInteractively(account, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(session.Username) || string.IsNullOrWhiteSpace(session.UUID) || string.IsNullOrWhiteSpace(session.AccessToken))
                throw new InvalidDataException("Microsoft sign-in completed without a usable Minecraft Java profile. Verify that this account owns Java Edition.");

            Guid uuid = Guid.ParseExact(session.UUID.Replace("-", string.Empty, StringComparison.Ordinal), "N");
            profile.AccountIdentifier = account.Identifier;
            loginHandler.AccountManager.SaveAccounts();
            status($"Signed in as {session.Username}. Fetching the secure-chat certificate...");

            PlayerCertificate? certificate = null;
            try
            {
                certificate = await services.FetchPlayerCertificateAsync(session.AccessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                status("Signed in successfully; secure-chat signing is temporarily unavailable.");
            }

            return new MinecraftIdentity(session.Username, uuid, session.AccessToken, certificate);
        }
        finally
        {
            authenticationLock.Release();
        }
    }
}
