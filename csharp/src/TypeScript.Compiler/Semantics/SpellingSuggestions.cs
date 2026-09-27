using System.Text;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Semantics;

internal static class SpellingSuggestions
{
    internal static async ValueTask<T?> FindAsync<T>(
        TextSlice name,
        IEnumerable<T> candidates,
        Func<T, ValueTask<TextSlice?>> getName,
        Comparison<T> compare,
        int maximumCandidates = 0,
        CancellationToken cancellation = default) where T : class
    {
        var input = GoUnicode.Runes(name);
        int maximumLengthDifference = Math.Max(2, (int)(input.Length * 0.34));
        double bestDistance = Math.Floor(input.Length * 0.4) + 0.9;
        T? best = null;
        int count = 0;
        double[] previous = [], current = [];
        foreach (var candidate in candidates)
        {
            cancellation.ThrowIfCancellationRequested();
            if (maximumCandidates > 0 && ++count > maximumCandidates)
                return null;
            TextSlice? candidateText = await getName(candidate).ConfigureAwait(false);
            if (candidateText is not { IsEmpty: false } text)
                continue;
            int length = Encoding.UTF8.GetByteCount(text);
            if (Math.Abs(length - input.Length) > maximumLengthDifference || text == name)
                continue;
            var runes = GoUnicode.Runes(text);
            if (length < 3 && !GoUnicode.EqualFold(runes, input))
                continue;
            double distance = Distance(input, runes, bestDistance, ref previous, ref current);
            if (distance < 0)
                continue;
            if (distance < bestDistance || best is null || distance == bestDistance && compare(candidate, best) < 0)
            {
                bestDistance = distance;
                best = candidate;
            }
        }
        return best;
    }

    private static double Distance(int[] first, int[] second, double maximum, ref double[] previous, ref double[] current)
    {
        if (previous.Length < second.Length + 1)
            Array.Resize(ref previous, second.Length + 1);
        if (current.Length < second.Length + 1)
            Array.Resize(ref current, second.Length + 1);
        double big = maximum + 0.01;
        for (int j = 0; j <= second.Length; j++)
            previous[j] = j;
        for (int i = 1; i <= first.Length; i++)
        {
            int minimumColumn = Math.Max((int)Math.Ceiling(i - maximum), 1);
            int maximumColumn = Math.Min((int)Math.Floor(maximum + i), second.Length);
            double columnMinimum = i;
            current[0] = i;
            for (int j = 1; j < minimumColumn; j++)
                current[j] = big;
            for (int j = minimumColumn; j <= maximumColumn; j++)
            {
                double substitution = previous[j - 1] + (GoUnicode.Lower(first[i - 1]) == GoUnicode.Lower(second[j - 1]) ? 0.1 : 2);
                double distance = first[i - 1] == second[j - 1]
                    ? previous[j - 1]
                    : Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, substitution));
                current[j] = distance;
                columnMinimum = Math.Min(columnMinimum, distance);
            }
            for (int j = maximumColumn + 1; j <= second.Length; j++)
                current[j] = big;
            if (columnMinimum > maximum)
                return -1;
            (previous, current) = (current, previous);
        }
        return previous[second.Length] > maximum ? -1 : previous[second.Length];
    }
}
