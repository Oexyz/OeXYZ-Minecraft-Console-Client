using System.IO.Compression;

namespace OeXYZ.Protocol;

internal sealed class MinecraftPacketStream : IAsyncDisposable
{
    internal const int MaximumPacketLength = 2 * 1024 * 1024;
    private readonly Stream stream;
    private Stream readStream;
    private Stream writeStream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private int compressionThreshold = -1;

    public event Action<int, int, int>? PacketWritten;

    public MinecraftPacketStream(Stream stream)
    {
        this.stream = stream;
        readStream = stream;
        writeStream = stream;
    }

    public void EnableCompression(int threshold)
    {
        if (threshold < 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        compressionThreshold = threshold;
    }

    public void EnableEncryption(byte[] sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        if (sharedSecret.Length != 16) throw new ArgumentException("Minecraft shared secrets are 16 bytes.", nameof(sharedSecret));
        if (!ReferenceEquals(readStream, stream)) throw new InvalidOperationException("Encryption is already enabled.");

        readStream = new MinecraftCfb8Stream(stream, sharedSecret, decrypt: true);
        writeStream = new MinecraftCfb8Stream(stream, sharedSecret, decrypt: false);
    }

    public async ValueTask<InboundPacket> ReadAsync(CancellationToken cancellationToken)
    {
        (int frameLength, int prefixBytes) = await ReadVarIntAsync(cancellationToken).ConfigureAwait(false);
        if (frameLength <= 0 || frameLength > MaximumPacketLength)
            throw new InvalidDataException($"Invalid packet frame length: {frameLength}.");

        byte[] frame = new byte[frameLength];
        await readStream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        byte[] packetData;

        if (compressionThreshold >= 0)
        {
            PacketReader frameReader = new(frame);
            int uncompressedLength = frameReader.ReadVarInt();
            ReadOnlySpan<byte> body = frameReader.ReadRemaining();
            if (uncompressedLength == 0)
            {
                if (body.Length >= compressionThreshold)
                    throw new InvalidDataException(
                        "An uncompressed packet reached or exceeded the negotiated compression threshold.");
                packetData = body.ToArray();
            }
            else
            {
                if (uncompressedLength < compressionThreshold || uncompressedLength > MaximumPacketLength)
                    throw new InvalidDataException("Compressed packet declared an invalid size.");
                using MemoryStream input = new(body.ToArray(), writable: false);
                using ZLibStream inflater = new(input, CompressionMode.Decompress);
                using MemoryStream output = new(uncompressedLength);
                byte[] buffer = new byte[16 * 1024];
                while (output.Length < uncompressedLength)
                {
                    int remaining = checked(uncompressedLength - (int)output.Length);
                    int requested = Math.Min(buffer.Length, remaining + 1);
                    int read = await inflater.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new InvalidDataException("Compressed packet ended before its declared size.");
                    if (output.Length + read > uncompressedLength)
                        throw new InvalidDataException("Compressed packet expands beyond its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                int trailing = await inflater.ReadAsync(buffer.AsMemory(0, 1), cancellationToken)
                    .ConfigureAwait(false);
                if (trailing != 0)
                    throw new InvalidDataException("Compressed packet expands beyond its declared size.");
                packetData = output.ToArray();
                if (packetData.Length != uncompressedLength)
                    throw new InvalidDataException("Compressed packet size did not match its declaration.");
            }
        }
        else
        {
            packetData = frame;
        }

        PacketReader packetReader = new(packetData);
        int packetId = packetReader.ReadVarInt();
        return new InboundPacket(packetId, packetReader.ReadRemaining().ToArray(), prefixBytes + frameLength);
    }

    public async ValueTask WriteAsync(int packetId, Action<PacketWriter>? writePayload, CancellationToken cancellationToken)
    {
        PacketWriter packet = new();
        packet.WriteVarInt(packetId);
        int packetIdLength = packet.Length;
        writePayload?.Invoke(packet);
        byte[] packetBytes = packet.ToArray();

        PacketWriter framedBody = new();
        if (compressionThreshold >= 0)
        {
            if (packetBytes.Length >= compressionThreshold)
            {
                framedBody.WriteVarInt(packetBytes.Length);
                using MemoryStream compressed = new();
                await using (ZLibStream deflater = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
                    await deflater.WriteAsync(packetBytes, cancellationToken).ConfigureAwait(false);
                framedBody.WriteBytes(compressed.ToArray());
            }
            else
            {
                framedBody.WriteVarInt(0);
                framedBody.WriteBytes(packetBytes);
            }
        }
        else
        {
            framedBody.WriteBytes(packetBytes);
        }

        byte[] body = framedBody.ToArray();
        PacketWriter frame = new();
        frame.WriteVarInt(body.Length);
        frame.WriteBytes(body);
        byte[] output = frame.ToArray();

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writeStream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
            await writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            PacketWritten?.Invoke(packetId, packetBytes.Length - packetIdLength, output.Length);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async ValueTask<(int Value, int BytesRead)> ReadVarIntAsync(CancellationToken cancellationToken)
    {
        int result = 0;
        byte[] oneByte = new byte[1];
        for (int position = 0; position < 35; position += 7)
        {
            await readStream.ReadExactlyAsync(oneByte, cancellationToken).ConfigureAwait(false);
            byte current = oneByte[0];
            result |= (current & 0x7F) << position;
            if ((current & 0x80) == 0) return (result, position / 7 + 1);
        }

        throw new InvalidDataException("Frame VarInt is too large.");
    }

    public async ValueTask DisposeAsync()
    {
        writeLock.Dispose();
        if (!ReferenceEquals(readStream, stream))
            await readStream.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(writeStream, stream) && !ReferenceEquals(writeStream, readStream))
            await writeStream.DisposeAsync().ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

}

internal sealed record InboundPacket(int Id, byte[] Payload, int WireLength);
