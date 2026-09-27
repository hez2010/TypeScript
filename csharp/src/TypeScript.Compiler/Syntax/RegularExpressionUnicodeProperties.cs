using System.Text;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Syntax;

internal static partial class RegularExpressionUnicodeProperties
{
    // Weighted edit distance used by TypeScript's diagnostic spelling suggestions.
    // Rune enumeration avoids splitting supplementary characters in group names.
    internal static TextSlice? Suggest(ReadOnlySpan<char> name, IEnumerable<TextSlice> candidates)
    {
        ReadOnlySpan<Rune> input = Runes(name);
        int maximumLengthDifference = Math.Max(2, (int)(input.Length * 0.34));
        double bestDistance = Math.Floor(input.Length * 0.4) + 0.9;
        TextSlice? best = null;
        double[] previous = [], current = [];
        foreach (TextSlice candidate in candidates)
        {
            if (candidate.Span.SequenceEqual(name) || Math.Abs(Encoding.UTF8.GetByteCount(candidate.Span) - input.Length) > maximumLengthDifference)
                continue;
            if (candidate.Length < 3 && !candidate.Span.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            ReadOnlySpan<Rune> value = Runes(candidate.Span);
            double distance = Distance(input, value, bestDistance, ref previous, ref current);
            if (distance < 0)
                continue;
            if (distance < bestDistance || best is null || candidate.CompareTo(best.Value) < 0)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    private static ReadOnlySpan<Rune> Runes(ReadOnlySpan<char> text)
    {
        Rune[] buffer = new Rune[text.Length];
        int count = 0;
        foreach (Rune rune in text.EnumerateRunes())
            buffer[count++] = rune;
        return buffer.AsSpan(0, count);
    }

    private static double Distance(ReadOnlySpan<Rune> input, ReadOnlySpan<Rune> value, double maximum,
        ref double[] previous, ref double[] current)
    {
        if (previous.Length < value.Length + 1)
            Array.Resize(ref previous, value.Length + 1);
        if (current.Length < value.Length + 1)
            Array.Resize(ref current, value.Length + 1);
        for (int j = 0; j <= value.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= input.Length; i++)
        {
            int min = Math.Max((int)Math.Ceiling(i - maximum), 1), max = Math.Min((int)Math.Floor(maximum + i), value.Length);
            current.AsSpan().Fill(maximum + 0.01);
            double columnMin = current[0] = i;
            for (int j = min; j <= max; j++)
            {
                double substitution = Rune.ToLowerInvariant(input[i - 1]) == Rune.ToLowerInvariant(value[j - 1]) ? 0.1 : 2;
                current[j] = input[i - 1] == value[j - 1]
                    ? previous[j - 1]
                    : Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, previous[j - 1] + substitution));
                columnMin = Math.Min(columnMin, current[j]);
            }
            if (columnMin > maximum)
                return -1;
            (previous, current) = (current, previous);
        }
        return previous[value.Length] > maximum ? -1 : previous[value.Length];
    }
}
