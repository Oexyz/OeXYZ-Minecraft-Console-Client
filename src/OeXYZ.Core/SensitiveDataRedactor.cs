using System.Text.RegularExpressions;

namespace OeXYZ.Core;

public static partial class SensitiveDataRedactor
{
    private static readonly HashSet<string> SensitiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "log", "l", "register", "reg", "changepassword", "password", "passwd"
    };

    public static bool IsSensitiveCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '/') return false;
        ReadOnlySpan<char> command = value.AsSpan(1);
        int separator = command.IndexOfAny(' ', '\t');
        string name = (separator < 0 ? command : command[..separator]).ToString();
        return SensitiveCommands.Contains(name);
    }

    public static string RedactCommand(string value)
    {
        if (!IsSensitiveCommand(value)) return value;
        string trimmed = value.AsSpan(1).TrimStart().ToString();
        int separator = trimmed.IndexOfAny(' ', '\t');
        string command = separator < 0 ? trimmed : trimmed[..separator];
        return $"/{command} [REDACTED]";
    }

    public static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        string result = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        return JsonTokenRegex().Replace(result, "$1[REDACTED]$3");
    }

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+/=-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)(\\\"(?:access_token|refresh_token|token)\\\"\\s*:\\s*\\\")([^\\\"]+)(\\\")", RegexOptions.CultureInvariant)]
    private static partial Regex JsonTokenRegex();
}
