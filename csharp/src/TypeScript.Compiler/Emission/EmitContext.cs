using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public readonly record struct EmitRange(int Pos, int End);
public sealed record SyntheticComment(SyntaxKind Kind, Utf8String Text, bool HasTrailingNewLine = false, bool HasLeadingNewLine = false);

/// <summary>Owns transformation metadata independently of immutable program syntax.</summary>
public sealed partial class EmitContext
{
    internal sealed class NodeData
    {
        internal EmitFlags Flags;
        internal EmitRange? CommentRange, SourceMapRange;
        internal Dictionary<SyntaxKind, EmitRange>? TokenRanges;
        internal List<SyntheticComment>? LeadingComments, TrailingComments;
        internal SyntaxNode? TextSource, AssignedName, ClassThis, Type;
        internal IdentifierNode? ExternalHelpersModuleName;
        internal List<EmitHelper>? Helpers;
        internal int? SnippetTabStop;

        internal NodeData Clone() => new()
        {
            Flags = Flags, CommentRange = CommentRange, SourceMapRange = SourceMapRange,
            TokenRanges = TokenRanges is null ? null : new(TokenRanges),
            ExternalHelpersModuleName = ExternalHelpersModuleName,
            Helpers = Helpers is null ? null : [.. Helpers], SnippetTabStop = SnippetTabStop
        };
    }

    private readonly Dictionary<SyntaxNode, SyntaxNode> originals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SyntaxNode, NodeData> nodes = new(ReferenceEqualityComparer.Instance);
    private readonly List<EmitHelper> helpers = [];
    private readonly HashSet<EmitHelper> helperSet = new(ReferenceEqualityComparer.Instance);
    public NodeFactory Factory { get; }

    public EmitContext() => Factory = new()
    {
        OnCreate = node => node.Flags |= NodeFlags.Synthesized,
        OnClone = CopyOriginal,
        OnUpdate = (node, original) => SetOriginal(node, original)
    };

    private void CopyOriginal(SyntaxNode node, SyntaxNode original)
    {
        SetOriginal(node, original);
        if (generatedNames.TryGetValue(original, out var generated))
            generatedNames[node] = generated;
    }

    internal NodeData Data(SyntaxNode node)
    {
        if (!nodes.TryGetValue(node, out var data))
            nodes.Add(node, data = new());
        return data;
    }

    internal NodeData? TryData(SyntaxNode node) => nodes.GetValueOrDefault(node);

    public void SetOriginal(SyntaxNode node, SyntaxNode original, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(original);
        if (ReferenceEquals(node, original))
            throw new ArgumentException("A node cannot be its own original", nameof(original));
        for (var ancestor = original; ancestor is not null; ancestor = originals.GetValueOrDefault(ancestor))
            if (ancestor == node)
                throw new ArgumentException("Cyclic original-node association", nameof(original));
        if (originals.TryGetValue(node, out var existing))
        {
            if (existing != original && !overwrite)
                throw new InvalidOperationException("Original node already set");
        }
        else if (nodes.TryGetValue(original, out var data))
            nodes[node] = data.Clone();
        originals[node] = original;
    }

    public SyntaxNode? Original(SyntaxNode node) => originals.GetValueOrDefault(node);
    public void UnsetOriginal(SyntaxNode node) => originals.Remove(node);

    public SyntaxNode MostOriginal(SyntaxNode node)
    {
        while (originals.TryGetValue(node, out var original))
            node = original;
        return node;
    }

    public SyntaxNode? ParseNode(SyntaxNode node)
    {
        node = MostOriginal(node);
        return (node.Flags & NodeFlags.Synthesized) == 0 ? node : null;
    }

    public T Clone<T>(T node) where T : SyntaxNode
    {
        var result = (T)node.ShallowClone();
        Factory.Cloned(result, node);
        return result;
    }

    public EmitFlags GetFlags(SyntaxNode node) => TryData(node)?.Flags ?? 0;
    public void SetFlags(SyntaxNode node, EmitFlags flags) => Data(node).Flags = flags;
    public void AddFlags(SyntaxNode node, EmitFlags flags) => Data(node).Flags |= flags;
    public EmitRange GetCommentRange(SyntaxNode node) => TryData(node)?.CommentRange ?? new(node.Pos, node.End);
    public void SetCommentRange(SyntaxNode node, EmitRange range) => Data(node).CommentRange = range;
    public EmitRange GetSourceMapRange(SyntaxNode node) => TryData(node)?.SourceMapRange ?? new(node.Pos, node.End);
    public void SetSourceMapRange(SyntaxNode node, EmitRange range) => Data(node).SourceMapRange = range;
    public EmitRange? GetTokenSourceMapRange(SyntaxNode node, SyntaxKind token) =>
        TryData(node)?.TokenRanges is { } ranges && ranges.TryGetValue(token, out var range) ? range : null;
    public void SetTokenSourceMapRange(SyntaxNode node, SyntaxKind token, EmitRange range) => (Data(node).TokenRanges ??= [])[token] = range;
    public IReadOnlyList<SyntheticComment> LeadingComments(SyntaxNode node) => TryData(node)?.LeadingComments ?? [];
    public IReadOnlyList<SyntheticComment> TrailingComments(SyntaxNode node) => TryData(node)?.TrailingComments ?? [];
    public void AddLeadingComment(SyntaxNode node, SyntheticComment comment) => (Data(node).LeadingComments ??= []).Add(comment);
    public void AddTrailingComment(SyntaxNode node, SyntheticComment comment) => (Data(node).TrailingComments ??= []).Add(comment);
    public void SetLeadingComments(SyntaxNode node, IEnumerable<SyntheticComment> comments) => Data(node).LeadingComments = [.. comments];
    public void SetTrailingComments(SyntaxNode node, IEnumerable<SyntheticComment> comments) => Data(node).TrailingComments = [.. comments];

    public void RequestHelper(EmitHelper helper)
    {
        var visiting = new HashSet<EmitHelper>(ReferenceEqualityComparer.Instance);
        var work = new Stack<(EmitHelper Helper, bool Visited)>();
        work.Push((helper, false));
        while (work.TryPop(out var item))
        {
            if (helperSet.Contains(item.Helper))
                continue;
            if (item.Helper.Scoped)
                throw new ArgumentException("Scoped helpers must be attached to their containing node", nameof(helper));
            if (item.Visited)
            {
                visiting.Remove(item.Helper);
                helperSet.Add(item.Helper);
                helpers.Add(item.Helper);
                continue;
            }
            if (!visiting.Add(item.Helper))
                throw new ArgumentException("Cyclic emit-helper dependency", nameof(helper));
            work.Push((item.Helper, true));
            for (int i = item.Helper.Dependencies.Count - 1; i >= 0; i--)
                work.Push((item.Helper.Dependencies[i], false));
        }
    }

    public IReadOnlyList<EmitHelper> ReadHelpers()
    {
        var result = helpers.ToArray();
        helpers.Clear();
        helperSet.Clear();
        return result;
    }

    public void AddHelper(SyntaxNode node, EmitHelper helper)
    {
        var items = Data(node).Helpers ??= [];
        if (!items.Contains(helper))
            items.Add(helper);
    }

    public IReadOnlyList<EmitHelper> GetHelpers(SyntaxNode node) => TryData(node)?.Helpers ?? [];
    public IdentifierNode? GetExternalHelpersModuleName(SyntaxNode node) => TryData(MostOriginal(node))?.ExternalHelpersModuleName;
    public void SetExternalHelpersModuleName(SyntaxNode node, IdentifierNode? name) => Data(MostOriginal(node)).ExternalHelpersModuleName = name;
    public bool HasRecordedExternalHelpers(SyntaxNode node) => (GetFlags(MostOriginal(node)) & EmitFlags.ExternalHelpers) != 0
        || GetExternalHelpersModuleName(node) is not null;
    public SyntaxNode? GetTextSource(SyntaxNode node) => TryData(node)?.TextSource;
    public void SetTextSource(SyntaxNode node, SyntaxNode source) => Data(node).TextSource = source;
    internal int? GetSnippetTabStop(SyntaxNode node) => TryData(node)?.SnippetTabStop;
    internal void SetSnippetTabStop(EmptyStatementNode node, int order) => Data(node).SnippetTabStop = order;
    public SyntaxNode? GetAssignedName(SyntaxNode node) => TryData(node)?.AssignedName;
    public void SetAssignedName(SyntaxNode node, SyntaxNode name) => Data(node).AssignedName = name;
    public SyntaxNode? GetClassThis(SyntaxNode node) => TryData(node)?.ClassThis;
    public void SetClassThis(SyntaxNode node, SyntaxNode expression) => Data(node).ClassThis = expression;
    public SyntaxNode? GetTypeNode(SyntaxNode node) => TryData(node)?.Type;
    public void SetTypeNode(SyntaxNode node, SyntaxNode? type) => Data(node).Type = type;
}

public sealed class EmitHelper(Utf8String name, Utf8String importName, Utf8String text, bool scoped = false,
    int? priority = null, IReadOnlyList<EmitHelper>? dependencies = null, Func<Func<Utf8String, Utf8String>, Utf8String>? textFactory = null)
{
    public Utf8String Name { get; } = name;
    public Utf8String ImportName { get; } = importName;
    public Utf8String Text { get; } = text;
    public bool Scoped { get; } = scoped;
    public int? Priority { get; } = priority;
    public IReadOnlyList<EmitHelper> Dependencies { get; } = Array.AsReadOnly(dependencies?.ToArray() ?? []);
    public Func<Func<Utf8String, Utf8String>, Utf8String>? TextFactory { get; } = textFactory;
}
