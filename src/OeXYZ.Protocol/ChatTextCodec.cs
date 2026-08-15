using System.Text;
using System.Text.Json;

namespace OeXYZ.Protocol;

public static class ChatTextCodec
{
    internal const int MaximumNbtListElements = 4_096;
    internal const int MaximumNbtCollectionElements = 8_192;
    private const int MaximumNbtNodes = 8_192;
    private const long MaximumNbtEstimatedAllocationBytes = 4L * 1024 * 1024;

    public static string FromJson(string json)
        => ParseJson(json).Text;

    public static FormattedChatText ParseJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            List<ChatRun> runs = [];
            AppendJson(document.RootElement, new ChatStyle(), runs);
            return Build(runs);
        }
        catch (JsonException)
        {
            return ParseLegacy(json);
        }
    }

    public static FormattedChatText ParseLegacy(string text, ChatStyle? initialStyle = null)
    {
        List<ChatRun> runs = [];
        ChatStyle style = initialStyle ?? new ChatStyle();
        StringBuilder buffer = new();

        void Flush()
        {
            if (buffer.Length == 0) return;
            AppendRun(runs, buffer.ToString(), style);
            buffer.Clear();
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] != '§' || index + 1 >= text.Length)
            {
                buffer.Append(text[index]);
                continue;
            }

            char code = char.ToLowerInvariant(text[++index]);
            if (code == 'x' && TryReadHexColor(text, ref index, out string? hexColor))
            {
                Flush();
                style = new ChatStyle(hexColor);
                continue;
            }

            string? color = LegacyColor(code);
            if (color is not null)
            {
                Flush();
                style = new ChatStyle(color);
                continue;
            }

            switch (code)
            {
                case 'l':
                    Flush();
                    style = style with { Bold = true };
                    break;
                case 'o':
                    Flush();
                    style = style with { Italic = true };
                    break;
                case 'n':
                    Flush();
                    style = style with { Underlined = true };
                    break;
                case 'm':
                    Flush();
                    style = style with { Strikethrough = true };
                    break;
                case 'r':
                    Flush();
                    style = initialStyle ?? new ChatStyle();
                    break;
            }
        }
        Flush();
        return Build(runs);
    }

    public static string? TranslationKeyFromJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("translate", out JsonElement translate)
                ? translate.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string FromAnonymousNbt(ref PacketReader reader)
        => ParseAnonymousNbt(ref reader).Text;

    public static FormattedChatText ParseAnonymousNbt(ref PacketReader reader)
    {
        try { return ReadAnonymousNbtFormatting(ref reader); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return ParseLegacy("[Unreadable server message]");
        }
    }

    internal static FormattedChatText ReadAnonymousNbtFormatting(ref PacketReader reader)
    {
        NbtParseBudget budget = new();
        object? value = ReadPayload(ref reader, reader.ReadByte(), 0, budget);
        List<ChatRun> runs = [];
        AppendNbt(value, new ChatStyle(), runs);
        return Build(runs);
    }

    private static string FlattenJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(FlattenJson)),
            JsonValueKind.Object => FlattenJsonObject(element),
            _ => string.Empty
        };
    }

    private static void AppendJson(JsonElement element, ChatStyle inherited, List<ChatRun> runs)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AppendFormattedLegacy(runs, element.GetString() ?? string.Empty, inherited);
                return;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                AppendFormattedLegacy(runs, FlattenJson(element), inherited);
                return;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray()) AppendJson(child, inherited, runs);
                return;
            case JsonValueKind.Object:
                ChatStyle style = ReadStyle(element, inherited);
                if (element.TryGetProperty("text", out JsonElement text))
                    AppendJson(text, style, runs);
                else if (element.TryGetProperty("translate", out JsonElement translate))
                {
                    string key = translate.GetString() ?? string.Empty;
                    string[] arguments = element.TryGetProperty("with", out JsonElement with) && with.ValueKind == JsonValueKind.Array
                        ? with.EnumerateArray().Select(FlattenJson).ToArray()
                        : [];
                    AppendFormattedLegacy(runs, Translate(key, arguments), style);
                }
                if (element.TryGetProperty("extra", out JsonElement extra)) AppendJson(extra, style, runs);
                return;
        }
    }

    private static ChatStyle ReadStyle(JsonElement element, ChatStyle inherited)
    {
        string? color = inherited.Color;
        if (element.TryGetProperty("color", out JsonElement colorElement) && colorElement.ValueKind == JsonValueKind.String)
        {
            string? requested = colorElement.GetString();
            color = string.Equals(requested, "reset", StringComparison.OrdinalIgnoreCase) ? null : requested;
        }
        return new ChatStyle(
            color,
            ReadBooleanStyle(element, "bold", inherited.Bold),
            ReadBooleanStyle(element, "italic", inherited.Italic),
            ReadBooleanStyle(element, "underlined", inherited.Underlined),
            ReadBooleanStyle(element, "strikethrough", inherited.Strikethrough));
    }

    private static bool ReadBooleanStyle(JsonElement element, string name, bool inherited) =>
        element.TryGetProperty(name, out JsonElement property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : inherited;

    private static void AppendFormattedLegacy(List<ChatRun> destination, string text, ChatStyle style)
    {
        FormattedChatText parsed = ParseLegacy(text, style);
        foreach (ChatRun run in parsed.Runs) AppendRun(destination, run.Text, run.Style);
    }

    private static void AppendRun(List<ChatRun> runs, string text, ChatStyle style)
    {
        text = TerminalTextSanitizer.Sanitize(text);
        if (text.Length == 0) return;
        if (runs.Count > 0 && runs[^1].Style == style)
            runs[^1] = runs[^1] with { Text = runs[^1].Text + text };
        else
            runs.Add(new ChatRun(text, style));
    }

    private static FormattedChatText Build(List<ChatRun> runs) =>
        new(string.Concat(runs.Select(run => run.Text)), runs.ToArray());

    private static bool TryReadHexColor(string text, ref int index, out string? color)
    {
        int cursor = index + 1;
        Span<char> digits = stackalloc char[6];
        for (int digit = 0; digit < digits.Length; digit++)
        {
            if (cursor + 1 >= text.Length || text[cursor] != '§' || !Uri.IsHexDigit(text[cursor + 1]))
            {
                color = null;
                return false;
            }
            digits[digit] = text[cursor + 1];
            cursor += 2;
        }
        index = cursor - 1;
        color = "#" + new string(digits);
        return true;
    }

    private static string? LegacyColor(char code) => code switch
    {
        '0' => "black",
        '1' => "dark_blue",
        '2' => "dark_green",
        '3' => "dark_aqua",
        '4' => "dark_red",
        '5' => "dark_purple",
        '6' => "gold",
        '7' => "gray",
        '8' => "dark_gray",
        '9' => "blue",
        'a' => "green",
        'b' => "aqua",
        'c' => "red",
        'd' => "light_purple",
        'e' => "yellow",
        'f' => "white",
        _ => null
    };

    private static string FlattenJsonObject(JsonElement element)
    {
        string result = string.Empty;
        if (element.TryGetProperty("text", out JsonElement text)) result += FlattenJson(text);
        else if (element.TryGetProperty("translate", out JsonElement translate))
        {
            string key = translate.GetString() ?? string.Empty;
            string[] arguments = element.TryGetProperty("with", out JsonElement with) && with.ValueKind == JsonValueKind.Array
                ? with.EnumerateArray().Select(FlattenJson).ToArray()
                : [];
            result += Translate(key, arguments);
        }
        if (element.TryGetProperty("extra", out JsonElement extra)) result += FlattenJson(extra);
        return result;
    }

    private static object? ReadPayload(
        ref PacketReader reader,
        byte type,
        int depth,
        NbtParseBudget budget)
    {
        if (depth > 64) throw new InvalidDataException("NBT nesting is too deep.");
        budget.ConsumeNode();
        return type switch
        {
            0 => null,
            1 => reader.ReadSignedByte(),
            2 => reader.ReadShort(),
            3 => reader.ReadInt(),
            4 => reader.ReadLong(),
            5 => reader.ReadFloat(),
            6 => reader.ReadDouble(),
            7 => ReadByteArray(ref reader, budget),
            8 => ReadNbtString(ref reader, budget),
            9 => ReadList(ref reader, depth + 1, budget),
            10 => ReadCompound(ref reader, depth + 1, budget),
            11 => ReadIntArray(ref reader, budget),
            12 => ReadLongArray(ref reader, budget),
            _ => throw new InvalidDataException($"Unknown NBT tag type {type}.")
        };
    }

    private static List<object?> ReadList(ref PacketReader reader, int depth, NbtParseBudget budget)
    {
        byte elementType = reader.ReadByte();
        int length = CheckedLength(reader.ReadInt(), IntPtr.Size, MaximumNbtListElements);
        if (elementType == 0 && length != 0)
            throw new InvalidDataException("An NBT TAG_End list must be empty.");
        budget.ReserveCollection(length, IntPtr.Size);
        List<object?> values = new(length);
        for (int index = 0; index < length; index++)
            values.Add(ReadPayload(ref reader, elementType, depth, budget));
        return values;
    }

    private static Dictionary<string, object?> ReadCompound(
        ref PacketReader reader,
        int depth,
        NbtParseBudget budget)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        while (true)
        {
            byte childType = reader.ReadByte();
            if (childType == 0) return values;
            string name = ReadNbtString(ref reader, budget);
            values[name] = ReadPayload(ref reader, childType, depth, budget);
        }
    }

    private static byte[] ReadByteArray(ref PacketReader reader, NbtParseBudget budget)
    {
        int length = CheckedLength(reader.ReadInt(), 1, MaximumNbtCollectionElements);
        budget.ReserveCollection(length, 1);
        return reader.ReadBytes(length);
    }

    private static int[] ReadIntArray(ref PacketReader reader, NbtParseBudget budget)
    {
        int length = CheckedLength(reader.ReadInt(), sizeof(int), MaximumNbtCollectionElements);
        budget.ReserveCollection(length, sizeof(int));
        int[] values = new int[length];
        for (int index = 0; index < length; index++) values[index] = reader.ReadInt();
        return values;
    }

    private static long[] ReadLongArray(ref PacketReader reader, NbtParseBudget budget)
    {
        int length = CheckedLength(reader.ReadInt(), sizeof(long), MaximumNbtCollectionElements);
        budget.ReserveCollection(length, sizeof(long));
        long[] values = new long[length];
        for (int index = 0; index < length; index++) values[index] = reader.ReadLong();
        return values;
    }

    private static string ReadNbtString(ref PacketReader reader, NbtParseBudget budget)
    {
        int before = reader.Remaining;
        string value = reader.ReadNbtString();
        budget.ReserveStringBytes(before - reader.Remaining - sizeof(ushort));
        return value;
    }

    private static int CheckedLength(int count, int bytesPerElement, int maximumElements)
    {
        if (count < 0 || count > maximumElements ||
            (long)count * bytesPerElement > MaximumNbtEstimatedAllocationBytes)
            throw new InvalidDataException("NBT collection length is outside safety limits.");
        return count;
    }

    private sealed class NbtParseBudget
    {
        private int nodes;
        private int collectionElements;
        private long estimatedAllocationBytes;

        public void ConsumeNode()
        {
            if (++nodes > MaximumNbtNodes)
                throw new InvalidDataException("NBT node budget was exceeded.");
            ReserveAllocation(32);
        }

        public void ReserveCollection(int count, int bytesPerElement)
        {
            if (count < 0 || collectionElements > MaximumNbtCollectionElements - count)
                throw new InvalidDataException("NBT collection budget was exceeded.");
            collectionElements += count;
            ReserveAllocation((long)count * bytesPerElement);
        }

        public void ReserveStringBytes(int encodedBytes)
        {
            if (encodedBytes < 0) throw new InvalidDataException("NBT string length is invalid.");
            ReserveAllocation((long)encodedBytes * sizeof(char));
        }

        private void ReserveAllocation(long bytes)
        {
            if (bytes < 0 || estimatedAllocationBytes > MaximumNbtEstimatedAllocationBytes - bytes)
                throw new InvalidDataException("NBT allocation budget was exceeded.");
            estimatedAllocationBytes += bytes;
        }
    }

    private static string FlattenNbt(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case sbyte or short or int or long or float or double:
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            case List<object?> list:
                return string.Concat(list.Select(FlattenNbt));
            case Dictionary<string, object?> compound:
                string result = string.Empty;
                if (compound.TryGetValue("text", out object? textValue)) result += FlattenNbt(textValue);
                else if (compound.TryGetValue(string.Empty, out object? unnamedText)) result += FlattenNbt(unnamedText);
                else if (compound.TryGetValue("translate", out object? translate))
                {
                    string key = FlattenNbt(translate);
                    string[] arguments = compound.TryGetValue("with", out object? with) && with is List<object?> list
                        ? list.Select(FlattenNbt).ToArray()
                        : [];
                    result += Translate(key, arguments);
                }
                if (compound.TryGetValue("extra", out object? extra)) result += FlattenNbt(extra);
                return result;
            default:
                return string.Empty;
        }
    }

    private static void AppendNbt(object? value, ChatStyle inherited, List<ChatRun> runs)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                AppendFormattedLegacy(runs, text, inherited);
                return;
            case sbyte or short or int or long or float or double:
                AppendFormattedLegacy(runs,
                    Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    inherited);
                return;
            case List<object?> list:
                foreach (object? child in list) AppendNbt(child, inherited, runs);
                return;
            case Dictionary<string, object?> compound:
                ChatStyle style = ReadNbtStyle(compound, inherited);
                if (compound.TryGetValue("text", out object? textValue))
                    AppendNbt(textValue, style, runs);
                else if (compound.TryGetValue(string.Empty, out object? unnamedText))
                    AppendNbt(unnamedText, style, runs);
                else if (compound.TryGetValue("translate", out object? translate))
                {
                    string key = FlattenNbt(translate);
                    IReadOnlyList<object?> arguments = compound.TryGetValue("with", out object? with) && with is List<object?> values
                        ? values
                        : [];
                    AppendTranslatedNbt(key, arguments, style, runs);
                }
                if (compound.TryGetValue("extra", out object? extra)) AppendNbt(extra, style, runs);
                return;
            default:
                return;
        }
    }

    private static ChatStyle ReadNbtStyle(Dictionary<string, object?> compound, ChatStyle inherited)
    {
        string? color = inherited.Color;
        if (compound.TryGetValue("color", out object? colorValue) && colorValue is string requested)
            color = string.Equals(requested, "reset", StringComparison.OrdinalIgnoreCase) ? null : requested;
        return new ChatStyle(
            color,
            ReadNbtBoolean(compound, "bold", inherited.Bold),
            ReadNbtBoolean(compound, "italic", inherited.Italic),
            ReadNbtBoolean(compound, "underlined", inherited.Underlined),
            ReadNbtBoolean(compound, "strikethrough", inherited.Strikethrough));
    }

    private static bool ReadNbtBoolean(Dictionary<string, object?> compound, string name, bool inherited)
    {
        if (!compound.TryGetValue(name, out object? value)) return inherited;
        return value switch
        {
            sbyte number => number != 0,
            short number => number != 0,
            int number => number != 0,
            long number => number != 0,
            _ => inherited
        };
    }

    private static void AppendTranslatedNbt(
        string key,
        IReadOnlyList<object?> arguments,
        ChatStyle style,
        List<ChatRun> runs)
    {
        void Text(string value) => AppendFormattedLegacy(runs, value, style);
        void Argument(int index)
        {
            if (index >= 0 && index < arguments.Count) AppendNbt(arguments[index], style, runs);
            else Text("?");
        }

        switch (key)
        {
            case "%s" when arguments.Count > 0:
                Argument(0);
                return;
            case "chat.type.text":
                Text("<"); Argument(0); Text("> "); Argument(1);
                return;
            case "chat.type.announcement":
                Text("["); Argument(0); Text("] "); Argument(1);
                return;
            case "chat.square_brackets":
                Text("["); Argument(0); Text("]");
                return;
            case "chat.type.advancement.task":
                Argument(0); Text(" has made the advancement "); Argument(1);
                return;
            default:
                string[] flattened = arguments.Select(FlattenNbt).ToArray();
                Text(Translate(key, flattened));
                return;
        }
    }

    private static string Translate(string key, IReadOnlyList<string> arguments)
    {
        if (MinecraftTranslations.TryGet(key, out string pattern))
            return FormatTranslationPattern(pattern, arguments);
        string Argument(int index) => index < arguments.Count ? arguments[index] : "?";
        return key switch
        {
            "chat.type.text" => $"<{Argument(0)}> {Argument(1)}",
            "chat.type.announcement" => $"[{Argument(0)}] {Argument(1)}",
            "chat.square_brackets" => $"[{Argument(0)}]",
            "chat.type.advancement.task" => $"{Argument(0)} has made the advancement {Argument(1)}",
            "multiplayer.player.joined" => $"{Argument(0)} joined the game",
            "multiplayer.player.left" => $"{Argument(0)} left the game",
            "commands.kill.successful" or "commands.kill.success.single" => $"Killed {Argument(0)}",
            _ when key.StartsWith("death.", StringComparison.Ordinal) =>
                arguments.Count == 0 ? "Player died" : string.Join(" ", arguments) + " died",
            _ when key.Contains('%', StringComparison.Ordinal) => FormatTranslationPattern(key, arguments),
            _ => arguments.Count == 0 ? key : key + ": " + string.Join(" ", arguments)
        };
    }

    private static string FormatTranslationPattern(string pattern, IReadOnlyList<string> arguments)
    {
        StringBuilder result = new(pattern.Length + arguments.Sum(argument => argument.Length));
        int sequentialIndex = 0;
        for (int index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] != '%' || index + 1 >= pattern.Length)
            {
                result.Append(pattern[index]);
                continue;
            }

            if (pattern[index + 1] == '%')
            {
                result.Append('%');
                index++;
                continue;
            }

            int cursor = index + 1;
            int explicitIndex = 0;
            while (cursor < pattern.Length && char.IsAsciiDigit(pattern[cursor]))
            {
                explicitIndex = checked(explicitIndex * 10 + pattern[cursor] - '0');
                cursor++;
            }
            bool indexed = cursor > index + 1 && cursor < pattern.Length && pattern[cursor] == '$';
            if (indexed) cursor++;
            if (cursor < pattern.Length && pattern[cursor] == 's')
            {
                int argumentIndex = indexed ? explicitIndex - 1 : sequentialIndex++;
                result.Append(argumentIndex >= 0 && argumentIndex < arguments.Count ? arguments[argumentIndex] : "?");
                index = cursor;
                continue;
            }
            result.Append('%');
        }
        return result.ToString();
    }

    private static string StripLegacyFormatting(string value)
    {
        if (!value.Contains('§', StringComparison.Ordinal)) return value;
        char[] result = new char[value.Length];
        int written = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '§' && index + 1 < value.Length)
            {
                index++;
                continue;
            }
            result[written++] = value[index];
        }
        return new string(result, 0, written);
    }
}
