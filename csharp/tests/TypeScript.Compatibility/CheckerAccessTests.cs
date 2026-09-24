using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerAccessTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Access assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}declare function __access(value:unknown):void;interface I{a:number;b?:string;readonly r:number}function f(x:I){__access(x.a);__access(x.b);__access(x.r=1);__access(x['a']);}class B<T>{p:T}class D extends B<string>{}class C{readonly p:number;constructor(){this.p=1;}method():void{this.p=2;}}";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        options.SetRaw("exactOptionalPropertyTypes", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new ProgramTypeHost(context, links, scope);
        var nodes = program.SourceFiles[0].Syntax.DescendantsAndSelf().ToArray();
        var accesses = nodes.OfType<CallExpressionNode>().Where(
            c => c.Expression is IdentifierNode { Text: "__access" }).Select(c => c.Arguments![0]).ToArray();
        Check(await host.Expressions.CheckAsync(accesses[0]) == context.NumberType);
        var optional = await host.Expressions.CheckAsync(accesses[1]);
        Check(optional is UnionType union && union.Types.Contains(context.MissingType) && union.Types.Contains(context.StringType));
        Check(await host.Expressions.CheckAsync(accesses[2]) is LiteralType { Value: 1d } && host.Diagnostics.Contains(2540));
        Check(await host.Expressions.CheckAsync(accesses[3]) == context.NumberType);
        Check(
            links.SymbolNodes.Get(accesses[0]).ResolvedSymbol?.Name == "a"
                && links.SymbolNodes.Get(accesses[3]).ResolvedSymbol?.Name == "a");

        var derived = (InterfaceType)await host.Declared.GetAsync(symbols.Globals["D"]);
        using (var cancellation = new CancellationTokenSource())
        {
            scope.BeforeValueResolution = cancellation.Cancel;
            try
            {
                await host.ClassBases.ConstructorAsync(derived, cancellation.Token);
                throw new InvalidOperationException("Base constructor cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        scope.BeforeValueResolution = null;
        Check(derived.ResolvedBaseConstructorType is null && host.Instantiation.Resolutions.Count == 0);
        var constructor = await host.ClassBases.ConstructorAsync(derived);
        Check(constructor.Symbol == symbols.Globals["B"]);
        var baseTypes = await host.Bases.GetAsync(derived);
        Check(
            baseTypes.Count == 1
                && baseTypes[0] is TypeReference reference
                && (await host.References.TypeArgumentsAsync(reference))[0] == context.StringType);
        Check(await host.Bases.HasBaseAsync(derived, await host.Declared.GetAsync(symbols.Globals["B"])));
        var constructors = await host.ClassBases.DefaultsAsync(derived);
        Check(constructors.Count == 1 && await host.Signatures.ReturnAsync(constructors[0]) == derived);
        Check(await host.ClassBases.ConstructorAsync(derived) == constructor);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.ClassBases.ConstructorAsync(derived, cancelled.Token);
            throw new InvalidOperationException("Cached base ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }

        var field = nodes.OfType<PropertyDeclarationNode>().Single(
            p => p.Name is IdentifierNode { Text: "p" } && p.Parent is ClassDeclarationNode { Name.Text: "C" });
        var property = symbols.Declaration(field)!;
        var targets = nodes.OfType<PropertyAccessExpressionNode>().Where(p => p.Expression?.Kind == SyntaxKind.ThisKeyword).ToArray();
        Check(!host.MemberAccess.ReadonlyAssignment(targets[0], property, 1));
        Check(host.MemberAccess.ReadonlyAssignment(targets[1], property, 1));
        Check(!host.MemberAccess.ReadonlyAssignment(targets[1], property, 0));

        var receiver = new IdentifierNode { Text = "x" };
        var inner = new PropertyAccessExpressionNode
        {
            Expression = receiver,
            Name = new IdentifierNode { Text = "a" },
            QuestionDotToken = new TokenNode(SyntaxKind.QuestionDotToken),
            Flags = NodeFlags.OptionalChain
        };
        var outer = new PropertyAccessExpressionNode
        {
            Expression = inner,
            Name = new IdentifierNode { Text = "b" },
            Flags = NodeFlags.OptionalChain
        };
        outer.SetParents();
        var nullable = await host.Algebra.UnionAsync([context.StringType, context.NullType, context.UndefinedType]);
        Check(await host.Optional.ReceiverAsync(nullable, receiver) == context.StringType);
        var marked = await host.Optional.PropagateAsync(context.NumberType, inner, true);
        Check(
            marked is UnionType markedUnion
                && markedUnion.Types.Contains(context.OptionalType)
                && !markedUnion.Types.Contains(context.UndefinedType));
        Check(await host.Optional.ReceiverAsync(marked, inner) == context.NumberType);
        var final = await host.Optional.PropagateAsync(context.NumberType, outer, true);
        Check(
            final is UnionType finalUnion
                && finalUnion.Types.Contains(context.UndefinedType)
                && !finalUnion.Types.Contains(context.OptionalType));
        Check(await host.Optional.PropagateAsync(context.NumberType, outer, false) == context.NumberType);
        try
        {
            await host.Optional.PropagateAsync(marked, inner, true, cancelled.Token);
            throw new InvalidOperationException("Optional propagation ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.FlowTypes.ActiveLoopCount == 0 && host.FlowTypes.SharedCount == 0);

        Check(GoUnicode.Lower(0x130) == 'i');
        Check(GoUnicode.EqualFold(GoUnicode.Runes("K"), GoUnicode.Runes("K")));
        Check(GoUnicode.Runes("\ud800").SequenceEqual([0xfffd, 0xfffd, 0xfffd]));
        Check(
            await SpellingSuggestions.FindAsync(
                "abcde",
                new[] { "abcdf", "abcda" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) == "abcda");
        Check(
            await SpellingSuggestions.FindAsync(
                "ixx",
                new[] { "İxx" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) == "İxx");
        Check(
            await SpellingSuggestions.FindAsync(
                "go",
                new[] { "gz" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare) is null);
        Check(
            await SpellingSuggestions.FindAsync(
                "abcde",
                new[] { "abcdf", "abcda" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare,
                1) is null);
        try
        {
            await SpellingSuggestions.FindAsync(
                "name",
                new[] { "Name" },
                s => ValueTask.FromResult<string?>(s),
                StringComparer.Ordinal.Compare,
                cancellation: cancelled.Token);
            throw new InvalidOperationException("Spelling search ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }

        SyntaxNode deep = new IdentifierNode { Text = "deep" };
        var start = deep;
        for (int i = 0; i < 20_000; i++)
            deep = new ParenthesizedExpressionNode { Expression = deep };
        var call = new CallExpressionNode { Expression = deep };
        call.SetParents();
        Check(AccessExpressions.MethodCall(start));
        Check(MemberAccessRules.SkipParentheses(deep) == start);
        var current = new PropertyAccessExpressionNode { Expression = start, Name = new IdentifierNode { Text = "p" } };
        SyntaxNode wrapped = current;
        for (int i = 0; i < 20_000; i++)
            wrapped = new ParenthesizedExpressionNode { Expression = wrapped };
        var deletion = new DeleteExpressionNode { Expression = wrapped };
        deletion.SetParents();
        Check(AccessExpressions.DeleteTarget(current));
        Console.WriteLine($"{checks} access/optional/member/class/cancellation/spelling assertions; 20,000-level traversal.");
    }
}
