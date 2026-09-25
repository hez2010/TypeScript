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

internal static class CheckerEmitSyntaxTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Emit syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "function f<T>(value:T):T{return value}class Scope<T>{}const text='é';const infinity=-1e999;const big=-12n;let ordinary=1;function anyReturn():any{}"),
            ["/project/other.ts"] = Wtf8.Encode("export const other=1;")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/other.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var f = source.DescendantsAndSelf().OfType<FunctionDeclarationNode>().First();
        var scope = source.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single();
        var variables = source.DescendantsAndSelf().OfType<VariableDeclarationNode>().ToDictionary(v => ((IdentifierNode)v.Name!).Text);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        const NodeBuilderFlags flags = NodeBuilderFlags.IgnoreErrors | NodeBuilderFlags.NoTruncation | NodeBuilderFlags.GenerateNamesForShadowedTypeParams;
        Check(await checker.SerializeReturnTypeForEmitAsync(f, scope, flags) == "T_1");
        Check(checker.TypeSyntaxScopeCount == 0);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeReturnTypeForEmitAsync(f, scope, flags, stop.Token);
            throw new InvalidOperationException("Canceled emit query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.SerializeReturnTypeForEmitAsync(f, scope, flags) == "T_1" && checker.TypeSyntaxScopeCount == 0);
        Check(await checker.SerializeLiteralConstForEmitAsync(variables["text"]) == "\"\\u00E9\"");
        Check(await checker.SerializeLiteralConstForEmitAsync(variables["infinity"]) == "-Infinity");
        Check(await checker.SerializeLiteralConstForEmitAsync(variables["big"]) == "-12n");
        Check(await checker.SerializeLiteralConstForEmitAsync(variables["ordinary"]) is null);
        Check(await checker.SerializeReturnTypeForEmitAsync(source.DescendantsAndSelf().OfType<FunctionDeclarationNode>().Last(), source,
            flags | NodeBuilderFlags.SuppressAnyReturnType) == "");
        var synthesized = new NodeFactory().NewIdentifier("generated");
        synthesized.Flags |= NodeFlags.Synthesized;
        Check(await checker.SerializeDeclarationTypeForEmitAsync(synthesized, source) == "any"
            && await checker.SerializeExpressionTypeForEmitAsync(synthesized, source) == "any"
            && (await checker.SerializeTypeParametersForEmitAsync(synthesized, source)).Count == 0);
        var foreign = new NodeFactory().NewIdentifier("foreign");
        try
        {
            await checker.SerializeExpressionTypeForEmitAsync(foreign, source);
            throw new InvalidOperationException("Foreign syntax accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var concurrent = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => checker.SerializeReturnTypeForEmitAsync(f, scope, flags).AsTask()));
        Check(concurrent.All(v => v == "T_1") && checker.TypeSyntaxScopeCount == 0);
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        var targets = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        var locations = new SyntaxNode?[] { null }.Concat(targets.Where(n => n is SourceFileNode
            || n is ClassDeclarationNode or FunctionDeclarationNode
                && (n as INamedNode)?.Name is IdentifierNode { Text: "Scope" })).ToArray();
        NodeBuilderFlags[] flags = [NodeBuilderFlags.NoTruncation,
            NodeBuilderFlags.NoTruncation | NodeBuilderFlags.GenerateNamesForShadowedTypeParams,
            NodeBuilderFlags.NoTruncation | NodeBuilderFlags.SuppressAnyReturnType,
            NodeBuilderFlags.NoTruncation | NodeBuilderFlags.UseSingleQuotesForStringLiteralType];
        writer.WriteStartArray("emitQueries");
        foreach (var node in targets)
        {
            bool declaration = node is VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode
                or PropertySignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode;
            if (declaration)
                foreach (var location in locations)
                    foreach (var flag in flags)
                    {
                        Start(0, node, location, flag);
                        writer.WriteStringValue(
                            await checker.SerializeDeclarationTypeForEmitAsync(node, location, flag | NodeBuilderFlags.IgnoreErrors));
                        writer.WriteEndArray();
                    }
            if (node is IInitializedNode { Initializer: { } expression })
                foreach (var location in locations)
                    foreach (var flag in flags)
                    {
                        Start(1, expression, location, flag);
                        writer.WriteStringValue(
                            await checker.SerializeExpressionTypeForEmitAsync(expression, location, flag | NodeBuilderFlags.IgnoreErrors));
                        writer.WriteEndArray();
                    }
            if (Signatures.FunctionLike(node))
                foreach (var location in locations)
                    foreach (var flag in flags)
                    {
                        Start(2, node, location, flag);
                        writer.WriteStringValue(
                            await checker.SerializeReturnTypeForEmitAsync(node, location, flag | NodeBuilderFlags.IgnoreErrors));
                        writer.WriteEndArray();
                        Start(3, node, location, flag);
                        writer.WriteStartArray();
                        foreach (var parameter in await checker.SerializeTypeParametersForEmitAsync(
                            node,
                            location,
                            flag | NodeBuilderFlags.IgnoreErrors))
                            writer.WriteStringValue(parameter);
                        writer.WriteEndArray();
                        writer.WriteEndArray();
                    }
            if (node is VariableDeclarationNode or PropertyDeclarationNode)
            {
                Start(4, node, null, 0);
                writer.WriteStringValue(await checker.SerializeLiteralConstForEmitAsync(node));
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
        void Start(int operation, SyntaxNode node, SyntaxNode? location, NodeBuilderFlags flag)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(operation);
            writer.WriteNumberValue(nodeId(node));
            writer.WriteNumberValue(nodeId(location));
            writer.WriteNumberValue((uint)flag);
        }
    }
}
