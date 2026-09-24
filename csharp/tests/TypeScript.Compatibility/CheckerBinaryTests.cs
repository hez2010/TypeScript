using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
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
        const string source = "interface Array<T>{length:number;[n:number]:T}interface ReadonlyArray<T>{readonly length:number;readonly[n:number]:T}interface Promise<T>{then(cb:(value:T)=>unknown):unknown}interface Late{then(cb:(value:number)=>void):void}interface Loop{then(cb:(value:Loop)=>void):void}interface Bad{then():void}interface Receiver{then(this:string,cb:(value:number)=>void):void}type Nested=Promise<Late>;";
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
        var nested = await host.Declared.GetAsync(symbols.Globals["Nested"]);
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
        var loop = await host.Declared.GetAsync(symbols.Globals["Loop"]);
        var location = symbols.Globals["Loop"].Declarations[0];
        Check(await host.Awaited.NoAliasAsync(loop, location) is null && host.Diagnostics.Contains(1062));
        Check(host.Awaited.StackDepth == 0);
        var bad = await host.Declared.GetAsync(symbols.Globals["Bad"]);
        Check(await host.Awaited.ThenableAsync(bad));
        Check(
            await host.Awaited.GetAsync(bad, errorNode: symbols.Globals["Bad"].Declarations[0]) is null && host.Diagnostics.Contains(1320));
        var receiver = await host.Declared.GetAsync(symbols.Globals["Receiver"]);
        var promised = await host.Awaited.PromisedAsync(receiver, symbols.Globals["Receiver"].Declarations[0]);
        Check(promised.Type is null && promised.ThisError == context.StringType && host.Diagnostics.Contains(2684));
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
        NumericLiteralNode Number(string text) => new() { Text = text };
        Check(await host.Expressions.CheckAsync(Binary(Number("1"), SyntaxKind.PlusToken, Number("2"))) == context.NumberType);
        Check(
            await host.Expressions.CheckAsync(
                Binary(new StringLiteralNode { Text = "a" }, SyntaxKind.PlusToken, Number("2"))) == context.StringType);
        Check(
            await host.Expressions.CheckAsync(
                Binary(
                    new BigIntLiteralNode { Text = "1n" },
                    SyntaxKind.AsteriskToken,
                    new BigIntLiteralNode { Text = "2n" })) == context.BigIntType);
        var equality = Binary(Number("1"), SyntaxKind.EqualsEqualsEqualsToken, Number("2"));
        int before = host.Diagnostics.Count;
        Check(await host.Expressions.CheckAsync(equality, CheckMode.TypeOnly) == context.BooleanType && host.Diagnostics.Count == before);
        Check(await host.Expressions.CheckAsync(equality) == context.BooleanType && host.Diagnostics.Contains(2367));
        Check(
            await host.Expressions.CheckAsync(Binary(Number("1"), SyntaxKind.EqualsToken, Number("2"))) is LiteralType { Value: 2d }
                && host.Diagnostics.Contains(2364));
        var coalesce = Binary(new TokenNode(SyntaxKind.NullKeyword), SyntaxKind.QuestionQuestionToken, Number("3"));
        Check(await host.Expressions.CheckAsync(coalesce) is LiteralType { Value: 3d } && host.Diagnostics.Contains(2871));
        Check(await host.ExpressionChecks.NullishnessAsync(coalesce) == 2);
        Check(BinaryExpressions.SideEffectFree(Binary(Number("1"), SyntaxKind.PlusToken, Number("2"))));
        Check(!BinaryExpressions.SideEffectFree(Binary(Number("1"), SyntaxKind.EqualsToken, Number("2"))));
        SyntaxNode deep = Number("1");
        for (int i = 0; i < 20_000; i++)
            deep = new BinaryExpressionNode { Left = deep, OperatorToken = new TokenNode(SyntaxKind.PlusToken), Right = Number("1") };
        Check(await host.Expressions.CheckAsync(deep) == context.NumberType);
        Check(host.Expressions.CurrentNode is null);
        Check(BinaryExpressions.SideEffectFree(deep));
        Check(await host.ExpressionChecks.NullishnessAsync(deep) == 2);
        Console.WriteLine($"{checks} binary/awaited/recursion/cancellation assertions; 20000-level binary expressions");
    }
}
