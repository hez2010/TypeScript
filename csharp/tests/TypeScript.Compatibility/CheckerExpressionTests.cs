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
        try
        {
            await host.Expressions.CheckAsync(new IdentifierNode { Text = "unhandled" });
            throw new InvalidOperationException("Unimplemented expression accepted");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("requires expression checking", StringComparison.Ordinal))
        {
            checks++;
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
        Console.WriteLine($"{checks} expression/enum/cache/cancellation assertions; 20000-level expression and constant traversal");
    }
}
