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

internal static class CheckerAccessibilityTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Accessibility assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("module"u8, "\"esnext\""u8);
        options.SetRaw("moduleResolution"u8, "\"bundler\""u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "import {Public as Renamed} from './barrel'; export interface Surface {value:Renamed;} export type Missing=Unknown; function local(){class Local{} return Local;}"),
            ["/project/dep.ts"u8] = Wtf8.Encode("class Hidden {} export class Public {field=1;}"),
            ["/project/barrel.ts"u8] = Wtf8.Encode("export {Public} from './dep';")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts"u8)!.Syntax;
        var dependency = program.GetFile("/project/dep.ts"u8)!.Syntax;
        var barrel = checker.Symbols.Declaration(program.GetFile("/project/barrel.ts"u8)!.Syntax)!;
        var dependencySymbol = checker.Symbols.Declaration(dependency)!;
        var publicNode = dependency.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Public"u8);
        var publicSymbol = checker.Symbols.Declaration(publicNode)!;
        var hidden = checker.Symbols.Declaration(
            dependency.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Hidden"u8))!;
        var local = checker.Symbols.Declaration(file.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single())!;
        Check(await checker.IsTypeSymbolAccessibleAsync(publicSymbol, file));
        Check(await checker.IsValueSymbolAccessibleAsync(publicSymbol, file));
        Check(await checker.IsSymbolAccessibleByFlagsAsync(publicSymbol, file, SymbolFlags.Type));
        var hiddenResult = await checker.GetSymbolAccessibilityDecisionAsync(hidden, file, SymbolFlags.Type);
        Check(
            hiddenResult.Accessibility == SymbolAccessibility.CannotBeNamed
                && hiddenResult.ErrorSymbol == hidden
                && hiddenResult.ErrorModule == dependencySymbol);
        Check(!await checker.IsTypeSymbolAccessibleAsync(hidden, file));
        Check(
            (await checker.GetSymbolAccessibilityDecisionAsync(
                local,
                file,
                SymbolFlags.Type)).Accessibility == SymbolAccessibility.NotAccessible);
        Check(
            (await checker.GetSymbolAccessibilityDecisionAsync(
                dependencySymbol,
                file,
                SymbolFlags.Namespace)).Accessibility == SymbolAccessibility.Accessible);
        Check(!await checker.IsSymbolAccessibleByFlagsAsync(dependencySymbol, file, SymbolFlags.Namespace));
        var containers = await checker.GetContainersOfSymbolAsync(publicSymbol, file, SymbolFlags.Type);
        Check(containers.Count == 3 && containers[0] == dependencySymbol && containers[1] == dependencySymbol && containers[2] == barrel);
        Check(program.ResolutionModeForUsage(file, null) == ReferenceResolutionMode.Unspecified);
        var import = file.DescendantsAndSelf().OfType<ImportDeclarationNode>().Single();
        Check(program.ResolutionModeForUsage(file, import.ModuleSpecifier) == ReferenceResolutionMode.Import);
        var retained = await checker.GetSymbolAccessibilityDecisionAsync(publicSymbol, file, SymbolFlags.Type, true);
        Check(retained.Accessibility == SymbolAccessibility.Accessible && retained.AliasesToMakeVisible is [ImportDeclarationNode]);
        var importSpecifier = file.DescendantsAndSelf().OfType<ImportSpecifierNode>().Single();
        Check(await checker.IsDeclarationVisibleAsync(importSpecifier));
        Check(
            (await checker.GetSymbolAccessibilityDecisionAsync(
                publicSymbol,
                file,
                SymbolFlags.Type,
                true)).AliasesToMakeVisible is { Count: 0 });
        var renamed = file.DescendantsAndSelf().OfType<TypeReferenceNode>().Single(
            n => (n.TypeName is IdentifierNode { Text: { Span: var matchedText } } && matchedText.SequenceEqual("Renamed"u8))).TypeName!;
        var entity = await checker.GetEntityNameVisibilityAsync(renamed, file);
        Check(entity.Accessibility == SymbolAccessibility.Accessible && entity.ErrorSymbolName.Length == 0);
        var unknown = file.DescendantsAndSelf().OfType<IdentifierNode>().Single(n => n.Text == "Unknown"u8);
        var unresolved = await checker.GetEntityNameVisibilityAsync(unknown, file);
        Check(
            unresolved.Accessibility == SymbolAccessibility.NotResolved
                && unresolved.ErrorSymbolName == "Unknown"u8
                && unresolved.ErrorNode == unknown);
        var field = checker.Symbols.Declaration(publicNode.Members!.OfType<PropertyDeclarationNode>().Single())!;
        var retry = await program.CreateCheckerAsync();
        retry.BeforeSymbolChainTable = _ =>
        {
            if (retry.SymbolContainerCacheCount != 0)
                throw new InvalidOperationException("accessibility callback");
        };
        try
        {
            await retry.GetSymbolAccessibilityDecisionAsync(field, file, SymbolFlags.Value, true);
            throw new InvalidOperationException("Accessibility callback was not reached");
        }
        catch (InvalidOperationException error) when (error.Message == "accessibility callback")
        {
            checks++;
        }
        finally
        {
            retry.BeforeSymbolChainTable = null;
        }
        Check(retry.SymbolContainerCacheCount == 0 && retry.AccessibleChainCacheCount == 0 && retry.SymbolTableAliasCacheCount == 0
            && retry.DeclarationVisibilityCount == 0);
        Check(
            (await retry.GetSymbolAccessibilityDecisionAsync(
                publicSymbol,
                file,
                SymbolFlags.Type,
                true)).Accessibility == SymbolAccessibility.Accessible);
        var cancelled = await program.CreateCheckerAsync();
        using var stop = new CancellationTokenSource();
        cancelled.BeforeSymbolChainTable = _ =>
        {
            if (cancelled.SymbolContainerCacheCount != 0)
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            await cancelled.GetSymbolAccessibilityDecisionAsync(field, file, SymbolFlags.Value, true, cancellation: stop.Token);
            throw new InvalidOperationException("Cancelled accessibility query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            cancelled.BeforeSymbolChainTable = null;
        }
        Check(
            cancelled.SymbolContainerCacheCount == 0
                && cancelled.AccessibleChainCacheCount == 0
                && cancelled.DeclarationVisibilityCount == 0);
        Check(await cancelled.IsTypeSymbolAccessibleAsync(publicSymbol, file));
        try
        {
            await checker.GetEntityNameVisibilityAsync(unknown, new BlockNode());
            throw new InvalidOperationException("Entity visibility accepted foreign enclosing syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(
            (await checker.GetSymbolAccessibilityDecisionAsync(
                null,
                file,
                SymbolFlags.All)).Accessibility == SymbolAccessibility.Accessible);
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var targets = new List<Symbol>();
        var seen = new HashSet<Symbol>();
        foreach (var node in nodes)
            if (SemanticSyntax.Source(node)?.FileName != "/project/globals.d.ts"u8 && QuerySyntax.Declaration(node)
                && checker.Symbols.Declaration(node) is { } symbol && seen.Add(symbol))
                targets.Add(symbol);
        var locations = nodes.Where(n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true
            && n is SourceFileNode or ModuleDeclarationNode or ClassDeclarationNode or ClassExpressionNode or FunctionDeclarationNode).ToArray();
        void Nodes(IReadOnlyList<SyntaxNode>? aliases)
        {
            writer.WriteStartArray();
            foreach (int id in (aliases ?? []).Select(nodeId).Order())
                writer.WriteNumberValue(id);
            writer.WriteEndArray();
        }
        void Start(int operation, SyntaxNode node, Symbol? symbol = null)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
            if (symbol is not null)
                writer.WriteNumberValue(symbolId(symbol));
        }
        writer.WriteStartArray("accessibilityQueries"u8);
        foreach (var location in locations)
            foreach (var target in targets)
            {
                foreach (var meaning in new[] { SymbolFlags.Value, SymbolFlags.Type, SymbolFlags.Namespace })
                {
                    Start(0, location, target);
                    writer.WriteNumberValue((uint)meaning);
                    writer.WriteStartArray();
                    foreach (var container in await checker.GetContainersOfSymbolAsync(target, location, meaning))
                        writer.WriteNumberValue(symbolId(container));
                    writer.WriteEndArray();
                    writer.WriteEndArray();
                    foreach (bool aliases in new[] { false, true })
                        foreach (bool modules in new[] { false, true })
                        {
                            Start(1, location, target);
                            writer.WriteNumberValue((uint)meaning);
                            writer.WriteBooleanValue(aliases);
                            writer.WriteBooleanValue(modules);
                            var result = await checker.GetSymbolAccessibilityDecisionAsync(target, location, meaning, aliases, modules);
                            writer.WriteNumberValue((int)result.Accessibility);
                            Nodes(result.AliasesToMakeVisible);
                            writer.WriteNumberValue(nodeId(result.ErrorNode));
                            writer.WriteEndArray();
                        }
                    Start(2, location, target);
                    writer.WriteNumberValue((uint)meaning);
                    writer.WriteBooleanValue(await checker.IsSymbolAccessibleByFlagsAsync(target, location, meaning));
                    writer.WriteEndArray();
                }
                Start(3, location, target);
                writer.WriteBooleanValue(await checker.IsTypeSymbolAccessibleAsync(target, location));
                writer.WriteBooleanValue(await checker.IsValueSymbolAccessibleAsync(target, location));
                writer.WriteEndArray();
            }
        foreach (var node in nodes)
            if (SemanticSyntax.Source(node)?.FileName.StartsWith("/project/main."u8, StringComparison.Ordinal) == true && EntityName(node))
            {
                Start(4, node);
                var result = await checker.GetEntityNameVisibilityAsync(node, node);
                writer.WriteNumberValue((int)result.Accessibility);
                Nodes(result.AliasesToMakeVisible);
                writer.WriteStringValue(result.ErrorSymbolName.Span);
                writer.WriteStringValue(result.ErrorModuleName.Span);
                writer.WriteNumberValue(nodeId(result.ErrorNode));
                writer.WriteEndArray();
            }
        writer.WriteEndArray();
    }

    private static bool EntityName(SyntaxNode node)
    {
        if (node is IdentifierNode or QualifiedNameNode)
            return true;
        while (node is PropertyAccessExpressionNode { Name: IdentifierNode } property)
            node = property.Expression!;
        return node is IdentifierNode;
    }
}
