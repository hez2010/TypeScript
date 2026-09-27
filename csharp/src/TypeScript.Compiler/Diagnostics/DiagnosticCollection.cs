namespace TypeScript.Compiler.Diagnostics;

internal static class DiagnosticCollection
{
    internal static Diagnostic[] SortAndDeduplicate(IEnumerable<Diagnostic> diagnostics)
    {
        var sorted = diagnostics.ToArray();
        if (sorted.Length < 2)
            return sorted;
        Array.Sort(sorted, Compare);
        var result = new List<Diagnostic>(sorted.Length);
        for (int i = 0; i < sorted.Length;)
        {
            var diagnostic = sorted[i];
            int end = i + 1;
            while (end < sorted.Length && DiagnosticEqualityComparer.WithoutRelatedInformation.Equals(diagnostic, sorted[end]))
                end++;
            if (end > i + 1)
            {
                var related = new List<Diagnostic>();
                for (int j = i; j < end; j++)
                    related.AddRange(sorted[j].RelatedInformation);
                related.Sort(Compare);
                var compact = new List<Diagnostic>(related.Count);
                foreach (var item in related)
                    if (compact.Count == 0 || !DiagnosticEqualityComparer.Instance.Equals(compact[^1], item))
                        compact.Add(item);
                diagnostic = diagnostic with { RelatedInformation = compact.ToArray() };
            }
            result.Add(diagnostic);
            i = end;
        }
        return result.ToArray();
    }

    internal static int Compare(Diagnostic left, Diagnostic right)
    {
        var pending = new Stack<(Diagnostic Left, Diagnostic Right)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            var a = pair.Left;
            var b = pair.Right;
            if (ReferenceEquals(a, b))
                continue;
            int result = CompareText(a.FileName ?? "", b.FileName ?? "");
            if (result == 0)
                result = a.Start.CompareTo(b.Start);
            if (result == 0)
                result = a.Length.CompareTo(b.Length);
            if (result == 0)
                result = a.Code.CompareTo(b.Code);
            if (result == 0)
                result = ((int)a.Message.Category).CompareTo((int)b.Message.Category);
            if (result == 0)
                result = CompareText(a.Source ?? "", b.Source ?? "");
            if (result == 0)
                result = CompareText(DiagnosticEqualityComparer.Identity(a), DiagnosticEqualityComparer.Identity(b));
            if (result == 0)
                result = CompareArguments(a.Arguments, b.Arguments);
            if (result == 0)
                result = CompareChains(a.MessageChain, b.MessageChain, true);
            if (result == 0)
                result = CompareChains(a.MessageChain, b.MessageChain, false);
            if (result == 0)
                result = b.RelatedInformation.Count.CompareTo(a.RelatedInformation.Count);
            if (result != 0)
                return result;
            for (int i = a.RelatedInformation.Count - 1; i >= 0; i--)
                pending.Push((a.RelatedInformation[i], b.RelatedInformation[i]));
        }
        return 0;
    }

    private static int CompareChains(IReadOnlyList<Diagnostic> left, IReadOnlyList<Diagnostic> right, bool size)
    {
        var pending = new Stack<(IReadOnlyList<Diagnostic> Left, IReadOnlyList<Diagnostic> Right, int Index)>();
        pending.Push((left, right, -1));
        while (pending.TryPop(out var pair))
        {
            int result = 0;
            if (pair.Index < 0)
            {
                if (size)
                    result = pair.Right.Count.CompareTo(pair.Left.Count);
                if (result == 0)
                    for (int i = pair.Left.Count - 1; i >= 0; i--)
                        pending.Push((pair.Left, pair.Right, i));
            }
            else
            {
                var a = pair.Left[pair.Index];
                var b = pair.Right[pair.Index];
                if (!size)
                    result = CompareArguments(a.Arguments, b.Arguments);
                if (result == 0)
                    pending.Push((a.MessageChain, b.MessageChain, -1));
            }
            if (result != 0)
                return result;
        }
        return 0;
    }

    private static int CompareArguments(string[] left, string[] right)
    {
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            int result = CompareText(left[i], right[i]);
            if (result != 0)
                return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    // UTF-8 and WTF-8 sort by code point, including unpaired surrogate values.
    private static int CompareText(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            int a = Point(left, ref i), b = Point(right, ref j);
            if (a != b)
                return a.CompareTo(b);
        }
        return (left.Length - i).CompareTo(right.Length - j);

        static int Point(string text, ref int position)
        {
            char first = text[position++];
            return char.IsHighSurrogate(first) && position < text.Length && char.IsLowSurrogate(text[position])
                ? char.ConvertToUtf32(first, text[position++]) : first;
        }
    }
}
