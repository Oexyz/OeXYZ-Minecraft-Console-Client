using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OeXYZ.Protocol;

internal sealed record PortableSrvEndpoint(string Target, ushort Port, ushort Priority, ushort Weight);

internal static class PortableSrvResolver
{
    private const int MaximumDnsMessageBytes = 4096;
    private const int MaximumAnswers = 64;

    public static async Task<PortableSrvEndpoint?> QueryAsync(string host, CancellationToken cancellationToken)
    {
        string queryName = "_minecraft._tcp." + new IdnMapping().GetAscii(host.TrimEnd('.'));
        foreach (IPAddress resolver in ReadNameServers("/etc/resolv.conf").Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort transaction = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            byte[] query = BuildQuery(queryName, transaction);
            byte[] response = new byte[MaximumDnsMessageBytes];
            using Socket socket = new(resolver.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                EndPoint endpoint = new IPEndPoint(resolver, 53);
                await socket.SendToAsync(query, SocketFlags.None, endpoint, timeout.Token).ConfigureAwait(false);
                SocketReceiveFromResult received = await socket.ReceiveFromAsync(
                    response,
                    SocketFlags.None,
                    resolver.AddressFamily == AddressFamily.InterNetworkV6
                        ? new IPEndPoint(IPAddress.IPv6Any, 0)
                        : new IPEndPoint(IPAddress.Any, 0),
                    timeout.Token).ConfigureAwait(false);
                IReadOnlyList<PortableSrvEndpoint> answers = ParseResponse(
                    response.AsSpan(0, received.ReceivedBytes), transaction);
                if (answers.Count > 0) return Select(answers);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }
        return null;
    }

    internal static IReadOnlyList<IPAddress> ReadNameServers(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return [];
        List<IPAddress> result = [];
        try
        {
            foreach (string raw in File.ReadLines(path).Take(256))
            {
                string line = raw.Trim();
                if (!line.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase)) continue;
                string[] fields = line["nameserver".Length..].Trim()
                    .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 0) continue;
                string value = fields[0];
                int zone = value.IndexOf('%');
                if (zone >= 0) value = value[..zone];
                if (IPAddress.TryParse(value, out IPAddress? address) && !result.Contains(address)) result.Add(address);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return result;
    }

    internal static byte[] BuildQuery(string queryName, ushort transaction)
    {
        List<byte> bytes = new(512);
        WriteUInt16(bytes, transaction);
        WriteUInt16(bytes, 0x0100);
        WriteUInt16(bytes, 1);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        WriteUInt16(bytes, 0);
        foreach (string label in queryName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length is 0 or > 63) throw new InvalidDataException("The DNS SRV name contains an invalid label.");
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        WriteUInt16(bytes, 33);
        WriteUInt16(bytes, 1);
        if (bytes.Count > 512) throw new InvalidDataException("The DNS SRV query exceeds 512 bytes.");
        return bytes.ToArray();
    }

    internal static IReadOnlyList<PortableSrvEndpoint> ParseResponse(ReadOnlySpan<byte> message, ushort transaction)
    {
        if (message.Length < 12 || message.Length > MaximumDnsMessageBytes)
            throw new InvalidDataException("The DNS response size is invalid.");
        if (ReadUInt16(message, 0) != transaction) throw new InvalidDataException("The DNS transaction ID does not match.");
        ushort flags = ReadUInt16(message, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x000F) != 0 || (flags & 0x0200) != 0)
            return [];
        int questions = ReadUInt16(message, 4);
        int answerCount = Math.Min((int)ReadUInt16(message, 6), MaximumAnswers);
        int offset = 12;
        for (int index = 0; index < questions; index++)
        {
            SkipName(message, ref offset);
            Require(message, offset, 4);
            offset += 4;
        }

        List<PortableSrvEndpoint> answers = [];
        for (int index = 0; index < answerCount; index++)
        {
            SkipName(message, ref offset);
            Require(message, offset, 10);
            ushort type = ReadUInt16(message, offset);
            ushort recordClass = ReadUInt16(message, offset + 2);
            ushort length = ReadUInt16(message, offset + 8);
            offset += 10;
            Require(message, offset, length);
            int recordEnd = offset + length;
            if (type == 33 && recordClass == 1 && length >= 7)
            {
                ushort priority = ReadUInt16(message, offset);
                ushort weight = ReadUInt16(message, offset + 2);
                ushort port = ReadUInt16(message, offset + 4);
                int targetOffset = offset + 6;
                string target = ReadName(message, ref targetOffset);
                if (targetOffset <= recordEnd && port > 0 && target.Length is > 0 and <= 255)
                    answers.Add(new PortableSrvEndpoint(target.TrimEnd('.'), port, priority, weight));
            }
            offset = recordEnd;
        }
        return answers;
    }

    private static PortableSrvEndpoint Select(IReadOnlyList<PortableSrvEndpoint> records)
    {
        ushort priority = records.Min(record => record.Priority);
        PortableSrvEndpoint[] eligible = records.Where(record => record.Priority == priority).ToArray();
        int totalWeight = eligible.Sum(record => record.Weight);
        if (totalWeight == 0) return eligible[RandomNumberGenerator.GetInt32(eligible.Length)];
        int ticket = RandomNumberGenerator.GetInt32(totalWeight);
        foreach (PortableSrvEndpoint record in eligible)
        {
            if (ticket < record.Weight) return record;
            ticket -= record.Weight;
        }
        return eligible[^1];
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        int position = offset;
        int? consumed = null;
        int jumps = 0;
        HashSet<int> visited = [];
        List<string> labels = [];
        while (true)
        {
            Require(message, position, 1);
            byte length = message[position++];
            if (length == 0)
            {
                offset = consumed ?? position;
                return string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                Require(message, position, 1);
                int pointer = ((length & 0x3F) << 8) | message[position++];
                if (pointer >= message.Length || !visited.Add(pointer) || ++jumps > 16)
                    throw new InvalidDataException("The DNS name compression pointer is invalid.");
                consumed ??= position;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0 || length > 63) throw new InvalidDataException("The DNS label length is invalid.");
            Require(message, position, length);
            labels.Add(Encoding.ASCII.GetString(message.Slice(position, length)));
            position += length;
            if (labels.Sum(label => label.Length + 1) > 255)
                throw new InvalidDataException("The decoded DNS name exceeds 255 bytes.");
        }
    }

    private static void SkipName(ReadOnlySpan<byte> message, ref int offset) => _ = ReadName(message, ref offset);

    private static void Require(ReadOnlySpan<byte> message, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > message.Length - length)
            throw new InvalidDataException("The DNS response is truncated.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> message, int offset)
    {
        Require(message, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
    }

    private static void WriteUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}
