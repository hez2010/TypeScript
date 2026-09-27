using System.Collections;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Ast;

public interface ITypedNode { SyntaxNode? Type { get; set; } }
public interface IInitializedNode { SyntaxNode? Initializer { get; set; } }
public interface IFullSignatureNode { SyntaxNode? FullSignature { get; set; } }
public interface ITypeExpressionNode { SyntaxNode? TypeExpression { get; set; } }
public interface IFunctionSignature : ITypedNode
{
    NodeList? TypeParameters { get; set; }
    NodeList? Parameters { get; set; }
}
public interface IModifiedNode { NodeList? Modifiers { get; set; } }
public interface INamedNode { SyntaxNode? Name { get; } }

public abstract class SyntaxNode(SyntaxKind kind)
{
    public SyntaxKind Kind { get; } = kind;
    public NodeFlags Flags { get; set; }
    public int Pos { get; set; } = -1;
    public int End { get; set; } = -1;
    public SyntaxNode? Parent { get; internal set; }
    public abstract int ChildCount { get; }

    public abstract SyntaxNode GetChild(int index);

    internal abstract void RewriteChildren(IReadOnlyDictionary<SyntaxNode, SyntaxNode> copies);

    internal virtual SyntaxNode ShallowClone() => (SyntaxNode)MemberwiseClone();

    internal virtual void ConvertPositions(Func<int, int> convert)
    {
        Pos = convert(Pos);
        End = convert(End);
    }

    public IEnumerable<SyntaxNode> DescendantsAndSelf()
    {
        var stack = new Stack<SyntaxNode>();
        stack.Push(this);
        while (stack.TryPop(out SyntaxNode? node))
        {
            yield return node;
            for (int i = node.ChildCount - 1; i >= 0; i--)
                stack.Push(node.GetChild(i));
        }
    }

    public void SetParents()
    {
        foreach (SyntaxNode node in DescendantsAndSelf())
            for (int i = 0; i < node.ChildCount; i++)
                node.GetChild(i).Parent = node;
    }

    public T DeepClone<T>(NodeFactory? factory = null) where T : SyntaxNode
    {
        var copies = new Dictionary<SyntaxNode, SyntaxNode>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(SyntaxNode Node, bool Visited)>();
        stack.Push((this, false));
        while (stack.TryPop(out var item))
        {
            if (!item.Visited)
            {
                stack.Push((item.Node, true));
                for (int i = item.Node.ChildCount - 1; i >= 0; i--)
                    stack.Push((item.Node.GetChild(i), false));
                continue;
            }
            var clone = item.Node.ShallowClone();
            clone.Parent = null;
            clone.RewriteChildren(copies);
            copies.Add(item.Node, clone);
            factory?.Cloned(clone, item.Node);
        }
        var result = (T)copies[this];
        result.SetParents();
        if (this is SourceFileNode originalFile && result is SourceFileNode clonedFile)
        {
            clonedFile.ReparsedClones = originalFile.ReparsedClones.Where(copies.ContainsKey).Select(n => copies[n]).ToArray();
            clonedFile.RemapSourceMetadata(originalFile, copies);
        }
        return result;
    }
}

public sealed class NodeList(SyntaxNode[] nodes, int pos = -1, int end = -1, bool isMissing = false) : IReadOnlyList<SyntaxNode>
{
    private readonly SyntaxNode[] nodes = nodes;
    public int Pos { get; private set; } = pos;
    public int End { get; private set; } = end;
    public bool IsMissing { get; } = isMissing;
    public bool HasTrailingComma => Count != 0 && this[Count - 1].End < End;
    public int Count => nodes.Length;
    public SyntaxNode this[int index] => nodes[index];

    public int IndexOf(SyntaxNode node) => Array.IndexOf(nodes, node);

    public IEnumerator<SyntaxNode> GetEnumerator() => ((IEnumerable<SyntaxNode>)nodes).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal NodeList Map(IReadOnlyDictionary<SyntaxNode, SyntaxNode> copies) =>
        new(Array.ConvertAll(nodes, n => copies[n]), Pos, End, IsMissing);

    internal void ConvertPositions(Func<int, int> convert)
    {
        Pos = convert(Pos);
        End = convert(End);
    }
}

public sealed partial class NodeFactory
{
    public Action<SyntaxNode>? OnCreate { get; init; }
    public Action<SyntaxNode, SyntaxNode>? OnClone { get; init; }
    public Action<SyntaxNode, SyntaxNode>? OnUpdate { get; init; }
    public int NodeCount { get; private set; }

    private T Created<T>(T node) where T : SyntaxNode
    {
        NodeCount++;
        OnCreate?.Invoke(node);
        return node;
    }

    internal void Cloned(SyntaxNode clone, SyntaxNode original)
    {
        NodeCount++;
        OnClone?.Invoke(clone, original);
    }

    public T Update<T>(T original, T replacement) where T : SyntaxNode
    {
        if (ReferenceEquals(original, replacement))
            return original;
        replacement.Pos = original.Pos;
        replacement.End = original.End;
        replacement.Flags = original.Flags;
        OnUpdate?.Invoke(replacement, original);
        return replacement;
    }
}
