using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Storage;

public readonly record struct NodeId(int Value)
{
    public bool IsNull => Value == 0;
}

public readonly record struct NodeHeader(SyntaxKind Kind, int Pos, int End, uint Flags = 0);
public interface INodePayload<TSelf> where TSelf : struct, INodePayload<TSelf>
{
    static abstract int Tag { get; }
    int ChildSlots { get; }

    NodeId ChildAt(int slot);
}

public readonly record struct NodeListData(NodeId[] Nodes, bool TrailingComma = false) : INodePayload<NodeListData>
{
    public static int Tag => 0;
    public int ChildSlots => Nodes.Length;

    public NodeId ChildAt(int slot) => Nodes[slot];
}

public interface INodeStore
{
    int Count { get; }

    NodeId Add<T>(NodeHeader header, T payload) where T : struct, INodePayload<T>;

    T Get<T>(NodeId id) where T : struct, INodePayload<T>;

    NodeHeader Header(NodeId id);

    int Tag(NodeId id);
}

// Both stores use the same generated payload types and algorithms. The class
// form specializes PayloadNode<T> without boxing; the arena form specializes
// managed chunks. Closed payload tags are generated, not runtime reflection.
public sealed class ClassNodeStore : INodeStore
{
    private abstract class StoredNode(NodeHeader header, int tag)
    {
        public readonly NodeHeader Header = header;
        public readonly int Tag = tag;
    }

    private sealed class PayloadNode<T>(NodeHeader header, T payload) : StoredNode(header, T.Tag) where T : struct, INodePayload<T>
    {
        public readonly T Payload = payload;
    }

    private readonly List<StoredNode> nodes = [];
    public int Count => nodes.Count;

    public NodeId Add<T>(NodeHeader header, T payload) where T : struct, INodePayload<T>
    {
        nodes.Add(new PayloadNode<T>(header, payload));
        return new(nodes.Count);
    }

    public T Get<T>(NodeId id) where T : struct, INodePayload<T> =>
            nodes[id.Value - 1] is PayloadNode<T> node ? node.Payload : throw new InvalidDataException("Payload type mismatch");

    public NodeHeader Header(NodeId id) => nodes[id.Value - 1].Header;

    public int Tag(NodeId id) => nodes[id.Value - 1].Tag;
}

public sealed class ArenaNodeStore : INodeStore
{
    private readonly record struct Entry(NodeHeader Header, int Tag, int PayloadIndex);

    private readonly Arena<Entry> entries = new();
    private readonly object?[] payloads = new object[SliceSchema.PayloadCount];
    public int Count => entries.Count;

    public NodeId Add<T>(NodeHeader header, T payload) where T : struct, INodePayload<T>
    {
        var arena = (Arena<T>)(payloads[T.Tag] ??= new Arena<T>());
        int index = arena.Add(payload).Index;
        entries.Add(new(header, T.Tag, index));
        return new(entries.Count);
    }

    public T Get<T>(NodeId id) where T : struct, INodePayload<T>
    {
        Entry entry = entries[new(entries, id.Value - 1)];
        if (entry.Tag != T.Tag)
            throw new InvalidDataException("Payload type mismatch");
        var arena = (Arena<T>)payloads[T.Tag]!;
        return arena[new(arena, entry.PayloadIndex)];
    }

    public NodeHeader Header(NodeId id) => entries[new(entries, id.Value - 1)].Header;

    public int Tag(NodeId id) => entries[new(entries, id.Value - 1)].Tag;
}
