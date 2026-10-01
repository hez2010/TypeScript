using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal enum ExpressionTransform { LogicalAssignment, NullishCoalescing, OptionalCatch, Exponentiation }

internal sealed class ExpressionLowering(EmitContext context, ExpressionTransform transform, CancellationToken cancellation = default)
    : SyntaxRewriter(context, cancellation)
{
    private NodeFactory F => Context.Factory;

    protected override async ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node)
    {
        if (transform == ExpressionTransform.OptionalCatch && node is CatchClauseNode { VariableDeclaration: null } clause)
            return F.NewCatchClause(F.NewVariableDeclaration(Context.NewTempVariable(), null, null, null), (BlockNode?)await VisitAsync(clause.Block));
        if (node is BinaryExpressionNode binary)
            switch (transform)
            {
                case ExpressionTransform.LogicalAssignment when binary.OperatorToken!.Kind is K.BarBarEqualsToken or K.AmpersandAmpersandEqualsToken or K.QuestionQuestionEqualsToken:
                    return await LogicalAssignmentAsync(binary);
                case ExpressionTransform.NullishCoalescing when binary.OperatorToken!.Kind == K.QuestionQuestionToken:
                    var left = (await VisitAsync(binary.Left))!;
                    var value = left;
                    if (!TransformSyntax.SimpleCopiable(left))
                    {
                        value = Temp();
                        left = Assignment(value, left);
                    }
                    return Context.Conditional(Context.NullCondition(left, value, false), value, (await VisitAsync(binary.Right))!);
                case ExpressionTransform.Exponentiation when binary.OperatorToken!.Kind is K.AsteriskAsteriskToken or K.AsteriskAsteriskEqualsToken:
                    return await ExponentiationAsync(binary);
            }
        return await VisitEachChildAsync(node);
    }

    private async ValueTask<SyntaxNode> ExponentiationAsync(BinaryExpressionNode node)
    {
        var left = (await VisitAsync(node.Left))!;
        var right = (await VisitAsync(node.Right))!;
        SyntaxNode target = left, value = left;
        if (node.OperatorToken!.Kind == K.AsteriskAsteriskEqualsToken)
            switch (left)
            {
                case ElementAccessExpressionNode access:
                    var receiver = Temp();
                    var argument = Temp();
                    target = F.NewElementAccessExpression(EmitContext.CopyRange(Assignment(receiver, access.Expression!), access.Expression!), null,
                        EmitContext.CopyRange(Assignment(argument, access.ArgumentExpression!), access.ArgumentExpression!), NodeFlags.None);
                    value = EmitContext.CopyRange(F.NewElementAccessExpression(receiver, null, argument, NodeFlags.None), access);
                    break;
                case PropertyAccessExpressionNode access:
                    var propertyReceiver = Temp();
                    target = EmitContext.CopyRange(F.NewPropertyAccessExpression(EmitContext.CopyRange(Assignment(propertyReceiver, access.Expression!), access.Expression!), null, access.Name, NodeFlags.None), access);
                    value = EmitContext.CopyRange(F.NewPropertyAccessExpression(propertyReceiver, null, access.Name, NodeFlags.None), access);
                    break;
            }
        var power = EmitContext.CopyRange(Context.MethodCall(F.NewIdentifier("Math"u8), "pow"u8, [value, right]), node);
        return node.OperatorToken.Kind == K.AsteriskAsteriskToken ? power : EmitContext.CopyRange(Assignment(target, power), node);
    }

    private async ValueTask<SyntaxNode> LogicalAssignmentAsync(BinaryExpressionNode node)
    {
        var left = TransformSyntax.SkipParentheses((await VisitAsync(node.Left))!);
        var target = left;
        var right = TransformSyntax.SkipParentheses((await VisitAsync(node.Right))!);
        if (left is PropertyAccessExpressionNode or ElementAccessExpressionNode)
        {
            var receiver = TransformSyntax.Expression(left)!;
            var capture = receiver;
            if (!TransformSyntax.SimpleCopiable(receiver))
            {
                receiver = Temp();
                capture = Assignment(receiver, TransformSyntax.Expression(left)!);
            }
            if (left is PropertyAccessExpressionNode property)
            {
                target = F.NewPropertyAccessExpression(receiver, null, property.Name, NodeFlags.None);
                left = F.NewPropertyAccessExpression(capture, null, property.Name, NodeFlags.None);
            }
            else
            {
                var element = (ElementAccessExpressionNode)left;
                var argument = element.ArgumentExpression!;
                var capturedArgument = argument;
                if (!TransformSyntax.SimpleCopiable(argument))
                {
                    argument = Temp();
                    capturedArgument = Assignment(argument, element.ArgumentExpression!);
                }
                target = F.NewElementAccessExpression(receiver, null, argument, NodeFlags.None);
                left = F.NewElementAccessExpression(capture, null, capturedArgument, NodeFlags.None);
            }
        }
        var operation = node.OperatorToken!.Kind switch
        {
            K.BarBarEqualsToken => K.BarBarToken, K.AmpersandAmpersandEqualsToken => K.AmpersandAmpersandToken, _ => K.QuestionQuestionToken
        };
        return Context.Binary(left, operation, F.NewParenthesizedExpression(Assignment(target, right)));
    }

    private IdentifierNode Temp()
    {
        var name = Context.NewTempVariable();
        Context.AddVariableDeclaration(name);
        return name;
    }
    private SyntaxNode Assignment(SyntaxNode left, SyntaxNode right) => Context.Binary(left, K.EqualsToken, right);
}
