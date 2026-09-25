using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private void TypeOperatorGrammar(TypeOperatorNode node)
    {
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        if (node.Operator == K.ReadonlyKeyword)
        {
            if (node.Type is not (ArrayTypeNode or TupleTypeNode))
                ErrorOnFirstToken(node, 1354);
            return;
        }
        if (node.Operator != K.UniqueKeyword)
            return;
        if (node.Type?.Kind != K.SymbolKeyword)
        {
            Error(node.Type ?? node, 1005, "symbol");
            return;
        }
        var parent = node.Parent;
        while (parent is ParenthesizedTypeNode)
            parent = parent.Parent;
        switch (parent)
        {
            case VariableDeclarationNode declaration:
                if (declaration.Name is not IdentifierNode)
                    Error(node, 1333);
                else if (declaration.Parent is not VariableDeclarationListNode { Parent: VariableStatementNode })
                    Error(node, 1334);
                else if ((declaration.Parent.Flags & NodeFlags.Const) == 0)
                    Error(declaration.Name, 1332);
                break;
            case PropertyDeclarationNode property:
                if (!SemanticSyntax.IsStatic(property) || !SemanticSyntax.HasModifier(property, K.ReadonlyKeyword))
                    Error(property.Name!, 1331);
                break;
            case PropertySignatureDeclarationNode property:
                if (!SemanticSyntax.HasModifier(property, K.ReadonlyKeyword))
                    Error(property.Name!, 1330);
                break;
            default:
                Error(node, 1335);
                break;
        }
    }
}
