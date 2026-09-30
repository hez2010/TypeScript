using System.Buffers.Binary;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

public static class SourceEncoding
{
    public static byte[] DecodeBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("\uFEFF"u8))
            return bytes[3..].ToArray();
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            bool little = bytes[0] == 0xFF;
            var text = new Utf8StringBuilder(bytes.Length);
            for (int at = 2; at + 1 < bytes.Length; at += 2)
            {
                int point = Read(bytes[at..], little);
                if (point is >= 0xD800 and <= 0xDBFF && at + 3 < bytes.Length)
                {
                    int low = Read(bytes[(at + 2)..], little);
                    if (low is >= 0xDC00 and <= 0xDFFF)
                    {
                        point = 0x10000 + (point - 0xD800 << 10) + low - 0xDC00;
                        at += 2;
                    }
                }
                text.AppendCodePoint(point);
            }
            return text.WrittenSpan.ToArray();
        }
        return bytes.ToArray();

        static int Read(ReadOnlySpan<byte> value, bool little) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(value) : BinaryPrimitives.ReadUInt16BigEndian(value);
    }

    internal static byte[] DecodeOwnedBytes(byte[] bytes) =>
        bytes.AsSpan().StartsWith("\uFEFF"u8) || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
            || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? DecodeBytes(bytes) : bytes;

    public static Utf8String Decode(ReadOnlySpan<byte> bytes) => new(DecodeBytes(bytes));
}
