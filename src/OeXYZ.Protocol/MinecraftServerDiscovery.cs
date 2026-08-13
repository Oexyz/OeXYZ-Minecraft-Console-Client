using System.Net.Sockets;
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
    public static async Task<MinecraftServerStatus> QueryAsync(
        string address,
        int customPort = 0,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ServerAddress endpoint = ServerAddress.Parse(address, customPort).ResolveSrv();
        using CancellationTokenSource timeoutSource = new(timeout ?? TimeSpan.FromSeconds(8));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using TcpClient client = new() { NoDelay = true };
        Stopwatch stopwatch = Stopwatch.StartNew();
        await client.ConnectAsync(endpoint.NetworkHost, endpoint.Port, linked.Token).ConfigureAwait(false);
        await using MinecraftPacketStream packets = new(client.GetStream());
        await packets.WriteAsync(0, writer =>
        {
            writer.WriteVarInt(776);
            writer.WriteString(endpoint.HandshakeHost, 255);
            writer.WriteUnsignedShort(endpoint.Port);
            writer.WriteVarInt(1);
        }, linked.Token).ConfigureAwait(false);
        await packets.WriteAsync(0, null, linked.Token).ConfigureAwait(false);
        InboundPacket response = await packets.ReadAsync(linked.Token).ConfigureAwait(false);
        stopwatch.Stop();
        if (response.Id != 0) throw new InvalidDataException("The server returned an invalid status packet.");
        PacketReader reader = new(response.Payload);
        string json = reader.ReadString(1_048_576);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement version = root.GetProperty("version");
        string versionName = version.TryGetProperty("name", out JsonElement name) ? Clean(name.GetString()) : "Unknown";
        int protocolVersion = version.TryGetProperty("protocol", out JsonElement protocol) && protocol.TryGetInt32(out int parsedProtocol)
            ? parsedProtocol
            : throw new InvalidDataException("The server status contains no protocol version.");
        int online = 0;
        int maximum = 0;
        if (root.TryGetProperty("players", out JsonElement players))
        {
            if (players.TryGetProperty("online", out JsonElement onlineElement)) onlineElement.TryGetInt32(out online);
            if (players.TryGetProperty("max", out JsonElement maxElement)) maxElement.TryGetInt32(out maximum);
        }
        string description = root.TryGetProperty("description", out JsonElement descriptionElement)
            ? descriptionElement.ValueKind == JsonValueKind.String
                ? Clean(descriptionElement.GetString())
                : ChatTextCodec.FromJson(descriptionElement.GetRawText())
            : string.Empty;
        byte[]? icon = root.TryGetProperty("favicon", out JsonElement favicon)
            ? TryReadServerIcon(favicon.GetString())
            : null;
        return new MinecraftServerStatus(endpoint, versionName, protocolVersion, online, maximum,
            description, checked((int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue)), icon);
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

    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        Span<char> buffer = stackalloc char[text.Length];
        int written = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '§' && index + 1 < text.Length)
            {
                index++;
                continue;
            }
            buffer[written++] = text[index];
        }
        return new string(buffer[..written]);
    }
}
