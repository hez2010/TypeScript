using TypeScript.Compiler.Text;
using System.Text;

namespace TypeScript.Compiler.Semantics;

internal static partial class JsCase
{
    internal static TextSlice Upper(TextSlice text) => Convert(text, true);

    internal static TextSlice Lower(TextSlice text) => Convert(text, false);

    private static TextSlice Convert(TextSlice text, bool upper)
    {
        if (!text.Span.ContainsAnyExceptInRange('\0', '\x7f'))
        {
            char[] mapped = new char[text.Length];
            if (upper)
                text.Span.ToUpperInvariant(mapped);
            else
                text.Span.ToLowerInvariant(mapped);
            return new(mapped);
        }
        var result = new StringBuilder(text.Length);
        bool casedBefore = false;
        for (int i = 0; i < text.Length;)
        {
            int start = i, scalar = Next(text, ref i);
            if (Mappings.TryGetValue(scalar, out var mapping))
                result.Append(upper ? mapping.Upper : mapping.FinalLower is not null && casedBefore && !CasedAfter(text, i)
                    ? mapping.FinalLower : mapping.Lower);
            else
                result.Append(text.Span.Slice(start, i - start));
            if (!InRanges(CaseIgnorable, scalar))
                casedBefore = InRanges(Cased, scalar);
        }
        return TextSlice.FromBuilder(result);
    }

    private static bool CasedAfter(TextSlice text, int index)
    {
        while (index < text.Length)
        {
            int scalar = Next(text, ref index);
            if (!InRanges(CaseIgnorable, scalar))
                return InRanges(Cased, scalar);
        }
        return false;
    }

    internal static int FirstScalarLength(TextSlice text) => text.Length == 0 ? 0
        : text.Length >= 2 && char.IsSurrogatePair(text[0], text[1]) ? 2 : 1;

    private static int Next(TextSlice text, ref int index)
    {
        char first = text[index++];
        return char.IsHighSurrogate(first) && index < text.Length && char.IsLowSurrogate(text[index])
            ? char.ConvertToUtf32(first, text[index++]) : first;
    }

    private static bool InRanges(ReadOnlySpan<int> ranges, int value)
    {
        int low = 0, high = ranges.Length / 3;
        while (low < high)
        {
            int middle = (low + high) / 2, index = middle * 3;
            if (value < ranges[index])
                high = middle;
            else if (value > ranges[index + 1])
                low = middle + 1;
            else
                return (value - ranges[index]) % ranges[index + 2] == 0;
        }
        return false;
    }
}
