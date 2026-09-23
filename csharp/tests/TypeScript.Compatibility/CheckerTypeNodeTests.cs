using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerTypeNodeTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Type node assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        const string text = "interface Array<T> {} interface ReadonlyArray<T> {} type A = A[]; type B<T=string,U=T> = [T,U]; type C=B<number>; type D<X> = X; type Missing = NotFound<string>; type Bad = Bad; type Generic<T> = {value:T};";
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/input.ts"] = Wtf8.Encode(text) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/input.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scopeHost = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scopeHost);
        var host = new ProgramTypeHost(context, links, scopeHost);
        var a = (TypeReference)await host.Declared.GetAsync(symbols.Globals["A"]);
        Check(a.ResolvedTypeArguments is null && a.Node is ArrayTypeNode);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.References.TypeArgumentsAsync(a);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(a.ResolvedTypeArguments is null && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check((await host.References.TypeArgumentsAsync(a))[0] == a);
        Check(await host.Declared.GetAsync(symbols.Globals["A"]) == a);
        var c = (TypeReference)await host.Declared.GetAsync(symbols.Globals["C"]);
        var arguments = await host.References.TypeArgumentsAsync(c);
        Check(arguments.SequenceEqual([context.NumberType, context.NumberType]));
        var b = symbols.Globals["B"];
        var bType = await host.Declared.GetAsync(b);
        var bParameters = links.TypeAliases.Get(b).TypeParameters!;
        Check(TypeReferences.Minimum(bParameters) == 0);
        Check(await host.References.AliasInstantiationAsync(b, bParameters) == bType);
        var missing = await host.Declared.GetAsync(symbols.Globals["Missing"]);
        Check(missing is IntrinsicType { IntrinsicName: "error", Alias: { TypeArguments.Count: 1 } });
        Check((missing.Alias!.Symbol.CheckFlags & CheckFlags.Unresolved) != 0);
        Check(await host.Declared.GetAsync(symbols.Globals["Bad"]) == context.ErrorType && host.Diagnostics.Contains(2456));
        Check(host.Instantiation.Resolutions.Count == 0);

        var d = symbols.Globals["D"];
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Declared.GetAsync(d);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(
            links.TypeAliases.Get(d).DeclaredType is null
                && links.TypeAliases.Get(d).Instantiations is null
                && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        var dType = await host.Declared.GetAsync(d);
        Check(dType is TypeParameter && await host.References.AliasInstantiationAsync(d, [context.StringType]) == context.StringType);
        var generic = (ObjectType)await host.Declared.GetAsync(symbols.Globals["Generic"]);
        Check(generic.Alias?.Symbol == symbols.Globals["Generic"] && generic.Members is null);
        var instance = (ObjectType)await host.References.AliasInstantiationAsync(symbols.Globals["Generic"], [context.StringType]);
        Check(instance.Target == generic && instance.Members is null);
        Check(await host.References.AliasInstantiationAsync(symbols.Globals["Generic"], [context.StringType]) == instance);

        var p = scopeHost.Scopes.Parameter(scopeHost.Scopes.Local(d).Single().Symbol!);
        var alias = context.CreateAlias(d, [p]);
        var indexed = context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.Writing, alias);
        Check(context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.Writing, context.CreateAlias(d, [p])) == indexed);
        Check(context.GetGenericIndexedAccess(p, context.StringType, 0, alias) != indexed && indexed.AccessFlags == 0);
        Check(context.GetGenericIndexedAccess(p, context.StringType, AccessFlags.IncludeUndefined, alias) != indexed);
        var foreign = new TypeContext(true, true);
        try
        {
            context.GetGenericIndexedAccess(p, foreign.StringType, 0);
            throw new InvalidOperationException("Foreign type accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }

        SyntaxNode nested = new ArrayTypeNode { ElementType = new TokenNode(SyntaxKind.StringKeyword) };
        for (int i = 0; i < 20_000; i++)
            nested = new ParenthesizedTypeNode { Type = nested };
        Check(TypeNodes.ArrayElementNode(nested)?.Kind == SyntaxKind.StringKeyword);
        var leaf = new ArrayTypeNode { ElementType = new TypeReferenceNode { TypeName = new IdentifierNode { Text = "D" } } };
        leaf.SetParents();
        SyntaxNode outer = leaf;
        for (int i = 0; i < 20_000; i++)
        {
            var next = new ParenthesizedTypeNode { Type = outer };
            outer.Parent = next;
            outer = next;
        }
        outer.Parent = new TypeAliasDeclarationNode(SyntaxKind.TypeAliasDeclaration) { Parent = program.SourceFiles[0].Syntax };
        Check(await host.References.DeferredAsync(leaf, false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Nodes.FromNodeAsync(new TokenNode(SyntaxKind.StringKeyword), cancellation.Token);
            throw new InvalidOperationException("Cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Console.WriteLine($"{checks} source-type/cache/cancellation assertions; array and alias ancestry depth 20000");
    }
}
