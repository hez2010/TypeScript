using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Semantics;

internal static partial class GoUnicode
{
    internal static int Upper(int value) => Lookup(UpperMap, value);
    internal static bool IsUpper(int value) => InRanges(UpperCaseRanges, value);
    internal static bool IsLower(int value) => InRanges(LowerCaseRanges, value);

    internal static Utf8String LowerText(Utf8String text)
    {
        var builder = new Utf8StringBuilder();
        foreach (int point in Runes(text)) builder.AppendCodePoint(Lower(point));
        return builder.ToUtf8String();
    }

    // Invariant globalization makes String.Normalize a no-op. The pinned canonical
    // decompositions preserve the reference's locale-independent import ordering.
    internal static Utf8String NaturalSortKey(Utf8String text)
    {
        var builder = new Utf8StringBuilder();
        List<(int Point, int Class)> segment = [];
        int nonstarters = 0;
        foreach (int point in Runes(text))
        {
            int counts = Math.Max(0, MapValue(StreamSafeCounts, point)), leading = counts & 3;
            if (nonstarters + leading > 30) { Flush(); nonstarters = 0; }
            nonstarters = leading == 0 ? counts >> 2 : nonstarters + leading;
            if (point is >= 0xac00 and <= 0xd7a3)
            {
                int syllable = point - 0xac00;
                Append(0x1100 + syllable / 588);
                Append(0x1161 + syllable % 588 / 28);
                if (syllable % 28 is var trailing && trailing != 0) Append(0x11a7 + trailing);
            }
            else if (MapValue(DecompositionIndex, point) is var offset && offset >= 0)
                foreach (int decomposed in Decompositions.Slice(offset + 1, Decompositions[offset])) Append(decomposed);
            else Append(point);
        }
        Flush();
        return builder.ToUtf8String();

        void Append(int point)
        {
            int combiningClass = Math.Max(0, MapValue(CombiningClasses, point));
            if (combiningClass == 0) Flush();
            int index = segment.Count;
            if (combiningClass != 0)
                while (index > 0 && segment[index - 1].Class > combiningClass) index--;
            segment.Insert(index, (point, combiningClass));
        }
        void Flush()
        {
            foreach (var entry in segment)
                if (!InRanges(NonspacingMarkRanges, entry.Point)) builder.AppendCodePoint(Lower(entry.Point));
            segment.Clear();
        }
    }

    private static int MapValue(ReadOnlySpan<int> map, int value)
    {
        int start = 0, end = map.Length / 2;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            if (map[middle * 2] < value) start = middle + 1;
            else if (map[middle * 2] > value) end = middle;
            else return map[middle * 2 + 1];
        }
        return -1;
    }

    private static bool InRanges(ReadOnlySpan<int> ranges, int value)
    {
        int start = 0, end = ranges.Length / 2;
        while (start < end)
        {
            int middle = start + (end - start) / 2;
            if (ranges[middle * 2] > value) end = middle;
            else if (ranges[middle * 2 + 1] < value) start = middle + 1;
            else return true;
        }
        return false;
    }
}
