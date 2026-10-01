using System.Buffers;
using System.Text;
using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class EmissionTests
{
    internal static void PrinterLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var request = input.RootElement;
            bool Flag(string name) => request.TryGetProperty(name, out var value) && value.GetBoolean();
            var fileName = Text(request, "file");
            if (fileName.Length == 0)
                fileName = "/source/input.ts"u8;
            var source = Parser.ParseSourceFile(new(fileName, Flag("json") ? ScriptKind.JSON : Flag("jsx") ? ScriptKind.TSX : ScriptKind.TS), new SourceText(Text(request)));
            var context = new EmitContext();
            if (Flag("eraseTypes"))
            {
                var options = new CompilerOptions();
                if (request.TryGetProperty("options", out var compilerOptions))
                    foreach (var property in compilerOptions.EnumerateObject())
                        options.Set(Utf8String.FromString(property.Name), property.Value);
                if (Flag("removeComments")) options.SetRaw("removeComments"u8, "true"u8);
                Checker? checker = null;
                Func<SourceFileNode, ModuleKind> emitFormat = _ => options.EmitModule;
                if (Flag("elideImports") || Flag("runtimeSyntax") || Flag("metadata") || Flag("legacyDecorators") || Flag("lowerExpressions") || Flag("inlineConstants") || Flag("useStrict") || Flag("transformJsx") || Flag("transformModules"))
                {
                    options.SetRaw("noLib"u8, "true"u8);
                    if (!request.TryGetProperty("options", out var configured) || !configured.TryGetProperty("module", out _))
                        options.SetRaw("module"u8, "99"u8);
                    options.SetRaw("moduleResolution"u8, "100"u8);
                    var files = new Dictionary<Utf8String, byte[]> { [fileName] = Text(request).Span.ToArray() };
                    if (request.TryGetProperty("files", out var extraFiles))
                        foreach (var file in extraFiles.EnumerateObject())
                            files[Utf8String.FromString(file.Name)] = JsonStrings.GetString(file.Value).Span.ToArray();
                    var program = CompilerProgram.CreateAsync(new MemoryFileSystem(files), "/source"u8,
                        new("/source/tsconfig.json"u8, options, [fileName], [], [], [])).AsTask().GetAwaiter().GetResult();
                    source = program.SourceFiles.Single(file => file.Syntax.FileName == fileName).Syntax;
                    checker = program.CreateCheckerAsync().AsTask().GetAwaiter().GetResult();
                    emitFormat = program.EmitModuleFormat;
                }
                if (Flag("metadata") && checker is not null)
                    source = (SourceFileNode)new MetadataTransformer(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                source = (SourceFileNode)new TypeEraser(context, options).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("elideImports") && checker is not null)
                    source = (SourceFileNode)new ImportElision(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("runtimeSyntax") && checker is not null)
                    source = (SourceFileNode)new RuntimeSyntaxTransformer(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("legacyDecorators") && checker is not null)
                    source = (SourceFileNode)new LegacyDecoratorsTransformer(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("transformJsx") && checker is not null)
                    source = (SourceFileNode)new JsxTransformer(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("lowerExpressions"))
                {
                    if (options.EmitTargetYear != int.MaxValue)
                        source = (SourceFileNode)new UsingTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    source = (SourceFileNode)new EsDecoratorsTransformer(context, options).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    source = (SourceFileNode)new ClassFieldsTransformer(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2021)
                        source = (SourceFileNode)new ExpressionLowering(context, ExpressionTransform.LogicalAssignment).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2020)
                    {
                        source = (SourceFileNode)new ExpressionLowering(context, ExpressionTransform.NullishCoalescing).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                        source = (SourceFileNode)new OptionalChainTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    }
                    if (options.EmitTargetYear < 2019)
                        source = (SourceFileNode)new ExpressionLowering(context, ExpressionTransform.OptionalCatch).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2018)
                        source = (SourceFileNode)new ObjectRestSpreadTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2018)
                        source = (SourceFileNode)new ForAwaitTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2018)
                        source = (SourceFileNode)new TaggedTemplateTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2017)
                        source = (SourceFileNode)new AsyncTransformer(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                    if (options.EmitTargetYear < 2016)
                        source = (SourceFileNode)new ExpressionLowering(context, ExpressionTransform.Exponentiation).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                }
                if (Flag("useStrict"))
                    source = (SourceFileNode)new UseStrictTransformer(context, options, file => request.TryGetProperty("moduleFormat", out var format)
                        ? (ModuleKind)format.GetInt32() : emitFormat(file)).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("transformModules") && checker is not null)
                    source = (SourceFileNode)new ModuleTransformer(context, options, checker, file => request.TryGetProperty("moduleFormat", out var moduleFormat)
                        ? (ModuleKind)moduleFormat.GetInt32() : emitFormat(file)).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                if (Flag("inlineConstants") && checker is not null && options.IsolatedModules != true && options.VerbatimModuleSyntax != true)
                    source = (SourceFileNode)new ConstEnumInliner(context, options, checker).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
                foreach (var helper in context.ReadHelpers())
                    context.AddHelper(source, helper);
            }
            if (Flag("synthetic"))
                source = (SourceFileNode)new SyntheticPrintRewriter(new()).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
            if (Flag("generated"))
                source = (SourceFileNode)new GeneratedPrintRewriter(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
            if (Flag("environments"))
                source = (SourceFileNode)new EnvironmentPrintRewriter(context).VisitAsync(source).AsTask().GetAwaiter().GetResult()!;
            if (Flag("externalHelpers"))
                context.AddFlags(source, EmitFlags.ExternalHelpers);
            if (request.TryGetProperty("helpers", out var helpers))
                foreach (var helper in helpers.EnumerateArray())
                {
                    SyntaxNode owner = Text(helper, "owner") == "body"u8
                        ? source.DescendantsAndSelf().OfType<BlockNode>().First() : source;
                    var factoryName = Text(helper, "factory");
                    context.AddHelper(owner, new(Text(helper, "name"), default, Text(helper),
                        helper.TryGetProperty("scoped", out var scoped) && scoped.GetBoolean(),
                        helper.TryGetProperty("priority", out var priority) ? priority.GetInt32() : null,
                        textFactory: factoryName.Length == 0 ? null : unique => Utf8String.Concat("var "u8, unique(factoryName), " = {};"u8)));
                }
            var printer = new SyntaxPrinter(new()
            {
                RemoveComments = Flag("removeComments"), OnlyPrintJSDocStyle = Flag("onlyJSDoc"), NeverAsciiEscape = Flag("neverAscii"),
                InlineSources = Flag("inlineSources"), OmitBraceSourceMapPositions = Flag("omitBraceMaps"),
                PreserveSourceNewlines = Flag("preserveNewlines"), OmitTrailingSemicolon = Flag("omitSemicolon"),
                NoEmitHelpers = Flag("noEmitHelpers"),
                NewLine = Text(request, "newLine") is { Length: > 0 } newLine ? newLine : "\n"u8,
                TargetYear = request.TryGetProperty("target", out var target) ? target.GetInt32() : 2015
            }, context);
            SourceMapGenerator? map = Flag("map") ? new("output.js"u8, default, "/out"u8) : null;
            var result = printer.Print(Flag("firstStatement") ? source.Statements![0] : source, source, map);
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartObject();
                writer.WriteBase64String("textBase64"u8, result.Span);
                if (map is not null)
                {
                    writer.WritePropertyName("map"u8);
                    writer.WriteRawValue(map.ToSourceMap().ToJson().Span);
                }
                writer.WriteEndObject();
            }
            Console.WriteLine(Encoding.UTF8.GetString(output.WrittenSpan));
        }
    }

    private sealed class SyntheticPrintRewriter(EmitContext context) : SyntaxRewriter(context)
    {
        protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            if (node is ParenthesizedExpressionNode expression)
                return await VisitAsync(expression.Expression);
            if (node is ParenthesizedTypeNode type)
                return await VisitAsync(type.Type);
            var result = await VisitEachChildAsync(Context.Clone(node));
            result!.Pos = result.End = -1;
            return result;
        }
        protected override async ValueTask<NodeList?> VisitListAsync(NodeList? nodes)
        {
            var result = await base.VisitListAsync(nodes);
            return result is null ? null : new(result.ToArray(), trailingComma: false);
        }
    }

    private sealed class EnvironmentPrintRewriter(EmitContext context) : SyntaxRewriter(context)
    {
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            var factory = Context.Factory;
            if (node is IdentifierNode { Text: var name } && (name == "$var"u8 || name == "$let"u8))
            {
                var temporary = Context.NewTempVariable();
                if (name == "$var"u8)
                    Context.AddVariableDeclaration(temporary);
                else
                    Context.AddLexicalDeclaration(temporary);
                return ValueTask.FromResult<SyntaxNode?>(temporary);
            }
            if (node is ExpressionStatementNode { Expression: CallExpressionNode { Expression: IdentifierNode { Text: var call } } })
            {
                if (call == "split"u8)
                    return ValueTask.FromResult<SyntaxNode?>(factory.NewSyntaxList([Statement("first"u8), Statement("second"u8)]));
                if (call == "remove"u8)
                    return ValueTask.FromResult<SyntaxNode?>(null);
            }
            return base.VisitNodeAsync(node);

            SyntaxNode Statement(Utf8String text) => factory.NewExpressionStatement(factory.NewCallExpression(factory.NewIdentifier(text), null, null, new([]), NodeFlags.None));
        }
    }

    private sealed class GeneratedPrintRewriter(EmitContext context) : SyntaxRewriter(context)
    {
        private readonly Dictionary<Utf8String, SyntaxNode> prototypes = [];
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            Utf8String text = node is IdentifierNode id ? id.Text : node is PrivateIdentifierNode privateId ? privateId.Text : default;
            if (text.StartsWith("$auto"u8) || text.StartsWith("$loop"u8) || text.StartsWith("$unique"u8) || text.StartsWith("#$private"u8))
            {
                if (!prototypes.TryGetValue(text, out var prototype))
                {
                    prototype = text.StartsWith("$auto"u8) ? Context.NewTempVariable()
                        : text.StartsWith("$loop"u8) ? Context.NewLoopVariable()
                        : text.StartsWith("#$private"u8) ? (SyntaxNode)Context.NewUniquePrivateName("#field"u8)
                        : Context.NewUniqueName("value"u8);
                    prototypes.Add(text, prototype);
                }
                var result = Context.Clone(prototype);
                Context.SetOriginal(result, node, overwrite: true);
                result.Pos = node.Pos;
                result.End = node.End;
                return ValueTask.FromResult<SyntaxNode?>(result);
            }
            return VisitEachChildAsync(node);
        }
    }

    internal static void FoundationLines()
    {
        while (Console.ReadLine() is { } line)
        {
            using var input = JsonDocument.Parse(line);
            var output = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(output))
            {
                var request = input.RootElement;
                if (request.GetProperty("operation").GetString() == "writer")
                    Writer(request, writer);
                else if (request.GetProperty("operation").GetString() == "map")
                    Map(request, writer);
                else if (request.GetProperty("operation").GetString() == "names")
                    Names(request, writer);
                else
                    throw new ArgumentException("Unknown emit foundation operation");
            }
            Console.WriteLine(Encoding.UTF8.GetString(output.WrittenSpan));
        }
    }

    private static Utf8String Text(JsonElement value, string name = "text") => value.TryGetProperty(name + "Base64", out var encoded)
        ? new(Convert.FromBase64String(encoded.GetString()!))
        : value.TryGetProperty(name, out var text) ? JsonStrings.GetString(text) : default;

    private static void Names(JsonElement input, Utf8JsonWriter json)
    {
        var context = new EmitContext();
        var blocked = input.TryGetProperty("blocked", out var block) ? block.EnumerateArray().Select(JsonStrings.GetString).ToHashSet() : [];
        var generator = new NameGenerator(context, (name, _) => !blocked.Contains(name));
        var names = new List<SyntaxNode>();
        var source = Parser.ParseSourceFile(new("/names.ts"u8), new SourceText(Text(input, "source")));
        Binder.Bind(source);
        json.WriteStartArray();
        foreach (var step in input.GetProperty("steps").EnumerateArray())
        {
            var options = new AutoGenerateOptions(step.TryGetProperty("flags", out var flags) ? (GeneratedIdentifierFlags)flags.GetInt32() : 0,
                Text(step, "prefix"), Text(step, "suffix"));
            int Index() => step.GetProperty("index").GetInt32();
            bool reuse = step.TryGetProperty("reuse", out var value) && value.GetBoolean();
            switch (step.GetProperty("kind").GetString())
            {
                case "auto": names.Add(context.NewTempVariable(options)); break;
                case "loop": names.Add(context.NewLoopVariable(options)); break;
                case "unique": names.Add(context.NewUniqueName(Text(step), options)); break;
                case "private": names.Add(context.NewUniquePrivateName(Text(step), options)); break;
                case "node":
                case "privateNode":
                    var node = source.DescendantsAndSelf().First(n => n.Kind.ToString() == step.GetProperty("target").GetString());
                    names.Add(step.GetProperty("kind").GetString() == "privateNode" ? context.NewGeneratedPrivateNameForNode(node, options)
                        : context.NewGeneratedNameForNode(node, options));
                    break;
                case "identifierNode": names.Add(context.NewGeneratedNameForNode(context.Factory.NewIdentifier(Text(step)), options)); break;
                case "clone": names.Add(context.Clone(names[Index()])); break;
                case "push": generator.PushScope(reuse); break;
                case "pop": generator.PopScope(reuse); break;
                case "generate": JsonStrings.WriteString(json, generator.GenerateName(names[Index()])); break;
                case "helper": JsonStrings.WriteString(json, generator.MakeFileLevelOptimisticUniqueName(Text(step))); break;
                default: throw new ArgumentException("Unknown name-generator operation");
            }
        }
        json.WriteEndArray();
    }

    private static void Writer(JsonElement input, Utf8JsonWriter json)
    {
        var writer = new EmitTextWriter(Text(input, "newLine"), input.TryGetProperty("indentSize", out var size) ? size.GetInt32() : 4);
        json.WriteStartArray();
        foreach (var step in input.GetProperty("steps").EnumerateArray())
        {
            switch (step.GetProperty("kind").GetString())
            {
                case "write": writer.Write(Text(step)); break;
                case "raw": writer.RawWrite(Text(step)); break;
                case "comment": writer.WriteComment(Text(step)); break;
                case "line": writer.WriteLine(step.TryGetProperty("force", out var force) && force.GetBoolean()); break;
                case "indent": writer.IncreaseIndent(); break;
                case "dedent": writer.DecreaseIndent(); break;
                case "clear": writer.Clear(); break;
                default: throw new ArgumentException("Unknown writer step");
            }
            json.WriteStartObject();
            json.WriteBase64String("textBase64"u8, writer.Text.Span);
            json.WriteNumber("line"u8, writer.Line);
            json.WriteNumber("column"u8, writer.Column);
            json.WriteNumber("position"u8, writer.Position);
            json.WriteNumber("indent"u8, writer.Indentation);
            json.WriteBoolean("lineStart"u8, writer.AtLineStart);
            json.WriteBoolean("trailingComment"u8, writer.HasTrailingComment);
            json.WriteBoolean("trailingWhitespace"u8, writer.HasTrailingWhitespace);
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void Map(JsonElement input, Utf8JsonWriter json)
    {
        var map = new SourceMapGenerator(Text(input, "file"), Text(input, "sourceRoot"), Text(input, "directory"),
            !input.TryGetProperty("caseSensitive", out var sensitive) || sensitive.GetBoolean(), Text(input, "currentDirectory"));
        var ids = new List<int>();
        foreach (var step in input.GetProperty("steps").EnumerateArray())
        {
            int Number(string name) => step.GetProperty(name).GetInt32();
            switch (step.GetProperty("kind").GetString())
            {
                case "source": ids.Add(map.AddSource(Text(step))); break;
                case "name": ids.Add(map.AddName(Text(step))); break;
                case "content": map.SetSourceContent(Number("source"), Text(step)); break;
                case "generated": map.AddGeneratedMapping(Number("line"), Number("column")); break;
                case "mapping": map.AddSourceMapping(Number("line"), Number("column"), Number("source"), Number("sourceLine"), Number("sourceColumn")); break;
                case "named": map.AddSourceMapping(Number("line"), Number("column"), Number("source"), Number("sourceLine"), Number("sourceColumn"), Number("name")); break;
                default: throw new ArgumentException("Unknown source map step");
            }
        }
        json.WriteStartObject();
        json.WriteStartArray("ids"u8);
        foreach (int id in ids)
            json.WriteNumberValue(id);
        json.WriteEndArray();
        json.WritePropertyName("map"u8);
        json.WriteRawValue(map.ToSourceMap().ToJson().Span);
        json.WriteEndObject();
    }

    internal static async Task<int> FoundationSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Emit foundation assertion {checks + 1}");
            checks++;
        }

        var writer = new EmitTextWriter();
        writer.Write("😀x"u8);
        Check(writer.Position == 5 && writer.Column == 3 && writer.Line == 0);
        writer.Write("\r"u8);
        writer.RawWrite("\n"u8);
        Check(writer.Line == 1 && writer.Column == 0 && writer.AtLineStart);
        writer.Write("z\u2028a"u8);
        Check(writer.Line == 2 && writer.Column == 1);
        writer.IncreaseIndent();
        writer.WriteLine();
        Check(writer.Line == 3 && writer.Column == 4);
        writer.Write("b"u8);
        Check(writer.Column == 5 && !writer.HasTrailingWhitespace);
        writer.WriteComment("/* c */"u8);
        Check(writer.HasTrailingComment);
        writer.Clear();
        Check(writer.Position == 0 && writer.Line == 0 && writer.Column == 0 && writer.AtLineStart && !writer.HasTrailingComment);

        var map = new SourceMapGenerator("out.js"u8, default, "/out"u8);
        Check(map.AddSource("/src/main.ts"u8) == 0 && map.AddSource("/src/main.ts"u8) == 0);
        Check(map.AddName("x"u8) == 0 && map.AddName("x"u8) == 0);
        map.SetSourceContent(0, "const x = '😀';"u8);
        map.AddSourceMapping(0, 0, 0, 0, 0, 0);
        map.AddSourceMapping(0, 5, 0, 0, 4);
        map.AddSourceMapping(1, 0, 0, 1, 0);
        map.AddGeneratedMapping(1, 2);
        map.AddSourceMapping(1, 2, 0, 2, 0);
        var result = map.ToSourceMap();
        Check(result.Mappings == "AAAAA,KAAI;AACJ,E"u8);
        Check(result.Sources[0] == "../src/main.ts"u8 && result.SourcesContent![0] == "const x = '😀';"u8);
        Check(map.ToSourceMap().ToJson() == result.ToJson());
        Check(Convert.FromBase64String(result.ToDataUrl()["data:application/json;base64,"u8.Length..].ToString()).AsSpan().SequenceEqual(result.ToJson().Span));
        try { map.AddSourceMapping(0, 0, 0, 0, 0); throw new InvalidOperationException("Accepted a backward generated line"); }
        catch (ArgumentOutOfRangeException) { checks++; }

        var context = new EmitContext();
        var source = Parser.ParseSourceFile(new("/input.ts"u8), new SourceText("const x = [x,]; function f(x: number) { return x; }"u8));
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var identity = new Identity(context);
        Check(await identity.VisitAsync(source) == source);
        var changed = (SourceFileNode)(await new Rename(context).VisitAsync(source))!;
        Check(changed != source && context.MostOriginal(changed) == source);
        Check(changed.DescendantsAndSelf().OfType<IdentifierNode>().All(n => n.Text != "x"u8));
        Check(source.DescendantsAndSelf().OfType<IdentifierNode>().Any(n => n.Text == "x"u8));
        Check(snapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(changed.DescendantsAndSelf().OfType<ArrayLiteralExpressionNode>().Single().Elements!.HasTrailingComma);
        var identifier = source.DescendantsAndSelf().OfType<IdentifierNode>().First();
        context.SetFlags(identifier, EmitFlags.NoSourceMap);
        context.SetTokenSourceMapRange(identifier, SyntaxKind.Identifier, new(1, 2));
        var clone = context.Clone(identifier);
        context.SetFlags(clone, EmitFlags.NoComments);
        context.SetTokenSourceMapRange(clone, SyntaxKind.Identifier, new(3, 4));
        Check(context.GetFlags(identifier) == EmitFlags.NoSourceMap && context.GetFlags(clone) == EmitFlags.NoComments);
        Check(context.GetTokenSourceMapRange(identifier, SyntaxKind.Identifier) == new EmitRange(1, 2));
        Check(context.GetTokenSourceMapRange(identifier, SyntaxKind.StringLiteral) is null);
        try { context.SetOriginal(identifier, clone); throw new InvalidOperationException("Accepted an original-node cycle"); }
        catch (ArgumentException) { checks++; }
        Check(context.Original(identifier) is null);
        var dependency = new EmitHelper("dependency"u8, "__dep"u8, "var __dep = 1;"u8);
        var helper = new EmitHelper("helper"u8, "__helper"u8, "var __helper = __dep;"u8, dependencies: [dependency]);
        context.RequestHelper(helper);
        context.RequestHelper(dependency);
        Check(context.ReadHelpers().SequenceEqual([dependency, helper]) && context.ReadHelpers().Count == 0);

        SyntaxNode deep = new IdentifierNode { Text = "x"u8 };
        for (int i = 0; i < 20_000; i++)
            deep = new ParenthesizedExpressionNode { Expression = deep };
        var transformed = (await new Rename(new()).VisitAsync(deep))!;
        Check(transformed.DescendantsAndSelf().Count() == 20_001);
        Check(transformed.DescendantsAndSelf().OfType<IdentifierNode>().Single().Text == "renamed"u8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try { await new Rename(context, stop.Token).VisitAsync(source); throw new InvalidOperationException("Missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(await identity.VisitAsync(source) == source);
        var printer = new SyntaxPrinter(context: context);
        var printed = printer.Print(transformed);
        Check(printed.Length == 40_007 && printed[20_000..20_007] == "renamed"u8);
        try { printer.Print(source, source, cancellation: stop.Token); throw new InvalidOperationException("Printer missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        writer.Write("discard"u8);
        printer.Write(source, source, writer);
        Check(!writer.Text.StartsWith("discard"u8) && writer.Text == printer.Print(source, source));
        Check(snapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        var eraseContext = new EmitContext();
        var erased = (SourceFileNode)(await new TypeEraser(eraseContext, new()).VisitAsync(source))!;
        Check(erased != source && eraseContext.MostOriginal(erased) == source);
        Check(erased.DescendantsAndSelf().OfType<ParameterDeclarationNode>().Single().Type is null
            && source.DescendantsAndSelf().OfType<ParameterDeclarationNode>().Single().Type is not null);
        Check(snapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        SyntaxNode assertions = new IdentifierNode { Text = "x"u8 };
        for (int i = 0; i < 20_000; i++)
            assertions = new AsExpressionNode { Expression = assertions, Type = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "T"u8 } } };
        var erasedAssertions = (await new TypeEraser(eraseContext, new()).VisitAsync(assertions))!;
        Check(erasedAssertions.DescendantsAndSelf().Count() == 20_001 && new SyntaxPrinter(context: eraseContext).Print(erasedAssertions) == "x"u8);
        try { await new TypeEraser(eraseContext, new(), stop.Token).VisitAsync(source); throw new InvalidOperationException("Type eraser missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        var runtimeOptions = new CompilerOptions();
        foreach (var option in new[] { "noLib", "experimentalDecorators", "emitDecoratorMetadata" })
            runtimeOptions.SetRaw(Utf8String.FromString(option), "true"u8);
        var runtimeSource = "namespace N { export class A {} @dec export class B { constructor(public a: A) {} @dec [key()]() { return B; } } export let {x = 1, ...rest} = source; } const enum E { A = 42 } const value = E.A;"u8;
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/input.ts"u8] = runtimeSource.ToArray() }), "/"u8,
            new("/tsconfig.json"u8, runtimeOptions, ["/input.ts"u8], [], [], []));
        var parsedRuntime = program.SourceFiles.Single().Syntax;
        var originalRuntime = new SyntaxPrinter().Print(parsedRuntime, parsedRuntime);
        var runtimeSnapshot = parsedRuntime.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var runtimeContext = new EmitContext();
        var runtimeChecker = await program.CreateCheckerAsync();
        var runtimeTree = await new MetadataTransformer(runtimeContext, runtimeOptions, runtimeChecker).VisitAsync(parsedRuntime);
        runtimeTree = await new TypeEraser(runtimeContext, runtimeOptions).VisitAsync(runtimeTree);
        runtimeTree = await new RuntimeSyntaxTransformer(runtimeContext, runtimeOptions, runtimeChecker).VisitAsync(runtimeTree);
        runtimeTree = await new LegacyDecoratorsTransformer(runtimeContext, runtimeOptions, runtimeChecker).VisitAsync(runtimeTree);
        runtimeTree = await new ConstEnumInliner(runtimeContext, runtimeOptions, runtimeChecker).VisitAsync(runtimeTree);
        Check(runtimeTree is not null && runtimeContext.EnvironmentDepth == (0, 0));
        Check(runtimeSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(parsedRuntime, parsedRuntime) == originalRuntime);
        Check(runtimeTree!.DescendantsAndSelf().OfType<ConstructorDeclarationNode>().Single().Parameters![0].ModifierList?.Count is null or 0);
        Check(!runtimeTree.DescendantsAndSelf().Any(n => n is DecoratorNode));
        var decoratedText = new SyntaxPrinter(context: runtimeContext).Print(runtimeTree, parsedRuntime);
        Check(decoratedText.Contains("return B_1;"u8) && decoratedText.Contains("B_1 = __decorate"u8));
        Check(decoratedText.Contains("42 /* E.A */"u8));
        try { await new LegacyDecoratorsTransformer(runtimeContext, runtimeOptions, runtimeChecker, stop.Token).VisitAsync(parsedRuntime); throw new InvalidOperationException("Decorator transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }

        var expressionSource = Parser.ParseSourceFile(new("/expressions.ts"u8),
            new SourceText("function f({a, ...rest}, x = obj.m?.()) { try { obj[key()] ??= 2; obj.x **= x; return obj?.x ?? 1; } catch { return tag`\\x`; } }"u8));
        var expressionSnapshot = expressionSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var expressionOriginal = new SyntaxPrinter().Print(expressionSource, expressionSource);
        var expressionContext = new EmitContext();
        SyntaxNode? expressionTree = expressionSource;
        foreach (var pass in new[] { ExpressionTransform.LogicalAssignment, ExpressionTransform.NullishCoalescing, ExpressionTransform.OptionalCatch, ExpressionTransform.Exponentiation })
            expressionTree = await new ExpressionLowering(expressionContext, pass).VisitAsync(expressionTree);
        expressionTree = await new OptionalChainTransformer(expressionContext).VisitAsync(expressionTree);
        expressionTree = await new ObjectRestSpreadTransformer(expressionContext).VisitAsync(expressionTree);
        expressionTree = await new TaggedTemplateTransformer(expressionContext).VisitAsync(expressionTree);
        Check(expressionContext.EnvironmentDepth == (0, 0));
        Check(expressionSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(expressionSource, expressionSource) == expressionOriginal);
        var expressionText = new SyntaxPrinter(context: expressionContext).Print(expressionTree!, expressionSource);
        Check(expressionText.Contains("Math.pow"u8) && expressionText.Contains("__makeTemplateObject"u8) && expressionText.Contains(".call(obj)"u8));
        Check(expressionText.Contains("__rest"u8) && !expressionText.Contains("...rest"u8));

        var asyncSource = Parser.ParseSourceFile(new("/async.ts"u8, ScriptKind.TS), new SourceText(
            "async function f(x) { var x; try { throw 0; } catch (x) { var x = await 1; } return [x, {arguments}]; } class C extends B { async m(k) { super[k] = await 1; return () => super.x; } }"u8));
        var asyncSnapshot = asyncSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var asyncOriginal = new SyntaxPrinter().Print(asyncSource, asyncSource);
        var asyncContext = new EmitContext();
        var asyncTree = (await new AsyncTransformer(asyncContext).VisitAsync(asyncSource))!;
        Check(asyncContext.EnvironmentDepth == (0, 0));
        Check(asyncSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(asyncSource, asyncSource) == asyncOriginal);
        var asyncText = new SyntaxPrinter(context: asyncContext).Print(asyncTree, asyncSource);
        Check(!asyncTree.DescendantsAndSelf().Any(n => n.Kind is SyntaxKind.AsyncKeyword or SyntaxKind.AwaitExpression));
        Check(asyncText.Contains("arguments: arguments_1"u8) && !asyncText.Contains("var x;"u8) && asyncText.Contains("_superIndex(k).value"u8));
        try { await new AsyncTransformer(asyncContext, stop.Token).VisitAsync(asyncSource); throw new InvalidOperationException("Async transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(asyncContext.EnvironmentDepth == (0, 0));

        SyntaxNode deepAwait = asyncContext.Factory.NewIdentifier("x"u8);
        for (int i = 0; i < 20_000; i++) deepAwait = asyncContext.Factory.NewAwaitExpression(deepAwait);
        var asyncFunction = asyncContext.Factory.NewFunctionDeclaration(new([asyncContext.Factory.NewToken(SyntaxKind.AsyncKeyword)]), null,
            asyncContext.Factory.NewIdentifier("f"u8), null, new([]), null, null,
            asyncContext.Factory.NewBlock(new([asyncContext.Factory.NewReturnStatement(deepAwait)]), true));
        var deepAsyncTree = (await new AsyncTransformer(asyncContext).VisitAsync(asyncFunction))!;
        Check(asyncContext.EnvironmentDepth == (0, 0) && deepAsyncTree.DescendantsAndSelf().OfType<YieldExpressionNode>().Count() == 20_000);
        Check(new SyntaxPrinter(context: asyncContext).Print(deepAsyncTree).Length > 120_000);

        var forAwaitSource = Parser.ParseSourceFile(new("/forawait.ts"u8, ScriptKind.TS), new SourceText(
            "async function* f(x) { var x; outer: inner: for await (const y of x) { yield y; continue outer; } } class C extends B { async *m(x = super.m()) { yield x; } }"u8));
        var forAwaitSnapshot = forAwaitSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var forAwaitOriginal = new SyntaxPrinter().Print(forAwaitSource, forAwaitSource);
        var forAwaitContext = new EmitContext();
        var forAwaitTransformer = new ForAwaitTransformer(forAwaitContext);
        var forAwaitTree = (await forAwaitTransformer.VisitAsync(forAwaitSource))!;
        Check(forAwaitContext.EnvironmentDepth == (0, 0));
        Check(forAwaitSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(forAwaitSource, forAwaitSource) == forAwaitOriginal);
        Check(!forAwaitTree.DescendantsAndSelf().Any(n => n.Kind is SyntaxKind.AsyncKeyword or SyntaxKind.AwaitExpression));
        var forAwaitText = new SyntaxPrinter(context: forAwaitContext).Print(forAwaitTree, forAwaitSource);
        Check(forAwaitText.Contains("outer: inner: for"u8) && forAwaitText.Contains("function* f_1(x)"u8) && forAwaitText.Contains("_super.m.call(this)"u8));
        try { await new ForAwaitTransformer(forAwaitContext, stop.Token).VisitAsync(forAwaitSource); throw new InvalidOperationException("For-await transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(forAwaitContext.EnvironmentDepth == (0, 0));
        var generatorFunction = forAwaitContext.Clone(asyncFunction);
        generatorFunction.AsteriskToken = forAwaitContext.Factory.NewToken(SyntaxKind.AsteriskToken);
        var deepGeneratorTree = (await forAwaitTransformer.VisitAsync(generatorFunction))!;
        Check(forAwaitContext.EnvironmentDepth == (0, 0) && deepGeneratorTree.DescendantsAndSelf().OfType<YieldExpressionNode>().Count() == 20_001);
        Check(new SyntaxPrinter(context: forAwaitContext).Print(deepGeneratorTree).Length > 300_000);
        Check(forAwaitContext.ReadHelpers().Any(h => h.Name == EmitHelpers.AsyncGenerator.Name));

        var usingSource = Parser.ParseSourceFile(new("/using.ts"u8, ScriptKind.TS), new SourceText(
            "using resource = acquire(); export const value = 42; export class C {} function f() { using named = class {}; return named; } async function g() { for (await using x of xs) { use(x); } }"u8));
        var usingSnapshot = usingSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var usingOriginal = new SyntaxPrinter().Print(usingSource, usingSource);
        var usingContext = new EmitContext();
        var usingTransformer = new UsingTransformer(usingContext);
        var usingTree = (await usingTransformer.VisitAsync(usingSource))!;
        Check(usingContext.EnvironmentDepth == (0, 0));
        Check(usingSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(usingSource, usingSource) == usingOriginal);
        Check(!usingTree.DescendantsAndSelf().OfType<VariableDeclarationListNode>().Any(n => (n.Flags & NodeFlags.Using) != 0));
        var usingText = new SyntaxPrinter(context: usingContext).Print(usingTree, usingSource);
        Check(usingText.Contains("export let value"u8) && usingText.Contains("__setFunctionName(this, \"named\")"u8) && usingText.Contains("await result_1"u8));
        try { await new UsingTransformer(usingContext, stop.Token).VisitAsync(usingSource); throw new InvalidOperationException("Using transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(usingContext.EnvironmentDepth == (0, 0));
        var deepResource = usingContext.Factory.NewIdentifier("resource"u8) as SyntaxNode;
        for (int i = 0; i < 20_000; i++) deepResource = usingContext.Factory.NewParenthesizedExpression(deepResource);
        var deepUsing = usingContext.Factory.NewBlock(new([usingContext.Factory.NewVariableStatement(null,
            usingContext.Factory.NewVariableDeclarationList(new([usingContext.Factory.NewVariableDeclaration(usingContext.Factory.NewIdentifier("x"u8), null, null, deepResource)]), NodeFlags.Using))]), false);
        var deepUsingTree = (await usingTransformer.VisitAsync(deepUsing))!;
        Check(usingContext.EnvironmentDepth == (0, 0) && deepUsingTree.DescendantsAndSelf().OfType<ParenthesizedExpressionNode>().Count() == 20_000);
        Check(new SyntaxPrinter(context: usingContext).Print(deepUsingTree).Length > 40_000);

        var fieldsSource = Parser.ParseSourceFile(new("/fields.ts"u8, ScriptKind.TS), new SourceText(
            "class C extends B { #x = 1; accessor y = 2; #m() { return this.#x; } static value = super.x++; f(o) { [this.#x] = [42]; return o.#m?.(); } }"u8));
        var fieldsSnapshot = fieldsSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var fieldsOriginal = new SyntaxPrinter().Print(fieldsSource, fieldsSource);
        var fieldsContext = new EmitContext();
        var fieldsOptions = new CompilerOptions();
        fieldsOptions.SetString("target"u8, "es2015"u8);
        var fieldsTransformer = new ClassFieldsTransformer(fieldsContext, fieldsOptions);
        var fieldsTree = (await fieldsTransformer.VisitAsync(fieldsSource))!;
        Check(fieldsContext.EnvironmentDepth == (0, 0));
        Check(fieldsSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(fieldsSource, fieldsSource) == fieldsOriginal);
        Check(!fieldsTree.DescendantsAndSelf().Any(n => n is PrivateIdentifierNode or PropertyDeclarationNode));
        var fieldsText = new SyntaxPrinter(context: fieldsContext).Print(fieldsTree, fieldsSource);
        Check(fieldsText.Contains("new WeakMap()"u8) && fieldsText.Contains("new WeakSet()"u8) && fieldsText.Contains("Reflect.set"u8));
        Check(fieldsText.Contains(".value]"u8) && fieldsText.Contains("?.call(o)"u8));
        try { await new ClassFieldsTransformer(fieldsContext, fieldsOptions, cancellation: stop.Token).VisitAsync(fieldsSource); throw new InvalidOperationException("Class fields transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(fieldsContext.EnvironmentDepth == (0, 0));
        SyntaxNode deepField = fieldsContext.Factory.NewIdentifier("value"u8);
        for (int i = 0; i < 20_000; i++) deepField = fieldsContext.Factory.NewParenthesizedExpression(deepField);
        var deepClass = fieldsContext.Clone((ClassDeclarationNode)fieldsSource.Statements![0]);
        deepClass.Members = new([fieldsContext.Factory.NewPropertyDeclaration(null, fieldsContext.Factory.NewPrivateIdentifier("#x"u8), null, null, deepField)]);
        fieldsContext.StartVariableEnvironment();
        var deepFieldsTree = (await fieldsTransformer.VisitAsync(fieldsContext.Factory.NewBlock(new([deepClass]), true)))!;
        Check(fieldsContext.EndVariableEnvironment().Count == 1 && fieldsContext.EnvironmentDepth == (0, 0));
        Check(deepFieldsTree.DescendantsAndSelf().OfType<ParenthesizedExpressionNode>().Count() == 20_000);
        Check(new SyntaxPrinter(context: fieldsContext).Print(deepFieldsTree).Length > 40_000);

        var esDecoratorSource = Parser.ParseSourceFile(new("/es-decorators.ts"u8, ScriptKind.TS), new SourceText(
            "@dec class C extends B { @dec accessor #x = 1; @dec static m() {} static value = super.x += 1; @dec method() { return this.#x; } constructor() { try { super(); } finally { use(this); } } }"u8));
        var esDecoratorSnapshot = esDecoratorSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var esDecoratorOriginal = new SyntaxPrinter().Print(esDecoratorSource, esDecoratorSource);
        var esDecoratorContext = new EmitContext();
        var esDecoratorTransformer = new EsDecoratorsTransformer(esDecoratorContext, fieldsOptions);
        var esDecoratorTree = (await esDecoratorTransformer.VisitAsync(esDecoratorSource))!;
        Check(esDecoratorContext.EnvironmentDepth == (0, 0));
        Check(esDecoratorSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(esDecoratorSource, esDecoratorSource) == esDecoratorOriginal);
        Check(!esDecoratorTree.DescendantsAndSelf().OfType<DecoratorNode>().Any());
        var esDecoratorText = new SyntaxPrinter(context: esDecoratorContext).Print(esDecoratorTree, esDecoratorSource);
        Check(esDecoratorText.Contains("__esDecorate"u8) && esDecoratorText.Contains("__runInitializers"u8) && esDecoratorText.Contains("Reflect.set"u8));
        Check(esDecoratorText.Contains("_private_x_descriptor"u8) && esDecoratorText.Contains("static { _classThis = this; }"u8));
        try { await new EsDecoratorsTransformer(esDecoratorContext, fieldsOptions, stop.Token).VisitAsync(esDecoratorSource); throw new InvalidOperationException("ES decorators transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(esDecoratorContext.EnvironmentDepth == (0, 0));
        SyntaxNode deepDecorator = esDecoratorContext.Factory.NewIdentifier("dec"u8);
        for (int i = 0; i < 20_000; i++) deepDecorator = esDecoratorContext.Factory.NewParenthesizedExpression(deepDecorator);
        var deepDecoratedClass = esDecoratorContext.Clone((ClassDeclarationNode)esDecoratorSource.Statements![0]);
        deepDecoratedClass.Modifiers = new([esDecoratorContext.Factory.NewDecorator(deepDecorator)]);
        deepDecoratedClass.Members = new([]); deepDecoratedClass.HeritageClauses = null;
        var deepDecoratedSource = esDecoratorContext.Clone(esDecoratorSource);
        deepDecoratedSource.Statements = new([deepDecoratedClass]);
        var deepDecoratedTree = (await esDecoratorTransformer.VisitAsync(deepDecoratedSource))!;
        Check(esDecoratorContext.EnvironmentDepth == (0, 0));
        Check(deepDecoratedTree.DescendantsAndSelf().OfType<ParenthesizedExpressionNode>().Count() >= 20_000);
        Check(new SyntaxPrinter(context: esDecoratorContext).Print(deepDecoratedTree).Length > 40_000);

        var jsxOptions = new CompilerOptions();
        jsxOptions.SetRaw("noLib"u8, "true"u8);
        jsxOptions.SetString("jsx"u8, "react-jsxdev"u8);
        var jsxSourceText = "export const value = <View key={42}><div title='&amp;'/> text</View>;"u8;
        var jsxProgram = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]> { ["/jsx.tsx"u8] = jsxSourceText.ToArray() }), "/"u8,
            new("/tsconfig.json"u8, jsxOptions, ["/jsx.tsx"u8], [], [], []));
        var jsxSource = jsxProgram.SourceFiles.Single().Syntax;
        var jsxChecker = await jsxProgram.CreateCheckerAsync();
        var jsxSnapshot = jsxSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var jsxOriginal = new SyntaxPrinter().Print(jsxSource, jsxSource);
        var jsxContext = new EmitContext();
        var jsxTransformer = new JsxTransformer(jsxContext, jsxOptions, jsxChecker);
        var jsxTree = (await jsxTransformer.VisitAsync(jsxSource))!;
        Check(jsxContext.EnvironmentDepth == (0, 0));
        Check(jsxSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(jsxSource, jsxSource) == jsxOriginal);
        Check(!jsxTree.DescendantsAndSelf().Any(n => n is JsxElementNode or JsxSelfClosingElementNode or JsxTextNode));
        var jsxText = new SyntaxPrinter(context: jsxContext).Print(jsxTree, jsxSource);
        Check(jsxText.Contains("react/jsx-dev-runtime"u8) && jsxText.Contains("title: '&'"u8) && jsxText.Contains("lineNumber: 1"u8));
        var jsxImport = jsxTree.DescendantsAndSelf().OfType<ImportSpecifierNode>().Single();
        Check(await jsxChecker.GetReferencedImportForEmitAsync(jsxImport.Name!) == jsxImport);
        Check(await (await jsxProgram.CreateCheckerAsync()).GetReferencedImportForEmitAsync(jsxImport.Name!) is null);
        Check(new SyntaxPrinter(context: jsxContext).Print((await jsxTransformer.VisitAsync(jsxSource))!, jsxSource) == jsxText);
        try { await new JsxTransformer(jsxContext, jsxOptions, jsxChecker, stop.Token).VisitAsync(jsxSource); throw new InvalidOperationException("JSX transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(jsxContext.EnvironmentDepth == (0, 0));
        var jsxTemplate = jsxSource.DescendantsAndSelf().OfType<JsxElementNode>().Single();
        SyntaxNode deepJsx = jsxSource.DescendantsAndSelf().OfType<JsxSelfClosingElementNode>().Single();
        for (int i = 0; i < 20_000; i++)
        {
            var nested = jsxContext.Clone(jsxTemplate);
            nested.Children = new([deepJsx]);
            deepJsx = nested;
        }
        var deepJsxSource = jsxContext.Clone(jsxSource);
        deepJsxSource.Statements = new([jsxContext.Factory.NewExpressionStatement(deepJsx)]);
        var deepJsxTree = (await jsxTransformer.VisitAsync(deepJsxSource))!;
        Check(jsxContext.EnvironmentDepth == (0, 0) && deepJsxTree.DescendantsAndSelf().OfType<CallExpressionNode>().Count() == 20_001);
        Check(new SyntaxPrinter(context: jsxContext).Print(deepJsxTree, jsxSource).Length > 400_000);

        var moduleOptions = new CompilerOptions();
        moduleOptions.SetRaw("noLib"u8, "true"u8);
        moduleOptions.SetString("module"u8, "commonjs"u8);
        var moduleProgram = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/module.ts"u8] = "export let x = 1; export {x as alias}; import {y} from './other'; export const read = () => y + x;"u8.ToArray(),
            ["/other.ts"u8] = "export const y = 42;"u8.ToArray()
        }), "/"u8, new("/tsconfig.json"u8, moduleOptions, ["/module.ts"u8], [], [], []));
        var moduleSource = moduleProgram.GetFile("/module.ts"u8)!.Syntax;
        var moduleChecker = await moduleProgram.CreateCheckerAsync();
        var moduleSnapshot = moduleSource.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        var moduleOriginal = new SyntaxPrinter().Print(moduleSource, moduleSource);
        var moduleContext = new EmitContext();
        var moduleTransformer = new ModuleTransformer(moduleContext, moduleOptions, moduleChecker, moduleProgram.EmitModuleFormat);
        var moduleTree = (await moduleTransformer.VisitAsync(moduleSource))!;
        var moduleText = new SyntaxPrinter(context: moduleContext).Print(moduleTree, moduleSource);
        Check(moduleContext.EnvironmentDepth == (0, 0));
        Check(moduleSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        Check(new SyntaxPrinter().Print(moduleSource, moduleSource) == moduleOriginal);
        Check(!moduleTree.DescendantsAndSelf().Any(n => n is ImportDeclarationNode or ExportDeclarationNode));
        Check(moduleText.Contains("exports.x = 1"u8) && moduleText.Contains("other_1.y + exports.x"u8) && moduleText.Contains("exports.alias = exports.x"u8));
        Check(new SyntaxPrinter(context: moduleContext).Print((await moduleTransformer.VisitAsync(moduleSource))!, moduleSource) == moduleText);
        try { await new ModuleTransformer(moduleContext, moduleOptions, moduleChecker, moduleProgram.EmitModuleFormat, stop.Token).VisitAsync(moduleSource); throw new InvalidOperationException("Module transform missed cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(moduleContext.EnvironmentDepth == (0, 0));
        SyntaxNode deepModule = moduleSource.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "x"u8);
        for (int i = 0; i < 20_000; i++) deepModule = moduleContext.Factory.NewParenthesizedExpression(deepModule);
        var deepModuleSource = moduleContext.Clone(moduleSource);
        deepModuleSource.Statements = new([moduleContext.Factory.NewExpressionStatement(deepModule)]);
        var deepModuleTree = (await moduleTransformer.VisitAsync(deepModuleSource))!;
        Check(moduleContext.EnvironmentDepth == (0, 0) && deepModuleTree.DescendantsAndSelf().OfType<ParenthesizedExpressionNode>().Count() == 20_000);
        Check(new SyntaxPrinter(context: moduleContext).Print(deepModuleTree, moduleSource).Contains("exports.x"u8));
        Check(moduleSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));
        SyntaxNode modulePattern = moduleSource.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "x"u8);
        for (int i = 0; i < 20_000; i++) modulePattern = moduleContext.Factory.NewArrayLiteralExpression(new([modulePattern]), false);
        var patternSource = moduleContext.Clone(moduleSource);
        patternSource.Statements = new([moduleSource.Statements![0], moduleSource.Statements[1],
            moduleContext.Factory.NewExpressionStatement(moduleContext.Binary(modulePattern, SyntaxKind.EqualsToken, moduleContext.Factory.NewIdentifier("items"u8)))]);
        var patternTree = (await moduleTransformer.VisitAsync(patternSource))!;
        Check(moduleContext.EnvironmentDepth == (0, 0) && patternTree.DescendantsAndSelf().OfType<ArrayLiteralExpressionNode>().Count() == 20_000);
        Check(patternTree.DescendantsAndSelf().OfType<SetAccessorDeclarationNode>().Count() == 1);
        Check(new SyntaxPrinter(context: moduleContext).Print(patternTree, moduleSource).Contains("exports.alias = exports.x = value"u8));
        Check(moduleSnapshot.All(p => p.Node.Parent == p.Parent && p.Node.Pos == p.Pos && p.Node.End == p.End && p.Node.Flags == p.Flags));

        var optionalContext = new EmitContext();
        SyntaxNode chain = optionalContext.Factory.NewIdentifier("x"u8);
        for (int i = 0; i < 20_000; i++)
            chain = optionalContext.Factory.NewPropertyAccessExpression(chain, i == 0 ? optionalContext.Factory.NewToken(SyntaxKind.QuestionDotToken) : null,
                optionalContext.Factory.NewIdentifier("x"u8), NodeFlags.OptionalChain);
        optionalContext.StartVariableEnvironment();
        var loweredChain = (await new OptionalChainTransformer(optionalContext).VisitAsync(chain))!;
        Check(optionalContext.EndVariableEnvironment().Count == 0 && loweredChain.DescendantsAndSelf().OfType<PropertyAccessExpressionNode>().Count() == 20_000);
        var chainText = new SyntaxPrinter(context: optionalContext).Print(loweredChain);
        Check(chainText.StartsWith("x === null || x === void 0 ? void 0 : x.x"u8) && chainText.Length > 40_000);

        var unwindContext = new EmitContext();
        using var duringVisit = new CancellationTokenSource();
        unwindContext.StartVariableEnvironment();
        var unwindSource = Parser.ParseSourceFile(new("/unwind.ts"u8), new SourceText("function f(x) { const y = cancel; }"u8));
        try { await new CancelDuringRewrite(unwindContext, duringVisit).VisitAsync(unwindSource); throw new InvalidOperationException("Missed in-flight cancellation"); }
        catch (OperationCanceledException) { checks++; }
        Check(unwindContext.EnvironmentDepth == (1, 1) && unwindContext.EndVariableEnvironment().Count == 0);
        Check(await new Identity(unwindContext).VisitAsync(unwindSource) == unwindSource && unwindContext.EnvironmentDepth == (0, 0));

        var destructuring = new EmitContext();
        SyntaxNode pattern = destructuring.Factory.NewIdentifier("x"u8);
        for (int i = 0; i < 20_000; i++)
            pattern = destructuring.Factory.NewBindingPattern(SyntaxKind.ArrayBindingPattern,
                new([destructuring.Factory.NewBindingElement(null, null, pattern, null)]));
        var variable = destructuring.Factory.NewVariableDeclaration(pattern, null, null, destructuring.Factory.NewIdentifier("source"u8));
        destructuring.StartVariableEnvironment();
        var flattened = await new DestructuringFlattener(destructuring, n => ValueTask.FromResult(n)).AssignmentAsync(variable, false);
        Check(destructuring.EndVariableEnvironment().Count == 0 && flattened.DescendantsAndSelf().OfType<ElementAccessExpressionNode>().Count() == 20_000);
        var flatText = new SyntaxPrinter(context: destructuring).Print(flattened);
        Check(flatText.StartsWith("x = source[0]"u8) && flatText.Length == 60_010);
        var assignmentPattern = await destructuring.BindingAssignmentAsync(variable.Name!);
        Check(assignmentPattern.DescendantsAndSelf().OfType<ArrayLiteralExpressionNode>().Count() == 20_000);
        return checks;
    }

    private sealed class CancelDuringRewrite(EmitContext context, CancellationTokenSource stop) : SyntaxRewriter(context, stop.Token)
    {
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            if (node is IdentifierNode id && id.Text == "cancel"u8)
            {
                Context.AddVariableDeclaration(Context.NewTempVariable());
                stop.Cancel();
                Cancellation.ThrowIfCancellationRequested();
            }
            return base.VisitNodeAsync(node);
        }
    }

    private sealed class Identity(EmitContext context) : SyntaxRewriter(context) { }

    private sealed class Rename(EmitContext context, CancellationToken cancellation = default) : SyntaxRewriter(context, cancellation)
    {
        protected override ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
        {
            if (node is IdentifierNode { Text: var text } identifier && text == "x"u8)
            {
                var result = Context.Clone(identifier);
                result.Text = "renamed"u8;
                return ValueTask.FromResult<SyntaxNode?>(result);
            }
            return base.VisitNodeAsync(node);
        }
    }
}
