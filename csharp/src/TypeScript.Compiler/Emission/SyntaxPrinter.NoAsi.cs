using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private SyntaxNode? ProtectFromAsi(SyntaxNode? node)
    {
        if (node is null || commentsDisabled)
            return node;
        var ancestors = new Stack<SyntaxNode>();
        var current = node;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is PartiallyEmittedExpressionNode partial && WillEmitLeadingNewLine(partial))
            {
                var original = context.ParseNode(partial);
                var parentheses = context.Factory.NewParenthesizedExpression(original is ParenthesizedExpressionNode ? partial.Expression : partial);
                if (original is ParenthesizedExpressionNode)
                {
                    context.SetOriginal(parentheses, partial);
                    parentheses.Pos = original.Pos;
                    parentheses.End = original.End;
                }
                current = parentheses;
                break;
            }
            var child = current switch
            {
                PartiallyEmittedExpressionNode n => n.Expression,
                PropertyAccessExpressionNode n => n.Expression,
                ElementAccessExpressionNode n => n.Expression,
                CallExpressionNode n => n.Expression,
                TaggedTemplateExpressionNode n => n.Tag,
                PostfixUnaryExpressionNode n => n.Operand,
                BinaryExpressionNode n => n.Left,
                ConditionalExpressionNode n => n.Condition,
                AsExpressionNode n => n.Expression,
                SatisfiesExpressionNode n => n.Expression,
                NonNullExpressionNode n => n.Expression,
                _ => null
            };
            if (child is null)
                return node;
            ancestors.Push(current);
            current = child;
        }
        while (ancestors.TryPop(out var ancestor))
        {
            var result = context.Clone(ancestor);
            switch (result)
            {
                case PartiallyEmittedExpressionNode n: n.Expression = current; break;
                case PropertyAccessExpressionNode n: n.Expression = current; break;
                case ElementAccessExpressionNode n: n.Expression = current; break;
                case CallExpressionNode n: n.Expression = current; break;
                case TaggedTemplateExpressionNode n: n.Tag = current; break;
                case PostfixUnaryExpressionNode n: n.Operand = current; break;
                case BinaryExpressionNode n: n.Left = current; break;
                case ConditionalExpressionNode n: n.Condition = current; break;
                case AsExpressionNode n: n.Expression = current; break;
                case SatisfiesExpressionNode n: n.Expression = current; break;
                case NonNullExpressionNode n: n.Expression = current; break;
            }
            current = result;
        }
        return current;
    }

    private bool WillEmitLeadingNewLine(SyntaxNode node)
    {
        if (sourceFile is null)
            return false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var comments = CommentRanges(sourceFile.Source.Text, node.Pos, trailing: false);
            if (comments.Any(c => c.Kind == SyntaxKind.SingleLineCommentTrivia || c.HasTrailingNewLine)
                || comments.Count != 0 && context.ParseNode(node)?.Parent is ParenthesizedExpressionNode
                || context.LeadingComments(node).Any(c => c.Kind == SyntaxKind.SingleLineCommentTrivia || c.HasTrailingNewLine))
                return true;
            if (node is not PartiallyEmittedExpressionNode { Expression: { } expression })
                return false;
            if (node.Pos != expression.Pos && CommentRanges(sourceFile.Source.Text, expression.Pos, trailing: true)
                .Any(c => c.Kind == SyntaxKind.SingleLineCommentTrivia || c.HasTrailingNewLine))
                return true;
            node = expression;
        }
    }
}
