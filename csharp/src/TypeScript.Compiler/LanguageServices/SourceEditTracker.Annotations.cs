using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class SourceEditTracker
{
    internal async ValueTask InsertTypeAnnotationAsync(SyntaxNode node, SyntaxNode type)
    {
        SyntaxNode? end;
        if (node is IFunctionSignature signature)
            end = await SyntaxNavigation.FindChildOfKindAsync(node, K.CloseParenToken, File, cancellation)
                ?? (node is ArrowFunctionNode ? signature.Parameters?.FirstOrDefault() : null);
        else end = (node switch
        {
            VariableDeclarationNode declaration => declaration.ExclamationToken,
            PropertyDeclarationNode property => property.PostfixToken,
            PropertySignatureDeclarationNode property => property.PostfixToken,
            ParameterDeclarationNode parameter => parameter.QuestionToken,
            _ => null,
        }) ?? node.DeclarationName;
        if (end is not null) Insert(end.End, type, new(Prefix: ": "u8));
    }

    internal async ValueTask ParenthesizeArrowParametersAsync(ArrowFunctionNode arrow)
    {
        if (arrow.Parameters is not { Count: > 0 } parameters
            || await SyntaxNavigation.FindChildOfKindAsync(arrow, K.CloseParenToken, File, cancellation) is not null) return;
        InsertText(Start(parameters[0]), "("u8);
        InsertText(parameters[^1].End, ")"u8);
    }
}
