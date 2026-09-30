using TypeScript.Compiler.Text;
using System.Text;

namespace TypeScript.Compiler.Semantics;

internal static partial class JsCase
{
    internal static Utf8String Upper(Utf8String text) => Convert(text, true);

    internal static Utf8String Lower(Utf8String text) => Convert(text, false);

    private static Utf8String Convert(Utf8String text, bool upper)
    {
        if (Ascii.IsValid(text))
        {
            byte[] mapped = new byte[text.Length];
            if (upper)
                Ascii.ToUpper(text, mapped, out _);
            else
                Ascii.ToLower(text, mapped, out _);
            return new(mapped);
        }
        var result = new Utf8StringBuilder(text.Length);
        bool casedBefore = false;
        for (int i = 0; i < text.Length;)
        {
            int start = i, scalar = Next(text, ref i);
            if (Mappings.TryGetValue(scalar, out var mapping))
                result.Append(upper ? mapping.Upper : mapping.FinalLower is { } final && casedBefore && !CasedAfter(text, i)
                    ? final : mapping.Lower);
            else if (scalar is >= 0xD800 and <= 0xDFFF)
                result.Append(text.Span.Slice(start, i - start));
            else
                result.AppendCodePoint(scalar);
            if (!InRanges(CaseIgnorable, scalar))
                casedBefore = InRanges(Cased, scalar);
        }
        return Utf8String.FromBuilder(result);
    }

    private static bool CasedAfter(Utf8String text, int index)
    {
        while (index < text.Length)
        {
            int scalar = Next(text, ref index);
            if (!InRanges(CaseIgnorable, scalar))
                return InRanges(Cased, scalar);
        }
        return false;
    }

    internal static int FirstScalarLength(Utf8String text)
    {
        Wtf8.Decode(text, out int width);
        return width;
    }

    private static int Next(Utf8String text, ref int index)
    {
        int point = Wtf8.Decode(text.Span[index..], out int width);
        index += width;
        return point;
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
