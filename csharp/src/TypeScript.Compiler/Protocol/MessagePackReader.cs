using System.Buffers.Binary;

namespace TypeScript.Compiler.Protocol;

internal ref struct MessagePackReader(ReadOnlyMemory<byte> data)
{
    private readonly ReadOnlyMemory<byte> data = data;
    private int position;

    private ReadOnlyMemory<byte> Take(int count)
    {
        if (count < 0 || count > data.Length - position) throw new InvalidDataException("Truncated MessagePack value");
        var result = data.Slice(position, count); position += count; return result;
    }
    private byte Byte() => Take(1).Span[0];
    private uint Number(byte tag, byte tag8, byte tag16, byte tag32) => tag8 != 0 && tag == tag8 ? Byte()
        : tag == tag16 ? BinaryPrimitives.ReadUInt16BigEndian(Take(2).Span)
        : tag == tag32 ? BinaryPrimitives.ReadUInt32BigEndian(Take(4).Span)
        : throw new InvalidDataException("Unexpected MessagePack value type");
    internal uint UInt()
    {
        byte tag = Byte();
        return tag <= 127 ? tag : Number(tag, 0xCC, 0xCD, 0xCE);
    }
    internal int Int() => UInt() is var value && value <= int.MaxValue ? (int)value : throw new InvalidDataException("MessagePack integer exceeds the supported range");
    internal bool Bool() => Byte() switch { 0xC2 => false, 0xC3 => true, _ => throw new InvalidDataException("Expected MessagePack boolean") };
    internal int Array()
    {
        byte tag = Byte();
        uint count = (tag & 0xF0) == 0x90 ? (uint)(tag & 15) : Number(tag, 0, 0xDC, 0xDD);
        if (count > data.Length - position) throw new InvalidDataException("Invalid MessagePack array length");
        return (int)count;
    }
    internal Utf8String String()
    {
        byte tag = Byte();
        uint length = (tag & 0xE0) == 0xA0 ? (uint)(tag & 31) : Number(tag, 0xD9, 0xDA, 0xDB);
        if (length > int.MaxValue) throw new InvalidDataException("Invalid MessagePack string length");
        return new(Take((int)length));
    }
}
