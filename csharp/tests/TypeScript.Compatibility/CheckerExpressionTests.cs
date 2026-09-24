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

internal static class CheckerExpressionTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Expression assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}enum E{A=1,B=A+2,C='x'}const a=3;let b=3;let c;const d=typeof 1;const e=void absent;const f=`x${'a'}`;";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }),
            "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new ProgramScopeHost(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new ProgramTypeHost(context, links, scope);
        var enumNode = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<EnumDeclarationNode>().Single();
        var enumMembers = enumNode.Members!.OfType<EnumMemberNode>().ToArray();
        host.BeforeConstantReference = _ => throw new OperationCanceledException();
        try
        {
            await host.EnumValues.GetAsync(enumMembers[1]);
            throw new InvalidOperationException("Enum cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((links.Nodes.Get(enumNode).Flags & NodeCheckFlags.EnumValuesComputed) == 0);
        host.BeforeConstantReference = null;
        Check((await host.EnumValues.GetAsync(enumMembers[0])).Value is 1d);
        Check((await host.EnumValues.GetAsync(enumMembers[1])).Value is 3d);
        Check((await host.EnumValues.GetAsync(enumMembers[2])).IsSyntacticallyString);
        Check((links.Nodes.Get(enumNode).Flags & NodeCheckFlags.EnumValuesComputed) != 0);
        var declarations = program.SourceFiles[0].Syntax.DescendantsAndSelf().OfType<VariableDeclarationNode>().ToDictionary(n => ((IdentifierNode)n.Name!).Text);
        var constant = await host.Values.GetAsync(symbols.Declaration(declarations["a"])!);
        Check(constant is LiteralType { Value: 3d, IsFreshLiteral: true });
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["b"])!) == context.NumberType);
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["c"])!) == context.AutoType);
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["d"])!) is UnionType { Types.Count: 8 });
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["e"])!) == context.UndefinedType);
        Check(host.DeferredExpressions.Contains(declarations["e"].Initializer!));
        Check(await host.Values.GetAsync(symbols.Declaration(declarations["f"])!) is LiteralType { Value: "xa" });
        host.BeforeExpressionFinish = () => throw new InvalidOperationException("Expression finish failure");
        try
        {
            await host.Expressions.CheckAsync(new NumericLiteralNode { Text = "1" });
            throw new InvalidOperationException("Expression failure ignored");
        }
        catch (InvalidOperationException error) when (error.Message == "Expression finish failure")
        {
            checks++;
        }
        finally
        {
            host.BeforeExpressionFinish = null;
        }
        Check(host.Expressions.CurrentNode is null);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.EnumValues.GetAsync(enumMembers[0], cancelled.Token);
            throw new InvalidOperationException("Cached enum ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check((await host.EnumValues.GetAsync(enumMembers[1])).Value is 3d);
        var evaluator = new ConstantEvaluator((node, _, _) => ValueTask.FromResult(node is IdentifierNode { Text: "ext" }
            ? new ConstantResult(4d, false, true, true) : default));
        var binary = new BinaryExpressionNode
        {
            Left = new IdentifierNode { Text = "ext" },
            OperatorToken = new TokenNode(SyntaxKind.PlusToken),
            Right = new StringLiteralNode { Text = "x" }
        };
        var evaluated = await evaluator.EvaluateAsync(binary);
        Check(evaluated is { Value: "4x", IsSyntacticallyString: true, ResolvedOtherFiles: true, HasExternalReferences: true });
        var negative = new PrefixUnaryExpressionNode { Operator = SyntaxKind.MinusToken, Operand = new NumericLiteralNode { Text = "0" } };
        Check(BitConverter.DoubleToUInt64Bits((double)(await evaluator.EvaluateAsync(negative)).Value!) == 0x8000000000000000);
        var assertion = new AsExpressionNode
        {
            Expression = new NumericLiteralNode { Text = "1" },
            Type = new TokenNode(SyntaxKind.NumberKeyword)
        };
        Check((await evaluator.EvaluateAsync(assertion)).Value is null);
        var skipping = new ConstantEvaluator(
            (_, _, _) => ValueTask.FromResult(default(ConstantResult)),
            OuterExpressionKinds.TypeAssertions);
        Check((await skipping.EvaluateAsync(assertion)).Value is 1d);
        SyntaxNode deep = new NumericLiteralNode { Text = "1" };
        for (int i = 0; i < 20_000; i++)
            deep = new ParenthesizedExpressionNode { Expression = deep };
        Check((await evaluator.EvaluateAsync(deep)).Value is 1d);
        Check(await host.Expressions.CheckAsync(deep) is LiteralType { Value: 1d });
        Check(host.Expressions.CurrentNode is null);
        deep = new NumericLiteralNode { Text = "1" };
        for (int i = 0; i < 20_000; i++)
            deep = new PrefixUnaryExpressionNode { Operator = SyntaxKind.TildeToken, Operand = deep };
        Check((await evaluator.EvaluateAsync(deep)).Value is 1d);
        checks += await LiteralSafety();
        Console.WriteLine(
            $"{checks} expression/literal/context/enum/cache/cancellation assertions; 20000-level expression, constant and context traversal");
    }

    private static async Task<int> LiteralSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Literal assertion {checks + 1}");
            checks++;
        }
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}const left='left';const right='right';const object={[left]:1,[right]:2};const array=[1,2];function f({value=1,nested:{text='a'}}){}";
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
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
        var literal = nodes.OfType<ObjectLiteralExpressionNode>().Single();
        var rawObject = symbols.Binding(literal)!.Get(literal)!.Symbol!;
        int oldTables = host.LateMembers.CachedTableCount;
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeExpressionFinish = () =>
            {
                if (host.Expressions.CurrentNode is IdentifierNode { Text: "right" })
                    cancellation.Cancel();
            };
            try
            {
                await host.LateMembers.TableAsync(rawObject, cancellation: cancellation.Token);
                throw new InvalidOperationException("Late member cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeExpressionFinish = null;
        Check(host.LateMembers.CachedTableCount == oldTables);
        Check(literal.Properties!.All(n => links.SymbolNodes.Get(n).ResolvedSymbol is null));
        Check(nodes.OfType<ComputedPropertyNameNode>().Last() is { } lastName && links.TypeNodes.Get(lastName).ResolvedType is null);
        var table = await host.LateMembers.TableAsync(rawObject);
        Check(table.ContainsKey("left") && table.ContainsKey("right"));
        Check((table["left"].CheckFlags & CheckFlags.Late) != 0 && table["left"].Parent == rawObject);
        Check(symbols.Binding(literal.Properties![0])!.Get(literal.Properties[0])!.Symbol!.Name == Symbol.InternalPrefix + "computed");
        Check(symbols.Declaration(literal.Properties[0]) == table["left"]);
        var array = nodes.OfType<ArrayLiteralExpressionNode>().Single();
        var inference = host.Inference.Create([context.NewTypeParameter()]);
        inference.Inferences[0].Candidates.Add(context.NumberType);
        inference.IntraExpressionSites.Add((array, context.NumberType));
        host.BeforeExpressionFinish = () =>
        {
            inference.Inferences[0].Candidates.Add(context.StringType);
            throw new InvalidOperationException("context-failure");
        };
        try
        {
            await host.Contexts.CheckWithAsync(array, host.AnyArray, inference);
            throw new InvalidOperationException("Context failure ignored");
        }
        catch (InvalidOperationException error) when (error.Message == "context-failure")
        {
            checks++;
        }
        host.BeforeExpressionFinish = null;
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Expressions.CurrentNode is null);
        Check(inference.Inferences[0].Candidates.SequenceEqual([context.NumberType]));
        Check(inference.IntraExpressionSites is [var site] && site.Node == array && site.Type == context.NumberType);
        Check(await host.Contexts.CheckWithAsync(array, host.AnyArray) is TypeReference);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0);
        var pattern = nodes.OfType<BindingPatternNode>().First();
        using (var cancellation = new CancellationTokenSource())
        {
            host.BeforeInitializer = _ => cancellation.Cancel();
            try
            {
                await host.BindingPatterns.GetAsync(pattern, true, cancellation: cancellation.Token);
                throw new InvalidOperationException("Binding pattern cancellation ignored");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        host.BeforeInitializer = null;
        Check(host.BindingPatterns.ActiveCount == 0 && host.Contexts.ContextDepth == 0);
        var implied = await host.BindingPatterns.GetAsync(pattern, true);
        Check(host.InferencePatterns[implied] == pattern && host.BindingPatterns.ActiveCount == 0);
        var fresh = await host.ObjectLiterals.CheckAsync(literal);
        var regular = await host.ObjectLiterals.RegularAsync(fresh);
        Check(
            fresh != regular
                && (fresh.ObjectFlags & ObjectFlags.FreshLiteral) != 0
                && (regular.ObjectFlags & ObjectFlags.FreshLiteral) == 0);
        Check(await host.ObjectLiterals.RegularAsync(fresh) == regular);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await host.ObjectLiterals.ComputedAsync(nodes.OfType<ComputedPropertyNameNode>().First(), cancelled.Token);
            throw new InvalidOperationException("Cached computed name cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        try
        {
            await host.ObjectLiterals.RegularAsync(new TypeContext(true, true).EmptyObjectType);
            throw new InvalidOperationException("Foreign object accepted");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        var leaf = new NumericLiteralNode { Text = "1" };
        SyntaxNode nested = leaf;
        for (int i = 0; i < 20_000; i++)
            nested = new ParenthesizedExpressionNode { Expression = nested };
        var declaration = new VariableDeclarationNode
        {
            Name = new IdentifierNode { Text = "deep" },
            Type = new TokenNode(SyntaxKind.NumberKeyword),
            Initializer = nested
        };
        declaration.SetParents();
        Check(await host.Contexts.GetAsync(leaf) == context.NumberType);
        Check(host.Contexts.ContextDepth == 0 && host.Contexts.InferenceDepth == 0 && host.Instantiation.Resolutions.Count == 0);
        return checks;
    }
}
