using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class DiagnosticNodePrinterTests
{
    internal static async Task<int> ComputedSafety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Computed symbol assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode("export class C {['é']=1; [0x10]=2;}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var file = program.GetFile("/project/main.ts")!.Syntax;
        var properties = file.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Select(n => checker.Symbols.Declaration(n)!).ToArray();
        var flags = SymbolFormatFlags.AllowAnyNodeKind | SymbolFormatFlags.WriteComputedProps;
        Check(await checker.GetSymbolDisplayNameAsync(properties[0], file, SymbolFlags.Value, flags) == "['é']");
        Check(await checker.GetSymbolDisplayNameAsync(properties[0], null, SymbolFlags.Value, flags) == "['\\u00E9']");
        Check(await checker.GetSymbolDisplayNameAsync(properties[1], file, SymbolFlags.Value, flags) == "[0x10]");
        Check(await checker.GetSymbolDisplayNameAsync(properties[1], null, SymbolFlags.Value, flags) == "[16]");
        Check(
            await checker.GetSymbolDisplayNameAsync(
                properties[0],
                file,
                SymbolFlags.Value,
                flags | SymbolFormatFlags.DoNotIncludeSymbolChain) == "['é']");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.GetSymbolDisplayNameAsync(properties[0], file, SymbolFlags.Value, flags, stop.Token);
            throw new InvalidOperationException("Canceled computed display completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(await checker.GetSymbolDisplayNameAsync(properties[0], file, SymbolFlags.Value, flags) == "['é']");
        return checks;
    }

    internal static int Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Diagnostic printer assertion {checks + 1}");
            checks++;
        }
        var source = Parser.ParseSourceFile(new("/project/main.ts"), new SourceText("class C { [0x10]:unknown; ['é']:unknown; }"));
        var names = source.DescendantsAndSelf().OfType<ComputedPropertyNameNode>().ToArray();
        var snapshot = source.DescendantsAndSelf().Select(n => (Node: n, n.Parent, n.Pos, n.End, n.Flags)).ToArray();
        Check(Checker.PrintDiagnosticNode(names[0], sourceFile: source) == "[0x10]");
        Check(Checker.PrintDiagnosticNode(names[0]) == "[16]");
        Check(Checker.PrintDiagnosticNode(names[1], sourceFile: source) == "['é']");
        Check(Checker.PrintDiagnosticNode(names[1]) == "['\\u00E9']");
        Check(Checker.PrintDiagnosticNode(names[1], true) == "['é']");
        Check(snapshot.All(p => p.Parent == p.Node.Parent && p.Pos == p.Node.Pos && p.End == p.Node.End && p.Flags == p.Node.Flags));
        var factory = new NodeFactory();
        SyntaxNode deep = factory.NewIdentifier("x");
        for (int i = 0; i < 20_000; i++)
            deep = factory.NewParenthesizedExpression(deep);
        Check(Checker.PrintDiagnosticNode(deep) == new string('(', 20_000) + "x" + new string(')', 20_000));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            Checker.PrintDiagnosticNode(deep, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled printer completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        Check(Checker.PrintDiagnosticNode(names[0], sourceFile: source) == "[0x10]");
        var failed = factory.NewToken(SyntaxKind.Unknown);
        try
        {
            Checker.PrintDiagnosticNode(failed);
            throw new InvalidOperationException("Unknown syntax printed successfully");
        }
        catch (NotSupportedException)
        {
            checks++;
        }
        Check(Checker.PrintDiagnosticNode(names[1], sourceFile: source) == "['é']");
        return checks;
    }
}
