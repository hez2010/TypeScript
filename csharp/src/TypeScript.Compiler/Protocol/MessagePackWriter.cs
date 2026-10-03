using System.Buffers;
using System.Buffers.Binary;

namespace TypeScript.Compiler.Protocol;

internal static class MessagePackWriter
{
    internal static void Array(IBufferWriter<byte> writer, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Prefix(writer, (uint)length, 15, 0x90, 0, 0xDC, 0xDD);
    }

    internal static void UInt(IBufferWriter<byte> writer, uint value) => Prefix(writer, value, 127, 0, 0xCC, 0xCD, 0xCE);
    internal static void Bool(IBufferWriter<byte> writer, bool value) => writer.Write([(byte)(value ? 0xC3 : 0xC2)]);
    internal static void String(IBufferWriter<byte> writer, Utf8String value)
    {
        Prefix(writer, (uint)value.Length, 31, 0xA0, 0xD9, 0xDA, 0xDB);
        writer.Write(value.Span);
    }
    internal static void Binary(IBufferWriter<byte> writer, ReadOnlySpan<byte> value)
    {
        BinaryHeader(writer, value.Length);
        writer.Write(value);
    }
    internal static void BinaryHeader(IBufferWriter<byte> writer, int length) =>
        Prefix(writer, checked((uint)length), 0, 0, 0xC4, 0xC5, 0xC6, fixedLength: false);
    private static void Prefix(IBufferWriter<byte> writer, uint value, uint maxFixed, byte tagFixed, byte tag8, byte tag16, byte tag32, bool fixedLength = true)
    {
        var target = writer.GetSpan(5);
        if (fixedLength && value <= maxFixed) { target[0] = (byte)(tagFixed | value); writer.Advance(1); }
        else if (tag8 != 0 && value <= byte.MaxValue) { target[0] = tag8; target[1] = (byte)value; writer.Advance(2); }
        else if (value <= ushort.MaxValue) { target[0] = tag16; BinaryPrimitives.WriteUInt16BigEndian(target[1..], (ushort)value); writer.Advance(3); }
        else { target[0] = tag32; BinaryPrimitives.WriteUInt32BigEndian(target[1..], value); writer.Advance(5); }
    }
}
