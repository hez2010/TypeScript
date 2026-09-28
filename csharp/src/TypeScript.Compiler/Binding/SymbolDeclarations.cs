using System.Collections;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Binding;

/// <summary>An immutable declaration sequence with an inline singleton.</summary>
public readonly struct SymbolDeclarations : IReadOnlyList<SyntaxNode>
{
    // A symbol normally has one declaration. Keep that node directly instead of
    // allocating a separate one-element array for every symbol.
    internal object? Data { get; }

    internal SymbolDeclarations(object? data) => Data = data;

    public int Length => Data switch
    {
        SyntaxNode[] nodes => nodes.Length,
        SyntaxNode => 1,
        _ => 0
    };

    public int Count => Length;

    public SyntaxNode this[int index] => Data switch
    {
        SyntaxNode[] nodes => nodes[index],
        SyntaxNode node when index == 0 => node,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public bool Contains(SyntaxNode node) => Data switch
    {
        SyntaxNode[] nodes => Array.IndexOf(nodes, node) >= 0,
        SyntaxNode single => ReferenceEquals(single, node),
        _ => false
    };

    public SymbolDeclarations Add(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Data is null)
            return new(node);
        if (Data is SyntaxNode single)
            return new(new[] { single, node });
        var current = (SyntaxNode[])Data;
        var result = new SyntaxNode[current.Length + 1];
        current.CopyTo(result, 0);
        result[^1] = node;
        return new(result);
    }

    public SymbolDeclarations AddRange(SymbolDeclarations nodes)
    {
        if (nodes.Length == 0)
            return this;
        if (Length == 0)
            return nodes;
        var result = new SyntaxNode[Length + nodes.Length];
        CopyTo(result, 0);
        nodes.CopyTo(result, Length);
        return new(result);
    }

    public SymbolDeclarations AddRange(IEnumerable<SyntaxNode> nodes)
    {
        if (nodes is SymbolDeclarations declarations)
            return AddRange(declarations);
        if (nodes is ICollection<SyntaxNode> collection)
        {
            if (collection.Count == 0)
                return this;
            if (Length == 0 && collection.Count == 1)
                return new(collection.First());
            var combined = new SyntaxNode[Length + collection.Count];
            CopyTo(combined, 0);
            collection.CopyTo(combined, Length);
            return new(combined);
        }
        var additions = nodes.ToArray();
        if (additions.Length == 0)
            return this;
        if (Length == 0)
            return additions.Length == 1 ? new(additions[0]) : new(additions);
        var result = new SyntaxNode[Length + additions.Length];
        CopyTo(result, 0);
        additions.CopyTo(result, Length);
        return new(result);
    }

    public SymbolDeclarations RemoveAll(Predicate<SyntaxNode> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (Data is SyntaxNode single)
            return predicate(single) ? default : this;
        if (Data is not SyntaxNode[] nodes)
            return this;
        int first = Array.FindIndex(nodes, predicate);
        if (first < 0)
            return this;
        var retained = new List<SyntaxNode>(nodes.Length - 1);
        for (int i = 0; i < first; i++)
            retained.Add(nodes[i]);
        for (int i = first + 1; i < nodes.Length; i++)
            if (!predicate(nodes[i]))
                retained.Add(nodes[i]);
        if (retained.Count == 0)
            return default;
        return retained.Count == 1 ? new(retained[0]) : new(retained.ToArray());
    }

    public SyntaxNode[] ToArray()
    {
        var result = new SyntaxNode[Length];
        CopyTo(result, 0);
        return result;
    }

    public SyntaxNode? FirstOrDefault() => Length == 0 ? null : this[0];

    public SyntaxNode? FirstOrDefault(Func<SyntaxNode, bool> predicate)
    {
        foreach (var node in this)
            if (predicate(node))
                return node;
        return null;
    }

    public T? FirstOfType<T>() where T : SyntaxNode
    {
        foreach (var node in this)
            if (node is T found)
                return found;
        return null;
    }

    public T? FirstOfType<T>(Func<T, bool> predicate) where T : SyntaxNode
    {
        foreach (var node in this)
            if (node is T found && predicate(found))
                return found;
        return null;
    }

    public bool Any(Func<SyntaxNode, bool> predicate) => FirstOrDefault(predicate) is not null;

    public bool AnyOfType<T>(Func<T, bool> predicate) where T : SyntaxNode => FirstOfType(predicate) is not null;

    public bool All(Func<SyntaxNode, bool> predicate)
    {
        foreach (var node in this)
            if (!predicate(node))
                return false;
        return true;
    }

    public int CountWhere(Func<SyntaxNode, bool> predicate)
    {
        int count = 0;
        foreach (var node in this)
            if (predicate(node))
                count++;
        return count;
    }

    private void CopyTo(SyntaxNode[] destination, int index)
    {
        if (Data is SyntaxNode[] nodes)
            nodes.CopyTo(destination, index);
        else if (Data is SyntaxNode node)
            destination[index] = node;
    }

    public Enumerator GetEnumerator() => new(Data);
    IEnumerator<SyntaxNode> IEnumerable<SyntaxNode>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<SyntaxNode>
    {
        private readonly SyntaxNode? single;
        private readonly SyntaxNode[]? nodes;
        private int index;

        internal Enumerator(object? data)
        {
            single = data as SyntaxNode;
            nodes = data as SyntaxNode[];
            index = -1;
        }

        public SyntaxNode Current => nodes is null ? single! : nodes[index];
        object IEnumerator.Current => Current;
        public bool MoveNext() => ++index < (nodes?.Length ?? (single is null ? 0 : 1));
        public void Reset() => index = -1;
        public void Dispose() { }
    }
}
