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
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "export namespace N {export class C {\"x-y\"=1;}} function local(){class Hidden{} return Hidden;} export default N;"),
            ["/project/dep.ts"] = Wtf8.Encode("class Private {} export class Public {}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/dep.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var dep = program.GetFile("/project/dep.ts")!.Syntax;
        var property = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Single())!;
        var hidden = checker.Symbols.Declaration(
            source.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Hidden"))!;
        var privateSymbol = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Private"))!;
        var module = checker.Symbols.Declaration(dep)!;
        Check(await checker.GetSymbolDisplayNameAsync(property) == "\"x-y\"");
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]");
        Check(await checker.GetSymbolDisplayNameAsync(module) == "\"/project/dep\"");
        Check(await checker.GetSymbolDisplayNameAsync(module, source) == "\"./dep\"");
        var inaccessible = await checker.GetSymbolAccessibilityAsync(hidden, source, SymbolFlags.Type);
        Check(
            inaccessible.Accessibility == SymbolAccessibility.NotAccessible
                && inaccessible.ErrorSymbolName == "Hidden"
                && inaccessible.ErrorModuleName == "");
        var foreign = await checker.GetSymbolAccessibilityAsync(privateSymbol, source, SymbolFlags.Type);
        Check(
            foreign.Accessibility == SymbolAccessibility.CannotBeNamed
                && foreign.ErrorSymbolName == "Private"
                && foreign.ErrorModuleName == "\"/project/dep\"");
        var exported = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<ExportAssignmentNode>().Single())!;
        Check(await checker.GetSymbolDisplayNameAsync(exported) == "N");
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
        Check(await fresh.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]");
        Check(await checker.GetSymbolDisplayNameAsync(property, source, SymbolFlags.Value) == "N.C[\"x-y\"]");
        try
        {
            await checker.GetSymbolDisplayNameAsync(property, Parser.ParseSourceFile(new("/foreign.ts"), new SourceText("")));
            throw new InvalidOperationException("Foreign node accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var targets = new List<Symbol>();
        var seen = new HashSet<Symbol>();
        foreach (var node in nodes)
            if (SemanticSyntax.Source(node)?.FileName != "/project/globals.d.ts" && QuerySyntax.Declaration(node)
                && checker.Symbols.Declaration(node) is { } symbol && seen.Add(symbol))
                targets.Add(symbol);
        SyntaxNode?[] locations = [null, .. nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true
            && n is SourceFileNode or ModuleDeclarationNode or ClassDeclarationNode or ClassExpressionNode or FunctionDeclarationNode)];
        void Start(int operation, SyntaxNode? location, Symbol target, SymbolFlags meaning)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(location));
            writer.WriteNumberValue(symbolId(target));
            writer.WriteNumberValue((uint)meaning);
        }
        writer.WriteStartArray("symbolDisplayQueries");
        foreach (var location in locations)
            foreach (var target in targets)
                foreach (var meaning in location is null
                    ? new[] { SymbolFlags.All }
                    : new[] { SymbolFlags.Value, SymbolFlags.Type, SymbolFlags.Namespace })
                {
                    Start(0, location, target, meaning);
                    writer.WriteStringValue(await checker.GetSymbolDisplayNameAsync(target, location, meaning));
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
                            writer.WriteStringValue(result.ErrorSymbolName);
                            writer.WriteStringValue(result.ErrorModuleName);
                            writer.WriteNumberValue(nodeId(result.ErrorNode));
                            writer.WriteEndArray();
                        }
                }
        writer.WriteEndArray();
    }
}
