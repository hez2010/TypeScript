using TypeScript.Compiler.Text;
namespace TypeScript.Compiler.Diagnostics;

// Record equality compares argument arrays by identity. Diagnostic collections
// instead compare their contents, including nested message and related records.
internal sealed class DiagnosticEqualityComparer(bool relatedInformation = true) : IEqualityComparer<Diagnostic>
{
    internal static DiagnosticEqualityComparer Instance { get; } = new();
    internal static DiagnosticEqualityComparer WithoutRelatedInformation { get; } = new(false);

    public bool Equals(Diagnostic? left, Diagnostic? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null)
            return false;
        var pending = new Stack<(Diagnostic Left, Diagnostic Right, bool Chain)>();
        pending.Push((left, right, false));
        while (pending.TryPop(out var pair))
        {
            var a = pair.Left;
            var b = pair.Right;
            if (ReferenceEquals(a, b))
                continue;
            if (a.Code != b.Code || !a.Arguments.AsSpan().SequenceEqual(b.Arguments)
                || a.MessageChain.Count != b.MessageChain.Count)
                return false;
            if (!pair.Chain)
            {
                if ((a.FileName ?? Utf8String.Empty) != (b.FileName ?? Utf8String.Empty) || a.Start != b.Start || a.Length != b.Length
                    || a.Message.Category != b.Message.Category || (a.Source ?? Utf8String.Empty) != (b.Source ?? Utf8String.Empty)
                    || Identity(a) != Identity(b) || relatedInformation && a.RelatedInformation.Count != b.RelatedInformation.Count)
                    return false;
                if (relatedInformation)
                    for (int i = 0; i < a.RelatedInformation.Count; i++)
                        pending.Push((a.RelatedInformation[i], b.RelatedInformation[i], false));
            }
            for (int i = 0; i < a.MessageChain.Count; i++)
                pending.Push((a.MessageChain[i], b.MessageChain[i], true));
        }
        return true;
    }

    public int GetHashCode(Diagnostic diagnostic)
    {
        var hash = new HashCode();
        hash.Add(diagnostic.FileName ?? Utf8String.Empty, Utf8StringComparer.Ordinal);
        hash.Add(diagnostic.Start);
        hash.Add(diagnostic.Length);
        hash.Add(diagnostic.Code);
        hash.Add(diagnostic.Message.Category);
        hash.Add(diagnostic.Source ?? Utf8String.Empty, Utf8StringComparer.Ordinal);
        hash.Add(Identity(diagnostic), Utf8StringComparer.Ordinal);
        foreach (Utf8String argument in diagnostic.Arguments)
            hash.Add(argument);
        hash.Add(diagnostic.MessageChain.Count);
        if (relatedInformation)
            hash.Add(diagnostic.RelatedInformation.Count);
        return hash.ToHashCode();
    }

    internal static Utf8String Identity(Diagnostic diagnostic) =>
        diagnostic.Code == DiagnosticCode.Custom ? diagnostic.Message.Text : diagnostic.Message.Key;
}
