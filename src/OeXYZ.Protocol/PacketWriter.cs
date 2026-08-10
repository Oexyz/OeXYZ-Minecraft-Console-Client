using System.Buffers.Binary;
using System.Text;

namespace OeXYZ.Protocol;

public sealed class PacketWriter
{
    private readonly MemoryStream stream = new();

    public int Length => checked((int)stream.Length);

    public void WriteBoolean(bool value) => stream.WriteByte(value ? (byte)1 : (byte)0);

    public void WriteByte(byte value) => stream.WriteByte(value);

    public void WriteSignedByte(sbyte value) => stream.WriteByte(unchecked((byte)value));

    public void WriteBytes(ReadOnlySpan<byte> value) => stream.Write(value);

    public void WriteVarIntPrefixedBytes(ReadOnlySpan<byte> value)
    {
        WriteVarInt(value.Length);
        WriteBytes(value);
    }

    public void WriteUnsignedShort(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }

    public void WriteInt(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    public void WriteLong(long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        stream.Write(buffer);
    }

    public void WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));

    public void WriteDouble(double value) => WriteLong(BitConverter.DoubleToInt64Bits(value));

    public void WriteVarInt(int value)
    {
        uint remaining = unchecked((uint)value);
        do
        {
            byte current = (byte)(remaining & 0x7F);
            remaining >>= 7;
            if (remaining != 0) current |= 0x80;
            stream.WriteByte(current);
        }
        while (remaining != 0);
    }

    public void WriteString(string value, int maximumCharacters = 32767)
    {
        if (value.Length > maximumCharacters) throw new ArgumentOutOfRangeException(nameof(value));
        byte[] encoded = Encoding.UTF8.GetBytes(value);
        if (encoded.Length > maximumCharacters * 3) throw new ArgumentOutOfRangeException(nameof(value));
        WriteVarInt(encoded.Length);
        WriteBytes(encoded);
    }

    public void WriteUuid(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        WriteBytes(bytes);
    }

    public byte[] ToArray() => stream.ToArray();
}
