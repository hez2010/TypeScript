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

internal static class CheckerTypeSyntaxTests
{
    internal static async Task<int> ConditionalSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Conditional syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "type A<T>=T extends (infer U)[] ? U : never;type B<T>=T extends {value:infer V extends string} ? V : never;type C={x?:'a'|'b'};"),
            ["/project/globals.d.ts"] = Wtf8.Encode(
                "interface Array<T>{length:number;[n:number]:T;}interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var declarations = source.Statements!.OfType<TypeAliasDeclarationNode>().ToArray();
        var a = await checker.Nodes.FromNodeAsync(declarations[0].Type!);
        var b = await checker.Nodes.FromNodeAsync(declarations[1].Type!);
        var c = await checker.Nodes.FromNodeAsync(declarations[2].Type!);
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        Check(await checker.SerializeTypeSyntaxAsync(a, source, true) == "T extends (infer U)[] ? U : never");
        Check(await checker.SerializeTypeSyntaxAsync(b, source, true) == "T extends { value: infer V extends string; } ? V : never");
        Check(await checker.SerializeTypeSyntaxAsync(c, source, true) == "{ x?: 'a' | 'b'; }");
        Check(await checker.SerializeTypeSyntaxAsync(c, null, true) == "{ x?: \"a\" | \"b\" | undefined; }");
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        var inferredNode = source.DescendantsAndSelf().OfType<InferTypeNode>().First().TypeParameter!;
        var inferred = await checker.Declared.GetAsync(checker.Symbols.Declaration(inferredNode)!);
        Check(await checker.SerializeTypeSyntaxAsync(inferred, source) == "U");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeTypeSyntaxAsync(b, source, true, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled conditional syntax completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(a, source, true) == "T extends (infer U)[] ? U : never");
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        return checks;
    }

    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Type syntax assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode("type A='é';type B=[number,string?];type C=number[];"),
            ["/project/globals.d.ts"] = Wtf8.Encode(
                "interface Array<T>{length:number;[n:number]:T;}interface ReadonlyArray<T>{readonly length:number;readonly [n:number]:T;}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/globals.d.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var nodes = source.Statements!.OfType<TypeAliasDeclarationNode>().Select(n => n.Type!).ToArray();
        var a = await checker.Nodes.FromNodeAsync(nodes[0]);
        var b = await checker.Nodes.FromNodeAsync(nodes[1]);
        var c = await checker.Nodes.FromNodeAsync(nodes[2]);
        Check(await checker.SerializeTypeSyntaxAsync(a) == "\"é\"");
        Check(await checker.SerializeTypeSyntaxAsync(b, source, expandAlias: true) == "[number, (string | undefined)?]");
        Check(await checker.SerializeTypeSyntaxAsync(c, source, expandAlias: true) == "number[]");
        Check(await checker.SerializeTypeSyntaxAsync(b, source) == "B");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.SerializeTypeSyntaxAsync(b, source, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled type syntax completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(b, source, expandAlias: true) == "[number, (string | undefined)?]");
        try
        {
            await checker.SerializeTypeSyntaxAsync(new TypeContext().StringType, source);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        try
        {
            await checker.SerializeTypeSyntaxAsync(a, Parser.ParseSourceFile(new("/foreign.ts"), new SourceText("")));
            throw new InvalidOperationException("Foreign syntax accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        Check(await checker.SerializeTypeSyntaxAsync(a) == "\"é\"");
        return checks;
    }

    internal static async Task WriteAsync(Utf8JsonWriter writer, SyntaxNode[] nodes, Checker checker, Func<SyntaxNode?, int> nodeId)
    {
        var main = nodes.Where(
            n => SemanticSyntax.Source(n)?.FileName.StartsWith("/project/main.", StringComparison.Ordinal) == true).ToArray();
        SyntaxNode?[] locations = [null, .. main.Where(n => n is SourceFileNode or ClassDeclarationNode or FunctionDeclarationNode)];
        writer.WriteStartArray("typeSyntaxQueries");
        foreach (var target in main.OfType<TypeAliasDeclarationNode>().Where(
            n => n.Name?.Text.StartsWith("Serialize", StringComparison.Ordinal) == true).Select(n => n.Type!))
        {
            var type = await checker.Nodes.FromNodeAsync(target);
            foreach (var location in locations)
                foreach (bool expand in new[] { false, true })
                    foreach (bool outside in new[] { false, true })
                    {
                        string value = await checker.SerializeTypeSyntaxAsync(type, location, expand, outside);
                        writer.WriteStartArray();
                        writer.WriteNumberValue(nodeId(target));
                        writer.WriteNumberValue(nodeId(location));
                        writer.WriteBooleanValue(expand);
                        writer.WriteBooleanValue(outside);
                        writer.WriteStringValue(value);
                        writer.WriteEndArray();
                    }
        }
        writer.WriteEndArray();
    }
}
