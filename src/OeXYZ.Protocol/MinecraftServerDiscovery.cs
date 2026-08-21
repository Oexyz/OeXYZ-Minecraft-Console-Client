using System.Diagnostics;
using System.Text.Json;

namespace OeXYZ.Protocol;

public sealed record MinecraftServerStatus(
    ServerAddress Address,
    string VersionName,
    int ProtocolVersion,
    int PlayersOnline,
    int PlayersMaximum,
    string Description,
    int PingMilliseconds,
    byte[]? ServerIconPng = null);

public static class MinecraftServerDiscovery
{
    internal const int MaximumVersionNameCharacters = 256;
    internal const int MaximumDescriptionCharacters = 4_096;

    public static async Task<MinecraftServerStatus> QueryAsync(
        string address,
        int customPort = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default,
        IConnectionDialer? dialer = null)
    {
        using CancellationTokenSource timeoutSource = new(timeout ?? TimeSpan.FromSeconds(8));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        ServerAddress endpoint = await ServerAddress.Parse(address, customPort).ResolveSrvAsync(linked.Token).ConfigureAwait(false);
        Stopwatch statusStopwatch = Stopwatch.StartNew();
        await using Stream transport = await (dialer ?? DirectConnectionDialer.Instance)
            .ConnectAsync(endpoint.NetworkHost, endpoint.Port, linked.Token).ConfigureAwait(false);
        await using MinecraftPacketStream packets = new(transport);
        await packets.WriteAsync(0, writer =>
        {
            writer.WriteVarInt(776);
            writer.WriteString(endpoint.HandshakeHost, 255);
            writer.WriteUnsignedShort(endpoint.Port);
            writer.WriteVarInt(1);
        }, linked.Token).ConfigureAwait(false);
        await packets.WriteAsync(0, null, linked.Token).ConfigureAwait(false);
        InboundPacket response = await packets.ReadAsync(linked.Token).ConfigureAwait(false);
        statusStopwatch.Stop();
        if (response.Id != 0) throw new InvalidDataException("The server returned an invalid status packet.");
        PacketReader reader = new(response.Payload);
        string json = reader.ReadString(1_048_576);
        if (reader.Remaining != 0) throw new InvalidDataException("The server status packet contains trailing data.");
        int pingMilliseconds = checked((int)Math.Min(statusStopwatch.ElapsedMilliseconds, int.MaxValue));
        long nonce = Stopwatch.GetTimestamp();
        try
        {
            Stopwatch pingStopwatch = Stopwatch.StartNew();
            await packets.WriteAsync(1, writer => writer.WriteLong(nonce), linked.Token).ConfigureAwait(false);
            InboundPacket pong = await packets.ReadAsync(linked.Token).ConfigureAwait(false);
            pingStopwatch.Stop();
            if (pong.Id != 1) throw new InvalidDataException("The server returned an invalid status pong packet.");
            PacketReader pongReader = new(pong.Payload);
            if (pongReader.ReadLong() != nonce || pongReader.Remaining != 0)
                throw new InvalidDataException("The server status pong did not match the ping.");
            pingMilliseconds = checked((int)Math.Min(pingStopwatch.ElapsedMilliseconds, int.MaxValue));
        }
        catch (EndOfStreamException)
        {
            // Some older servers close immediately after their JSON response.
            // Retain the clearly defined status-handshake RTT for those servers.
        }
        catch (IOException)
        {
            // Some older servers close immediately after their JSON response.
            // Retain the clearly defined status-handshake RTT for those servers.
        }
        return ParseResponse(endpoint, json, pingMilliseconds);
    }

    internal static MinecraftServerStatus ParseResponse(
        ServerAddress endpoint,
        string json,
        int pingMilliseconds)
    {
        if (json.Length > 1_048_576) throw new InvalidDataException("The server status JSON exceeds the safety limit.");
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out JsonElement version) ||
            version.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The server status contains no version object.");
        string versionName = version.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
            ? NormalizeField(name.GetString(), MaximumVersionNameCharacters)
            : "Unknown";
        int protocolVersion = version.TryGetProperty("protocol", out JsonElement protocol) && protocol.TryGetInt32(out int parsedProtocol)
            ? parsedProtocol
            : throw new InvalidDataException("The server status contains no protocol version.");
        int online = 0;
        int maximum = 0;
        if (root.TryGetProperty("players", out JsonElement players) && players.ValueKind == JsonValueKind.Object)
        {
            if (players.TryGetProperty("online", out JsonElement onlineElement)) onlineElement.TryGetInt32(out online);
            if (players.TryGetProperty("max", out JsonElement maxElement)) maxElement.TryGetInt32(out maximum);
        }
        online = Math.Max(0, online);
        maximum = Math.Max(0, maximum);
        string description = root.TryGetProperty("description", out JsonElement descriptionElement)
            ? descriptionElement.ValueKind switch
            {
                JsonValueKind.String => NormalizeField(descriptionElement.GetString(), MaximumDescriptionCharacters),
                JsonValueKind.Object or JsonValueKind.Array =>
                    LimitField(ChatTextCodec.FromJson(descriptionElement.GetRawText()), MaximumDescriptionCharacters),
                _ => string.Empty
            }
            : string.Empty;
        byte[]? icon = root.TryGetProperty("favicon", out JsonElement favicon) &&
                       favicon.ValueKind == JsonValueKind.String
            ? TryReadServerIcon(favicon.GetString())
            : null;
        return new MinecraftServerStatus(endpoint, versionName, protocolVersion, online, maximum,
            description, pingMilliseconds, icon);
    }

    private static byte[]? TryReadServerIcon(string? dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (string.IsNullOrWhiteSpace(dataUrl) ||
            !dataUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            dataUrl.Length > 512_000)
            return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
            return bytes.Length is > 8 and <= 256_000 &&
                   bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                ? bytes
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string NormalizeField(string? text, int maximumCharacters) =>
        LimitField(ChatTextCodec.ParseLegacy(text ?? string.Empty).Text, maximumCharacters);

    private static string LimitField(string text, int maximumCharacters)
    {
        if (text.Length <= maximumCharacters) return text;
        int length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }
}
