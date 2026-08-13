namespace OeXYZ.Core;

public sealed record CliArguments(
    string Command,
    string? Target,
    string? Account,
    string? ConfigPath,
    string? LogFile,
    string LogLevel,
    bool InspectPackets,
    bool ShowHelp)
{
    public static CliArguments Parse(IReadOnlyList<string> arguments)
    {
        string? command = null;
        string? target = null;
        string? account = null;
        string? config = null;
        string? logFile = null;
        string logLevel = "information";
        bool inspect = false;
        bool help = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string value = arguments[index];
            switch (value.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?": help = true; break;
                case "--inspect-packets": inspect = true; break;
                case "--account": account = ReadValue(arguments, ref index, value); break;
                case "--config": config = ReadValue(arguments, ref index, value); break;
                case "--log-file": logFile = ReadValue(arguments, ref index, value); break;
                case "--log-level": logLevel = ReadValue(arguments, ref index, value).ToLowerInvariant(); break;
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
        return new CliArguments(command ?? string.Empty, target, account, config, logFile, logLevel, inspect, help);
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }
}
