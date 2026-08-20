using System.Buffers;
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
    internal const int MaximumDnsMessageBytes = 4096;
    private const int MaximumAnswers = 64;
    private const int MaximumOtherRecords = 128;
    private static readonly TimeSpan PerResolverTimeout = TimeSpan.FromSeconds(3);

    public static async Task<PortableSrvEndpoint?> QueryAsync(string host, CancellationToken cancellationToken)
    {
        string queryName = "_minecraft._tcp." + new IdnMapping().GetAscii(host.TrimEnd('.'));
        foreach (IPAddress resolver in ReadNameServers("/etc/resolv.conf").Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort transaction = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1);
            byte[] query = BuildQuery(queryName, transaction);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PerResolverTimeout);
            try
            {
                IReadOnlyList<PortableSrvEndpoint> answers = await QueryResolverAsync(
                    resolver, 53, queryName, transaction, query, timeout.Token).ConfigureAwait(false);
                if (answers.Count > 0) return Select(answers);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is SocketException or IOException or InvalidDataException)
            {
            }
        }
        return null;
    }

    internal static async Task<IReadOnlyList<PortableSrvEndpoint>> QueryResolverAsync(
        IPAddress resolver,
        int resolverPort,
        string queryName,
        ushort transaction,
        byte[] query,
        CancellationToken cancellationToken)
    {
        byte[] response = new byte[MaximumDnsMessageBytes + 1];
        if (resolverPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(resolverPort));
        IPEndPoint endpoint = new(resolver, resolverPort);
        using Socket socket = new(resolver.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.SendToAsync(query, SocketFlags.None, endpoint, cancellationToken).ConfigureAwait(false);
        SocketReceiveFromResult received = await socket.ReceiveFromAsync(
            response,
            SocketFlags.None,
            resolver.AddressFamily == AddressFamily.InterNetworkV6
                ? new IPEndPoint(IPAddress.IPv6Any, 0)
                : new IPEndPoint(IPAddress.Any, 0),
            cancellationToken).ConfigureAwait(false);
        if (received.RemoteEndPoint is not IPEndPoint source ||
            !source.Address.Equals(resolver) || source.Port != resolverPort)
            throw new InvalidDataException("The DNS response came from an unexpected source.");
        if (received.ReceivedBytes > MaximumDnsMessageBytes)
            throw new InvalidDataException("The DNS response exceeds the safety limit.");

        try
        {
            return ParseResponse(response.AsSpan(0, received.ReceivedBytes), transaction, queryName);
        }
        catch (DnsTruncatedException)
        {
            byte[] tcpResponse = await QueryTcpAsync(endpoint, query, cancellationToken).ConfigureAwait(false);
            return ParseResponse(tcpResponse, transaction, queryName);
        }
    }

    private static async Task<byte[]> QueryTcpAsync(
        IPEndPoint endpoint,
        byte[] query,
        CancellationToken cancellationToken)
    {
        using Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using NetworkStream stream = new(socket, ownsSocket: false);
        byte[] framedQuery = new byte[query.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(framedQuery, checked((ushort)query.Length));
        query.CopyTo(framedQuery, 2);
        await stream.WriteAsync(framedQuery, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
        if (length is < 12 or > MaximumDnsMessageBytes)
            throw new InvalidDataException("The DNS-over-TCP response size is invalid.");
        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
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
            if (encoded.Length is 0 or > 63 || encoded.Any(value => !IsSafeLabelByte(value)))
                throw new InvalidDataException("The DNS SRV name contains an invalid label.");
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        WriteUInt16(bytes, 33);
        WriteUInt16(bytes, 1);
        if (bytes.Count > 512) throw new InvalidDataException("The DNS SRV query exceeds 512 bytes.");
        return bytes.ToArray();
    }

    internal static IReadOnlyList<PortableSrvEndpoint> ParseResponse(
        ReadOnlySpan<byte> message,
        ushort transaction) => ParseResponse(message, transaction, expectedQuestion: null);

    internal static IReadOnlyList<PortableSrvEndpoint> ParseResponse(
        ReadOnlySpan<byte> message,
        ushort transaction,
        string? expectedQuestion)
    {
        if (message.Length < 12 || message.Length > MaximumDnsMessageBytes)
            throw new InvalidDataException("The DNS response size is invalid.");
        if (ReadUInt16(message, 0) != transaction)
            throw new InvalidDataException("The DNS transaction ID does not match.");
        ushort flags = ReadUInt16(message, 2);
        if ((flags & 0x8000) == 0) throw new InvalidDataException("The DNS message is not a response.");
        if ((flags & 0x7800) != 0) throw new InvalidDataException("The DNS response opcode is invalid.");
        int questions = ReadUInt16(message, 4);
        int answerCount = ReadUInt16(message, 6);
        int authorityCount = ReadUInt16(message, 8);
        int additionalCount = ReadUInt16(message, 10);
        if (questions != 1) throw new InvalidDataException("The DNS response must contain exactly one question.");
        if (answerCount > MaximumAnswers || authorityCount > MaximumOtherRecords ||
            additionalCount > MaximumOtherRecords)
            throw new InvalidDataException("The DNS response contains too many records.");

        int offset = 12;
        string question = ReadName(message, ref offset);
        Require(message, offset, 4);
        ushort questionType = ReadUInt16(message, offset);
        ushort questionClass = ReadUInt16(message, offset + 2);
        offset += 4;
        if (questionType != 33 || questionClass != 1)
            throw new InvalidDataException("The DNS response question is not SRV/IN.");
        if (expectedQuestion is not null &&
            !string.Equals(question.TrimEnd('.'), expectedQuestion.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The DNS response question does not match the query.");
        if ((flags & 0x0200) != 0) throw new DnsTruncatedException();
        int responseCode = flags & 0x000F;
        if (responseCode != 0) return [];

        bool serviceUnavailable = false;
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
                if (targetOffset != recordEnd)
                    throw new InvalidDataException("The DNS SRV record length is inconsistent.");
                if (target.Length == 0)
                {
                    serviceUnavailable = true;
                }
                else if (port == 0)
                {
                    throw new InvalidDataException("The DNS SRV record contains an invalid port.");
                }
                else
                {
                    answers.Add(new PortableSrvEndpoint(target.TrimEnd('.'), port, priority, weight));
                }
            }
            offset = recordEnd;
        }
        return serviceUnavailable ? [] : answers;
    }

    internal static PortableSrvEndpoint Select(
        IReadOnlyList<PortableSrvEndpoint> records,
        Func<int, int>? next = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) throw new ArgumentException("At least one SRV record is required.", nameof(records));
        next ??= RandomNumberGenerator.GetInt32;
        ushort priority = records.Min(record => record.Priority);
        PortableSrvEndpoint[] eligible = records
            .Where(record => record.Priority == priority)
            .OrderBy(record => record.Weight == 0 ? 0 : 1)
            .ToArray();
        int totalWeight = eligible.Sum(record => record.Weight);
        if (totalWeight == 0) return eligible[CheckedRandom(next, eligible.Length)];
        int ticket = CheckedRandom(next, checked(totalWeight + 1));
        int running = 0;
        foreach (PortableSrvEndpoint record in eligible)
        {
            running += record.Weight;
            if (running >= ticket) return record;
        }
        return eligible[^1];
    }

    private static int CheckedRandom(Func<int, int> next, int exclusiveMaximum)
    {
        int value = next(exclusiveMaximum);
        if (value < 0 || value >= exclusiveMaximum)
            throw new InvalidOperationException("The injected SRV random source returned an invalid value.");
        return value;
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
            if ((length & 0xC0) != 0 || length > 63)
                throw new InvalidDataException("The DNS label length is invalid.");
            Require(message, position, length);
            ReadOnlySpan<byte> label = message.Slice(position, length);
            if (label.ContainsAnyExcept(SafeLabelSearchValues))
                throw new InvalidDataException("The DNS name contains an invalid label.");
            labels.Add(Encoding.ASCII.GetString(label));
            position += length;
            if (labels.Sum(value => value.Length + 1) > 255)
                throw new InvalidDataException("The decoded DNS name exceeds 255 bytes.");
        }
    }

    private static readonly SearchValues<byte> SafeLabelSearchValues = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"u8);

    private static bool IsSafeLabelByte(byte value) =>
        (value >= (byte)'a' && value <= (byte)'z') ||
        (value >= (byte)'A' && value <= (byte)'Z') ||
        (value >= (byte)'0' && value <= (byte)'9') || value is (byte)'-' or (byte)'_';

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

    private sealed class DnsTruncatedException : IOException
    {
        public DnsTruncatedException() : base("The DNS response is truncated and requires TCP fallback.")
        {
        }
    }
}
