using System.Text.Json;

namespace OeXYZ.Protocol;

internal static class ChatTextCodec
{
    public static string FromJson(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return StripLegacyFormatting(FlattenJson(document.RootElement));
        }
        catch (JsonException)
        {
            return StripLegacyFormatting(json);
        }
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
    {
        try
        {
            object? value = ReadPayload(ref reader, reader.ReadByte(), 0);
            return StripLegacyFormatting(FlattenNbt(value));
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return "[Unreadable server message]";
        }
    }

    private static string FlattenJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(FlattenJson)),
            JsonValueKind.Object => FlattenJsonObject(element),
            _ => string.Empty
        };
    }

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

    private static object? ReadPayload(ref PacketReader reader, byte type, int depth)
    {
        if (depth > 64) throw new InvalidDataException("NBT nesting is too deep.");
        return type switch
        {
            0 => null,
            1 => reader.ReadSignedByte(),
            2 => reader.ReadShort(),
            3 => reader.ReadInt(),
            4 => reader.ReadLong(),
            5 => reader.ReadFloat(),
            6 => reader.ReadDouble(),
            7 => reader.ReadBytes(CheckedLength(reader.ReadInt(), 1)),
            8 => reader.ReadNbtString(),
            9 => ReadList(ref reader, depth + 1),
            10 => ReadCompound(ref reader, depth + 1),
            11 => ReadIntArray(ref reader),
            12 => ReadLongArray(ref reader),
            _ => throw new InvalidDataException($"Unknown NBT tag type {type}.")
        };
    }

    private static List<object?> ReadList(ref PacketReader reader, int depth)
    {
        byte elementType = reader.ReadByte();
        int length = CheckedLength(reader.ReadInt(), 1);
        List<object?> values = new(length);
        for (int index = 0; index < length; index++) values.Add(ReadPayload(ref reader, elementType, depth));
        return values;
    }

    private static Dictionary<string, object?> ReadCompound(ref PacketReader reader, int depth)
    {
        Dictionary<string, object?> values = new(StringComparer.Ordinal);
        while (true)
        {
            byte childType = reader.ReadByte();
            if (childType == 0) return values;
            string name = reader.ReadNbtString();
            values[name] = ReadPayload(ref reader, childType, depth);
        }
    }

    private static int[] ReadIntArray(ref PacketReader reader)
    {
        int length = CheckedLength(reader.ReadInt(), 4);
        int[] values = new int[length];
        for (int index = 0; index < length; index++) values[index] = reader.ReadInt();
        return values;
    }

    private static long[] ReadLongArray(ref PacketReader reader)
    {
        int length = CheckedLength(reader.ReadInt(), 8);
        long[] values = new long[length];
        for (int index = 0; index < length; index++) values[index] = reader.ReadLong();
        return values;
    }

    private static int CheckedLength(int count, int bytesPerElement)
    {
        if (count < 0 || count > 1_000_000 || (long)count * bytesPerElement > 8_000_000)
            throw new InvalidDataException("NBT collection length is outside safety limits.");
        return count;
    }

    private static string FlattenNbt(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string text:
                return text;
            case List<object?> list:
                return string.Concat(list.Select(FlattenNbt));
            case Dictionary<string, object?> compound:
                string result = string.Empty;
                if (compound.TryGetValue("text", out object? textValue)) result += FlattenNbt(textValue);
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

    private static string Translate(string key, IReadOnlyList<string> arguments)
    {
        string Argument(int index) => index < arguments.Count ? arguments[index] : "?";
        return key switch
        {
            "chat.type.text" => $"<{Argument(0)}> {Argument(1)}",
            "chat.type.announcement" => $"[{Argument(0)}] {Argument(1)}",
            "multiplayer.player.joined" => $"{Argument(0)} joined the game",
            "multiplayer.player.left" => $"{Argument(0)} left the game",
            "commands.kill.successful" or "commands.kill.success.single" => $"Killed {Argument(0)}",
            _ when key.StartsWith("death.", StringComparison.Ordinal) =>
                arguments.Count == 0 ? "Player died" : string.Join(" ", arguments) + " died",
            _ => arguments.Count == 0 ? key : key + ": " + string.Join(" ", arguments)
        };
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
