namespace OeXYZ.Core;

public sealed record CliArguments(
    string Command,
    string? Target,
    string? Account,
    string? Address,
    string? MinecraftVersion,
    string? Group,
    string? LoginHint,
    int Port,
    string? ConfigPath,
    string? LogFile,
    string LogLevel,
    bool InspectPackets,
    string? AccountKeyFile,
    int HealthPort,
    bool Dashboard,
    bool NoInput,
    int MaximumSessions,
    bool JsonOutput,
    bool ShowHelp)
{
    public static CliArguments Parse(IReadOnlyList<string> arguments)
    {
        string? command = null;
        string? target = null;
        string? account = null;
        string? address = null;
        string? minecraftVersion = null;
        string? group = null;
        string? loginHint = null;
        int port = 0;
        string? config = null;
        string? logFile = null;
        string logLevel = "information";
        bool inspect = false;
        string? accountKeyFile = null;
        int healthPort = 0;
        bool dashboard = false;
        bool noInput = false;
        int maximumSessions = 16;
        bool jsonOutput = false;
        bool help = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string value = arguments[index];
            switch (value.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": help = true; break;
                case "--inspect-packets": inspect = true; break;
                case "--dashboard": dashboard = true; break;
                case "--no-input": noInput = true; break;
                case "--json": jsonOutput = true; break;
                case "--account": account = ReadValue(arguments, ref index, value); break;
                case "--address": address = ReadValue(arguments, ref index, value); break;
                case "--minecraft-version": minecraftVersion = ReadValue(arguments, ref index, value); break;
                case "--group": group = ReadValue(arguments, ref index, value); break;
                case "--login-hint": loginHint = ReadValue(arguments, ref index, value); break;
                case "--port": port = ReadInteger(arguments, ref index, value, 1, 65535); break;
                case "--config": config = ReadValue(arguments, ref index, value); break;
                case "--log-file": logFile = ReadValue(arguments, ref index, value); break;
                case "--log-level": logLevel = ReadValue(arguments, ref index, value).ToLowerInvariant(); break;
                case "--account-key-file": accountKeyFile = ReadValue(arguments, ref index, value); break;
                case "--health-port": healthPort = ReadInteger(arguments, ref index, value, 1, 65535); break;
                case "--max-sessions": maximumSessions = ReadInteger(arguments, ref index, value, 1, 128); break;
                default:
                    if (value.StartsWith("-", StringComparison.Ordinal))
                        throw new ArgumentException($"Unknown option: {value}");
                    if (command is null) command = value.ToLowerInvariant();
                    else if (target is null) target = value;
                    else throw new ArgumentException($"Unexpected argument: {value}");
                    break;
            }
        }

        if (logLevel is not ("trace" or "debug" or "information" or "warning" or "error"))
            throw new ArgumentException("--log-level must be trace, debug, information, warning, or error.");
        return new CliArguments(
            command ?? string.Empty,
            target,
            account,
            address,
            minecraftVersion,
            group,
            loginHint,
            port,
            config,
            logFile,
            logLevel,
            inspect,
            accountKeyFile,
            healthPort,
            dashboard,
            noInput,
            maximumSessions,
            jsonOutput,
            help);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]) ||
            arguments[index].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }

    private static int ReadInteger(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        int minimum,
        int maximum)
    {
        string raw = ReadValue(arguments, ref index, option);
        if (!int.TryParse(raw, out int value) || value < minimum || value > maximum)
            throw new ArgumentException($"{option} must be between {minimum} and {maximum}.");
        return value;
    }
}
