using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OeXYZ.Core;

public static partial class SensitiveDataRedactor
{
    internal const int MaximumStructuredJsonCharacters = 256 * 1024;
    private const int MaximumStructuredJsonDepth = 64;
    private const string RedactedValue = "[REDACTED]";
    private const string RedactedJsonDocument = "\"[REDACTED]\"";

    private static readonly HashSet<string> SensitiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "log", "l", "register", "reg", "changepassword", "password", "passwd"
    };

    private static readonly HashSet<string> SensitiveJsonProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "token",
        "access_token", "access-token", "accesstoken",
        "refresh_token", "refresh-token", "refreshtoken",
        "client_secret", "client-secret", "clientsecret"
    };

    public static bool IsSensitiveCommand(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               TryGetSensitiveCommand(value.AsSpan(), out _, out _);
    }

    public static string RedactCommand(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!TryGetSensitiveCommand(value.AsSpan(), out _, out int commandEnd)) return value;
        return value[..commandEnd] + " [REDACTED]";
    }

    public static string RedactText(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (TryRedactStructuredJson(value, out string structured)) return structured;
        return RedactUnstructuredText(value);
    }

    private static string RedactUnstructuredText(string value)
    {
        string result = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        result = JsonSecretRegex().Replace(result, static match =>
            match.Groups[1].Value + "\"[REDACTED]\"");
        result = KeyValueSecretRegex().Replace(result, "$1[REDACTED]");
        return SensitiveCommandLineRegex().Replace(result, "$1 [REDACTED]");
    }

    private static bool TryRedactStructuredJson(string value, out string redacted)
    {
        ReadOnlySpan<char> candidate = value.AsSpan().Trim();
        redacted = string.Empty;
        if (candidate.Length < 2 ||
            !((candidate[0] == '{' && candidate[^1] == '}') ||
              (candidate[0] == '[' && candidate[^1] == ']')))
            return false;

        if (candidate.Length > MaximumStructuredJsonCharacters)
        {
            redacted = RedactedJsonDocument;
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(candidate.ToString(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumStructuredJsonDepth
            });
            ArrayBufferWriter<byte> buffer = new(Math.Min(candidate.Length, 4096));
            using (Utf8JsonWriter writer = new(buffer))
            {
                WriteRedactedJson(writer, document.RootElement);
                writer.Flush();
            }
            redacted = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        catch (JsonException)
        {
            // A complete-looking JSON value must fail closed. Falling back to
            // text regexes here could expose escaped property names or compound
            // values when parsing fails because of malformed/deep input.
            redacted = RedactedJsonDocument;
        }
        return true;
    }

    private static void WriteRedactedJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (SensitiveJsonProperties.Contains(property.Name)) writer.WriteStringValue(RedactedValue);
                    else WriteRedactedJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray()) WriteRedactedJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactUnstructuredText(element.GetString() ?? string.Empty));
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool TryGetSensitiveCommand(
        ReadOnlySpan<char> value,
        out int commandStart,
        out int commandEnd)
    {
        commandStart = 0;
        while (commandStart < value.Length && char.IsWhiteSpace(value[commandStart])) commandStart++;
        commandEnd = commandStart;
        if (commandStart >= value.Length || value[commandStart] != '/') return false;

        commandEnd = commandStart + 1;
        while (commandEnd < value.Length && !char.IsWhiteSpace(value[commandEnd])) commandEnd++;
        ReadOnlySpan<char> name = value[(commandStart + 1)..commandEnd];
        int namespaceSeparator = name.LastIndexOf(':');
        if (namespaceSeparator >= 0) name = name[(namespaceSeparator + 1)..];
        return name.Length > 0 && SensitiveCommands.Contains(name.ToString());
    }

    [GeneratedRegex("(?i)(Bearer\\s+)[A-Za-z0-9._~+/=-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("""(?i)("(?:access[_-]?token|refresh[_-]?token|token|password|client[_-]?secret)"\s*:\s*)("(?:\\.|[^"\\])*"|[^,}\]\s]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretRegex();

    [GeneratedRegex("""(?i)((?<!["A-Za-z0-9_])(?:access[_-]?token|refresh[_-]?token|password|client[_-]?secret)\s*[=:]\s*)("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|[^\s,;&]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex("""(?im)(/(?:[a-z0-9_.-]+:)*(?:login|log|l|register|reg|changepassword|password|passwd))(?=\s|$)[^\r\n]*""", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveCommandLineRegex();
}
