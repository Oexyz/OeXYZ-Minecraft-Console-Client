using System.Buffers.Binary;
using System.Text;

namespace OeXYZ.Protocol;

public ref struct PacketReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> data;
    private int offset;

    public PacketReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
        offset = 0;
    }

    public int Remaining => data.Length - offset;
    public bool ReadBoolean() => ReadByte() != 0;
    public byte ReadByte() => Take(1)[0];
    public sbyte ReadSignedByte() => unchecked((sbyte)ReadByte());
    public short ReadShort() => BinaryPrimitives.ReadInt16BigEndian(Take(2));
    public ushort ReadUnsignedShort() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64BigEndian(Take(8));
    public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadLong());

    public int ReadVarInt()
    {
        int result = 0;
        for (int position = 0; position < 35; position += 7)
        {
            byte current = ReadByte();
            result |= (current & 0x7F) << position;
            if ((current & 0x80) == 0) return result;
        }

        throw new InvalidDataException("VarInt is too large.");
    }

    public string ReadString(int maximumCharacters = 32767)
    {
        int byteLength = ReadVarInt();
        if (byteLength < 0 || byteLength > maximumCharacters * 3 || byteLength > Remaining)
            throw new InvalidDataException("String length is outside the protocol limits.");
        string value;
        try { value = StrictUtf8.GetString(Take(byteLength)); }
        catch (DecoderFallbackException exception) { throw new InvalidDataException("String contains invalid UTF-8.", exception); }
        if (value.Length > maximumCharacters) throw new InvalidDataException("String is too long.");
        return value;
    }

    public Guid ReadUuid() => new(Take(16), bigEndian: true);

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public string ReadNbtString()
    {
        int byteLength = ReadUnsignedShort();
        return DecodeModifiedUtf8(Take(byteLength));
    }

    public ReadOnlySpan<byte> ReadRemaining() => Take(Remaining);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new EndOfStreamException("Packet ended unexpectedly.");
        ReadOnlySpan<byte> result = data.Slice(offset, count);
        offset += count;
        return result;
    }

    private static string DecodeModifiedUtf8(ReadOnlySpan<byte> bytes)
    {
        StringBuilder result = new(bytes.Length);
        for (int offset = 0; offset < bytes.Length;)
        {
            byte first = bytes[offset++];
            if (first <= 0x7F)
            {
                result.Append((char)first);
                continue;
            }

            if ((first & 0xE0) == 0xC0)
            {
                byte second = Continuation(bytes, ref offset);
                int codeUnit = ((first & 0x1F) << 6) | (second & 0x3F);
                if (codeUnit is > 0 and < 0x80)
                    throw new InvalidDataException("NBT string contains an overlong modified UTF-8 sequence.");
                result.Append((char)codeUnit);
                continue;
            }

            if ((first & 0xF0) == 0xE0)
            {
                byte second = Continuation(bytes, ref offset);
                byte third = Continuation(bytes, ref offset);
                int codeUnit = ((first & 0x0F) << 12) | ((second & 0x3F) << 6) | (third & 0x3F);
                if (codeUnit < 0x800)
                    throw new InvalidDataException("NBT string contains an overlong modified UTF-8 sequence.");
                // Java's modified UTF-8 encodes supplementary characters as two
                // three-byte UTF-16 surrogate code units (CESU-8). Appending the
                // code units preserves the valid surrogate pair in a .NET string.
                result.Append((char)codeUnit);
                continue;
            }

            if ((first & 0xF8) == 0xF0)
            {
                byte second = Continuation(bytes, ref offset);
                byte third = Continuation(bytes, ref offset);
                byte fourth = Continuation(bytes, ref offset);
                int scalar = ((first & 0x07) << 18) | ((second & 0x3F) << 12) |
                             ((third & 0x3F) << 6) | (fourth & 0x3F);
                if (scalar is < 0x10000 or > 0x10FFFF)
                    throw new InvalidDataException("NBT string contains an invalid UTF-8 scalar value.");
                result.Append(char.ConvertFromUtf32(scalar));
                continue;
            }

            throw new InvalidDataException("NBT string contains invalid modified UTF-8.");
        }
        return result.ToString();
    }

    private static byte Continuation(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset >= bytes.Length || (bytes[offset] & 0xC0) != 0x80)
            throw new InvalidDataException("NBT string contains a truncated modified UTF-8 sequence.");
        return bytes[offset++];
    }
}
