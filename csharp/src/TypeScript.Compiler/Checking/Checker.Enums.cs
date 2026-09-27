using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<Symbol> checkedEnums = [];

    private async ValueTask CheckEnumSourceAsync(EnumDeclarationNode node, CancellationToken cancellation)
    {
        CheckDeclarationName(node);
        if (ReservedTypeName(node.Name!.Text))
            Error(node.Name, DiagnosticCode.EnumNameCannotBe0, node.Name.Text);
        ExportedDeclaration(node, true);
        await CheckMergedExportsAsync(node, cancellation).ConfigureAwait(false);
        foreach (var member in node.Members!)
            await CheckSourceElementAsync(member, cancellation).ConfigureAwait(false);
        if (ErasableSyntaxOnly && (node.Flags & (NodeFlags.Ambient | NodeFlags.JavaScriptFile)) == 0)
            Error(node, DiagnosticCode.ThisSyntaxIsNotAllowedWhenErasableSyntaxOnlyIsEnabled);
        if (node.Members.Count != 0)
            await EnumValues.GetAsync((EnumMemberNode)node.Members[0], cancellation).ConfigureAwait(false);
        var symbol = program.Symbols.Declaration(node)!;
        if (!checkedEnums.Add(symbol))
            return;
        bool constant = SemanticSyntax.HasModifier(node, SyntaxKind.ConstKeyword), missing = false;
        foreach (var declaration in symbol.Declarations.OfType<EnumDeclarationNode>())
        {
            if (SemanticSyntax.HasModifier(declaration, SyntaxKind.ConstKeyword) != constant)
                Error(declaration.Name!, DiagnosticCode.EnumDeclarationsMustAllBeConstOrNonConst);
            if (declaration.Members!.FirstOrDefault() is EnumMemberNode { Initializer: null } first)
            {
                if (missing)
                    Error(
                        first.Name!,
                        DiagnosticCode.InAnEnumWithMultipleDeclarationsOnlyOneDeclarationCanOmitAnInitializerForItsFirstEnumElement);
                missing = true;
            }
        }
    }
}
