using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Parser
{
    private void ValidateJson(SyntaxNode root)
    {
        void NodeError(SyntaxNode node, DiagnosticMessage message)
        {
            var trivia = new Scanner(source);
            trivia.ResetPosition(node.Pos);
            trivia.Scan();
            int start = node.Pos == node.End ? node.Pos : trivia.TokenStart;
            diagnostics.Add(new(message, start, node.End - start, []));
        }
        var work = new Stack<(SyntaxNode Node, bool PropertyName)>();
        work.Push((root, false));
        while (work.TryPop(out var item))
        {
            var node = item.Node;
            if (item.PropertyName)
            {
                if (node is not StringLiteralNode text || (text.TokenFlags & TokenFlags.SingleQuote) != 0)
                    NodeError(node, Messages.String_literal_with_double_quotes_expected);
                continue;
            }
            switch (node)
            {
                case KeywordExpressionNode { Kind: K.TrueKeyword or K.FalseKeyword or K.NullKeyword }:
                case NumericLiteralNode:
                    break;
                case StringLiteralNode literal:
                    if ((literal.TokenFlags & TokenFlags.SingleQuote) != 0)
                        NodeError(node, Messages.String_literal_with_double_quotes_expected);
                    break;
                case PrefixUnaryExpressionNode { Operator: K.MinusToken, Operand: NumericLiteralNode }:
                    break;
                case ObjectLiteralExpressionNode { Properties: { } properties }:
                    for (int i = properties.Count - 1; i >= 0; i--)
                    {
                        if (properties[i] is not PropertyAssignmentNode property)
                        {
                            NodeError(properties[i], Messages.Property_assignment_expected);
                            continue;
                        }
                        if (property.Initializer is not null)
                            work.Push((property.Initializer, false));
                        if (property.Name is not null)
                            work.Push((property.Name, true));
                    }
                    break;
                case ArrayLiteralExpressionNode { Elements: { } elements }:
                    for (int i = elements.Count - 1; i >= 0; i--)
                        work.Push((elements[i], false));
                    break;
                default:
                    NodeError(
                        node,
                        Messages.Property_value_can_only_be_string_literal_numeric_literal_true_false_null_object_literal_or_array_literal);
                    break;
            }
        }
    }
}
