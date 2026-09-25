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

internal static class CheckerSymbolChainTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol chain assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        const string source = "import * as Long from './dep'; import {named as Z} from './dep'; import {named as A} from './dep'; "
            + "import Root=require('./dep'); function shadow(Long:number,Z:number,A:number){return Root.named;} "
            + "function globalShadow(globalValue:string){return globalThis.globalValue;} "
            + "const cls=class Self<T>{method(value:T){return Self;}};";
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(source),
            ["/project/dep.ts"] = Wtf8.Encode("export const named=1;"),
            ["/project/global.d.ts"] = Wtf8.Encode("declare const globalValue:number;")
        }), "/project", new("/project/tsconfig.json", options, ["/project/global.d.ts", "/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var target = checker.Symbols.Declaration(program.GetFile("/project/dep.ts")!.Syntax)!.Exports["named"];
        var chain = await checker.GetAccessibleSymbolChainAsync(target, file, SymbolFlags.Value);
        Check(chain is [var shortest] && shortest.Name == "Z");
        Check(await checker.GetAccessibleSymbolChainAsync(target, file, SymbolFlags.Value) == chain);
        var external = await checker.GetAccessibleSymbolChainAsync(target, file, SymbolFlags.Value, true);
        Check(external is [var root, var member] && root.Name == "Root" && member == target);
        var shadow = nodes.OfType<FunctionDeclarationNode>().Single(n => n.Name?.Text == "shadow");
        var qualified = await checker.GetAccessibleSymbolChainAsync(target, shadow.Body!, SymbolFlags.Value);
        Check(qualified is [var qualifier, var property] && qualifier.Name == "Root" && property == target);
        var globalShadow = nodes.OfType<FunctionDeclarationNode>().Single(n => n.Name?.Text == "globalShadow");
        var global = checker.Symbols.Globals["globalValue"];
        var globalChain = await checker.GetAccessibleSymbolChainAsync(global, globalShadow.Body!, SymbolFlags.Value);
        Check(
            globalChain is [var globalThis, var globalMember] && globalThis == checker.Symbols.GlobalThisSymbol && globalMember == global);
        var expression = nodes.OfType<ClassExpressionNode>().Single();
        var method = nodes.OfType<MethodDeclarationNode>().Single();
        var self = checker.Symbols.Declaration(expression)!;
        Check(await checker.GetAccessibleSymbolChainAsync(self, method.Body!, SymbolFlags.Type) is [var local] && local == self);
        Check(await checker.GetAccessibleSymbolChainAsync(checker.Symbols.Declaration(method), method.Body!, SymbolFlags.Value) is null);
        Check(await checker.GetAccessibleSymbolChainAsync(null, file, SymbolFlags.Type) is null);
        try
        {
            ((IList<Symbol>)chain!)[0] = target;
            throw new InvalidOperationException("Accessible chain was mutable");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        Check((await checker.GetAccessibleSymbolChainAsync(target, file, SymbolFlags.Value))![0].Name == "Z");
        var retry = await program.CreateCheckerAsync();
        retry.BeforeSymbolChainTable = _ =>
        {
            if (retry.SymbolTableAliasCacheCount != 0)
                throw new InvalidOperationException("chain callback");
        };
        try
        {
            await retry.GetAccessibleSymbolChainAsync(target, shadow.Body!, SymbolFlags.Value);
            throw new InvalidOperationException("Chain callback was not reached");
        }
        catch (InvalidOperationException error) when (error.Message == "chain callback")
        {
            checks++;
        }
        finally
        {
            retry.BeforeSymbolChainTable = null;
        }
        Check(retry.AccessibleChainCacheCount == 0 && retry.SymbolTableAliasCacheCount == 0);
        Check(
            await retry.GetAccessibleSymbolChainAsync(target, shadow.Body!, SymbolFlags.Value) is [var recovered, ..]
                && recovered.Name == "Root");
        var cancelled = await program.CreateCheckerAsync();
        using var stop = new CancellationTokenSource();
        int visits = 0;
        cancelled.BeforeSymbolChainTable = _ =>
        {
            if (++visits == 3)
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            await cancelled.GetAccessibleSymbolChainAsync(target, shadow.Body!, SymbolFlags.Value, cancellation: stop.Token);
            throw new InvalidOperationException("Cancelled chain query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            cancelled.BeforeSymbolChainTable = null;
        }
        Check(cancelled.AccessibleChainCacheCount == 0 && cancelled.SymbolTableAliasCacheCount == 0);
        Check(await cancelled.GetAccessibleSymbolChainAsync(target, file, SymbolFlags.Value) is [var retried] && retried.Name == "Z");
        try
        {
            await checker.GetAccessibleSymbolChainAsync(target, new BlockNode(), SymbolFlags.Value);
            throw new InvalidOperationException("Chain query accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        SyntaxNode deep = file;
        for (int i = 0; i < 20_000; i++)
            deep = new BlockNode { Parent = deep };
        Check(await checker.GetAccessibleSymbolChainAsync(target, deep, SymbolFlags.Value) is [var deepAlias] && deepAlias.Name == "Z");
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var seen = new HashSet<Symbol>();
        var targets = new List<Symbol>();
        foreach (var node in nodes)
            if (SemanticSyntax.Source(node)?.FileName != "/project/globals.d.ts" && QuerySyntax.Declaration(node)
                && checker.Symbols.Declaration(node) is { } symbol && seen.Add(symbol))
                targets.Add(symbol);
        if (checker.Symbols.Globals.TryGetValue("globalValue", out var global))
            targets.Add(global);
        var locations = new List<SyntaxNode?> { null };
        locations.AddRange(
            nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true
            && (n is SourceFileNode or BlockNode or ModuleDeclarationNode or ClassDeclarationNode or ClassExpressionNode
                or InterfaceDeclarationNode
                or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode)));
        writer.WriteStartArray("symbolChainQueries");
        foreach (var location in locations)
            foreach (var target in targets)
                foreach (var meaning in new[] { SymbolFlags.Value, SymbolFlags.Type, SymbolFlags.Namespace })
                    foreach (bool externalOnly in new[] { false, true })
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(nodeId(location));
                        writer.WriteNumberValue(symbolId(target));
                        writer.WriteNumberValue((uint)meaning);
                        writer.WriteBooleanValue(externalOnly);
                        var chain = await checker.GetAccessibleSymbolChainAsync(target, location, meaning, externalOnly);
                        if (chain is null)
                            writer.WriteNullValue();
                        else
                        {
                            writer.WriteStartArray();
                            foreach (var symbol in chain)
                                writer.WriteNumberValue(symbolId(symbol));
                            writer.WriteEndArray();
                        }
                        writer.WriteEndArray();
                    }
        writer.WriteEndArray();
    }
}
