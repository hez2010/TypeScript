using System.Buffers;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class DeclarationEmissionTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException($"Declaration emit assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("declaration"u8, "true"u8);
        options.SetString("target"u8, "esnext"u8);
        options.SetString("module"u8, "esnext"u8);
        options.SetString("moduleResolution"u8, "bundler"u8);
        var files = new Dictionary<Utf8String, byte[]>
        {
            ["/input.ts"u8] = "import {Shape} from './other'; declare const shape: Shape; export function read() {return shape;} function host(x: number) {return x;} host.item = shape; export const alias = host; export class C {#value = 1; constructor(public x: number) {} private work(x: string) {return x;}} export type Deep = number;"u8.ToArray(),
            ["/other.ts"u8] = "export interface Shape {value: number}"u8.ToArray(),
            ["/second.ts"u8] = "export const answer = 42;"u8.ToArray()
        };
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/"u8,
            new("/tsconfig.json"u8, options, ["/input.ts"u8, "/second.ts"u8], [], [], []));
        var source = program.GetFile("/input.ts"u8)!.Syntax;
        var second = program.GetFile("/second.ts"u8)!.Syntax;
        var checker = await program.CreateCheckerAsync();
        var snapshot = program.SourceFiles.SelectMany(file => file.Syntax.DescendantsAndSelf()).Select(node => (Node: node, node.Parent, node.Pos, node.End, node.Flags, node.BindingId)).ToArray();
        bool Unchanged() => snapshot.All(item => item.Node.Parent == item.Parent && item.Node.Pos == item.Pos && item.Node.End == item.End && item.Node.Flags == item.Flags && item.Node.BindingId == item.BindingId);
        var context = new EmitContext();
        var transformer = new DeclarationTransformer(context, checker, options);
        var tree = (SourceFileNode)(await transformer.VisitAsync(source))!;
        var printer = new SyntaxPrinter(new() { OnlyPrintJSDocStyle = true }, context);
        var text = printer.Print(tree, tree);
        Check(text.Contains("import { Shape }"u8) && text.Contains("namespace host"u8) && text.Contains("shape as item"u8));
        Check(tree.IsDeclarationFile && !source.IsDeclarationFile && transformer.Diagnostics.Count == 0);
        Check(Unchanged() && checker.TypeSyntaxScopeCount == 0 && context.EnvironmentDepth == (0, 0));
        var again = (SourceFileNode)(await transformer.VisitAsync(source))!;
        Check(new SyntaxPrinter(new() { OnlyPrintJSDocStyle = true }, context).Print(again, again) == text);
        var other = (SourceFileNode)(await transformer.VisitAsync(second))!;
        Check(new SyntaxPrinter(new() { OnlyPrintJSDocStyle = true }, context).Print(other, other) == "export declare const answer = 42;\n"u8);
        Check(transformer.Diagnostics.Count == 0 && Unchanged() && checker.TypeSyntaxScopeCount == 0);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        try { await new DeclarationTransformer(context, checker, options, stop.Token).VisitAsync(source); throw new InvalidOperationException("Declaration transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(Unchanged() && checker.TypeSyntaxScopeCount == 0 && context.EnvironmentDepth == (0, 0));

        var assignment = source.DescendantsAndSelf().OfType<BinaryExpressionNode>().Single();
        var host = source.Statements!.OfType<FunctionDeclarationNode>().Single(function => function.Name!.Text == "host"u8);
        using var cancelQuery = new CancellationTokenSource();
        var tracker = new DeclarationSymbolTracker(_ => { cancelQuery.Cancel(); cancelQuery.Token.ThrowIfCancellationRequested(); });
        int cacheCount = checker.SerializedTypeSyntaxCount;
        try
        {
            await checker.CreateExpandoTypeForEmitAsync(assignment, source, checker.Symbols.Declaration(host)!, checker.Symbols.Declaration(assignment)!,
                "host"u8, "item"u8, context, NodeBuilderFlags.NoTruncation, tracker, NodeBuilderInternalFlags.AllowUnresolvedNames, cancelQuery.Token);
            throw new InvalidOperationException("Expando type query missed cancellation");
        }
        catch (OperationCanceledException) { checks++; }
        Check(checker.TypeSyntaxScopeCount == 0 && checker.SerializedTypeSyntaxCount == cacheCount && Unchanged());
        var recovered = (SourceFileNode)(await new DeclarationTransformer(new EmitContext(), checker, options).VisitAsync(source))!;
        Check(recovered.IsDeclarationFile && checker.TypeSyntaxScopeCount == 0);

        var consumerFiles = new Dictionary<Utf8String, byte[]>
        {
            ["/input.d.ts"u8] = text.Span.ToArray(), ["/other.d.ts"u8] = files["/other.ts"u8],
            ["/consumer.ts"u8] = "import {read, alias, C} from './input'; const n: number = read().value; const item: number = alias.item.value; const instance: C = new C(1);"u8.ToArray()
        };
        var consumer = await CompilerProgram.CreateAsync(new MemoryFileSystem(consumerFiles), "/"u8,
            new("/tsconfig.json"u8, options, ["/consumer.ts"u8], [], [], []));
        var consumerChecker = await consumer.CreateCheckerAsync();
        await consumerChecker.CheckSourceFileAsync(consumer.GetFile("/consumer.ts"u8)!.Syntax);
        Check(consumer.SourceFiles.All(file => file.Syntax.ParseDiagnostics.Count == 0));
        Check(consumerChecker.DetailedDiagnosticsForProgramFile(consumer.GetFile("/consumer.ts"u8)!.Syntax).Count == 0);

        var deepContext = new EmitContext();
        var alias = source.Statements!.OfType<TypeAliasDeclarationNode>().Single();
        SyntaxNode nested = alias.Type!;
        for (int i = 0; i < 20_000; i++) nested = deepContext.Factory.NewParenthesizedTypeNode(nested);
        var deepAlias = deepContext.Clone(alias); deepAlias.Type = nested;
        var deepSource = deepContext.Clone(source); deepSource.Statements = new([deepAlias]);
        var deepTree = (SourceFileNode)(await new DeclarationTransformer(deepContext, checker, options).VisitAsync(deepSource))!;
        Check(deepTree.DescendantsAndSelf().OfType<ParenthesizedTypeNode>().Count() == 20_000);
        Check(new SyntaxPrinter(context: deepContext).Print(deepTree, source).Contains("number"u8));
        Check(Unchanged() && deepContext.EnvironmentDepth == (0, 0) && checker.TypeSyntaxScopeCount == 0);
        return checks;
    }

    internal static void Lines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            try { Console.WriteLine(EmitAsync(input.RootElement).GetAwaiter().GetResult()); }
            catch (Exception error)
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("error"u8, error.Message);
                    writer.WriteString("stack"u8, error.StackTrace);
                    writer.WriteEndObject();
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
        }
    }

    private static async Task<string> EmitAsync(JsonElement request)
    {
        Utf8String fileName = request.TryGetProperty("file", out var file) ? JsonStrings.GetString(file) : "/source/input.ts"u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("declaration"u8, "true"u8);
        options.SetString("module"u8, "esnext"u8);
        options.SetString("moduleResolution"u8, "bundler"u8);
        options.SetString("outDir"u8, "/out"u8);
        if (request.TryGetProperty("options", out var configured))
            foreach (var property in configured.EnumerateObject()) options.Set(Utf8String.FromString(property.Name), property.Value);
        var files = new Dictionary<Utf8String, byte[]> { [fileName] = JsonStrings.GetString(request.GetProperty("text")).Span.ToArray() };
        if (request.TryGetProperty("files", out var extras))
            foreach (var property in extras.EnumerateObject()) files[Utf8String.FromString(property.Name)] = JsonStrings.GetString(property.Value).Span.ToArray();
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/source"u8,
            new("/source/tsconfig.json"u8, options, [fileName], [], [], []));
        var source = program.SourceFiles.Single(candidate => candidate.Syntax.FileName == fileName).Syntax;
        var checker = await program.CreateCheckerAsync();
        var context = new EmitContext();
        var transformer = new DeclarationTransformer(context, checker, options, declarationFilePath: "/out/input.d.ts"u8);
        var transformed = (SourceFileNode)(await transformer.VisitAsync(source))!;
        var printer = new SyntaxPrinter(new()
        {
            OnlyPrintJSDocStyle = true, RemoveComments = options.RemoveComments == true, OmitBraceSourceMapPositions = true,
            NewLine = request.TryGetProperty("newLine", out var newLine) ? JsonStrings.GetString(newLine) : "\n"u8
        }, context);
        var map = request.TryGetProperty("map", out var useMap) && useMap.GetBoolean() ? new SourceMapGenerator("input.d.ts"u8, default, "/out"u8) : null;
        var text = printer.Print(transformed, transformed, map);
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteBase64String("textBase64"u8, text.Span);
            writer.WritePropertyName("diagnostics"u8);
            WriteDiagnostics(writer, transformer.Diagnostics);
            if (map is not null) { writer.WritePropertyName("map"u8); writer.WriteRawValue(map.ToSourceMap().ToJson().Span); }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(output.WrittenSpan);
    }

    internal static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<Diagnostic> diagnostics)
    {
        writer.WriteStartArray();
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("file"u8, (diagnostic.FileName ?? Utf8String.Empty).Span);
            writer.WriteNumber("code"u8, (int)diagnostic.Code);
            writer.WriteNumber("start"u8, diagnostic.Start);
            writer.WriteNumber("length"u8, diagnostic.Length);
            writer.WriteString("message"u8, diagnostic.Format().Span);
            writer.WritePropertyName("related"u8); WriteDiagnostics(writer, diagnostic.RelatedInformation);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
