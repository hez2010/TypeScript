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

internal static class CheckerVisibilityTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Declaration visibility assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        const string source = "namespace N { export class Item {private field=1;} } import A=N; import B=A; export {B}; "
            + "import {named} from './dep'; const hidden=1; export function visible(){return hidden;}";
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source), ["/project/dep.ts"] = Wtf8.Encode("export const named=1;") }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var file = program.GetFile("/project/main.ts")!.Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var ns = nodes.OfType<ModuleDeclarationNode>().Single();
        var aliases = nodes.OfType<ImportEqualsDeclarationNode>().ToArray();
        var exported = nodes.OfType<ExportSpecifierNode>().Single();
        var import = nodes.OfType<ImportSpecifierNode>().Single();
        var hidden = nodes.OfType<VariableDeclarationNode>().Single(n => n.Name is IdentifierNode { Text: { Span: "hidden" } });
        var member = nodes.OfType<PropertyDeclarationNode>().Single();
        var checker = await program.CreateCheckerAsync();
        Check(await checker.IsDeclarationVisibleAsync(file));
        Check(!await checker.IsDeclarationVisibleAsync(ns));
        Check(!await checker.IsDeclarationVisibleAsync(import));
        Check(await checker.GetVisibleDeclarationsAsync(checker.Symbols.Declaration(import)!, false) is { Count: 0 });
        Check(!await checker.IsDeclarationVisibleAsync(import));
        Check(await checker.GetVisibleDeclarationsAsync(checker.Symbols.Declaration(import)!, true) is [ImportDeclarationNode]);
        Check(await checker.IsDeclarationVisibleAsync(import));
        Check(!await checker.IsDeclarationVisibleAsync(hidden));
        Check(await checker.GetVisibleDeclarationsAsync(checker.Symbols.Declaration(hidden)!, true) is [VariableStatementNode]);
        Check(await checker.IsDeclarationVisibleAsync(hidden));
        await checker.PrecalculateDeclarationEmitVisibilityAsync(file);
        Check(await checker.IsDeclarationVisibleAsync(ns));
        foreach (var alias in aliases)
            Check(await checker.IsDeclarationVisibleAsync(alias));
        Check(await checker.IsDeclarationVisibleAsync(nodes.OfType<ClassDeclarationNode>().Single()));
        Check(!await checker.IsDeclarationVisibleAsync(member));
        checker.BeforeVisibilityNode = _ => throw new InvalidOperationException("Precalculation repeated");
        await checker.PrecalculateDeclarationEmitVisibilityAsync(file);
        checker.BeforeVisibilityNode = null;
        Check(checker.VisibilityFileCount == 1);
        var cancelledChecker = await program.CreateCheckerAsync();
        Check(!await cancelledChecker.IsDeclarationVisibleAsync(ns));
        foreach (var alias in aliases)
            Check(!await cancelledChecker.IsDeclarationVisibleAsync(alias));
        int baselineCount = cancelledChecker.DeclarationVisibilityCount;
        using var stop = new CancellationTokenSource();
        cancelledChecker.BeforeVisibilityNode = node =>
        {
            if (node == exported.Name)
            {
                stop.Cancel();
                stop.Token.ThrowIfCancellationRequested();
            }
        };
        try
        {
            await cancelledChecker.PrecalculateDeclarationEmitVisibilityAsync(file, stop.Token);
            throw new InvalidOperationException("Cancelled visibility precalculation completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        finally
        {
            cancelledChecker.BeforeVisibilityNode = null;
        }
        Check(cancelledChecker.VisibilityFileCount == 0 && cancelledChecker.DeclarationVisibilityCount == baselineCount);
        Check(!await cancelledChecker.IsDeclarationVisibleAsync(ns));
        foreach (var alias in aliases)
            Check(!await cancelledChecker.IsDeclarationVisibleAsync(alias));
        await cancelledChecker.PrecalculateDeclarationEmitVisibilityAsync(file);
        Check(cancelledChecker.VisibilityFileCount == 1 && await cancelledChecker.IsDeclarationVisibleAsync(ns));
        var failedChecker = await program.CreateCheckerAsync();
        Check(!await failedChecker.IsDeclarationVisibleAsync(import));
        baselineCount = failedChecker.DeclarationVisibilityCount;
        var combined = new Symbol(SymbolFlags.Transient | SymbolFlags.TypeAlias, "combined");
        combined.DeclarationList.Add(import);
        combined.DeclarationList.Add(member);
        failedChecker.BeforeVisibilityNode = node =>
        {
            if (node == member)
                throw new InvalidOperationException("visibility callback");
        };
        try
        {
            await failedChecker.GetVisibleDeclarationsAsync(combined, true);
            throw new InvalidOperationException("Visibility callback was not reached");
        }
        catch (InvalidOperationException error) when (error.Message == "visibility callback")
        {
            checks++;
        }
        finally
        {
            failedChecker.BeforeVisibilityNode = null;
        }
        Check(failedChecker.DeclarationVisibilityCount == baselineCount && !await failedChecker.IsDeclarationVisibleAsync(import));
        Check(await failedChecker.GetVisibleDeclarationsAsync(checker.Symbols.Declaration(import)!, true) is [ImportDeclarationNode]);
        var leaf = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "T" } };
        SyntaxNode current = leaf;
        for (int i = 0; i < 20_000; i++)
        {
            var parent = new ParenthesizedTypeNode { Type = current };
            current.Parent = parent;
            current = parent;
        }
        current.Parent = file;
        Check(await checker.IsDeclarationVisibleAsync(leaf));
        var synthetic = new TypeReferenceNode { Flags = NodeFlags.Synthesized, Parent = file };
        Check(!await checker.IsDeclarationVisibleAsync(synthetic));
        try
        {
            await checker.IsDeclarationVisibleAsync(new TypeReferenceNode());
            throw new InvalidOperationException("Visibility accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.IsDeclarationVisibleAsync(file));
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker,
        Func<Symbol?, int> symbolId, Func<SyntaxNode?, int> nodeId)
    {
        var selected = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        var entries = new List<(SyntaxNode Node, int Owner, int DocumentationIndex)>();
        foreach (var node in selected)
        {
            entries.Add((node, nodeId(node), -1));
            int index = 0;
            foreach (var comment in await SemanticSyntax.Source(node)!.GetDocumentationAsync(node))
                foreach (var part in comment.DescendantsAndSelf())
                    entries.Add((part, nodeId(node), index++));
        }
        var symbols = new HashSet<Symbol>();
        var declarations = new List<(SyntaxNode Node, Symbol Symbol)>();
        foreach (var node in selected)
            if (QuerySyntax.Declaration(node) && checker.Symbols.Declaration(node) is { } symbol && symbols.Add(symbol))
                declarations.Add((node, symbol));
        writer.WriteStartArray("visibilityQueries");
        async Task Visibility(int phase)
        {
            foreach (var entry in entries)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(phase);
                writer.WriteNumberValue(entry.Owner);
                writer.WriteNumberValue(entry.DocumentationIndex);
                writer.WriteNumberValue((int)entry.Node.Kind);
                writer.WriteBooleanValue(await checker.IsDeclarationVisibleAsync(entry.Node));
                writer.WriteEndArray();
            }
        }
        await Visibility(0);
        foreach (var file in selected.OfType<SourceFileNode>())
            await checker.PrecalculateDeclarationEmitVisibilityAsync(file);
        await Visibility(1);
        foreach (var (node, symbol) in declarations)
            foreach (bool compute in new[] { false, true })
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(2);
                writer.WriteNumberValue(nodeId(node));
                writer.WriteNumberValue(symbolId(symbol));
                writer.WriteBooleanValue(compute);
                var aliases = await checker.GetVisibleDeclarationsAsync(symbol, compute);
                if (aliases is null)
                    writer.WriteNullValue();
                else
                {
                    writer.WriteStartArray();
                    foreach (int id in aliases.Select(nodeId).Order())
                        writer.WriteNumberValue(id);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            }
        await Visibility(3);
        foreach (var file in selected.OfType<SourceFileNode>())
            await checker.PrecalculateDeclarationEmitVisibilityAsync(file);
        await Visibility(4);
        writer.WriteEndArray();
    }
}
