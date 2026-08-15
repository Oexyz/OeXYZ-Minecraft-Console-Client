using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using System.Security.Cryptography;
using OeXYZ.Core;
using OeXYZ.Protocol;
using OeXYZ.Session;
using XboxAuthNet.Game.Accounts;

namespace OeXYZ.Authentication;

public delegate ValueTask<byte[]> AccountSecretProvider(
    Action<string> status,
    CancellationToken cancellationToken);

public sealed record MicrosoftDeviceCodePrompt(
    string UserCode,
    string VerificationUrl,
    DateTimeOffset ExpiresOn);

public delegate void MicrosoftDeviceCodePromptHandler(MicrosoftDeviceCodePrompt? prompt);

public sealed class AuthenticationService : IIdentityProvider, IAsyncDisposable
{
    private readonly string protectedAccountsPath;
    private readonly AccountSecretProvider? accountSecretProvider;
    private readonly MicrosoftDeviceCodePromptHandler? deviceCodePromptHandler;
    private EncryptedJsonStorage? encryptedStorage;
    private readonly MinecraftServicesClient services = new();
    private readonly SemaphoreSlim authenticationLock = new(1, 1);

    public AuthenticationService(
        string protectedAccountsPath,
        AccountSecretProvider? accountSecretProvider = null,
        MicrosoftDeviceCodePromptHandler? deviceCodePromptHandler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedAccountsPath);
        this.protectedAccountsPath = Path.GetFullPath(protectedAccountsPath);
        this.accountSecretProvider = accountSecretProvider;
        this.deviceCodePromptHandler = deviceCodePromptHandler;
    }

    public async Task<MinecraftIdentity> GetIdentityAsync(
        AccountProfile profile,
        Action<string> status,
        CancellationToken cancellationToken)
    {
        if (profile.Kind == AccountKind.Offline)
        {
            ProfileRules.EnsureValidOfflineName(profile.LoginHint);
            status("Using offline identity. This only works on offline-mode servers.");
            return MinecraftIdentity.Offline(profile.LoginHint);
        }

        await authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (string Username, Guid Uuid, string AccessToken, string AccountIdentifier) authentication =
                await MicrosoftAccountStore.ExecuteAsync(
                    protectedAccountsPath,
                    async storeCancellationToken =>
                    {
                        // JsonXboxGameAccountManager keeps an in-memory snapshot. Build it
                        // only after acquiring the interprocess lock so SaveAccounts can
                        // never overwrite accounts added by a different OeXYZ process.
                        JELoginHandler handler = await CreateLoginHandlerAsync(
                            status, storeCancellationToken).ConfigureAwait(false);
                        IXboxGameAccount? existingAccount = MicrosoftAccountStore.FindAccount(
                            handler.AccountManager, profile);
                        IXboxGameAccount account;
                        MSession session;

                        if (existingAccount is not null)
                        {
                            account = existingAccount;
                            status("Refreshing the protected Microsoft session...");
                            try
                            {
                                session = await handler.AuthenticateSilently(
                                    account, storeCancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch
                            {
                                status(OperatingSystem.IsWindows()
                                    ? "Microsoft needs confirmation. Your browser will open securely."
                                    : "Microsoft needs confirmation. Follow the device sign-in instructions shown in this terminal.");
                                try
                                {
                                    session = await handler.AuthenticateInteractively(
                                        account, storeCancellationToken).ConfigureAwait(false);
                                }
                                finally
                                {
                                    if (!OperatingSystem.IsWindows()) deviceCodePromptHandler?.Invoke(null);
                                }
                            }
                        }
                        else
                        {
                            status(OperatingSystem.IsWindows()
                                ? "Opening Microsoft sign-in in your default browser..."
                                : "Starting Microsoft device sign-in. The temporary code is shown only in this terminal.");
                            account = handler.AccountManager.NewAccount();
                            try
                            {
                                session = await handler.AuthenticateInteractively(
                                    account, storeCancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                if (!OperatingSystem.IsWindows()) deviceCodePromptHandler?.Invoke(null);
                            }
                        }

                        if (string.IsNullOrWhiteSpace(session.Username) || string.IsNullOrWhiteSpace(session.UUID) || string.IsNullOrWhiteSpace(session.AccessToken))
                            throw new InvalidDataException("Microsoft sign-in completed without a usable Minecraft Java profile. Verify that this account owns Java Edition.");

                        Guid uuid = Guid.ParseExact(session.UUID.Replace("-", string.Empty, StringComparison.Ordinal), "N");
                        string accountIdentifier = string.IsNullOrWhiteSpace(account.Identifier)
                            ? throw new InvalidDataException("Microsoft sign-in completed without a persistent account identifier.")
                            : account.Identifier;
                        MicrosoftAccountStore.BindAccount(handler.AccountManager, account, profile.Id);
                        handler.AccountManager.SaveAccounts();
                        return (session.Username!, uuid, session.AccessToken!, accountIdentifier);
                    },
                    cancellationToken).ConfigureAwait(false);

            profile.AccountIdentifier = authentication.AccountIdentifier;
            status($"Signed in as {authentication.Username}. Fetching the secure-chat certificate...");

            PlayerCertificate? certificate = null;
            try
            {
                certificate = await services.FetchPlayerCertificateAsync(
                    authentication.AccessToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                status("Signed in successfully; secure-chat signing is temporarily unavailable.");
            }

            return new MinecraftIdentity(
                authentication.Username,
                authentication.Uuid,
                authentication.AccessToken,
                certificate);
        }
        finally
        {
            authenticationLock.Release();
        }
    }

    public async Task PrepareAsync(Action<string> status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        await authenticationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await MicrosoftAccountStore.ExecuteAsync(
                protectedAccountsPath,
                async storeCancellationToken =>
                {
                    _ = await CreateLoginHandlerAsync(status, storeCancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            authenticationLock.Release();
        }
    }

    private async Task<JELoginHandler> CreateLoginHandlerAsync(
        Action<string> status,
        CancellationToken cancellationToken)
    {
        byte[]? linuxSecret = null;
        EncryptedJsonStorage? candidateStorage = null;
        try
        {
            bool initializeStore = !File.Exists(protectedAccountsPath);
            XboxAuthNet.Game.Accounts.JsonStorage.IJsonStorage storage;
            if (OperatingSystem.IsWindows())
            {
                storage = new ProtectedJsonStorage(protectedAccountsPath);
            }
            else
            {
                if (accountSecretProvider is null)
                    throw new PlatformNotSupportedException(
                        "Microsoft accounts on Linux require a protected account-store passphrase. Use --account-key-file or an interactive terminal.");
                if (deviceCodePromptHandler is null)
                    throw new PlatformNotSupportedException(
                        "Linux Microsoft login requires a device-code prompt handler.");
                if (encryptedStorage is null)
                {
                    linuxSecret = await accountSecretProvider(status, cancellationToken).ConfigureAwait(false);
                    candidateStorage = new EncryptedJsonStorage(protectedAccountsPath, linuxSecret);
                    storage = candidateStorage;
                }
                else
                {
                    storage = encryptedStorage;
                }
            }

            JsonXboxGameAccountManager accountManager = new(
                storage,
                JEGameAccount.FromSessionStorage,
                JsonXboxGameAccountManager.DefaultSerializerOption);
            JELoginHandlerBuilder builder = new JELoginHandlerBuilder()
                .WithAccountManager(accountManager);
            if (!OperatingSystem.IsWindows())
            {
                builder.WithOAuthProvider(new MicrosoftLiveDeviceCodeProvider(
                    JELoginHandler.DefaultMicrosoftOAuthClientInfo,
                    prompt => deviceCodePromptHandler!(prompt)));
            }
            JELoginHandler handler = builder.Build();
            if (initializeStore)
            {
                // Establish a valid empty document (and, on Linux, its salt)
                // before releasing the lock even if interactive auth later fails.
                accountManager.SaveAccounts();
            }
            if (candidateStorage is not null)
            {
                encryptedStorage = candidateStorage;
                candidateStorage = null;
            }
            return handler;
        }
        finally
        {
            candidateStorage?.Dispose();
            if (linuxSecret is not null) CryptographicOperations.ZeroMemory(linuxSecret);
        }
    }

    public ValueTask DisposeAsync()
    {
        encryptedStorage?.Dispose();
        authenticationLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
