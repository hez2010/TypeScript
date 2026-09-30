using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using Type = TypeScript.Compiler.Checking.Type;

namespace TypeScript.Compatibility;

internal static class CheckerBinaryTests
{
    internal static async Task Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Binary assertion {checks + 1}");
            checks++;
        }
        Utf8String source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface Promise<T>{then(cb:(value:T)=>unknown):unknown}interface Late{then(cb:(value:number)=>void):void}interface Loop{then(cb:(value:Loop)=>void):void}interface Bad{then():void}interface Receiver{then(this:string,cb:(value:number)=>void):void}type Nested=Promise<Late>;"u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.ts"u8] = source.Span.ToArray() }),
            "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var context = new TypeContext(true, true);
        var links = new CheckerLinks();
        var scope = new CheckerEnvironment(context, links);
        var symbols = await CheckerSymbols.CreateAsync(program, links, scope);
        var host = new Checker(context, links, scope);
        var nested = await host.Declared.GetAsync(symbols.Globals["Nested"u8]);
        host.BeforeNode = _ => throw new OperationCanceledException();
        try
        {
            await host.Awaited.NoAliasAsync(nested);
            throw new InvalidOperationException("Awaited cancellation ignored");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Awaited.StackDepth == 0 && host.Instantiation.Resolutions.Count == 0);
        host.BeforeNode = null;
        Check(await host.Awaited.NoAliasAsync(nested) == context.NumberType);
        Check(await host.Awaited.NoAliasAsync(nested) == context.NumberType);
        var loop = await host.Declared.GetAsync(symbols.Globals["Loop"u8]);
        var location = symbols.Globals["Loop"u8].Declarations[0];
        Check(
            await host.Awaited.NoAliasAsync(loop, location) is null
                && host.Diagnostics.Contains(
                    DiagnosticCode.TypeIsReferencedDirectlyOrIndirectlyInTheFulfillmentCallbackOfItsOwnThenMethod));
        Check(host.Awaited.StackDepth == 0);
        var bad = await host.Declared.GetAsync(symbols.Globals["Bad"u8]);
        Check(await host.Awaited.ThenableAsync(bad));
        Check(
            await host.Awaited.GetAsync(bad, errorNode: symbols.Globals["Bad"u8].Declarations[0]) is null
                && host.Diagnostics.Contains(
                    DiagnosticCode.TypeOfAwaitOperandMustEitherBeAValidPromiseOrMustNotContainACallableThenMember));
        var receiver = await host.Declared.GetAsync(symbols.Globals["Receiver"u8]);
        var promised = await host.Awaited.PromisedAsync(receiver, symbols.Globals["Receiver"u8].Declarations[0]);
        Check(
            promised.Type is null
                && promised.ThisError == context.StringType
                && host.Diagnostics.Contains(DiagnosticCode.TheThisContextOfType0IsNotAssignableToMethodSThisOfType1));
        Check((await host.Awaited.PromisedAsync(context.StringType)).Type is null);
        Check(await host.Awaited.NoAliasAsync(context.StringType) == context.StringType);
        Check(await host.Awaited.NoAliasAsync(context.AnyType) == context.AnyType);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await host.Awaited.NoAliasAsync(nested, cancellation: cancellation.Token);
            throw new InvalidOperationException("Cached await ignored cancellation");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(host.Awaited.StackDepth == 0 && await host.Awaited.NoAliasAsync(nested) == context.NumberType);
        BinaryExpressionNode Binary(SyntaxNode left, SyntaxKind op, SyntaxNode right)
        {
            var result = new BinaryExpressionNode { Left = left, OperatorToken = new TokenNode(op), Right = right };
            result.SetParents();
            return result;
        }
        NumericLiteralNode Number(Utf8String text) => new() { Text = text };
        Check(await host.Expressions.CheckAsync(Binary(Number("1"u8), SyntaxKind.PlusToken, Number("2"u8))) == context.NumberType);
        Check(
            await host.Expressions.CheckAsync(
                Binary(new StringLiteralNode { Text = "a"u8 }, SyntaxKind.PlusToken, Number("2"u8))) == context.StringType);
        Check(
            await host.Expressions.CheckAsync(
                Binary(
                    new BigIntLiteralNode { Text = "1n"u8 },
                    SyntaxKind.AsteriskToken,
                    new BigIntLiteralNode { Text = "2n"u8 })) == context.BigIntType);
        var equality = Binary(Number("1"u8), SyntaxKind.EqualsEqualsEqualsToken, Number("2"u8));
        int before = host.Diagnostics.Count;
        Check(await host.Expressions.CheckAsync(equality, CheckMode.TypeOnly) == context.BooleanType && host.Diagnostics.Count == before);
        Check(
            await host.Expressions.CheckAsync(equality) == context.BooleanType
                && host.Diagnostics.Contains(DiagnosticCode.ThisComparisonAppearsToBeUnintentionalBecauseTheTypes0And1HaveNoOverlap));
        Check(
            await host.Expressions.CheckAsync(Binary(Number("1"u8), SyntaxKind.EqualsToken, Number("2"u8))) is LiteralType { Value: 2d }
                && host.Diagnostics.Contains(DiagnosticCode.TheLeftHandSideOfAnAssignmentExpressionMustBeAVariableOrAPropertyAccess));
        var coalesce = Binary(new TokenNode(SyntaxKind.NullKeyword), SyntaxKind.QuestionQuestionToken, Number("3"u8));
        Check(
            await host.Expressions.CheckAsync(coalesce) is LiteralType { Value: 3d }
                && host.Diagnostics.Contains(DiagnosticCode.ThisExpressionIsAlwaysNullish));
        Check(await host.ExpressionChecks.NullishnessAsync(coalesce) == 2);
        Check(BinaryExpressions.SideEffectFree(Binary(Number("1"u8), SyntaxKind.PlusToken, Number("2"u8))));
        Check(!BinaryExpressions.SideEffectFree(Binary(Number("1"u8), SyntaxKind.EqualsToken, Number("2"u8))));
        SyntaxNode deep = Number("1"u8);
        for (int i = 0; i < 20_000; i++)
            deep = new BinaryExpressionNode { Left = deep, OperatorToken = new TokenNode(SyntaxKind.PlusToken), Right = Number("1"u8) };
        Check(await host.Expressions.CheckAsync(deep) == context.NumberType);
        Check(host.Expressions.CurrentNode is null);
        Check(BinaryExpressions.SideEffectFree(deep));
        Check(await host.ExpressionChecks.NullishnessAsync(deep) == 2);
        checks += await DestructuringSafety();
        Console.WriteLine($"{checks} binary/awaited/recursion/cancellation assertions; 20000-level binary expressions");
    }

    private static async Task<int> DestructuringSafety()
    {
        Utf8String source = """
            interface Array<T> { length: number; [n: number]: T; }
            interface ReadonlyArray<T> { readonly length: number; readonly [n: number]: T; }
            function values(input: { n?: number; label: string }, tuple: [number, string]) {
                let n: number, label: string, rest: { label: string };
                ({ n = 1, ...rest } = input);
                [n, label] = tuple;
                const number: number = n;
                const text: string = rest.label;
                for ([n, label] of [tuple]) { const value: number = n; }
                let union: string | number;
                [union] = [1];
                const narrowed: number = union;
                [{ n: n = 2 }] = [input];
            }
            declare const tuple: [number];
            let n: number, s: string, array: number[];
            (() => [n, s] = tuple);
            [...array, n] = [1];
            let [...tail, last] = [1];
            const [...trailing,] = [1];
            [n] = ['wrong'];
            """u8;
        var options = new CompilerOptions();
        options.SetRaw("noLib"u8, "true"u8);
        options.SetRaw("strict"u8, "true"u8);
        options.SetRaw("noUncheckedIndexedAccess"u8, "true"u8);
        options.SetRaw("target"u8, "\"es5\""u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        { ["/project/main.ts"u8] = source.Span.ToArray() }), "/project"u8,
            new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.SourceFiles[0].Syntax;
        var nodes = file.DescendantsAndSelf().ToArray();
        var parents = nodes.Select(n => n.Parent).ToArray();
        await checker.CheckSourceFileAsync(file);
        var codes = checker.DiagnosticCodesForFile(file).Order().ToArray();
        DiagnosticCode[] expected =
            [
                DiagnosticCode.ARestParameterOrBindingPatternMayNotHaveATrailingComma,
                DiagnosticCode.Type0IsNotAssignableToType1,
                DiagnosticCode.Type0IsNotAssignableToType1,
                DiagnosticCode.Type0IsNotAssignableToType1,
                DiagnosticCode.ARestElementMustBeLastInADestructuringPattern,
                DiagnosticCode.ARestElementMustBeLastInADestructuringPattern,
                DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2,
                DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2,
                DiagnosticCode.TupleType0OfLength1HasNoElementAtIndex2
            ];
        if (!codes.SequenceEqual(expected))
            throw new InvalidOperationException($"Destructuring diagnostics: {string.Join(',', codes)}");
        if (!nodes.Select(n => n.Parent).SequenceEqual(parents))
            throw new InvalidOperationException("Destructuring changed source parents");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await checker.DestructuringAsync(
                nodes.OfType<ArrayLiteralExpressionNode>().First(),
                checker.Context.AnyType,
                0,
                false,
                cancellation.Token);
            throw new InvalidOperationException("Destructuring cancellation ignored");
        }
        catch (OperationCanceledException) { }
        return 3;
    }
}
