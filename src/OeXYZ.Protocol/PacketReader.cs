using System.Buffers.Binary;
using System.Text;

namespace OeXYZ.Protocol;

public ref struct PacketReader
{
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
        string value = Encoding.UTF8.GetString(Take(byteLength));
        if (value.Length > maximumCharacters) throw new InvalidDataException("String is too long.");
        return value;
    }

    public Guid ReadUuid() => new(Take(16), bigEndian: true);

    public byte[] ReadBytes(int count) => Take(count).ToArray();

    public string ReadNbtString()
    {
        int byteLength = ReadUnsignedShort();
        return Encoding.UTF8.GetString(Take(byteLength));
    }

    public ReadOnlySpan<byte> ReadRemaining() => Take(Remaining);

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > Remaining) throw new EndOfStreamException("Packet ended unexpectedly.");
        ReadOnlySpan<byte> result = data.Slice(offset, count);
        offset += count;
        return result;
    }
}
