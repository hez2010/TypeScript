using System.Text;

namespace TypeScript.Compiler.Syntax;

internal static partial class RegularExpressionUnicodeProperties
{
    // Weighted edit distance used by TypeScript's diagnostic spelling suggestions.
    // Rune enumeration avoids splitting supplementary characters in group names.
    internal static string? Suggest(string name, IEnumerable<string> candidates)
    {
        Rune[] input = name.EnumerateRunes().ToArray();
        int maximumLengthDifference = Math.Max(2, (int)(input.Length * 0.34));
        double bestDistance = Math.Floor(input.Length * 0.4) + 0.9;
        string? best = null;
        foreach (string candidate in candidates)
        {
            if (candidate == name || Math.Abs(Encoding.UTF8.GetByteCount(candidate) - input.Length) > maximumLengthDifference) continue;
            if (candidate.Length < 3 && !StringComparer.OrdinalIgnoreCase.Equals(candidate, name)) continue;
            Rune[] value = candidate.EnumerateRunes().ToArray();
            double distance = Distance(input, value, bestDistance);
            if (distance < 0) continue;
            if (distance < bestDistance || best is null || StringComparer.Ordinal.Compare(candidate, best) < 0)
            { bestDistance = distance; best = candidate; }
        }
        return best;
    }
    private static double Distance(Rune[] input, Rune[] value, double maximum)
    {
        double[] previous = new double[value.Length + 1], current = new double[value.Length + 1];
        for (int j = 0; j <= value.Length; j++) previous[j] = j;
        for (int i = 1; i <= input.Length; i++)
        {
            int min = Math.Max((int)Math.Ceiling(i - maximum), 1), max = Math.Min((int)Math.Floor(maximum + i), value.Length);
            current.AsSpan().Fill(maximum + 0.01);
            double columnMin = current[0] = i;
            for (int j = min; j <= max; j++)
            {
                double substitution = Rune.ToLowerInvariant(input[i - 1]) == Rune.ToLowerInvariant(value[j - 1]) ? 0.1 : 2;
                current[j] = input[i - 1] == value[j - 1] ? previous[j - 1] : Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, previous[j - 1] + substitution));
                columnMin = Math.Min(columnMin, current[j]);
            }
            if (columnMin > maximum) return -1;
            (previous, current) = (current, previous);
        }
        return previous[value.Length] > maximum ? -1 : previous[value.Length];
    }
}
