namespace OeXYZ.Protocol;

internal sealed record DecodedPlayerChat(Guid SenderUuid, string Text);

internal static class PlayerChatDecoder
{
    public static DecodedPlayerChat Decode(
        ReadOnlySpan<byte> payload,
        int protocolVersion,
        Func<Guid, string?> resolvePlayerName)
    {
        bool expectsGlobalIndex = protocolVersion >= 770;
        DecodedPlayerChat? expected = TryDecode(payload, expectsGlobalIndex);
        DecodedPlayerChat? alternate = TryDecode(payload, !expectsGlobalIndex);
        DecodedPlayerChat decoded = expected ?? alternate
            ?? throw new InvalidDataException("The player-chat packet has an unsupported layout.");

        // Some ViaVersion/proxy combinations advertise a newer protocol while forwarding
        // the pre-1.21.5 chat body. A shifted parse commonly yields only "<player>".
        if (expected is not null && LooksLikeBareSender(expected.Text) &&
            alternate is not null && !LooksLikeBareSender(alternate.Text))
            decoded = alternate;

        string? sender = resolvePlayerName(decoded.SenderUuid);
        if (!string.IsNullOrWhiteSpace(sender) && !AlreadyContainsSender(decoded.Text, sender))
            decoded = decoded with { Text = $"<{sender}> {decoded.Text}" };
        return decoded;
    }

    private static DecodedPlayerChat? TryDecode(ReadOnlySpan<byte> payload, bool hasGlobalIndex)
    {
        try
        {
            PacketReader reader = new(payload);
            if (hasGlobalIndex && reader.ReadVarInt() < 0) return null;
            Guid sender = reader.ReadUuid();
            if (reader.ReadVarInt() < 0) return null;
            byte hasSignature = reader.ReadByte();
            if (hasSignature > 1) return null;
            if (hasSignature == 1) _ = reader.ReadBytes(256);
            string text = reader.ReadString(256);
            if (reader.Remaining > 0)
            {
                _ = reader.ReadLong();
                _ = reader.ReadLong();
                int previousCount = reader.ReadVarInt();
                if (previousCount is < 0 or > 20) return null;
                for (int index = 0; index < previousCount; index++)
                {
                    int messageId = reader.ReadVarInt();
                    if (messageId < 0) return null;
                    if (messageId == 0) _ = reader.ReadBytes(256);
                }

                byte hasUnsignedContent = reader.ReadByte();
                if (hasUnsignedContent > 1) return null;
                if (hasUnsignedContent == 1)
                {
                    string unsignedText = ChatTextCodec.FromAnonymousNbt(ref reader);
                    if (!string.IsNullOrWhiteSpace(unsignedText) &&
                        !string.Equals(unsignedText, "[Unreadable server message]", StringComparison.Ordinal))
                        text = unsignedText;
                }
            }
            if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return null;
            return new DecodedPlayerChat(sender, text);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            return null;
        }
    }

    private static bool LooksLikeBareSender(string text)
    {
        if (text.Length is < 3 or > 18 || text[0] != '<' || text[^1] != '>') return false;
        return text.AsSpan(1, text.Length - 2).IndexOfAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_".AsSpan()) < 0;
    }

    private static bool AlreadyContainsSender(string text, string sender) =>
        text.StartsWith($"<{sender}>", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith($"{sender}:", StringComparison.OrdinalIgnoreCase);
}
