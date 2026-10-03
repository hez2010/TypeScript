using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Protocol;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class AstCodecTests
{
    internal static async Task<int> SafetyAsync()
    {
        int assertions = 0;
        void Check(bool condition, string reason) { assertions++; if (!condition) throw new InvalidOperationException(reason); }
        var source = await Parser.ParseSourceFileAsync(new("/test.ts"u8), new("/// <reference types=\"node\" />\n/** Description {@link Foo}. */ export const x = `😀${1}`;"u8));
        var encoded = await AstEncoder.EncodeAsync(source);
        var decoded = AstDecoder.Decode(encoded.Bytes);
        var file = (SourceFileNode)decoded.Root;
        Check(file.FileName == source.FileName && file.Source.Text == source.Source.Text, "Decoded source metadata");
        Check(file.TypeReferenceDirectives.SequenceEqual(source.TypeReferenceDirectives), "Decoded file references");
        Check(decoded.Index.Nodes.Select(n => n?.Kind).SequenceEqual(encoded.Index.Nodes.Select(n => n?.Kind)), "Stable wire node indices");
        foreach (var node in decoded.Index.Nodes.OfType<SyntaxNode>())
        {
            foreach (var child in node.DescendantsAndSelf().Skip(1)) Check(child.Parent is not null, "Decoded children retain their parent");
            if ((node.Flags & NodeFlags.HasJSDoc) != 0)
                Check((await file.GetDocumentationAsync(node)).Count == (await source.GetDocumentationAsync(encoded.Index.GetNode(decoded.Index.GetIndex(node))!)).Count, "Decoded documentation cache");
        }
        var packet = new AstPacket(encoded.Bytes);
        int nodesOffset = packet.NodesOffset;
        int extendedOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes.AsSpan(32));
        int structuredOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.Bytes.AsSpan(36));
        void Reject(byte[] data, string reason)
        {
            try { AstDecoder.Decode(data); }
            catch (InvalidDataException) { assertions++; return; }
            throw new InvalidOperationException("Accepted invalid AST: " + reason);
        }
        void Mutate(int offset, uint value, string reason)
        {
            var data = encoded.Bytes.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value); Reject(data, reason);
        }
        Reject([], "empty packet");
        for (int length = 1; length < AstPacket.HeaderSize; length++) Reject(encoded.Bytes[..length], "truncated header");
        Reject(encoded.Bytes[..^1], "truncated node");
        Mutate(0, 8u << 24, "old coordinate protocol");
        Mutate(24, 0, "section overlaps header");
        Mutate(28, uint.MaxValue, "section out of bounds");
        Mutate(AstPacket.HeaderSize, uint.MaxValue, "string start out of bounds");
        Mutate(nodesOffset, 1, "non-null sentinel");
        Mutate(nodesOffset + NodeRecord.Size, uint.MaxValue, "list root");
        Mutate(nodesOffset + 2 * NodeRecord.Size + 16, 0, "multiple roots");
        Mutate(nodesOffset + 2 * NodeRecord.Size + 16, 2, "parent cycle");
        Mutate(nodesOffset + 2 * NodeRecord.Size + 12, 1, "backward sibling");
        Mutate(nodesOffset + 2 * NodeRecord.Size + 12, 0, "disconnected sibling");
        Mutate(nodesOffset + 2 * NodeRecord.Size + 20, uint.MaxValue, "list length");
        Mutate(nodesOffset + 3 * NodeRecord.Size + 20, 0xC0000000, "unsupported node data tag");
        Mutate(nodesOffset + 3 * NodeRecord.Size, 0x7FFFFFFF, "unknown syntax kind");
        Mutate(extendedOffset, 1, "unaligned string index");
        Mutate(extendedOffset + 12, 3, "language variant");
        Mutate(extendedOffset + 16, 255, "script kind");
        Mutate(extendedOffset + 24, uint.MaxValue - 1, "structured offset");
        Mutate(extendedOffset + 44, (uint)packet.NodeCount, "node metadata index");
        Mutate(4, 0, "source hash");
        Mutate(20, 4, "parse options");
        var truncatedArray = encoded.Bytes.ToArray(); truncatedArray[structuredOffset] = 0xDD;
        BinaryPrimitives.WriteUInt32BigEndian(truncatedArray.AsSpan(structuredOffset + 1), uint.MaxValue);
        Reject(truncatedArray, "oversized metadata array");
        var random = new Random(1729);
        for (int i = 0; i < 2000; i++)
        {
            byte[] data = encoded.Bytes.ToArray(); int offset = random.Next(data.Length);
            data[offset] ^= (byte)(1 << random.Next(8));
            try { AstDecoder.Decode(data); }
            catch (InvalidDataException) { }
            assertions++;
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { AstDecoder.Decode(encoded.Bytes, canceled.Token); Check(false, "Decoder cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        try { await AstEncoder.EncodeAsync(source, cancellation: canceled.Token); Check(false, "Encoder cancellation"); }
        catch (OperationCanceledException) { assertions++; }
        SyntaxNode deep = new IdentifierNode { Text = "x"u8 };
        for (int i = 0; i < 30000; i++) deep = new PrefixUnaryExpressionNode { Operator = SyntaxKind.PlusToken, Operand = deep };
        var deepEncoded = await AstEncoder.EncodeAsync(deep);
        var deepDecoded = AstDecoder.Decode(deepEncoded.Bytes);
        Check(deepDecoded.Index.Nodes.Count == 30002, "Deep AST decoding uses an explicit stack");
        Check((await AstEncoder.EncodeAsync(deepDecoded.Root)).Bytes.AsSpan().SequenceEqual(deepEncoded.Bytes), "Deep subtree round trip");
        var retained = MakeWeakIndex();
        for (int i = 0; i < 6 && (retained.Source.IsAlive || retained.Index.IsAlive); i++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        Check(!retained.Source.IsAlive && !retained.Index.IsAlive, "The node-index cache releases source files and tables");
        return assertions;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Source, WeakReference Index) MakeWeakIndex()
    {
        var source = Parser.ParseSourceFile(new("/collect.ts"u8), new("export const x = 1;"u8));
        var table = AstEncoder.GetNodeIndexTableAsync(source).AsTask().GetAwaiter().GetResult();
        return (new(source), new(table));
    }

    internal static async Task DecodeLinesAsync()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            byte[] bytes = input.RootElement.GetProperty("bytes").GetBytesFromBase64();
            var decoded = AstDecoder.Decode(bytes);
            var encoded = await AstEncoder.EncodeAsync(decoded.Root, decoded.Root as SourceFileNode, decoded.Options);
            if (!encoded.Bytes.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Reference packet changed during decoding and encoding");
            Console.WriteLine("{\"roundTrip\":true}");
        }
    }

    internal static async Task LinesAsync()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line); var data = input.RootElement;
            var name = JsonStrings.GetString(data.GetProperty("fileName"));
            var source = SourceText.FromOwnedBytes(data.GetProperty("text").GetBytesFromBase64());
            bool Flag(string flag) => data.TryGetProperty(flag, out var value) && value.GetBoolean();
            var parse = new ParseOptions(name, data.TryGetProperty("scriptKind", out var kind) ? (ScriptKind)kind.GetInt32() : ScriptKind.Unknown,
                ForceExternalModule: Flag("force"), JsxExternalModule: Flag("jsx"));
            var file = await Parser.ParseSourceFileAsync(parse, source);
            if (Flag("bind")) await Binder.BindAsync(file);
            SyntaxNode root = file;
            var early = await AstEncoder.GetNodeIndexTableAsync(file);
            if (data.TryGetProperty("nodeKind", out var selected) && selected.GetInt32() != 0)
                root = early.Nodes.First(node => node?.Kind == (SyntaxKind)selected.GetInt32())!;
            var options = new AstEncodingOptions
            {
                ParseOptions = parse, Path = data.TryGetProperty("path", out var path) ? JsonStrings.GetString(path) : name,
                SupplementalSourceFiles = data.TryGetProperty("supplemental", out var supplemental) ? supplemental.EnumerateArray().Select(JsonStrings.GetString).ToArray() : [],
                CanonicalSourceFileName = data.TryGetProperty("canonical", out var canonical) ? JsonStrings.GetString(canonical) : default
            };
            if (data.TryGetProperty("mapping", out var mapping))
            {
                var segments = mapping.GetProperty("segments").EnumerateArray().Select(s => new MappingSegment(s[0].GetInt32(), s[1].GetInt32(), s[2].GetInt32(), s[3].GetInt32(), (MappingKind)s[4].GetInt32(), (MappingFeature)s[5].GetInt32()));
                var directives = mapping.GetProperty("directives").EnumerateArray().Select(d => new MappedDiagnosticDirective(d[0].GetInt32(), d[1].GetInt32(), d[2].GetInt32(), d[3].GetInt32(), d[4].GetInt32() != 0, default, (DiagnosticCode)d[5].GetInt32())).ToArray();
                options = options with { Mapping = new(file, SourceText.FromOwnedBytes(mapping.GetProperty("original").GetBytesFromBase64()), new(segments),
                    JsonStrings.GetString(mapping.GetProperty("virtualFileName")), JsonStrings.GetString(mapping.GetProperty("mapper")), default, directives), IncludeSpanMap = mapping.GetProperty("hasSpanMap").GetBoolean() };
            }
            var encoded = await AstEncoder.EncodeAsync(root, file, options);
            if (root == file && (!ReferenceEquals(early, encoded.Index) || !encoded.Index.Nodes.SequenceEqual(early.Nodes)))
                throw new InvalidOperationException("Eager node handles changed during encoding");
            var packet = new AstPacket(encoded.Bytes);
            if (root == file && !packet.HasContentHash(source.Bytes.Span)) throw new InvalidOperationException("Incorrect source hash");
            var decoded = AstDecoder.Decode(encoded.Bytes);
            var reencoded = await AstEncoder.EncodeAsync(decoded.Root, decoded.Root as SourceFileNode, decoded.Options);
            if (!reencoded.Bytes.AsSpan().SequenceEqual(encoded.Bytes)) throw new InvalidOperationException("AST round trip changed the packet");
            if (Flag("compact"))
            {
                var compact = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(compact))
                {
                    writer.WriteStartObject(); writer.WriteString("hash", Convert.ToHexStringLower(SHA256.HashData(encoded.Bytes)));
                    writer.WriteNumber("nodes", encoded.Index.Nodes.Count); writer.WriteStartArray("kinds");
                    foreach (int kindValue in encoded.Index.Nodes.OfType<SyntaxNode>().Select(node => (int)node.Kind).Distinct().Order()) writer.WriteNumberValue(kindValue);
                    writer.WriteEndArray(); writer.WriteEndObject();
                }
                Console.WriteLine(System.Text.Encoding.UTF8.GetString(compact.WrittenSpan));
                continue;
            }
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject(); writer.WriteBase64String("bytes", encoded.Bytes); writer.WriteStartArray("kinds");
                foreach (var node in encoded.Index.Nodes) writer.WriteNumberValue((int)(node?.Kind ?? 0));
                writer.WriteEndArray();
                var syntaxNodes = early.Nodes.OfType<SyntaxNode>().Distinct().ToArray();
                var ids = syntaxNodes.Select((node, index) => (node, Id: index + 1)).ToDictionary(pair => pair.node, pair => pair.Id);
                writer.WritePropertyName("tree"); BindingSyntax.Write(writer, file, syntaxNodes, Flag("bind"));
                writer.WriteStartObject("metadata");
                WriteNodes("imports", file.Imports); WriteNodes("augmentations", file.ModuleAugmentations);
                writer.WriteStartArray("ambient"); foreach (var value in file.AmbientModuleNames) JsonStrings.WriteString(writer, value.Span); writer.WriteEndArray();
                writer.WriteStartObject("documentation");
                foreach (var node in syntaxNodes)
                    if ((node.Flags & NodeFlags.HasJSDoc) != 0) WriteNodes(ids[node].ToString(System.Globalization.CultureInfo.InvariantCulture), await file.GetDocumentationAsync(node));
                writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndObject();
                void WriteNodes(string key, IEnumerable<SyntaxNode> nodes)
                {
                    writer.WriteStartArray(key); foreach (var node in nodes) writer.WriteNumberValue(ids[node]); writer.WriteEndArray();
                }
            }
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(output.WrittenSpan));
        }
    }
}
