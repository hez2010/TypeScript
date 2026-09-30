using System.Text.Json;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerSymbolDisplayTests
{
    internal static async Task<int> TypeArgumentsSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol type arguments assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "type Bound={value:number};class C<T extends Bound>{field!:T}declare const concrete:C<{value:number}>;concrete.field;")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var property = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Single())!;
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        const SymbolFormatFlags flags = SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteTypeParametersOrArguments;
        var before = (checker.DeclarationVisibilityCount, checker.VisibilityFileCount,
            checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount);
        using var stop = new CancellationTokenSource();
        bool visited = false;
        checker.BeforeVisibilityNode = _ =>
        {
            visited = true;
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
        };
        try
        {
            await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value, flags, stop.Token);
            throw new InvalidOperationException("Cancellation while formatting a constraint was not observed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            checker.BeforeVisibilityNode = null;
        }
        Check(visited);
        Check(before == (checker.DeclarationVisibilityCount, checker.VisibilityFileCount,
            checker.AccessibleChainCacheCount, checker.SymbolTableAliasCacheCount, checker.SymbolContainerCacheCount));
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value, flags) == "C<T extends Bound>.field"u8);
        var access = source.DescendantsAndSelf().OfType<PropertyAccessExpressionNode>().Single();
        var instantiated = await checker.GetSymbolAtLocationAsync(access);
        Check(instantiated is not null && (instantiated.CheckFlags & CheckFlags.Instantiated) != 0);
        Check(await checker.GetSymbolDisplayNameAsync(instantiated!, source, SymbolFlags.Value, flags) == "C<{ value: number; }>.field"u8);
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "C.field"u8);
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value,
            flags | SymbolFormatFlags.DoNotIncludeSymbolChain) == "field"u8);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> FormatSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol format assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode("namespace N {export class C{}} import A=N.C; export default class Named{}"),
            ["/project/other.ts"u8] = Wtf8.Encode("export const other=1;")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/other.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var other = program.GetFile("/project/other.ts"u8)!.Syntax;
        var symbol = checker.Symbols.Declaration(
            source.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "C"u8))!;
        var export = checker.Symbols.Declaration(source)!.Exports["default"u8];
        Check(await checker.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.None) == "A"u8);
        Check(
            await checker.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.UseOnlyExternalAliasing) == "N.C"u8);
        Check(await checker.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.DoNotIncludeSymbolChain) == "C"u8);
        Check(await checker.GetSymbolDisplayNameAsync(export, other, SymbolFlags.Type, SymbolFormatFlags.None) == "default"u8);
        Check(
            await checker.GetSymbolDisplayNameAsync(
                export,
                other,
                SymbolFlags.Type,
                SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) == "Named"u8);
        Check(await checker.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.None) == "A"u8);
        var fresh = await program.CreateCheckerAsync();
        var before = (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount);
        using var stop = new CancellationTokenSource();
        fresh.BeforeSymbolChainTable = _ => stop.Cancel();
        try
        {
            await fresh.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.UseOnlyExternalAliasing, stop.Token);
            throw new InvalidOperationException("Canceled format completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(before == (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount));
        fresh.BeforeSymbolChainTable = null;
        Check(await fresh.GetSymbolDisplayNameAsync(symbol, source, SymbolFlags.Type, SymbolFormatFlags.UseOnlyExternalAliasing) == "N.C"u8);
        return checks;
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol display assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "export namespace N {export class C {\"x-y\"=1;}} function local(){class Hidden{} return Hidden;} export default N;"),
            ["/project/dep.ts"u8] = Wtf8.Encode("class Private {} export class Public {}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/dep.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var dep = program.GetFile("/project/dep.ts"u8)!.Syntax;
        var property = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Single())!;
        var hidden = checker.Symbols.Declaration(
            source.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Hidden"u8))!;
        var privateSymbol = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Private"u8))!;
        var module = checker.Symbols.Declaration(dep)!;
        Check(await checker.GetSymbolDisplayNameAsync(property) == "\"x-y\""u8);
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]"u8);
        Check(await checker.GetSymbolDisplayNameAsync(module) == "\"/project/dep\""u8);
        Check(await checker.GetSymbolDisplayNameAsync(module, source) == "\"./dep\""u8);
        var inaccessible = await checker.GetSymbolAccessibilityAsync(hidden, source, SymbolFlags.Type);
        Check(
            inaccessible.Accessibility == SymbolAccessibility.NotAccessible
                && inaccessible.ErrorSymbolName == "Hidden"u8
                && inaccessible.ErrorModuleName == ""u8);
        var foreign = await checker.GetSymbolAccessibilityAsync(privateSymbol, source, SymbolFlags.Type);
        Check(
            foreign.Accessibility == SymbolAccessibility.CannotBeNamed
                && foreign.ErrorSymbolName == "Private"u8
                && foreign.ErrorModuleName == "\"/project/dep\""u8);
        var exported = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<ExportAssignmentNode>().Single())!;
        Check(await checker.GetSymbolDisplayNameAsync(exported) == "N"u8);
        Check((await checker.GetSymbolAccessibilityAsync(null, null, SymbolFlags.All)).Accessibility == SymbolAccessibility.Accessible);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        foreach (Func<Task> query in new Func<Task>[] {
            async () =>
            {
                await checker.GetSymbolDisplayNameAsync(property, source, cancellation: stop.Token);
            },
            async () =>
            {
                await checker.GetSymbolAccessibilityAsync(hidden, source, SymbolFlags.Type, cancellation: stop.Token);
            } })
        {
            try
            {
                await query();
                throw new InvalidOperationException("Canceled display completed");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        var fresh = await program.CreateCheckerAsync();
        var before = (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount);
        fresh.BeforeSymbolContainer = _ => throw new IOException("display failure");
        try
        {
            await fresh.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value);
            throw new InvalidOperationException("Display failure ignored");
        }
        catch (IOException error) when (error.Message == "display failure")
        {
            checks++;
        }
        Check(before == (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount));
        fresh.BeforeSymbolContainer = null;
        using var during = new CancellationTokenSource();
        fresh.BeforeSymbolChainTable = _ => during.Cancel();
        try
        {
            await fresh.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value, during.Token);
            throw new InvalidOperationException("Mid-display cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(before == (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount));
        fresh.BeforeSymbolChainTable = null;
        Check(await fresh.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]"u8);
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]"u8);
        try
        {
            await checker.GetSymbolDisplayNameAsync(property, Parser.ParseSourceFile(new("/foreign.ts"u8), new SourceText(""u8)));
            throw new InvalidOperationException("Foreign node accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId, bool formats = false, IReadOnlyList<int>? formatFlags = null,
        bool typeNodes = false)
    {
        var targets = new List<Symbol>();
        var seen = new HashSet<Symbol>();
        foreach (var node in nodes)
        {
            if (SemanticSyntax.Source(node)?.FileName != "/project/globals.d.ts"u8 && QuerySyntax.Declaration(node)
                && checker.Symbols.Declaration(node) is { } symbol && seen.Add(symbol))
                targets.Add(symbol);
            if (formats && formatFlags?.Any(f => (f & 5) == 5) == true
                && node is PropertyAccessExpressionNode or ElementAccessExpressionNode
                && await checker.GetSymbolAtLocationAsync(node) is { } member && seen.Add(member))
                targets.Add(member);
        }
        SyntaxNode?[] locations = [null, .. nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true
            && (n is SourceFileNode or ModuleDeclarationNode or ClassDeclarationNode or ClassExpressionNode or FunctionDeclarationNode
                || typeNodes && n is ImportDeclarationNode or ExportDeclarationNode or ImportTypeNode))];
        void Start(int operation, SyntaxNode? location, Symbol target, SymbolFlags meaning)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(location));
            writer.WriteNumberValue(symbolId(target));
            writer.WriteNumberValue((uint)meaning);
        }
        writer.WriteStartArray(typeNodes ? Utf8String.Copy("symbolTypeNodeQueries"u8) : formats ? Utf8String.Copy("symbolFormatQueries"u8) : Utf8String.Copy("symbolDisplayQueries"u8));
        foreach (var location in locations)
            foreach (var target in targets)
                foreach (var meaning in location is null && !typeNodes
                    ? new[] { SymbolFlags.All }
                    : new[] { SymbolFlags.Value, SymbolFlags.Type, SymbolFlags.Namespace })
                {
                    if (typeNodes)
                    {
                        var factory = new NodeFactory();
                        for (int mode = 0; mode < 8; mode++)
                            for (int arguments = 0; arguments < 3; arguments++)
                            {
                                SyntaxNode[]? args = arguments switch
                                {
                                    1 => [factory.NewKeywordTypeNode(SyntaxKind.NumberKeyword)],
                                    2 =>
                                        [
                                            factory.NewArrayTypeNode(factory.NewKeywordTypeNode(SyntaxKind.NumberKeyword)),
                                            factory.NewLiteralTypeNode(factory.NewStringLiteral("é"u8, TokenFlags.None))
                                        ],
                                    _ => null
                                };
                                Utf8String result = await checker.GetSymbolTypeReferenceAsync(
                                    target,
                                    location,
                                    meaning,
                                    args,
                                    (mode & 1) != 0,
                                    (mode & 2) != 0,
                                    (mode & 4) != 0);
                                writer.WriteStartArray();
                                writer.WriteNumberValue(nodeId(location));
                                writer.WriteNumberValue(symbolId(target));
                                writer.WriteNumberValue((uint)meaning);
                                writer.WriteNumberValue(mode);
                                writer.WriteNumberValue(arguments);
                                writer.WriteStringValue(result.Span);
                                writer.WriteEndArray();
                            }
                        continue;
                    }
                    if (formats)
                    {
                        foreach (var flags in formatFlags ?? [0, 1, 2, 6, 8, 10, 12, 14, 16, 17, 32, 34, 36, 38, 40, 42, 44, 46, 64])
                        {
                            writer.WriteStartArray();
                            writer.WriteNumberValue(nodeId(location));
                            writer.WriteNumberValue(symbolId(target));
                            writer.WriteNumberValue((uint)meaning);
                            writer.WriteNumberValue(flags);
                            writer.WriteStringValue(
                                (await checker.GetSymbolDisplayNameAsync(target, location, meaning, (SymbolFormatFlags)flags)).Span);
                            writer.WriteEndArray();
                        }
                        continue;
                    }
                    Start(0, location, target, meaning);
                    writer.WriteStringValue((await checker.GetSymbolDisplayNameAsync(target, location, meaning)).Span);
                    writer.WriteEndArray();
                    if (location is null)
                        continue;
                    foreach (bool aliases in new[] { false, true })
                        foreach (bool modules in new[] { false, true })
                        {
                            var result = await checker.GetSymbolAccessibilityAsync(target, location, meaning, aliases, modules);
                            Start(1, location, target, meaning);
                            writer.WriteBooleanValue(aliases);
                            writer.WriteBooleanValue(modules);
                            writer.WriteNumberValue((int)result.Accessibility);
                            writer.WriteStartArray();
                            foreach (int id in (result.AliasesToMakeVisible ?? []).Select(nodeId).Order())
                                writer.WriteNumberValue(id);
                            writer.WriteEndArray();
                            writer.WriteStringValue(result.ErrorSymbolName.Span);
                            writer.WriteStringValue(result.ErrorModuleName.Span);
                            writer.WriteNumberValue(nodeId(result.ErrorNode));
                            writer.WriteEndArray();
                        }
                }
        writer.WriteEndArray();
    }
}
