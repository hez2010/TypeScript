using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class CheckerSymbolTypeNodeTests
{
    internal static async Task<int> Safety()
    {
        int checks = 0;
        void Check(bool condition)
        {
            if (!condition)
                throw new InvalidOperationException($"Symbol type node assertion {checks + 1}");
            checks++;
        }
        var options = new CompilerOptions();
        options.SetRaw("noLib", "true");
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["/project/main.ts"] = Wtf8.Encode(
                "import type {Public as Alias} from './dep'; export namespace N {export class C {field=1;}}"),
            ["/project/dep.ts"] = Wtf8.Encode("export class Public {} class Hidden {} export class Other {}")
        }), "/project", new("/project/tsconfig.json", options, ["/project/main.ts", "/project/dep.ts"], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts")!.Syntax;
        var dep = program.GetFile("/project/dep.ts")!.Syntax;
        var visible = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Public"))!;
        var hidden = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Hidden"))!;
        var other = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Other"))!;
        var member = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Single())!;
        Check(await checker.GetSymbolTypeReferenceAsync(visible, null, SymbolFlags.Type) == "Public");
        Check(await checker.GetSymbolTypeReferenceAsync(visible, source, SymbolFlags.Type) == "Alias");
        var factory = new NodeFactory();
        SyntaxNode[] arguments = [factory.NewKeywordTypeNode(SyntaxKind.NumberKeyword)];
        Check(await checker.GetSymbolTypeReferenceAsync(visible, source, SymbolFlags.Type, arguments) == "Alias<number>");
        Check(arguments[0].Parent is null && arguments[0].Pos == -1 && arguments[0].End == -1);
        Check(await checker.GetSymbolTypeReferenceAsync(hidden, source, SymbolFlags.Type) == "Hidden");
        Check(await checker.GetSymbolTypeReferenceAsync(hidden, source, SymbolFlags.Value) == "typeof Hidden");
        Check(await checker.GetSymbolTypeReferenceAsync(other, source, SymbolFlags.Type) == "import(\"./dep\").Other");
        Check(await checker.GetSymbolTypeReferenceAsync(other, source, SymbolFlags.Value) == "typeof import(\"./dep\").Other");
        Check(await checker.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type) == "N.C[\"field\"]");
        Check(await checker.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type, forbidIndexedAccess: true) == "N.C.field");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        try
        {
            await checker.GetSymbolTypeReferenceAsync(visible, source, SymbolFlags.Type, cancellation: stop.Token);
            throw new InvalidOperationException("Canceled type node completed");
        }
        catch (OperationCanceledException)
        {
            checks++;
        }
        var fresh = await program.CreateCheckerAsync();
        var before = (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount);
        fresh.BeforeMemberTable = _ => throw new IOException("member failure");
        try
        {
            await fresh.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type);
            throw new InvalidOperationException("Member failure ignored");
        }
        catch (IOException error) when (error.Message == "member failure")
        {
            checks++;
        }
        Check(before == (fresh.AccessibleChainCacheCount, fresh.SymbolTableAliasCacheCount, fresh.SymbolContainerCacheCount));
        fresh.BeforeMemberTable = null;
        Check(await fresh.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type) == "N.C[\"field\"]");
        return checks;
    }
}
