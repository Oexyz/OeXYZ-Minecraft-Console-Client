using OeXYZ.Core;
using OeXYZ.Protocol;
using System.Text;

namespace OeXYZ.Cli;

internal sealed class SetupWizard
{
    internal const int MaximumManagedSessions = 16;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<AccountProfile, CancellationToken, Task<string>> microsoftLogin;
    private readonly Action<ProfileDocument> save;
    private readonly bool container;

    public SetupWizard(
        TextReader input,
        TextWriter output,
        Func<AccountProfile, CancellationToken, Task<string>> microsoftLogin,
        Action<ProfileDocument> save,
        bool container = false)
    {
        this.input = input ?? throw new ArgumentNullException(nameof(input));
        this.output = new TerminalSafeTextWriter(output ?? throw new ArgumentNullException(nameof(output)));
        this.microsoftLogin = microsoftLogin ?? throw new ArgumentNullException(nameof(microsoftLogin));
        this.save = save ?? throw new ArgumentNullException(nameof(save));
        this.container = container;
    }

    public async Task RunAsync(ProfileDocument profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        output.WriteLine("OeXYZ guided setup");
        output.WriteLine("Profiles and encrypted sessions stay in the configured persistent volumes.");
        output.WriteLine();

        int accountsAdded = 0;
        if (profiles.Accounts.Count > 0)
        {
            PrintAccounts(profiles);
            while (ReadYesNo("Add another account?", defaultValue: false))
            {
                await AddAccountAsync(profiles, cancellationToken).ConfigureAwait(false);
                accountsAdded++;
            }
        }
        else
        {
            do
            {
                await AddAccountAsync(profiles, cancellationToken).ConfigureAwait(false);
                accountsAdded++;
            } while (ReadYesNo("Add another account?", defaultValue: false));
        }

        bool mustAddSession = profiles.ManagedSessions.Count == 0;
        bool addSession = mustAddSession || ReadYesNo(
            accountsAdded > 0 ? "Configure a managed session for the new account(s)?" : "Add another managed session?",
            defaultValue: accountsAdded > 0);
        while (addSession)
        {
            if (profiles.ManagedSessions.Count >= MaximumManagedSessions)
                throw new InvalidDataException($"At most {MaximumManagedSessions} managed sessions may be configured.");

            AccountProfile account = SelectAccount(profiles);
            ServerProfile server = SelectOrAddServer(profiles);
            SessionBookmark binding = new() { AccountId = account.Id, ServerId = server.Id };
            if (profiles.ManagedSessions.Contains(binding))
            {
                output.WriteLine($"{account.DisplayName} -> {server.DisplayName} is already configured.");
            }
            else
            {
                profiles.ManagedSessions.Add(binding);
                save(profiles);
                output.WriteLine($"Managed session added: {account.DisplayName} -> {server.DisplayName}");
            }
            addSession = ReadYesNo("Add another managed session?", defaultValue: false);
        }

        save(profiles);
        PrintSummary(profiles);
        output.WriteLine();
        if (container)
        {
            output.WriteLine("Setup complete. Start the background client with one of:");
            output.WriteLine("  Public latest image: docker compose up -d");
            output.WriteLine("  Local source build: docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --no-build");
            output.WriteLine("Follow received messages with: docker compose logs --follow oexyz");
        }
        else
        {
            output.WriteLine("Setup complete. Start a native supervisor with: oexyz supervise --no-input");
            output.WriteLine("For Microsoft service sessions, also provide the account key or use the supplied systemd unit.");
        }
    }

    private async Task AddAccountAsync(ProfileDocument profiles, CancellationToken cancellationToken)
    {
        string type = ReadChoice(
            "Account type: [1] Microsoft  [2] Offline",
            "1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1"] = "microsoft",
                ["m"] = "microsoft",
                ["microsoft"] = "microsoft",
                ["2"] = "offline",
                ["o"] = "offline",
                ["offline"] = "offline"
            });

        if (type == "offline")
        {
            string offlinePlayerName;
            while (true)
            {
                offlinePlayerName = ReadRequired("Offline player name", 16);
                try { ProfileRules.EnsureValidOfflineName(offlinePlayerName); }
                catch (InvalidDataException exception)
                {
                    output.WriteLine(exception.Message);
                    continue;
                }
                if (profiles.Accounts.Any(account =>
                        string.Equals(account.DisplayName, offlinePlayerName, StringComparison.OrdinalIgnoreCase)))
                {
                    output.WriteLine($"An account profile named '{offlinePlayerName}' already exists.");
                    continue;
                }
                break;
            }

            profiles.Accounts.Add(new AccountProfile
            {
                DisplayName = offlinePlayerName,
                Kind = AccountKind.Offline,
                LoginHint = offlinePlayerName
            });
            save(profiles);
            output.WriteLine($"Offline account '{offlinePlayerName}' added. It works only on offline-mode servers.");
            return;
        }

        string profileName = ReadUniqueName("Microsoft account profile name", "account", profiles.Accounts.Select(x => x.DisplayName));
        string loginHint = ReadOptional("Login email hint (optional)", 256);
        AccountProfile microsoft = new()
        {
            DisplayName = profileName,
            Kind = AccountKind.Microsoft,
            LoginHint = loginHint
        };
        output.WriteLine("Microsoft sign-in will now open or show a device-code prompt.");
        string signedInName = await microsoftLogin(microsoft, cancellationToken).ConfigureAwait(false);
        profiles.Accounts.Add(microsoft);
        save(profiles);
        output.WriteLine($"Microsoft profile '{profileName}' is ready as {signedInName}.");
    }

    private AccountProfile SelectAccount(ProfileDocument profiles)
    {
        if (profiles.Accounts.Count == 1) return profiles.Accounts[0];
        output.WriteLine("Choose the account for this session:");
        for (int index = 0; index < profiles.Accounts.Count; index++)
        {
            AccountProfile account = profiles.Accounts[index];
            output.WriteLine($"  [{index + 1}] {account.DisplayName} ({account.Kind})");
        }
        int selected = ReadNumber("Account", 1, profiles.Accounts.Count, 1);
        return profiles.Accounts[selected - 1];
    }

    private ServerProfile SelectOrAddServer(ProfileDocument profiles)
    {
        if (profiles.Servers.Count == 0) return AddServer(profiles);
        output.WriteLine("Choose an existing server or add a new one:");
        for (int index = 0; index < profiles.Servers.Count; index++)
        {
            ServerProfile server = profiles.Servers[index];
            output.WriteLine($"  [{index + 1}] {server.DisplayName} ({server.Address})");
        }
        int addIndex = profiles.Servers.Count + 1;
        output.WriteLine($"  [{addIndex}] Add a new server");
        int selected = ReadNumber("Server", 1, addIndex, 1);
        return selected == addIndex ? AddServer(profiles) : profiles.Servers[selected - 1];
    }

    private ServerProfile AddServer(ProfileDocument profiles)
    {
        string name = ReadUniqueName("Server profile name", "server", profiles.Servers.Select(x => x.DisplayName));
        string address;
        while (true)
        {
            address = ReadRequired("Server address (host or host:port)", 512);
            try { _ = ServerAddress.Parse(address); }
            catch (FormatException exception)
            {
                output.WriteLine(exception.Message);
                continue;
            }
            break;
        }

        string group = ReadOptional("Group", 64, "AFK");
        string version;
        while (true)
        {
            version = ReadOptional("Minecraft version", 64, "auto");
            try
            {
                if (!string.Equals(version, "auto", StringComparison.OrdinalIgnoreCase))
                    _ = ProtocolCatalog.LoadEmbedded().Resolve(version);
                break;
            }
            catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
            {
                output.WriteLine(exception.Message);
            }
        }

        ServerProfile server = new()
        {
            DisplayName = name,
            Address = address,
            Version = version,
            Group = group
        };
        profiles.Servers.Add(server);
        return server;
    }

    private void PrintAccounts(ProfileDocument profiles)
    {
        output.WriteLine("Existing accounts:");
        foreach (AccountProfile account in profiles.Accounts)
            output.WriteLine($"  - {account.DisplayName} ({account.Kind})");
        output.WriteLine();
    }

    private void PrintSummary(ProfileDocument profiles)
    {
        output.WriteLine();
        output.WriteLine($"Configured {profiles.Accounts.Count} account(s), {profiles.Servers.Count} server(s), " +
                         $"and {profiles.ManagedSessions.Count} managed session(s):");
        foreach (SessionBookmark binding in profiles.ManagedSessions)
        {
            AccountProfile? account = profiles.Accounts.FirstOrDefault(item => item.Id == binding.AccountId);
            ServerProfile? server = profiles.Servers.FirstOrDefault(item => item.Id == binding.ServerId);
            if (account is not null && server is not null)
                output.WriteLine($"  - {account.DisplayName} -> {server.DisplayName} ({server.Address})");
        }
    }

    private string ReadUniqueName(string prompt, string kind, IEnumerable<string> existingNames)
    {
        HashSet<string> names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            string value;
            try { value = ProfileRules.NormalizeProfileName(ReadRequired(prompt, 64), kind); }
            catch (InvalidDataException exception)
            {
                output.WriteLine(exception.Message);
                continue;
            }
            if (names.Contains(value))
            {
                output.WriteLine($"A {kind} profile named '{value}' already exists.");
                continue;
            }
            return value;
        }
    }

    private bool ReadYesNo(string prompt, bool defaultValue)
    {
        while (true)
        {
            output.Write($"{prompt} [{(defaultValue ? "Y/n" : "y/N")}]: ");
            output.Flush();
            string value = ReadLine().Trim();
            if (value.Length == 0) return defaultValue;
            if (value is "y" or "Y" || value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("j", StringComparison.OrdinalIgnoreCase) || value.Equals("ja", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value is "n" or "N" || value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("nein", StringComparison.OrdinalIgnoreCase))
                return false;
            output.WriteLine("Please answer yes or no.");
        }
    }

    private string ReadChoice(
        string prompt,
        string defaultValue,
        IReadOnlyDictionary<string, string> choices)
    {
        while (true)
        {
            output.Write($"{prompt} [{defaultValue}]: ");
            output.Flush();
            string value = ReadLine().Trim();
            if (value.Length == 0) value = defaultValue;
            if (choices.TryGetValue(value, out string? result)) return result;
            output.WriteLine("Please choose one of the displayed options.");
        }
    }

    private int ReadNumber(string prompt, int minimum, int maximum, int defaultValue)
    {
        while (true)
        {
            output.Write($"{prompt} [{defaultValue}]: ");
            output.Flush();
            string value = ReadLine().Trim();
            if (value.Length == 0) return defaultValue;
            if (int.TryParse(value, out int parsed) && parsed >= minimum && parsed <= maximum) return parsed;
            output.WriteLine($"Enter a number between {minimum} and {maximum}.");
        }
    }

    private string ReadRequired(string prompt, int maximumLength)
    {
        while (true)
        {
            output.Write($"{prompt}: ");
            output.Flush();
            string value = ReadLine().Trim();
            if (value.Length is > 0 && value.Length <= maximumLength && value.IndexOf('\0') < 0) return value;
            output.WriteLine($"A value of 1-{maximumLength} characters is required.");
        }
    }

    private string ReadOptional(string prompt, int maximumLength, string defaultValue = "")
    {
        while (true)
        {
            output.Write(defaultValue.Length == 0 ? $"{prompt}: " : $"{prompt} [{defaultValue}]: ");
            output.Flush();
            string value = ReadLine().Trim();
            if (value.Length == 0) return defaultValue;
            if (value.Length <= maximumLength && value.IndexOf('\0') < 0) return value;
            output.WriteLine($"The value must not exceed {maximumLength} characters.");
        }
    }

    private string ReadLine() => input.ReadLine()
        ?? throw new InvalidDataException("Setup stopped because standard input was closed.");

    private sealed class TerminalSafeTextWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Flush() => inner.Flush();
        public override Task FlushAsync() => inner.FlushAsync();
        public override void Write(char value) => inner.Write(value);
        public override void Write(char[] buffer, int index, int count) =>
            inner.Write(TerminalTextSanitizer.Sanitize(new string(buffer, index, count)));
        public override void Write(string? value) => inner.Write(TerminalTextSanitizer.Sanitize(value));
        public override void WriteLine() => inner.WriteLine();
        public override void WriteLine(string? value) => inner.WriteLine(TerminalTextSanitizer.Sanitize(value));
    }
}
