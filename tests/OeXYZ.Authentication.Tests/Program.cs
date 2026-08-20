using System.Security.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using OeXYZ.Authentication;
using OeXYZ.Core;
using OeXYZ.Session;
using XboxAuthNet.Game.Accounts;
using XboxAuthNet.Game.Accounts.JsonStorage;

if (args is ["--account-store-worker", string mode, string storePath, string gatePath, string workerId, string profileId])
{
    await RunAccountStoreWorkerAsync(mode, storePath, gatePath, workerId, profileId);
    return;
}

List<string> passed = [];
string root = Path.Combine(Path.GetTempPath(), "oexyz-auth-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Run("encrypted account storage round trip and backup", () =>
    {
        string path = Path.Combine(root, "roundtrip", "accounts.bin");
        byte[] secret = Encoding.UTF8.GetBytes("correct horse battery staple");
        using EncryptedJsonStorage storage = new(path, secret);
        JsonObject first = new() { ["account"] = "OeXYZ", ["token"] = "private-value" };
        storage.Write(first, null);
        byte[] encrypted = File.ReadAllBytes(path);
        True(!Encoding.UTF8.GetString(encrypted).Contains("private-value", StringComparison.Ordinal),
            "Plaintext account data was visible in the encrypted file.");
        JsonNode loaded = storage.ReadAsJsonNode() ?? throw new InvalidOperationException("Account JSON was missing.");
        Equal("OeXYZ", loaded["account"]?.GetValue<string>() ?? string.Empty);
        storage.Write(new JsonObject { ["account"] = "OeXYZ2" }, null);
        True(File.Exists(path + ".bak"), "An existing encrypted account file was not backed up.");
        CryptographicOperations.ZeroMemory(secret);
    });

    await RunAsync("interprocess transactions preserve accounts and deduplicate parallel first login", async () =>
    {
        string directory = Path.Combine(root, "parallel-account-store");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, OperatingSystem.IsWindows() ? "accounts.dat" : "accounts.bin");
        string gate = Path.Combine(directory, "start.gate");
        string mode = OperatingSystem.IsWindows() ? "dpapi" : "aes";
        const int workerCount = 8;
        const string sharedProfileId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string[] profileIds = Enumerable.Range(0, workerCount)
            .Select(index => index < workerCount / 2
                ? sharedProfileId
                : (index + 1).ToString("x32"))
            .ToArray();
        Process[] workers = Enumerable.Range(0, workerCount)
            .Select(index => StartTestProcess(
                "--account-store-worker",
                mode,
                path,
                gate,
                (index + 100).ToString("x32"),
                profileIds[index]))
            .ToArray();
        Task<string>[] errors = workers.Select(worker => worker.StandardError.ReadToEndAsync()).ToArray();
        Task<string>[] output = workers.Select(worker => worker.StandardOutput.ReadToEndAsync()).ToArray();

        try
        {
            DateTime readyDeadline = DateTime.UtcNow.AddSeconds(30);
            while (Directory.GetFiles(directory, "start.gate.ready-*").Length < workerCount)
            {
                if (workers.Any(worker => worker.HasExited))
                    throw new InvalidOperationException("An account-store worker exited before the concurrency gate opened.");
                if (DateTime.UtcNow >= readyDeadline)
                    throw new TimeoutException("Account-store workers did not reach the concurrency gate.");
                await Task.Delay(25);
            }
            File.WriteAllText(gate, "go");

            await WaitForWorkersAsync(workers, TimeSpan.FromSeconds(60));
            _ = await Task.WhenAll(output);
            string[] errorText = await Task.WhenAll(errors);
            for (int index = 0; index < workers.Length; index++)
            {
                if (workers[index].ExitCode != 0)
                    throw new InvalidOperationException(
                        $"Account-store worker {index} failed ({workers[index].ExitCode}): {errorText[index]}");
            }

            using AccountStorageHandle handle = OpenAccountStorage(mode, path);
            JsonXboxGameAccountManager manager = CreateAccountManager(handle.Storage);
            List<IXboxGameAccount> accounts = manager.GetAccounts().ToList();
            int expectedAccounts = profileIds.Distinct(StringComparer.Ordinal).Count();
            True(expectedAccounts == accounts.Count,
                $"Expected {expectedAccounts} persisted accounts, received {accounts.Count}. Keys: " +
                string.Join(" | ", accounts.Select(account => string.Join(",", account.SessionStorage.Keys))));
            Equal(expectedAccounts, accounts.Select(account => account.Identifier)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (string profileId in profileIds.Distinct(StringComparer.Ordinal))
            {
                AccountProfile profile = CreateMicrosoftProfile(profileId);
                IXboxGameAccount? bound = MicrosoftAccountStore.FindAccount(manager, profile);
                True(bound is not null, $"Profile {profileId} lost its persistent account binding.");
            }
            True(File.Exists(path + ".lock"), "The interprocess account-store lock was not retained.");
            if (!OperatingSystem.IsWindows())
                True(File.Exists(path + ".bak"), "Concurrent encrypted account-store updates did not retain a backup.");
            Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
            if (!OperatingSystem.IsWindows())
            {
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path + ".lock"));
            }
        }
        finally
        {
            await StopAndDisposeWorkersAsync(workers);
        }
    });

    Run("profile binding removes only duplicate bindings from other accounts", () =>
    {
        string path = Path.Combine(root, "binding-cleanup", "accounts.bin");
        byte[] secret = Encoding.UTF8.GetBytes("binding cleanup regression secret");
        try
        {
            using EncryptedJsonStorage storage = new(path, secret);
            JsonXboxGameAccountManager manager = CreateAccountManager(storage);
            IXboxGameAccount first = manager.NewAccount();
            IXboxGameAccount second = manager.NewAccount();
            first.SessionStorage.Set(JEProfileSource.KeyName, new JEProfile
            {
                UUID = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                Username = "First"
            });
            second.SessionStorage.Set(JEProfileSource.KeyName, new JEProfile
            {
                UUID = "cccccccccccccccccccccccccccccccc",
                Username = "Second"
            });

            Guid duplicateProfileId = Guid.ParseExact("dddddddddddddddddddddddddddddddd", "N");
            Guid retainedProfileId = Guid.ParseExact("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "N");
            string duplicateKey = MicrosoftAccountStore.GetProfileBindingKey(duplicateProfileId);
            string retainedKey = MicrosoftAccountStore.GetProfileBindingKey(retainedProfileId);
            first.SessionStorage.Set(duplicateKey, duplicateProfileId.ToString("N"));
            second.SessionStorage.Set(duplicateKey, duplicateProfileId.ToString("N"));
            first.SessionStorage.Set(retainedKey, retainedProfileId.ToString("N"));

            MicrosoftAccountStore.BindAccount(manager, second, duplicateProfileId);
            manager.SaveAccounts();

            JsonXboxGameAccountManager reloaded = CreateAccountManager(storage);
            List<IXboxGameAccount> accounts = reloaded.GetAccounts().ToList();
            Equal(1, accounts.Count(account => account.SessionStorage.Keys.Contains(
                duplicateKey, StringComparer.Ordinal)));
            True(accounts.Single(account => account.Identifier == first.Identifier)
                    .SessionStorage.Keys.Contains(retainedKey, StringComparer.Ordinal),
                "Binding cleanup removed a different OeXYZ profile binding.");
            IXboxGameAccount? selected = MicrosoftAccountStore.FindAccount(
                reloaded,
                CreateMicrosoftProfile(duplicateProfileId.ToString("N")));
            Equal(second.Identifier ?? string.Empty, selected?.Identifier ?? string.Empty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    });

    await RunAsync("account-store lock retries contention, cancels, and propagates permanent errors", async () =>
    {
        string path = Path.Combine(root, "lock-behavior", "accounts.bin");
        await using FileStream held = await AccountStoreLock.AcquireAsync(path, CancellationToken.None);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(150));
        OperationCanceledException canceled = await ThrowsAndReturnAsync<OperationCanceledException>(async () =>
        {
            await using FileStream unexpected = await AccountStoreLock.AcquireAsync(path, cancellation.Token);
        });
        Equal(cancellation.Token, canceled.CancellationToken);

        int attempts = 0;
        IOException permanent = new("Permanent account-lock fixture.", unchecked((int)0x80070070));
        IOException observed = await ThrowsAndReturnAsync<IOException>(async () =>
        {
            await using FileStream unexpected = await AccountStoreLock.AcquireAsync(
                Path.Combine(root, "permanent-lock-error", "accounts.bin"),
                CancellationToken.None,
                (_, _) =>
                {
                    attempts++;
                    throw permanent;
                });
        });
        True(ReferenceEquals(permanent, observed), "A permanent lock error was replaced or retried.");
        Equal(1, attempts);
    });

    Run("DPAPI storage is platform-gated and enforces payload, ciphertext, and JSON-depth limits", () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            ProtectedJsonStorage unavailable = new(Path.Combine(root, "dpapi-unavailable.dat"));
            Throws<PlatformNotSupportedException>(() => unavailable.ReadAsJsonNode());
            Throws<PlatformNotSupportedException>(() => unavailable.Write(new JsonObject(), null));
            return;
        }

        string oversizedPayloadPath = Path.Combine(root, "dpapi-bounds", "payload.dat");
        ProtectedJsonStorage oversizedPayload = new(oversizedPayloadPath);
        JsonObject tooLarge = new()
        {
            ["payload"] = new string('x', ProtectedJsonStorage.MaximumPayloadBytes)
        };
        Throws<InvalidDataException>(() => oversizedPayload.Write(tooLarge, null));
        True(!File.Exists(oversizedPayloadPath), "An oversized DPAPI payload was written.");

        string oversizedCiphertextPath = Path.Combine(root, "dpapi-bounds", "ciphertext.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(oversizedCiphertextPath)!);
        using (FileStream file = File.Create(oversizedCiphertextPath))
            file.SetLength(ProtectedJsonStorage.MaximumEncryptedBytes + 1L);
        ProtectedJsonStorage oversizedCiphertext = new(oversizedCiphertextPath);
        Throws<InvalidDataException>(() => oversizedCiphertext.ReadAsJsonNode());

        string deepPath = Path.Combine(root, "dpapi-bounds", "deep.dat");
        ProtectedJsonStorage deepStorage = new(deepPath);
        JsonObject deep = new();
        JsonObject cursor = deep;
        for (int depth = 0; depth < 70; depth++)
        {
            JsonObject child = new();
            cursor["child"] = child;
            cursor = child;
        }
        deepStorage.Write(deep, new JsonSerializerOptions { MaxDepth = 128 });
        Throws<JsonException>(() => deepStorage.ReadAsJsonNode());
    });

    Run("wrong passphrase and tampering are rejected", () =>
    {
        string path = Path.Combine(root, "tamper", "accounts.bin");
        using (EncryptedJsonStorage storage = new(path, Encoding.UTF8.GetBytes("first secure passphrase")))
            storage.Write(new JsonObject { ["token"] = "never expose" }, null);

        using (EncryptedJsonStorage wrong = new(path, Encoding.UTF8.GetBytes("second secure passphrase")))
            Throws<CryptographicException>(() => wrong.ReadAsJsonNode());

        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
        using EncryptedJsonStorage tampered = new(path, Encoding.UTF8.GetBytes("first secure passphrase"));
        Throws<CryptographicException>(() => tampered.ReadAsJsonNode());
    });

    Run("oversized and foreign account stores are rejected", () =>
    {
        string oversized = Path.Combine(root, "oversized.bin");
        using (FileStream file = File.Create(oversized)) file.SetLength(9L * 1024 * 1024);
        Throws<InvalidDataException>(() => new EncryptedJsonStorage(
            oversized, Encoding.UTF8.GetBytes("secure passphrase value")));

        string foreign = Path.Combine(root, "foreign.bin");
        File.WriteAllText(foreign, "not an encrypted OeXYZ account store");
        Throws<InvalidDataException>(() => new EncryptedJsonStorage(
            foreign, Encoding.UTF8.GetBytes("secure passphrase value")));
    });

    await RunAsync("Live device flow uses the Minecraft Live endpoints and handles pending polling", async () =>
    {
        DateTimeOffset now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        Queue<HttpResponseMessage> responses = new([
            Json(HttpStatusCode.OK,
                """{"device_code":"device-fixture","user_code":"ABCD-EFGH","verification_uri":"https://www.microsoft.com/link","expires_in":900,"interval":5}"""),
            Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}"""),
            Json(HttpStatusCode.OK,
                """{"access_token":"access-fixture","refresh_token":"refresh-fixture","token_type":"bearer","expires_in":3600,"scope":"service::user.auth.xboxlive.com::MBI_SSL"}""")
        ]);
        RecordingHttpHandler handler = new(responses);
        using HttpClient http = new(handler);
        List<TimeSpan> delays = [];
        MicrosoftLiveDeviceCodeClient client = new(
            http,
            "00000000402b5328",
            "service::user.auth.xboxlive.com::MBI_SSL",
            () => now,
            (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            });
        MicrosoftDeviceCodePrompt? prompt = null;

        XboxAuthNet.OAuth.MicrosoftOAuthResponse token = await client.AuthenticateAsync(
            value => prompt = value,
            CancellationToken.None);

        Equal("ABCD-EFGH", prompt?.UserCode ?? string.Empty);
        Equal("https://www.microsoft.com/link", prompt?.VerificationUrl.TrimEnd('/') ?? string.Empty);
        Equal("access-fixture", token.AccessToken ?? string.Empty);
        Equal("refresh-fixture", token.RawRefreshToken ?? string.Empty);
        Equal(2, delays.Count);
        True(handler.Requests[0].StartsWith(MicrosoftLiveDeviceCodeClient.DeviceCodeEndpoint, StringComparison.Ordinal),
            "The device request did not use login.live.com.");
        True(handler.Requests.Skip(1).All(value => value.StartsWith(MicrosoftLiveDeviceCodeClient.TokenEndpoint, StringComparison.Ordinal)),
            "Token polling did not use login.live.com.");
        True(handler.Requests.All(value => !value.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)),
            "The legacy Minecraft client ID was incorrectly sent to an Entra/MSAL endpoint.");
        True(handler.FormBodies[0].Contains("client_id=00000000402b5328", StringComparison.Ordinal),
            "The Minecraft Java client ID was missing from the device request.");
    });

    await RunAsync("Live device flow honors slow_down and rejects non-Microsoft verification URLs", async () =>
    {
        DateTimeOffset now = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
        Queue<HttpResponseMessage> responses = new([
            Json(HttpStatusCode.OK,
                """{"device_code":"device-fixture","user_code":"ABCD-EFGH","verification_uri":"https://microsoft.com/link","expires_in":900,"interval":5}"""),
            Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}"""),
            Json(HttpStatusCode.OK,
                """{"access_token":"access-fixture","refresh_token":"refresh-fixture","token_type":"bearer","expires_in":3600}""")
        ]);
        using HttpClient http = new(new RecordingHttpHandler(responses));
        List<TimeSpan> delays = [];
        MicrosoftLiveDeviceCodeClient client = new(
            http,
            "00000000402b5328",
            "service::user.auth.xboxlive.com::MBI_SSL",
            () => now,
            (duration, _) =>
            {
                delays.Add(duration);
                now += duration;
                return Task.CompletedTask;
            });
        _ = await client.AuthenticateAsync(_ => { }, CancellationToken.None);
        Equal(TimeSpan.FromSeconds(5), delays[0]);
        Equal(TimeSpan.FromSeconds(10), delays[1]);

        using HttpClient hostileHttp = new(new RecordingHttpHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK,
                """{"device_code":"device-fixture","user_code":"ABCD-EFGH","verification_uri":"https://example.invalid/phish","expires_in":900,"interval":5}""")
        ])));
        MicrosoftLiveDeviceCodeClient hostile = new(
            hostileHttp,
            "00000000402b5328",
            "service::user.auth.xboxlive.com::MBI_SSL");
        await ThrowsAsync<MicrosoftDeviceAuthenticationException>(() =>
            hostile.AuthenticateAsync(_ => { }, CancellationToken.None));
    });

    await RunAsync("Live device flow rejects oversized authentication responses", async () =>
    {
        string oversized = new('x', MicrosoftLiveDeviceCodeClient.MaximumResponseBytes + 1);
        using HttpClient http = new(new RecordingHttpHandler(new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, oversized)
        ])));
        MicrosoftLiveDeviceCodeClient client = new(
            http,
            "00000000402b5328",
            "service::user.auth.xboxlive.com::MBI_SSL");
        await ThrowsAsync<MicrosoftDeviceAuthenticationException>(() =>
            client.AuthenticateAsync(_ => { }, CancellationToken.None));
    });

    Run("encrypted files reject a foreign envelope format", () =>
    {
        string path = Path.Combine(root, "envelope", "account-cache.bin");
        byte[] secret = Encoding.UTF8.GetBytes("independent protected cache key");
        byte[] payload = Encoding.UTF8.GetBytes("bounded encrypted fixture");
        try
        {
            using (EncryptedFileStorage storage = new(
                       path, secret, "OEXYZAC2", "account store", 8 * 1024 * 1024))
            {
                storage.Write(payload);
                byte[] clear = storage.Read() ?? throw new InvalidOperationException("Encrypted fixture was missing.");
                try { True(payload.AsSpan().SequenceEqual(clear), "Encrypted fixture round trip changed bytes."); }
                finally { CryptographicOperations.ZeroMemory(clear); }
            }

            byte[] encrypted = File.ReadAllBytes(path);
            True(encrypted.AsSpan().IndexOf(payload) < 0, "Encrypted fixture was visible on disk.");
            Throws<InvalidDataException>(() =>
            {
                using EncryptedFileStorage wrongFormat = new(
                    path, secret, "OEXYZMS1", "foreign cache", 4 * 1024 * 1024);
            });
            if (!OperatingSystem.IsWindows())
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(payload);
        }
    });

    Run("account key generation is private and never overwrites", () =>
    {
        string path = Path.Combine(root, "generated-key", "account.key");
        FileStreamOptions creationOptions = AccountKeyFile.CreatePrivateFileOptions();
        Equal(FileMode.CreateNew, creationOptions.Mode);
        Equal(FileShare.None, creationOptions.Share);
        if (!OperatingSystem.IsWindows())
            Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                creationOptions.UnixCreateMode ?? throw new InvalidOperationException("The Unix key mode was not set at creation."));
        AccountKeyFile.Generate(path);
        byte[] key = File.ReadAllBytes(path);
        try
        {
            Equal(44, key.Length);
            True(Convert.TryFromBase64String(Encoding.ASCII.GetString(key), new byte[32], out int written) && written == 32,
                "Generated account key was not a 256-bit base64 value.");
            Throws<IOException>(() => AccountKeyFile.Generate(path));
            if (!OperatingSystem.IsWindows())
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    });

    await RunAsync("authentication preparation initializes the store and consumes a Linux secret once", async () =>
    {
        int calls = 0;
        string path = Path.Combine(root, "prepare", "accounts.bin");
        AccountSecretProvider provider = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(Encoding.UTF8.GetBytes("prepare-only secure passphrase"));
        };
        await using AuthenticationService authentication = new(path, provider, _ => { });
        await authentication.PrepareAsync(_ => { }, CancellationToken.None);
        await authentication.PrepareAsync(_ => { }, CancellationToken.None);
        Equal(OperatingSystem.IsWindows() ? 0 : 1, calls);
        True(File.Exists(path), "Authentication preparation did not initialize the account store.");
        True(File.Exists(path + ".lock"), "Authentication preparation did not use the account-store lock.");
    });

    await RunAsync("silent reconnect never starts an interactive Microsoft flow", async () =>
    {
        string path = Path.Combine(root, "silent-only", "accounts.bin");
        int devicePrompts = 0;
        await using AuthenticationService authentication = new(
            path,
            (_, _) => ValueTask.FromResult(Encoding.UTF8.GetBytes("silent-only secure passphrase")),
            prompt => { if (prompt is not null) devicePrompts++; });
        AccountProfile profile = new()
        {
            DisplayName = "Silent-only test",
            Kind = AccountKind.Microsoft,
            LoginHint = "silent@example.invalid"
        };
        await ThrowsAsync<AuthenticationInteractionRequiredException>(() =>
            authentication.GetIdentityAsync(
                profile,
                _ => { },
                CancellationToken.None,
                AuthenticationInteractionMode.SilentOnly));
        Equal(0, devicePrompts);
        True(File.Exists(path), "Silent-only authentication did not leave a valid protected empty store.");
    });

    await RunAsync("corrupt account storage is not misclassified as interactive consent", async () =>
    {
        string path = Path.Combine(root, "corrupt-silent", "accounts.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [0x01, 0x02, 0x03, 0x04]);
        await using AuthenticationService authentication = new(
            path,
            (_, _) => ValueTask.FromResult(Encoding.UTF8.GetBytes("corrupt-store secure passphrase")),
            _ => { });
        AccountProfile profile = new()
        {
            DisplayName = "Corrupt store",
            Kind = AccountKind.Microsoft,
            LoginHint = "corrupt@example.invalid"
        };
        bool rejected = false;
        try
        {
            _ = await authentication.GetIdentityAsync(
                profile,
                _ => { },
                CancellationToken.None,
                AuthenticationInteractionMode.InteractiveAllowed);
        }
        catch (AuthenticationInteractionRequiredException exception)
        {
            throw new InvalidOperationException(
                "A corrupt account store was incorrectly reported as requiring interaction.", exception);
        }
        catch (Exception)
        {
            rejected = true;
        }
        True(rejected, "A corrupt account store was accepted unexpectedly.");
    });
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

Console.WriteLine($"PASS: {passed.Count} authentication storage tests");
foreach (string name in passed) Console.WriteLine($"  - {name}");
return;

void Run(string name, Action test)
{
    test();
    passed.Add(name);
}

async Task RunAsync(string name, Func<Task> test)
{
    await test();
    passed.Add(name);
}

static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
}

static void True(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void Throws<TException>(Action action) where TException : Exception
{
    try { action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task<TException> ThrowsAndReturnAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try { await action(); }
    catch (TException exception) { return exception; }
    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task RunAccountStoreWorkerAsync(
    string mode,
    string storePath,
    string gatePath,
    string workerId,
    string profileId)
{
    string ready = gatePath + ".ready-" + workerId;
    File.WriteAllText(ready, "ready");
    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
    while (!File.Exists(gatePath))
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("The account-store worker gate was never opened.");
        await Task.Delay(10);
    }

    await using (AuthenticationService service = new(
                     storePath,
                     (_, _) => ValueTask.FromResult(CreateTestAccountSecret()),
                     _ => { }))
    {
        // Preparation itself must participate in the same interprocess
        // transaction, most importantly while the first Linux salt is chosen.
        await service.PrepareAsync(_ => { }, CancellationToken.None);
    }

    AccountProfile profile = CreateMicrosoftProfile(profileId);
    _ = await MicrosoftAccountStore.ExecuteAsync(
        storePath,
        async _ =>
        {
            using AccountStorageHandle handle = OpenAccountStorage(mode, storePath);
            JsonXboxGameAccountManager manager = CreateAccountManager(handle.Storage);
            IXboxGameAccount? account = MicrosoftAccountStore.FindAccount(manager, profile);
            if (account is null)
            {
                account = manager.NewAccount();
                account.SessionStorage.Set(
                    JEProfileSource.KeyName,
                    new JEProfile
                    {
                        UUID = Guid.ParseExact(workerId, "N").ToString("N"),
                        Username = "Worker" + workerId[..8]
                    });
            }

            MicrosoftAccountStore.BindAccount(manager, account, profile.Id);
            await Task.Delay(75);
            manager.SaveAccounts();
            return account.Identifier ?? throw new InvalidOperationException("The synthetic account has no identifier.");
        },
        CancellationToken.None);
}

static AccountProfile CreateMicrosoftProfile(string profileId) => new()
{
    Id = Guid.ParseExact(profileId, "N"),
    DisplayName = "Profile " + profileId[..8],
    Kind = AccountKind.Microsoft,
    LoginHint = string.Empty,
    AccountIdentifier = null
};

static JsonXboxGameAccountManager CreateAccountManager(IJsonStorage storage) =>
    new(
        storage,
        JEGameAccount.FromSessionStorage,
        JsonXboxGameAccountManager.DefaultSerializerOption);

static AccountStorageHandle OpenAccountStorage(string mode, string path)
{
    if (string.Equals(mode, "dpapi", StringComparison.Ordinal))
        return new AccountStorageHandle(new ProtectedJsonStorage(path), null, null);
    if (!string.Equals(mode, "aes", StringComparison.Ordinal))
        throw new ArgumentException($"Unknown account-store worker mode '{mode}'.", nameof(mode));
    byte[] secret = CreateTestAccountSecret();
    try
    {
        EncryptedJsonStorage storage = new(path, secret);
        return new AccountStorageHandle(storage, storage, secret);
    }
    catch
    {
        CryptographicOperations.ZeroMemory(secret);
        throw;
    }
}

static byte[] CreateTestAccountSecret() =>
    Encoding.UTF8.GetBytes("parallel account store regression secret");

static Process StartTestProcess(params string[] arguments)
{
    string processPath = Environment.ProcessPath
                         ?? throw new InvalidOperationException("The test process path is unavailable.");
    ProcessStartInfo start = new(processPath)
    {
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };
    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start an account-store worker.");
}

static async Task WaitForWorkersAsync(Process[] workers, TimeSpan timeout)
{
    using CancellationTokenSource watchdog = new(timeout);
    try
    {
        await Task.WhenAll(workers.Select(worker => worker.WaitForExitAsync(watchdog.Token)));
    }
    catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
    {
        throw new TimeoutException($"Account-store workers exceeded the {timeout.TotalSeconds:0}-second watchdog.");
    }
}

static async Task StopAndDisposeWorkersAsync(IEnumerable<Process> workers)
{
    Process[] processes = workers.ToArray();
    StopWorkers(processes);
    using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(5));
    try
    {
        await Task.WhenAll(processes
            .Where(worker => !worker.HasExited)
            .Select(worker => worker.WaitForExitAsync(cleanupTimeout.Token)));
    }
    catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested)
    {
    }
    foreach (Process worker in processes) worker.Dispose();
}

static void StopWorkers(IEnumerable<Process> workers)
{
    foreach (Process worker in workers)
    {
        try
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or System.ComponentModel.Win32Exception
                                          or NotSupportedException)
        {
        }
    }
}

static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
{
    Content = new StringContent(body, Encoding.UTF8, "application/json")
};

internal sealed class RecordingHttpHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public List<string> FormBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
        FormBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (responses.Count == 0) throw new InvalidOperationException("No fake HTTP response remained.");
        return responses.Dequeue();
    }
}

internal sealed class AccountStorageHandle(
    IJsonStorage storage,
    IDisposable? disposable,
    byte[]? secret) : IDisposable
{
    public IJsonStorage Storage { get; } = storage;

    public void Dispose()
    {
        disposable?.Dispose();
        if (secret is not null) CryptographicOperations.ZeroMemory(secret);
    }
}
