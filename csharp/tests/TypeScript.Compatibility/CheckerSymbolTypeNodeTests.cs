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
        options.SetRaw("noLib"u8, "true"u8);
        var program = await CompilerProgram.CreateAsync(new MemoryFileSystem(new Dictionary<Utf8String, byte[]>
        {
            ["/project/main.ts"u8] = Wtf8.Encode(
                "import type {Public as Alias} from './dep'; export namespace N {export class C {field=1;}}"),
            ["/project/dep.ts"u8] = Wtf8.Encode("export class Public {} class Hidden {} export class Other {}")
        }), "/project"u8, new("/project/tsconfig.json"u8, options, ["/project/main.ts"u8, "/project/dep.ts"u8], [], [], []));
        var checker = await program.CreateCheckerAsync();
        var source = program.GetFile("/project/main.ts"u8)!.Syntax;
        var dep = program.GetFile("/project/dep.ts"u8)!.Syntax;
        var visible = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Public"u8))!;
        var hidden = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Hidden"u8))!;
        var other = checker.Symbols.Declaration(
            dep.DescendantsAndSelf().OfType<ClassDeclarationNode>().Single(n => n.Name?.Text == "Other"u8))!;
        var member = checker.Symbols.Declaration(source.DescendantsAndSelf().OfType<PropertyDeclarationNode>().Single())!;
        Check(await checker.GetSymbolTypeReferenceAsync(visible, null, SymbolFlags.Type) == "Public"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(visible, source, SymbolFlags.Type) == "Alias"u8);
        var factory = new NodeFactory();
        SyntaxNode[] arguments = [factory.NewKeywordTypeNode(SyntaxKind.NumberKeyword)];
        Check(await checker.GetSymbolTypeReferenceAsync(visible, source, SymbolFlags.Type, arguments) == "Alias<number>"u8);
        Check(arguments[0].Parent is null && arguments[0].Pos == -1 && arguments[0].End == -1);
        Check(await checker.GetSymbolTypeReferenceAsync(hidden, source, SymbolFlags.Type) == "Hidden"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(hidden, source, SymbolFlags.Value) == "typeof Hidden"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(other, source, SymbolFlags.Type) == "import(\"./dep\").Other"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(other, source, SymbolFlags.Value) == "typeof import(\"./dep\").Other"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type) == "N.C[\"field\"]"u8);
        Check(await checker.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type, forbidIndexedAccess: true) == "N.C.field"u8);
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
        Check(await fresh.GetSymbolTypeReferenceAsync(member, source, SymbolFlags.Type) == "N.C[\"field\"]"u8);
        return checks;
    }
}
