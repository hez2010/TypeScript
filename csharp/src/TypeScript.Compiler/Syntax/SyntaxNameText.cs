using System.Text;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Syntax;

internal static class SyntaxNameText
{
    public static string Get(SyntaxNode? node, bool propertyAccess = true)
    {
        if (node is IdentifierNode identifier)
            return identifier.Text;
        if (node is PrivateIdentifierNode privateIdentifier)
            return privateIdentifier.Text;
        if (node is null)
            return "";
        var text = new StringBuilder();
        var parts = new Stack<SyntaxNode?>();
        parts.Push(node);
        while (parts.TryPop(out SyntaxNode? part))
        {
            switch (part)
            {
                case null:
                    text.Append('.');
                    break;
                case IdentifierNode name:
                    text.Append(name.Text);
                    break;
                case PrivateIdentifierNode name:
                    text.Append(name.Text);
                    break;
                case QualifiedNameNode name:
                    if (name.Right is not null)
                        parts.Push(name.Right);
                    parts.Push(null);
                    if (name.Left is not null)
                        parts.Push(name.Left);
                    break;
                case PropertyAccessExpressionNode name when propertyAccess:
                    if (name.Name is not null)
                        parts.Push(name.Name);
                    parts.Push(null);
                    if (name.Expression is not null)
                        parts.Push(name.Expression);
                    break;
            }
        }
        return text.ToString();
    }
}
