using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Protocol;

public sealed class AstNodeIndexTable
{
    private readonly Dictionary<SyntaxNode, uint> indices = new(ReferenceEqualityComparer.Instance);
    public IReadOnlyList<SyntaxNode?> Nodes { get; }
    internal AstNodeIndexTable(SyntaxNode?[] nodes)
    {
        Nodes = Array.AsReadOnly(nodes);
        for (uint index = 1; index < nodes.Length; index++) if (nodes[index] is { } node) indices.TryAdd(node, index);
    }
    public uint GetIndex(SyntaxNode node) => indices.GetValueOrDefault(node);
    public SyntaxNode? GetNode(uint index) => index < Nodes.Count ? Nodes[(int)index] : null;
}

public sealed record AstEncodingOptions
{
    public ParseOptions ParseOptions { get; init; }
    public Utf8String Path { get; init; }
    public MappedSourceFile? Mapping { get; init; }
    public bool IncludeSpanMap { get; init; } = true;
    public IReadOnlyList<Utf8String> SupplementalSourceFiles { get; init; } = [];
    public Utf8String CanonicalSourceFileName { get; init; }
    internal IReadOnlyDictionary<SyntaxNode, JSDocNode[]>? DecodedDocumentation { get; init; }
}

public sealed record EncodedAst(byte[] Bytes, AstNodeIndexTable Index);
internal readonly record struct AstWireFrame(SyntaxNode? Node, NodeList? List, uint Parent);

/// <summary>Encodes the complete syntax tree using version 9 and UTF-8 byte positions.</summary>
public static class AstEncoder
{
    private const uint None = uint.MaxValue;
    private static readonly ConditionalWeakTable<SourceFileNode, AstNodeIndexTable> sourceIndices = new();

    public static async ValueTask<AstNodeIndexTable> GetNodeIndexTableAsync(SourceFileNode source, CancellationToken cancellation = default)
    {
        if (sourceIndices.TryGetValue(source, out var existing)) return existing;
        var nodes = new List<SyntaxNode?> { null };
        await WalkAsync(source, source, (frame, _) => nodes.Add(frame.Node), cancellation).ConfigureAwait(false);
        return sourceIndices.GetValue(source, _ => new(nodes.ToArray()));
    }

    public static async ValueTask<EncodedAst> EncodeAsync(SyntaxNode root, SourceFileNode? source = null,
        AstEncodingOptions? options = null, CancellationToken cancellation = default)
    {
        source ??= root as SourceFileNode;
        options ??= new();
        var strings = new StringTable(root is SourceFileNode file ? file.Source.Text : default);
        var extended = new ArrayBufferWriter<byte>();
        var structured = new ArrayBufferWriter<byte>();
        var records = new List<NodeRecord> { default };
        var nodes = new List<SyntaxNode?> { null };
        var siblings = new Dictionary<uint, int>();
        await WalkAsync(root, source, (frame, index) =>
        {
            nodes.Add(frame.Node);
            if (siblings.TryGetValue(frame.Parent, out int previous)) records[previous] = records[previous] with { Next = index };
            siblings[frame.Parent] = (int)index;
            records.Add(frame.Node is { } node
                ? new(node.Kind, node.Pos, node.End, 0, frame.Parent, Data(node), (uint)(node.BindingId == 0 ? node.Flags : node.BindingFlags))
                : new((K)None, frame.List!.Pos, frame.List.End, 0, frame.Parent, (uint)frame.List.Count, frame.List.HasTrailingComma ? 1u : 0));
        }, cancellation, options.DecodedDocumentation).ConfigureAwait(false);
        var table = new AstNodeIndexTable(nodes.ToArray());
        if (root is SourceFileNode fullSource) table = sourceIndices.GetValue(fullSource, _ => table);
        var extendedBytes = extended.WrittenSpan.ToArray();
        if (root is SourceFileNode sf)
        {
            Patch(32, NodeIndices(sf.Imports));
            Patch(36, NodeIndices(sf.ModuleAugmentations));
            Patch(40, Strings(sf.AmbientModuleNames));
            Patch(44, sf.ExternalModuleIndicator is { } indicator ? table.GetIndex(indicator) : 0);
        }
        int stringOffsets = AstPacket.HeaderSize;
        int stringData = checked(stringOffsets + strings.Offsets.Count * sizeof(uint));
        int extendedOffset = checked(stringData + strings.Data.WrittenCount);
        int structuredOffset = checked(extendedOffset + extendedBytes.Length);
        int nodesOffset = checked(structuredOffset + structured.WrittenCount);
        byte[] result = new byte[checked(nodesOffset + records.Count * NodeRecord.Size)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)AstPacket.FormatVersion << 24);
        if (root is SourceFileNode rootSource)
        {
            BinaryPrimitives.WriteUInt128LittleEndian(result.AsSpan(4), XxHash128.HashToUInt128(rootSource.Source.Bytes.Span));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), (options.ParseOptions.JsxExternalModule ? 1u : 0) | (options.ParseOptions.ForceExternalModule ? 2u : 0));
        }
        uint[] offsets = [(uint)stringOffsets, (uint)stringData, (uint)extendedOffset, (uint)structuredOffset, (uint)nodesOffset];
        for (int i = 0; i < offsets.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24 + i * 4), offsets[i]);
        for (int i = 0; i < strings.Offsets.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(stringOffsets + i * 4), strings.Offsets[i]);
        strings.Data.WrittenSpan.CopyTo(result.AsSpan(stringData));
        extendedBytes.CopyTo(result.AsSpan(extendedOffset)); structured.WrittenSpan.CopyTo(result.AsSpan(structuredOffset));
        for (int i = 0; i < records.Count; i++) records[i].Write(result.AsSpan(nodesOffset + i * NodeRecord.Size));
        return new(result, table);

        void Patch(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(extendedBytes.AsSpan(offset), value);
        uint NodeIndices(IReadOnlyList<SyntaxNode> references)
        {
            if (references.Count == 0) return None;
            uint offset = (uint)structured.WrittenCount; MessagePackWriter.Array(structured, references.Count);
            foreach (var node in references) MessagePackWriter.UInt(structured, table.GetIndex(node));
            return offset;
        }
        uint Strings(IReadOnlyList<Utf8String> values)
        {
            if (values.Count == 0) return None;
            uint offset = (uint)structured.WrittenCount; MessagePackWriter.Array(structured, values.Count);
            foreach (var value in values) MessagePackWriter.String(structured, value);
            return offset;
        }
        uint References(IReadOnlyList<FileReference> references)
        {
            if (references.Count == 0) return None;
            uint offset = (uint)structured.WrittenCount; MessagePackWriter.Array(structured, references.Count);
            foreach (var reference in references)
            {
                MessagePackWriter.Array(structured, 5); MessagePackWriter.UInt(structured, checked((uint)reference.Pos));
                MessagePackWriter.UInt(structured, checked((uint)reference.End)); MessagePackWriter.String(structured, reference.FileName);
                MessagePackWriter.UInt(structured, (uint)reference.ResolutionMode); MessagePackWriter.Bool(structured, reference.Preserve);
            }
            return offset;
        }
        uint Data(SyntaxNode node)
        {
            uint type = AstWireSchema.DataType(node), common = AstWireSchema.CommonData(node);
            if (type == 0) return common | AstWireSchema.ChildrenMask(node);
            if (type == 1u << 30) return type | common | Limited(strings.Add(AstWireSchema.Text(node), node));
            uint offset = Limited((uint)extended.WrittenCount);
            if (node is SourceFileNode sf)
            {
                uint text = strings.Add(sf.Source.Text, sf), original = text;
                var mapping = options.Mapping;
                if (mapping is not null && mapping.Original.Text != sf.Source.Text) original = strings.Add(mapping.Original.Text);
                uint fileName = strings.Add(sf.FileName), path = strings.Add(options.Path.IsEmpty ? sf.FileName : options.Path);
                uint references = References(sf.ReferencedFiles), types = References(sf.TypeReferenceDirectives), libs = References(sf.LibReferenceDirectives);
                uint map = None;
                if (mapping is not null && options.IncludeSpanMap)
                {
                    map = (uint)structured.WrittenCount; MessagePackWriter.Array(structured, mapping.Map.Segments.Count);
                    foreach (var segment in mapping.Map.Segments)
                    {
                        MessagePackWriter.Array(structured, segment.Features == MappingFeature.All ? 5 : 6);
                        foreach (int value in new[] { segment.VirtualStart, segment.VirtualEnd - segment.VirtualStart, segment.OriginalStart,
                            segment.OriginalEnd - segment.OriginalStart, (int)segment.Kind }) MessagePackWriter.UInt(structured, checked((uint)value));
                        if (segment.Features != MappingFeature.All) MessagePackWriter.UInt(structured, (uint)segment.Features);
                    }
                }
                uint supplemental = Strings(options.SupplementalSourceFiles);
                uint canonical = options.CanonicalSourceFileName.IsEmpty ? None : strings.Add(options.CanonicalSourceFileName);
                uint mapper = mapping is not null && !mapping.MapperIdentity.IsEmpty ? strings.Add(mapping.MapperIdentity) : None;
                uint virtualFile = mapping is not null && !mapping.VirtualFileName.IsEmpty ? strings.Add(mapping.VirtualFileName) : None;
                uint directives = None;
                if (mapping is { Directives.Count: > 0 })
                {
                    directives = (uint)structured.WrittenCount; MessagePackWriter.Array(structured, mapping.Directives.Count);
                    foreach (var directive in mapping.Directives)
                    {
                        MessagePackWriter.Array(structured, 6);
                        foreach (int value in new[] { directive.OriginalStart, directive.OriginalEnd - directive.OriginalStart, directive.VirtualStart,
                            directive.VirtualEnd - directive.VirtualStart, directive.Expect ? 1 : 0, (int)directive.UnusedCode })
                            MessagePackWriter.UInt(structured, checked((uint)value));
                    }
                }
                Write(extended, text, fileName, path, sf.ScriptKind is ScriptKind.TSX or ScriptKind.JSX or ScriptKind.JS or ScriptKind.JSON ? 1u : 0, (uint)sf.ScriptKind,
                    references, types, libs, None, None, None, 0, original, map, supplemental, canonical, mapper, virtualFile, directives);
            }
            else
            {
                uint text = strings.Add(AstWireSchema.Text(node), node);
                switch (node)
                {
                    case TemplateHeadNode n: Write(extended, text, strings.Add(n.RawText, node), (uint)n.TemplateFlags); break;
                    case TemplateMiddleNode n: Write(extended, text, strings.Add(n.RawText, node), (uint)n.TemplateFlags); break;
                    case TemplateTailNode n: Write(extended, text, strings.Add(n.RawText, node), (uint)n.TemplateFlags); break;
                    case NoSubstitutionTemplateLiteralNode n: Write(extended, text, (uint)n.TemplateFlags); break;
                    case StringLiteralNode n: Write(extended, text, (uint)n.TokenFlags); break;
                    case NumericLiteralNode n: Write(extended, text, (uint)n.TokenFlags); break;
                    case BigIntLiteralNode n: Write(extended, text, (uint)n.TokenFlags); break;
                    case RegularExpressionLiteralNode n: Write(extended, text, (uint)n.TokenFlags); break;
                    default: throw new InvalidDataException($"Unsupported AST extended data: {node.Kind}");
                }
            }
            return type | common | offset;
        }
    }

    private static uint Limited(uint value) => value <= 0xFFFFFF ? value : throw new InvalidDataException("AST data exceeds the 24-bit wire limit");
    private static void Write(IBufferWriter<byte> writer, params ReadOnlySpan<uint> values)
    {
        foreach (uint value in values) { BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(4), value); writer.Advance(4); }
    }
    private static async ValueTask WalkAsync(SyntaxNode root, SourceFileNode? source, Action<AstWireFrame, uint> visit, CancellationToken cancellation,
        IReadOnlyDictionary<SyntaxNode, JSDocNode[]>? decodedDocumentation = null)
    {
        var stack = new Stack<AstWireFrame>(); stack.Push(new(root, null, 0));
        uint index = 0;
        while (stack.TryPop(out var frame))
        {
            cancellation.ThrowIfCancellationRequested();
            visit(frame, ++index);
            if (frame.Node is { } node)
            {
                if ((node.Flags & NodeFlags.HasJSDoc) != 0)
                {
                    IReadOnlyList<JSDocNode> documentation = decodedDocumentation is not null && decodedDocumentation.TryGetValue(node, out var decoded) ? decoded
                        : source is not null ? await source.GetDocumentationAsync(node, cancellation).ConfigureAwait(false) : [];
                    for (int i = documentation.Count - 1; i >= 0; i--) stack.Push(new(documentation[i], null, index));
                }
                AstWireSchema.PushChildren(node, index, stack);
            }
            else for (int i = frame.List!.Count - 1; i >= 0; i--) stack.Push(new(frame.List[i], null, index));
        }
    }

    private sealed class StringTable
    {
        private readonly Utf8String source;
        internal readonly List<uint> Offsets = [];
        internal readonly ArrayBufferWriter<byte> Data = new();
        internal StringTable(Utf8String source) { this.source = source; Data.Write(source.Span); }
        internal uint Add(Utf8String text, SyntaxNode? node = null)
        {
            uint index = checked((uint)Offsets.Count);
            if (node is SourceFileNode) { Offsets.Add(checked((uint)node.Pos)); Offsets.Add(checked((uint)node.End)); return index; }
            if (node is { } n && n.End > n.Pos && n.End <= source.Length)
            {
                int end = n.End - (n.Kind is K.StringLiteral or K.TemplateTail or K.NoSubstitutionTemplateLiteral ? 1 : 0);
                int start = end - text.Length;
                if (start >= 0 && source.Span[start..end].SequenceEqual(text.Span))
                { Offsets.Add((uint)start); Offsets.Add((uint)end); return index; }
            }
            Offsets.Add(checked((uint)Data.WrittenCount)); Data.Write(text.Span); Offsets.Add(checked((uint)Data.WrittenCount)); return index;
        }
    }
}
