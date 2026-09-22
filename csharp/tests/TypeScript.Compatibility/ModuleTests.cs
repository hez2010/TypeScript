using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compatibility;

internal static class ModuleTests
{
    public static void Run()
    {
        int assertions = 0;
        void Check(bool valid, string message)
        {
            assertions++;
            if (!valid)
                throw new InvalidDataException(message);
        }
        SourceFileNode Parse(string text) => Parser.ParseSourceFile(new("module.ts"), new SourceText(text));
        var imports = Parse("import { type, type as, type as as, type as as as, type A as B, 'x' as y } from 'pkg';");
        Check(imports.ParseDiagnostics.Count == 0, "Type/as import forms parse");
        var importDeclaration = (ImportDeclarationNode)imports.Statements![0];
        var importSpecifiers = ((NamedImportsNode)importDeclaration.ImportClause!.NamedBindings!).Elements!
            .Cast<ImportSpecifierNode>().ToArray();
        Check(
            importSpecifiers.Select(s => s.IsTypeOnly).SequenceEqual([false, true, false, true, true, false]),
            "Type/as import erasure flags");
        Check(importSpecifiers.Select(s => s.Name!.Text).SequenceEqual(["type", "as", "as", "as", "B", "y"]), "Type/as import local names");
        Check(
            importSpecifiers[2].PropertyName is IdentifierNode { Text: "type" }
                && importSpecifiers[3].PropertyName is IdentifierNode { Text: "as" },
            "Type/as import remote names");
        Check(importSpecifiers[5].PropertyName is StringLiteralNode { Text: "x" }, "Arbitrary module export names imported by alias");
        var exports = Parse("export { type, type as, type as as, type as as as, type A as 'B' };");
        var exportSpecifiers = ((NamedExportsNode)((ExportDeclarationNode)exports.Statements![0]).ExportClause!).Elements!
            .Cast<ExportSpecifierNode>().ToArray();
        Check(
            exports.ParseDiagnostics.Count == 0
                && exportSpecifiers.Select(s => s.IsTypeOnly).SequenceEqual([false, true, false, true, true]),
            "Type/as export erasure flags");
        Check(exportSpecifiers[^1].Name is StringLiteralNode { Text: "B" }, "Arbitrary exported alias");
        foreach (var (text, phase, name) in new[]
        {
            ("import type from 'p';", SyntaxKind.Unknown, "type"),
            ("import type from from 'p';", SyntaxKind.TypeKeyword, "from"),
            ("import defer from 'p';", SyntaxKind.Unknown, "defer"),
            ("import defer from from 'p';", SyntaxKind.DeferKeyword, "from"),
        })
        {
            var file = Parse(text);
            var declaration = (ImportDeclarationNode)file.Statements![0];
            Check(
                file.ParseDiagnostics.Count == 0
                    && declaration.ImportClause!.PhaseModifier == phase
                    && declaration.ImportClause.Name!.Text == name,
                "Import phase and default name: " + text);
        }
        var equals = Parse("import type from = Names.Value;");
        Check(
            equals.Statements![0] is ImportEqualsDeclarationNode { IsTypeOnly: true, Name.Text: "from" },
            "Type import equals disambiguation");
        foreach (string text in new[]
        {
            "import 'p'\n(function () {})();",
            "import x from 'p'\n[1].map(f);",
            "export { x } from 'p'\n(function () {})();",
            "export * from 'p'\n[1].map(f);"
        })
        {
            var file = Parse(text);
            Check(
                file.ParseDiagnostics.Count == 0 && file.Statements!.Count == 2 && file.Statements[1] is ExpressionStatementNode,
                "Module string does not consume next statement: " + text);
        }
        Check(
            ((ExportDeclarationNode)Parse("export * as '😀' from 'p';").Statements![0]).ExportClause
                is NamespaceExportNode { Name: StringLiteralNode { Text: "😀" } },
            "String namespace export name");
        Check(
            Parse("export default async function () {}").Statements![0] is FunctionDeclarationNode { Modifiers: { } functionModifiers }
                && functionModifiers.Select(m => m.Kind).SequenceEqual(
                    [SyntaxKind.ExportKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.AsyncKeyword]),
            "Default async declaration retains modifiers");
        Check(
            Parse("export default\n@dec\nclass C {}").Statements![0] is ClassDeclarationNode { Modifiers: { } classModifiers }
                && classModifiers.Select(m => m.Kind).SequenceEqual(
                    [SyntaxKind.ExportKeyword, SyntaxKind.DefaultKeyword, SyntaxKind.Decorator]),
            "Default decorated declaration across newline");
        Check(
            Parse("declare export { x };").Statements![0] is ExportDeclarationNode { Modifiers: { Count: 1 } },
            "Leading modifier survives export declaration");
        var bad = Parse("import 10;");
        Check(bad.ParseDiagnostics.Count > 0 && bad.Statements![0] is not ImportDeclarationNode, "Invalid bare numeric import is rejected");
        foreach (SyntaxNode node in imports.DescendantsAndSelf())
            for (int i = 0; i < node.ChildCount; i++)
                Check(ReferenceEquals(node.GetChild(i).Parent, node), "Import tree parent ownership");
        Console.WriteLine($"Module syntax: {assertions} assertions");
    }
}
