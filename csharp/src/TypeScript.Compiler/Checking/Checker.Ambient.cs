using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private async ValueTask CheckAmbientInitializerAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var flags = node.Flags | (node.Parent is VariableDeclarationListNode list ? list.Flags : 0);
        if ((flags & NodeFlags.Ambient) == 0 || node is not IInitializedNode { Initializer: { } initializer }
            || SemanticSyntax.Source(node)?.ParseDiagnostics.Count != 0)
            return;
        bool constant = SemanticSyntax.HasModifier(node, SyntaxKind.ReadonlyKeyword)
            || node is VariableDeclarationNode && (flags & NodeFlags.Constant) != 0;
        if (!constant || node is ITypedNode { Type: not null })
        {
            Error(initializer, DiagnosticCode.InitializersAreNotAllowedInAmbientContexts);
            return;
        }
        static bool SimpleLiteral(SyntaxNode expression) => expression is StringLiteralNode or NoSubstitutionTemplateLiteralNode
            or NumericLiteralNode
            || expression is PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: NumericLiteralNode };
        if (SimpleLiteral(initializer) || initializer is BigIntLiteralNode
            || initializer.Kind is SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword
            || initializer is PrefixUnaryExpressionNode { Operator: SyntaxKind.MinusToken, Operand: BigIntLiteralNode })
            return;
        bool enumReference = initializer is PropertyAccessExpressionNode
            || initializer is ElementAccessExpressionNode element && SimpleLiteral(element.ArgumentExpression!)
                && ConstantEvaluator.EntityName(element.Expression!);
        if (!enumReference || ((await CachedExpressionAsync(initializer, 0, cancellation)).Flags & TypeFlags.EnumLike) == 0)
            Error(initializer, DiagnosticCode.AConstInitializerInAnAmbientContextMustBeAStringOrNumericLiteralOrLiteralEnumReference);
    }
}
