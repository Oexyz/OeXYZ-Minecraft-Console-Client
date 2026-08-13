using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using OeXYZ.Core;
using OeXYZ.Protocol;
using OeXYZ.Session;
using XboxAuthNet.Game.Accounts;

namespace OeXYZ.Authentication;

public sealed class AuthenticationService : IIdentityProvider
{
    private readonly string protectedAccountsPath;
    private JELoginHandler? loginHandler;
    private readonly MinecraftServicesClient services = new();
    private readonly SemaphoreSlim authenticationLock = new(1, 1);

    public AuthenticationService(string protectedAccountsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedAccountsPath);
        this.protectedAccountsPath = Path.GetFullPath(protectedAccountsPath);
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
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Microsoft login currently requires Windows DPAPI. Offline profiles remain portable; device-code storage is planned for v1.3.");
            JELoginHandler handler = GetLoginHandler();
            IXboxGameAccount? account = string.IsNullOrWhiteSpace(profile.AccountIdentifier)
                ? null
                : handler.AccountManager.GetAccounts()
                    .FirstOrDefault(candidate => string.Equals(candidate.Identifier, profile.AccountIdentifier, StringComparison.OrdinalIgnoreCase));

            MSession session;
            if (account is not null)
            {
                status("Refreshing the protected Microsoft session...");
                try
                {
                    session = await handler.AuthenticateSilently(account, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    status("Microsoft needs confirmation. Your browser will open securely.");
                    session = await handler.AuthenticateInteractively(account, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                status("Opening Microsoft sign-in in your default browser...");
                account = handler.AccountManager.NewAccount();
                session = await handler.AuthenticateInteractively(account, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(session.Username) || string.IsNullOrWhiteSpace(session.UUID) || string.IsNullOrWhiteSpace(session.AccessToken))
                throw new InvalidDataException("Microsoft sign-in completed without a usable Minecraft Java profile. Verify that this account owns Java Edition.");

            Guid uuid = Guid.ParseExact(session.UUID.Replace("-", string.Empty, StringComparison.Ordinal), "N");
            profile.AccountIdentifier = account.Identifier;
            handler.AccountManager.SaveAccounts();
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

    private JELoginHandler GetLoginHandler()
    {
        if (loginHandler is not null) return loginHandler;
        JsonXboxGameAccountManager accountManager = new(
            new ProtectedJsonStorage(protectedAccountsPath),
            JEGameAccount.FromSessionStorage,
            JsonXboxGameAccountManager.DefaultSerializerOption);
        loginHandler = new JELoginHandlerBuilder()
            .WithAccountManager(accountManager)
            .Build();
        return loginHandler;
    }
}
