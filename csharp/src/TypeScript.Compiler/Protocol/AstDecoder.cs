using System.Buffers.Binary;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Protocol;

public sealed record DecodedAst(SyntaxNode Root, AstNodeIndexTable Index, AstEncodingOptions Options);

/// <summary>Reconstructs typed syntax and source metadata from a version 9 UTF-8 AST packet.</summary>
public static class AstDecoder
{
    public static DecodedAst Decode(ReadOnlySpan<byte> bytes, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        return new Decoder(bytes, cancellation).Decode(cancellation);
    }

    // Client factories encode editable source files without a content hash or concrete root positions.
    internal static DecodedAst DecodeForPrinting(ReadOnlySpan<byte> bytes, CancellationToken cancellation) =>
        new Decoder(bytes, cancellation).Decode(cancellation, factoryNodes: true);

    private sealed class Decoder
    {
        private const uint None = uint.MaxValue;
        private readonly AstPacket packet;
        private readonly byte[] bytes;
        private readonly int stringOffsets, stringData, extendedData, structuredData;
        private readonly SyntaxNode?[] nodes;
        private readonly NodeList?[] lists;
        private readonly Dictionary<SyntaxNode, JSDocNode[]> documentation = new(ReferenceEqualityComparer.Instance);
        private AstEncodingOptions options = new();
        private bool factoryNodes;

        internal Decoder(ReadOnlySpan<byte> data, CancellationToken cancellation)
        {
            packet = new(data, cancellation); bytes = packet.Reencode();
            stringOffsets = Header(24); stringData = Header(28); extendedData = Header(32); structuredData = Header(36);
            if (stringOffsets != AstPacket.HeaderSize || (stringData - stringOffsets) % 8 != 0 || (structuredData - extendedData) % 4 != 0)
                throw new InvalidDataException("Invalid AST string or extended-data section size");
            nodes = new SyntaxNode?[packet.NodeCount]; lists = new NodeList?[packet.NodeCount];
            for (uint index = 0; index < (stringData - stringOffsets) / 4; index += 2) { cancellation.ThrowIfCancellationRequested(); _ = String(index); }
        }

        internal DecodedAst Decode(CancellationToken cancellation, bool factoryNodes = false)
        {
            this.factoryNodes = factoryNodes;
            for (int index = packet.NodeCount - 1; index > 0; index--)
            {
                cancellation.ThrowIfCancellationRequested();
                var record = packet.GetNode(index);
                var children = new AstChildReader(packet, nodes, lists, index);
                if ((uint)record.Kind == None)
                {
                    var items = children.AllNodes();
                    if (record.Data != items.Length || record.Flags > 1) throw new InvalidDataException("Invalid AST list metadata");
                    lists[index] = new(items, record.Pos, record.End, trailingComma: record.Flags == 1);
                    continue;
                }
                var node = AstWireSchema.Create(record.Kind);
                if (node is SourceFileNode && index != 1) throw new InvalidDataException("A source file must be the AST root");
                uint dataType = record.Data & 0xC0000000, payload = record.Data & 0xFFFFFF;
                if (dataType != AstWireSchema.DataType(node)) throw new InvalidDataException("Incorrect AST node data type");
                AstWireSchema.ReadCommon(node, record.Data);
                if (AstWireSchema.CommonData(node) != (record.Data & 0x3F000000)) throw new InvalidDataException("Invalid AST common flags");
                node.Pos = record.Pos; node.End = record.End; node.Flags = (NodeFlags)record.Flags;
                if (dataType == 1u << 30) AstWireSchema.SetText(node, String(payload));
                else if (dataType == 2u << 30) ReadExtended(node, payload);
                AstWireSchema.ReadChildren(node, payload, ref children);
                if (node is SourceFileNode sf && sf.EndOfFileToken?.Kind != K.EndOfFile) throw new InvalidDataException("Invalid source-file EOF token");
                if (dataType == 0 && AstWireSchema.ChildrenMask(node) != payload) throw new InvalidDataException("Invalid AST child mask");
                var docs = children.Documentation();
                if (docs.Length != 0 && (node.Flags & NodeFlags.HasJSDoc) == 0) throw new InvalidDataException("Unexpected AST documentation");
                if ((node.Flags & NodeFlags.HasJSDoc) != 0) documentation.Add(node, docs);
                nodes[index] = node;
            }
            for (int index = 2; index < nodes.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (nodes[index] is not { } node) continue;
                int parent = (int)packet.GetNode(index).Parent;
                if (lists[parent] is not null) parent = (int)packet.GetNode(parent).Parent;
                node.Parent = nodes[parent] ?? throw new InvalidDataException("Invalid AST parent");
            }
            var root = nodes[1] ?? throw new InvalidDataException("An AST root cannot be a list");
            if (root is SourceFileNode source)
            {
                source.SetDocumentation(documentation);
                source.NodeCount = nodes.Count(node => node is not null);
                ReadSourceReferences(source, packet.GetNode(1).Data & 0xFFFFFF, cancellation);
                if (!factoryNodes && !packet.HasContentHash(source.Source.Bytes.Span)) throw new InvalidDataException("AST content hash does not match its source");
            }
            else if (BinaryPrimitives.ReadUInt128LittleEndian(bytes.AsSpan(4)) != 0 || Header(20) != 0)
                throw new InvalidDataException("Only source-file packets can carry a source hash and parse options");
            return new(root, new(nodes), options with { DecodedDocumentation = documentation });
        }

        private int Header(int offset) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
        private Utf8String String(uint index)
        {
            if ((index & 1) != 0 || index >= (stringData - stringOffsets) / 4) throw new InvalidDataException("Invalid AST string index");
            int offset = stringOffsets + (int)index * 4;
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)), end = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            if (end < start || end > extendedData - stringData) throw new InvalidDataException("Invalid AST string range");
            return new(bytes.AsMemory(stringData + (int)start, (int)(end - start)));
        }
        private uint[] Extended(uint offset, int count)
        {
            if ((offset & 3) != 0 || (ulong)offset + (uint)count * 4 > (uint)(structuredData - extendedData))
                throw new InvalidDataException("Invalid AST extended-data range");
            uint[] result = new uint[count];
            for (int i = 0; i < count; i++) result[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(extendedData + (int)offset + i * 4));
            return result;
        }
        private MessagePackReader Structured(uint offset)
        {
            if (offset >= packet.NodesOffset - structuredData) throw new InvalidDataException("Invalid AST structured-data offset");
            return new(bytes.AsMemory(structuredData + (int)offset, packet.NodesOffset - structuredData - (int)offset));
        }
        private void ReadExtended(SyntaxNode node, uint offset)
        {
            var values = Extended(offset, node is SourceFileNode ? 19 : node is TemplateHeadNode or TemplateMiddleNode or TemplateTailNode ? 3 : 2);
            if (node is SourceFileNode source)
            {
                source.Source = new(String(values[0])); source.FileName = String(values[1]);
                if (values[4] > (uint)ScriptKind.Deferred) throw new InvalidDataException("Invalid AST script kind");
                source.ScriptKind = (ScriptKind)values[4];
                uint variant = source.ScriptKind is ScriptKind.TSX or ScriptKind.JSX or ScriptKind.JS or ScriptKind.JSON ? 1u : 0;
                if (values[3] != variant) throw new InvalidDataException("Invalid AST language variant");
                source.IsDeclarationFile = (source.Flags & NodeFlags.Ambient) != 0;
                if ((source.Pos != 0 || source.End != source.Source.Length) && !(factoryNodes && source.Pos == source.End && source.Pos is -1 or 0))
                    throw new InvalidDataException("Invalid AST source range");
                uint flags = (uint)Header(20);
                if ((flags & ~3u) != 0) throw new InvalidDataException("Invalid AST parse options");
                options = new() { Path = String(values[2]), ParseOptions = new(source.FileName, source.ScriptKind,
                    ForceExternalModule: (flags & 2) != 0, JsxExternalModule: (flags & 1) != 0) };
                return;
            }
            AstWireSchema.SetText(node, String(values[0]));
            switch (node)
            {
                case TemplateHeadNode n: n.RawText = String(values[1]); n.TemplateFlags = (TokenFlags)values[2]; break;
                case TemplateMiddleNode n: n.RawText = String(values[1]); n.TemplateFlags = (TokenFlags)values[2]; break;
                case TemplateTailNode n: n.RawText = String(values[1]); n.TemplateFlags = (TokenFlags)values[2]; break;
                case NoSubstitutionTemplateLiteralNode n: n.TemplateFlags = (TokenFlags)values[1]; break;
                case StringLiteralNode n: n.TokenFlags = (TokenFlags)values[1]; break;
                case NumericLiteralNode n: n.TokenFlags = (TokenFlags)values[1]; break;
                case BigIntLiteralNode n: n.TokenFlags = (TokenFlags)values[1]; break;
                case RegularExpressionLiteralNode n: n.TokenFlags = (TokenFlags)values[1]; break;
                default: throw new InvalidDataException("Unsupported AST extended data");
            }
        }
        private void ReadSourceReferences(SourceFileNode source, uint offset, CancellationToken cancellation)
        {
            uint[] values = Extended(offset, 19);
            source.ReferencedFiles = References(values[5]); source.TypeReferenceDirectives = References(values[6]); source.LibReferenceDirectives = References(values[7]);
            source.Imports = NodeReferences(values[8]); source.ModuleAugmentations = NodeReferences(values[9]);
            source.AmbientModuleNames = Strings(values[10]);
            source.ExternalModuleIndicator = values[11] == 0 ? null : Node(values[11]);
            var original = new SourceText(String(values[12]));
            MappedSourceFile? mapping = null;
            if (values[13] != None || values[18] != None || values[16] != None || values[17] != None || original.Text != source.Source.Text)
            {
                var reader = values[13] == None ? default : Structured(values[13]);
                var segments = new MappingSegment[values[13] == None ? 0 : reader.Array()];
                for (int i = 0; i < segments.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int length = reader.Array();
                    if (length is not (5 or 6)) throw new InvalidDataException("Invalid AST mapping tuple");
                    int start = reader.Int(), end = End(start, reader.Int(), source.Source.Length), originalStart = reader.Int(), originalEnd = End(originalStart, reader.Int(), original.Length);
                    var kind = (MappingKind)reader.Int(); var features = length == 6 ? (MappingFeature)reader.UInt() : MappingFeature.All;
                    segments[i] = new(start, end, originalStart, originalEnd, kind, features);
                }
                var map = new SpanMap(segments);
                try { map.Validate(source.Source.Bytes.Span, original.Bytes.Span); }
                catch (MappingException error) { throw new InvalidDataException("Invalid AST content mapping", error); }
                var directives = new List<MappedDiagnosticDirective>();
                if (values[18] != None)
                {
                    reader = Structured(values[18]); int count = reader.Array();
                    for (int i = 0; i < count; i++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (reader.Array() != 6) throw new InvalidDataException("Invalid AST directive tuple");
                        int originalStart = reader.Int(), originalEnd = End(originalStart, reader.Int(), original.Length), start = reader.Int(), end = End(start, reader.Int(), source.Source.Length);
                        int expect = reader.Int();
                        if (expect > 1) throw new InvalidDataException("Invalid AST directive policy");
                        directives.Add(new(originalStart, originalEnd, start, end, expect == 1, default, (DiagnosticCode)reader.Int()));
                    }
                }
                mapping = new(source, original, map, Optional(values[17]), Optional(values[16]), default, directives.AsReadOnly());
            }
            options = options with { Mapping = mapping, IncludeSpanMap = values[13] != None,
                SupplementalSourceFiles = Strings(values[14]), CanonicalSourceFileName = Optional(values[15]) };

            Utf8String Optional(uint index) => index == None ? default : String(index);
            SyntaxNode Node(uint index) => index < nodes.Length && nodes[index] is { } node ? node : throw new InvalidDataException("Invalid AST node reference");
            SyntaxNode[] NodeReferences(uint position)
            {
                if (position == None) return [];
                var reader = Structured(position); var result = new SyntaxNode[reader.Array()];
                for (int i = 0; i < result.Length; i++) { cancellation.ThrowIfCancellationRequested(); result[i] = Node(reader.UInt()); }
                return result;
            }
            Utf8String[] Strings(uint position)
            {
                if (position == None) return [];
                var reader = Structured(position); var result = new Utf8String[reader.Array()];
                for (int i = 0; i < result.Length; i++) { cancellation.ThrowIfCancellationRequested(); result[i] = reader.String(); }
                return result;
            }
            FileReference[] References(uint position)
            {
                if (position == None) return [];
                var reader = Structured(position); var result = new FileReference[reader.Array()];
                for (int i = 0; i < result.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (reader.Array() != 5) throw new InvalidDataException("Invalid AST reference tuple");
                    int start = reader.Int(), end = reader.Int();
                    if (end < start || end > source.Source.Length) throw new InvalidDataException("Invalid AST reference range");
                    var name = reader.String(); var mode = (ReferenceResolutionMode)reader.Int();
                    if (mode is not (ReferenceResolutionMode.Unspecified or ReferenceResolutionMode.Import or ReferenceResolutionMode.Require)) throw new InvalidDataException("Invalid AST reference resolution mode");
                    result[i] = new(name, start, end, mode, reader.Bool());
                }
                return result;
            }
        }
        private static int End(int start, int length, int maximum) => start <= maximum && length <= maximum - start ? start + length : throw new InvalidDataException("Invalid AST mapped range");
    }
}

internal ref struct AstChildReader
{
    private readonly SyntaxNode?[] nodes;
    private readonly NodeList?[] lists;
    private readonly int[] children;
    private readonly int syntaxCount;
    private int position;

    internal AstChildReader(AstPacket packet, SyntaxNode?[] nodes, NodeList?[] lists, int parent)
    {
        this.nodes = nodes; this.lists = lists;
        var indices = new List<int>();
        if (parent + 1 < packet.NodeCount && packet.GetNode(parent + 1).Parent == parent)
            for (int index = parent + 1; index != 0; index = (int)packet.GetNode(index).Next) indices.Add(index);
        children = indices.ToArray(); syntaxCount = children.Length;
        while (syntaxCount > 0 && nodes[children[syntaxCount - 1]] is JSDocNode) syntaxCount--;
    }
    private int Next() => position < syntaxCount ? children[position++] : throw new InvalidDataException("Missing AST child");
    internal T Node<T>() where T : SyntaxNode => nodes[Next()] is T node ? node : throw new InvalidDataException("Incorrect AST child type");
    internal T? Node<T>(uint mask, int bit) where T : SyntaxNode => (mask & (1u << bit)) == 0 ? null : Node<T>();
    internal T? OptionalNode<T>() where T : SyntaxNode => position < syntaxCount ? Node<T>() : null;
    internal NodeList List() => lists[Next()] ?? throw new InvalidDataException("Expected an AST child list");
    internal NodeList? List(uint mask, int bit) => (mask & (1u << bit)) == 0 ? null : List();
    internal SyntaxNode[] Raw(uint mask, int bit) => (mask & (1u << bit)) == 0 ? [] : AllNodes();
    internal SyntaxNode[] AllNodes()
    {
        var result = new SyntaxNode[syntaxCount - position];
        for (int i = 0; i < result.Length; i++) result[i] = Node<SyntaxNode>();
        return result;
    }
    internal SyntaxNode? ExtraNode(uint mask, int bit, int count)
    {
        int remaining = System.Numerics.BitOperations.PopCount((mask >> bit) & ((1u << count) - 1));
        return syntaxCount - position > remaining ? Node<SyntaxNode>() : null;
    }
    internal JSDocNode[] Documentation()
    {
        if (position != syntaxCount) throw new InvalidDataException("Unexpected AST children");
        var result = new JSDocNode[children.Length - syntaxCount];
        for (int i = 0; i < result.Length; i++) result[i] = (JSDocNode)nodes[children[syntaxCount + i]]!;
        return result;
    }
}
