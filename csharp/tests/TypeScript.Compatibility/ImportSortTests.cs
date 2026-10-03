using System.Buffers.Binary;
using System.Security.Cryptography;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ImportSortTests
{
    internal static string NormalizationDigest()
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int cases = 0;
        for (int point = 0; point <= 0x10ffff; point++)
        {
            if (point is >= 0xd800 and <= 0xdfff) continue;
            var text = new Utf8StringBuilder().AppendCodePoint(point).ToUtf8String();
            Add(text);
        }
        uint seed = 0x7354162a;
        int[] alphabet = [0x41, 0xe9, 0x345, 0x1d165, 0x302e, 0x302f, 0x3099, 0x315, 0x300, 0x1e9b, 0x344,
            0x1161, 0x11a8, 0xac00, 0x1fa, 0x034f, 0x0f73, 0x0f75, 0x0f81, 0x33c4, 0x1f600, 0x5d0, 0x5b0, 0x10ffff];
        for (int i = 0; i < 10_000; i++)
        {
            var builder = new Utf8StringBuilder();
            int length = (int)(Next() % 90);
            for (int j = 0; j < length; j++) builder.AppendCodePoint(alphabet[Next() % alphabet.Length]);
            Add(builder.ToUtf8String());
        }
        for (int point = 0; point < 256; point++) Add(Utf8String.Copy([(byte)point, 0xed, 0xa0, 0x80, 0xff]));
        return $"{cases} {Convert.ToHexStringLower(digest.GetHashAndReset())}";

        uint Next() { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; return seed; }
        void Add(Utf8String input)
        {
            cases++;
            Span<byte> length = stackalloc byte[4];
            foreach (var output in new[] { GoUnicode.LowerText(input), GoUnicode.NaturalSortKey(input) })
            {
                BinaryPrimitives.WriteInt32LittleEndian(length, output.Length);
                digest.AppendData(length); digest.AppendData(output);
            }
        }
    }
}
