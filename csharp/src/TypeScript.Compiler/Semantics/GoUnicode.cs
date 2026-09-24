using System.Buffers;
using System.Text;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Semantics;

internal static partial class GoUnicode
{
    internal static int Lower(int value) => Lookup(LowerMap, value);

    internal static int Fold(int value) => Lookup(FoldMap, value);

    private static int Lookup(ReadOnlySpan<int> map, int value)
    {
        int low = 0, high = map.Length / 2;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (map[middle * 2] < value)
                low = middle + 1;
            else if (map[middle * 2] > value)
                high = middle;
            else
                return map[middle * 2 + 1];
        }
        return value;
    }

    // Go's []rune uses strict UTF-8 and consumes one byte per invalid encoding.
    internal static int[] Runes(string text)
    {
        ReadOnlySpan<byte> bytes = Wtf8.Encode(text);
        List<int> result = [];
        while (!bytes.IsEmpty)
        {
            if (Rune.DecodeFromUtf8(bytes, out var rune, out int consumed) == OperationStatus.Done)
            {
                result.Add(rune.Value);
                bytes = bytes[consumed..];
            }
            else
            {
                result.Add(0xfffd);
                bytes = bytes[1..];
            }
        }
        return result.ToArray();
    }

    internal static bool EqualFold(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] == right[i])
                continue;
            int folded = Fold(left[i]);
            while (folded != left[i] && folded != right[i])
                folded = Fold(folded);
            if (folded != right[i])
                return false;
        }
        return true;
    }
}
