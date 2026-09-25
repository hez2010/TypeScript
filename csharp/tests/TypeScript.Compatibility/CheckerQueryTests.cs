using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerQueryTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Checker location query assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        options.SetRaw("strict", "true");
        const string source = "const value=1; class C { field=1; } interface I { field:number; } "
            + "let input: string|number; if(typeof input === 'string'){ input; }";
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.ts"] = Wtf8.Encode(source) }), "/project",
            new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        var literal = file.DescendantsAndSelf().OfType<NumericLiteralNode>().First();
        var type = await checker.GetTypeAtLocationAsync(literal);
        Check(type is LiteralType { Value: 1d } && type == checker.Context.GetNumberLiteralType(1));
        var declared = await checker.GetTypeAtLocationAsync(literal.Parent!);
        Check(declared is LiteralType { Value: 1d } declarationLiteral && declarationLiteral.RegularType == type);
        Check(await checker.GetTypeAtLocationAsync(((VariableDeclarationNode)literal.Parent!).Name!) == declared);
        Check(await checker.GetTypeAtLocationAsync(file) == checker.Context.ErrorType);
        var declaration = file.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single();
        var classType = await checker.GetTypeAtLocationAsync(declaration);
        Check(classType is InterfaceType && await checker.GetTypeAtLocationAsync(declaration.Name!) == classType);
        var narrowed = file.DescendantsAndSelf().OfType<IdentifierNode>().Last(n => n.Text == "input");
        Check(await checker.GetTypeAtLocationAsync(narrowed) == checker.Context.StringType);
        var other = await program.CreateCheckerAsync();
        Check(await other.GetTypeAtLocationAsync(literal) != type);
        var repeated = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ => await checker.GetTypeAtLocationAsync(literal)));
        Check(repeated.All(t => t == type));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await checker.GetTypeAtLocationAsync(literal, cancelled.Token);
            throw new InvalidOperationException("Cancelled location query completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.GetTypeAtLocationAsync(literal) == type);
        try
        {
            await checker.GetTypeAtLocationAsync(new NumericLiteralNode { Text = "1" });
            throw new InvalidOperationException("Location query accepted foreign syntax");
        }
        catch (ArgumentException)
        {
            checks++;
        }
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        checker.BeforeExpressionFinish = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("Location query was not released");
        };
        var active = Task.Run(async () => await checker.GetTypeAtLocationAsync(literal));
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(30)));
            using var stop = new CancellationTokenSource();
            var queued = checker.GetTypeAtLocationAsync(literal, stop.Token);
            Check(!queued.IsCompleted);
            stop.Cancel();
            try
            {
                await queued;
                throw new InvalidOperationException("Queued location query ignored cancellation");
            }
            catch (OperationCanceledException)
            {
                checks++;
            }
        }
        finally
        {
            release.Set();
        }
        Check(await active == type);
        checker.BeforeExpressionFinish = null;
        Check(await checker.GetTypeAtLocationAsync(literal) == type);
        var leaf = new IdentifierNode { Text = "T" };
        SyntaxNode nested = leaf;
        for (int i = 0; i < 20_000; i++)
        {
            var parent = new QualifiedNameNode { Left = new IdentifierNode { Text = "N" }, Right = nested };
            nested.Parent = parent;
            nested = parent;
        }
        Check(!QuerySyntax.Expression(leaf.Parent!));
        var jsOptions = new CompilerOptions();
        jsOptions.SetRaw("noLib", "true");
        jsOptions.SetRaw("allowJs", "true");
        var jsProgram = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        { ["/project/main.js"] = Wtf8.Encode("/** @type {number} */ let value=1;") }), "/project",
            new("/project/tsconfig.json", jsOptions, ["/project/main.js"], [], [], []));
        var jsFile = jsProgram.GetFile("/project/main.js")!.Syntax;
        var owner = jsFile.DescendantsAndSelf().OfType<VariableStatementNode>().Single();
        var documentation = await jsFile.GetDocumentationAsync(owner);
        var original = documentation.SelectMany(d => d.DescendantsAndSelf()).Single(n => n.Kind == SyntaxKind.NumberKeyword);
        var reparsed = QuerySyntax.Reparsed(original);
        Check(reparsed != original && (reparsed.Flags & NodeFlags.Reparsed) != 0);
        var jsChecker = await jsProgram.CreateCheckerAsync();
        Check(await jsChecker.GetTypeAtLocationAsync(original) == jsChecker.Context.NumberType);
        Check(await jsChecker.GetTypeAtLocationAsync(reparsed) == jsChecker.Context.NumberType);
        return checks;
    }
}
