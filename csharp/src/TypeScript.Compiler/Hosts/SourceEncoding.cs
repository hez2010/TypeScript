using System.Buffers.Binary;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Hosts;

public static class SourceEncoding
{
    public static byte[] DecodeBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return bytes[3..].ToArray();
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return Wtf8.Encode(Decode(bytes));
        return bytes.ToArray();
    }
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return Wtf8.DecodeString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            bool little = bytes[0] == 0xFF;
            char[] text = new char[(bytes.Length - 2) / 2];
            for (int i = 0; i < text.Length; i++)
                text[i] = (char)(little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2 + i * 2, 2)) : BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(2 + i * 2, 2)));
            return new string(text);
        }
        return Wtf8.DecodeString(bytes);
    }
}
