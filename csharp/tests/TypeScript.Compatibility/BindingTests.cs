using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class BindingTests
{
    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var input = document.RootElement;
            var file = Parser.ParseSourceFile(
                new(input.GetProperty("fileName").GetString()!),
                new SourceText(input.GetProperty("text").GetBytesFromBase64()));
            var binding = Binder.Bind(file);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                if (input.TryGetProperty("exportTree", out var export) && export.GetBoolean())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("binding");
                    Write(writer, Encode(binding));
                    writer.WritePropertyName("tree");
                    BindingSyntax.Write(writer, file);
                    writer.WriteString("syntaxFingerprint", SyntaxFingerprint(file));
                    writer.WriteEndObject();
                }
                else
                    Write(writer, Encode(binding));
            }
            Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        }
    }

    internal static string SyntaxFingerprint(SourceFileNode file)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var node in file.DescendantsAndSelf())
            {
                writer.WriteStartArray();
                writer.WriteNumberValue((uint)node.Kind);
                writer.WriteNumberValue((uint)node.Flags);
                writer.WriteNumberValue(node.Pos);
                writer.WriteNumberValue(node.End);
                writer.WriteNumberValue(node.ChildCount);
                AstScalarProperties.Write(writer, node);
                AstScalarProperties.WriteLists(writer, node, p => p);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static object[] Encode(BoundSourceFile binding)
    {
        var file = binding.SourceFile;
        SyntaxNode[] nodes = file.DescendantsAndSelf().ToArray();
        var nodeIds = nodes.Select((n, i) => (n, i: i + 1)).ToDictionary(p => p.n, p => p.i);
        int Node(SyntaxNode? node) => node is null ? 0 : nodeIds.GetValueOrDefault(node);
        var symbols = new List<Symbol>();
        var symbolIds = new Dictionary<Symbol, int>();
        int Symbol(Symbol? symbol)
        {
            if (symbol is null)
                return 0;
            if (!symbolIds.TryGetValue(symbol, out int id))
            {
                symbols.Add(symbol);
                symbolIds.Add(symbol, id = symbols.Count);
            }
            return id;
        }
        var flows = new List<FlowNode>();
        var flowIds = new Dictionary<FlowNode, int>();
        int Flow(FlowNode? flow)
        {
            if (flow is null)
                return 0;
            if (!flowIds.TryGetValue(flow, out int id))
            {
                flows.Add(flow);
                flowIds.Add(flow, id = flows.Count);
            }
            return id;
        }
        string Name(Symbol symbol)
        {
            if (symbol.Name.Span.StartsWith(TypeScript.Compiler.Binding.Symbol.InternalPrefix + "#", StringComparison.Ordinal)
                && symbol.Parent is { Declarations.Length: > 0 } parent)
                return "__#" + Node(parent.Declarations[0]) + symbol.Name[symbol.Name.Span.IndexOf('@')..].ToString();
            if (symbol.Name.Span.StartsWith(TypeScript.Compiler.Binding.Symbol.InternalPrefix + "\"", StringComparison.Ordinal)
                && symbol.Name.Span.Contains(
                    "pattern@",
                    StringComparison.Ordinal) && symbol.Declarations.FirstOrDefault() is ModuleDeclarationNode { Attributes: { } attributes })
                return TypeScript.Compiler.Binding.Symbol.EscapeName(symbol.Name[..(symbol.Name.Span.LastIndexOf('@') + 1)]).ToString() + Node(attributes);
            return TypeScript.Compiler.Binding.Symbol.EscapeName(symbol.Name).ToString();
        }
        object[] Table(IReadOnlyDictionary<TextSlice, Symbol>? table) => table?.Values.OrderBy(Name, StringComparer.Ordinal)
            .Select(s => (object)new object[] { Name(s), Symbol(s) }).ToArray() ?? [];
        var nr = new List<object>();
        foreach (var node in nodes)
        {
            var data = binding.Get(node);
            nr.Add(new object[] { (int)node.Kind, node.Pos, node.End, Symbol(data?.Symbol), Symbol(data?.LocalSymbol), Table(data?.Locals),
                Flow(data?.Flow), Flow(data?.EndFlow), Flow(data?.ReturnFlow), (uint)((data?.Flags ?? node.Flags)
                    & (NodeFlags.ExportContext | NodeFlags.ContainsThis | NodeFlags.ReachabilityAndEmitFlags | NodeFlags.Unreachable)) });
        }
        var globals = Table(binding.GlobalExports);
        var sr = new List<object>();
        for (int i = 0; i < symbols.Count; i++)
        {
            var symbol = symbols[i];
            sr.Add(
                new object[] { (uint)symbol.Flags, Name(symbol), Symbol(symbol.Parent), Symbol(symbol.ExportSymbol), Node(symbol.ValueDeclaration),
                symbol.Declarations.Select(Node).ToArray(), Table(symbol.Members), Table(symbol.Exports) });
        }
        var fr = new List<object>();
        for (int i = 0; i < flows.Count; i++)
        {
            var flow = flows[i];
            // Discover labels before a single antecedent, matching the reference serializer's order.
            var antecedents = flow.Antecedents.Select(Flow).ToArray();
            int reduced = Flow(flow.ReducedTarget);
            var reducedAntecedents = flow.ReducedAntecedents.Select(Flow).ToArray();
            fr.Add(
                new object[]
                {
                    (uint)flow.Flags,
                    Node(flow.Node),
                    Flow(flow.Antecedent),
                    antecedents,
                    flow.ClauseStart,
                    flow.ClauseEnd,
                    reduced,
                    reducedAntecedents
                });
        }
        return
            [
                nr,
                sr,
                fr,
                binding.Diagnostics.Select(DiagnosticValue).ToArray(),
                globals,
                Node(binding.CommonJSModuleIndicator)
            ];
    }

    private static object[] DiagnosticValue(Diagnostic diagnostic) => [diagnostic.Code, diagnostic.Start, diagnostic.Length,
        diagnostic.Arguments.Select(a => Convert.ToBase64String(Wtf8.Encode(a))).ToArray(),
        diagnostic.RelatedInformation.Select(DiagnosticValue).ToArray()];

    internal static void Write(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case int n:
                writer.WriteNumberValue(n);
                break;
            case uint n:
                writer.WriteNumberValue(n);
                break;
            case TextSlice slice:
                writer.WriteStringValue(slice.Span);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case System.Collections.IEnumerable list:
                writer.WriteStartArray();
                foreach (object item in list)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unsupported binding probe value");
        }
    }
}
