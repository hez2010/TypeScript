using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ModuleTests
{
    public static void Run()
    {
        int assertions = 0;
        void Check(bool valid, Utf8String message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message.ToString());
        }
        SourceFileNode Parse(Utf8String text) => Parser.ParseSourceFile(new("module.ts"u8), new SourceText(text));
        var imports = Parse("import { type, type as, type as as, type as as as, type A as B, 'x' as y } from 'pkg';"u8);
        Check(imports.ParseDiagnostics.Count == 0, "Type/as import forms parse"u8);
        var importDeclaration = (ImportDeclarationNode)imports.Statements![0];
        var importSpecifiers = ((NamedImportsNode)importDeclaration.ImportClause!.NamedBindings!).Elements!
            .Cast<ImportSpecifierNode>().ToArray();
        Check(
            importSpecifiers.Select(s => s.IsTypeOnly).SequenceEqual([false, true, false, true, true, false]),
            "Type/as import erasure flags"u8);
        Check(importSpecifiers.Select(s => s.Name!.Text).SequenceEqual([Utf8String.Copy("type"u8), Utf8String.Copy("as"u8), Utf8String.Copy("as"u8), Utf8String.Copy("as"u8), Utf8String.Copy("B"u8), Utf8String.Copy("y"u8)]), "Type/as import local names"u8);
        Check(
            importSpecifiers[2].PropertyName is IdentifierNode { Text: { Span: var matchedText } } && matchedText.SequenceEqual("type"u8)
                && importSpecifiers[3].PropertyName is IdentifierNode { Text: { Span: var matchedText2 } } && matchedText2.SequenceEqual("as"u8),
            "Type/as import remote names"u8);
        Check(importSpecifiers[5].PropertyName is StringLiteralNode { Text: { Span: var matchedText3 } } && matchedText3.SequenceEqual("x"u8), "Arbitrary module export names imported by alias"u8);
        var exports = Parse("export { type, type as, type as as, type as as as, type A as 'B' };"u8);
        var exportSpecifiers = ((NamedExportsNode)((ExportDeclarationNode)exports.Statements![0]).ExportClause!).Elements!
            .Cast<ExportSpecifierNode>().ToArray();
        Check(
            exports.ParseDiagnostics.Count == 0
                && exportSpecifiers.Select(s => s.IsTypeOnly).SequenceEqual([false, true, false, true, true]),
            "Type/as export erasure flags"u8);
        Check(exportSpecifiers[^1].Name is StringLiteralNode { Text: { Span: var matchedText4 } } && matchedText4.SequenceEqual("B"u8), "Arbitrary exported alias"u8);
        foreach (var (text, phase, name) in new[]
        {
            ("import type from 'p';", SyntaxKind.Unknown, "type"),
            ("import type from from 'p';", SyntaxKind.TypeKeyword, "from"),
            ("import defer from 'p';", SyntaxKind.Unknown, "defer"),
            ("import defer from from 'p';", SyntaxKind.DeferKeyword, "from"),
        })
        {
            var file = Parse(Utf8String.FromString(text));
            var declaration = (ImportDeclarationNode)file.Statements![0];
            Check(
                file.ParseDiagnostics.Count == 0
                    && declaration.ImportClause!.PhaseModifier == phase
                    && declaration.ImportClause.Name!.Text == Utf8String.FromString(name),
                Utf8String.Copy("Import phase and default name: "u8) + Utf8String.FromString(text));
        }
        var equals = Parse("import type from = Names.Value;"u8);
        Check(
            equals.Statements![0] is ImportEqualsDeclarationNode { IsTypeOnly: true, Name.Text: { Span: var matchedText5 } } && matchedText5.SequenceEqual("from"u8),
            "Type import equals disambiguation"u8);
        foreach (Utf8String text in new Utf8String[]        {
            "import 'p'\n(function () {})();"u8,
            "import x from 'p'\n[1].map(f);"u8,
            "export { x } from 'p'\n(function () {})();"u8,
            "export * from 'p'\n[1].map(f);"u8
        })
        {
            var file = Parse(text);
            Check(
                file.ParseDiagnostics.Count == 0 && file.Statements!.Count == 2 && file.Statements[1] is ExpressionStatementNode,
                Utf8String.Copy("Module string does not consume next statement: "u8) + text);
        }
        Check(
            ((ExportDeclarationNode)Parse("export * as '😀' from 'p';"u8).Statements![0]).ExportClause is NamespaceExportNode { Name: StringLiteralNode { Text: { Span: var matchedText6 } } } && matchedText6.SequenceEqual("😀"u8),
            "String namespace export name"u8);
        Check(
            Parse("export default async function () {}"u8).Statements![0] is FunctionDeclarationNode { Modifiers: { } functionModifiers }
                && functionModifiers.Select(m => m.Kind).SequenceEqual(
                    [SyntaxKind.ExportKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.AsyncKeyword]),
            "Default async declaration retains modifiers"u8);
        Check(
            Parse("export default\n@dec\nclass C {}"u8).Statements![0] is ClassDeclarationNode { Modifiers: { } classModifiers }
                && classModifiers.Select(m => m.Kind).SequenceEqual(
                    [SyntaxKind.ExportKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.Decorator]),
            "Default decorated declaration across newline"u8);
        Check(
            Parse("declare export { x };"u8).Statements![0] is ExportDeclarationNode { Modifiers: { Count: 1 } },
            "Leading modifier survives export declaration"u8);
        var bad = Parse("import 10;"u8);
        Check(bad.ParseDiagnostics.Count > 0 && bad.Statements![0] is not ImportDeclarationNode, "Invalid bare numeric import is rejected"u8);
        foreach (SyntaxNode node in imports.DescendantsAndSelf())
            for (int i = 0; i < node.ChildCount; i++)
                Check(ReferenceEquals(node.GetChild(i).Parent, node), "Import tree parent ownership"u8);
        Console.WriteLine($"Module syntax: {assertions} assertions");
    }
}
