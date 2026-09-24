using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal static class ReferenceSyntax
{
    internal static int AccessKind(SyntaxNode node)
    {
        bool reverse = false;
        int access;
        while (true)
        {
            switch (node.Parent)
            {
                case ParenthesizedExpressionNode or ArrayLiteralExpressionNode:
                    node = node.Parent;
                    continue;
                case PrefixUnaryExpressionNode prefix:
                    access = prefix.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken ? 2 : 0;
                    break;
                case PostfixUnaryExpressionNode postfix:
                    access = postfix.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken ? 2 : 0;
                    break;
                case BinaryExpressionNode binary:
                    access = binary.Left == node && BinaryExpressions.Assignment(binary.OperatorToken!.Kind)
                        ? binary.OperatorToken.Kind == SyntaxKind.EqualsToken ? 1 : 2 : 0;
                    break;
                case PropertyAccessExpressionNode property when property.Name == node:
                    node = property;
                    continue;
                case PropertyAssignmentNode property:
                    reverse ^= property.Name == node;
                    node = property.Parent!;
                    continue;
                case ShorthandPropertyAssignmentNode property when property.ObjectAssignmentInitializer != node:
                    node = property.Parent!;
                    continue;
                case ForInOrOfStatementNode loop:
                    access = loop.Initializer == node ? 1 : 0;
                    break;
                default:
                    access = 0;
                    break;
            }
            return reverse && access != 2 ? 1 - access : access;
        }
    }

    internal static SyntaxNode? AssignmentTarget(SyntaxNode node)
    {
        while (true)
        {
            switch (node.Parent)
            {
                case BinaryExpressionNode binary:
                    return binary.Left == node && BinaryExpressions.Assignment(binary.OperatorToken!.Kind) ? binary : null;
                case PrefixUnaryExpressionNode prefix:
                    return prefix.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken ? prefix : null;
                case PostfixUnaryExpressionNode postfix:
                    return postfix.Operator is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken ? postfix : null;
                case ForInOrOfStatementNode loop:
                    return loop.Initializer == node ? loop : null;
                case ParenthesizedExpressionNode or ArrayLiteralExpressionNode or SpreadElementNode or NonNullExpressionNode:
                    node = node.Parent;
                    continue;
                case SpreadAssignmentNode:
                    node = node.Parent.Parent!;
                    continue;
                case ShorthandPropertyAssignmentNode property when property.Name == node:
                    node = property.Parent!;
                    continue;
                case PropertyAssignmentNode property when property.Name != node:
                    node = property.Parent!;
                    continue;
                default:
                    return null;
            }
        }
    }

    internal static int AssignmentKind(SyntaxNode node) => AssignmentTarget(node) switch
    {
        BinaryExpressionNode binary => binary.OperatorToken!.Kind is SyntaxKind.EqualsToken or SyntaxKind.BarBarEqualsToken
            or SyntaxKind.AmpersandAmpersandEqualsToken or SyntaxKind.QuestionQuestionEqualsToken ? 1 : 2,
        PrefixUnaryExpressionNode or PostfixUnaryExpressionNode => 2,
        ForInOrOfStatementNode => 1,
        _ => 0
    };

    internal static bool ValidTypeOnlyUse(SyntaxNode node)
    {
        if ((node.Flags & (NodeFlags.Ambient | NodeFlags.JSDoc)) != 0 || DeclarationOrder.InTypeQuery(node))
            return true;
        if (node is IdentifierNode)
        {
            var parent = node.Parent;
            while (parent is PropertyAccessExpressionNode or ExpressionWithTypeArgumentsNode)
                parent = parent.Parent;
            if (parent is HeritageClauseNode heritage && (heritage.Token == SyntaxKind.ImplementsKeyword
                || heritage.Parent is InterfaceDeclarationNode))
                return true;
        }
        var current = node;
        while (current is IdentifierNode or PropertyAccessExpressionNode)
            current = current.Parent!;
        if (current is ComputedPropertyNameNode && (SemanticSyntax.HasModifier(current.Parent!, SyntaxKind.AbstractKeyword)
            || current.Parent?.Parent is InterfaceDeclarationNode or TypeLiteralNode))
            return true;
        return !IsExpression(node)
            && !(node is IdentifierNode && node.Parent is ShorthandPropertyAssignmentNode shorthand && shorthand.Name == node);
    }

    internal static bool IsExpression(SyntaxNode node)
    {
        while (true)
        {
            switch (node.Kind)
            {
                case SyntaxKind.SuperKeyword or SyntaxKind.NullKeyword or SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword
                    or SyntaxKind.RegularExpressionLiteral or SyntaxKind.ArrayLiteralExpression or SyntaxKind.ObjectLiteralExpression
                    or SyntaxKind.PropertyAccessExpression or SyntaxKind.ElementAccessExpression or SyntaxKind.CallExpression or SyntaxKind.NewExpression
                    or SyntaxKind.TaggedTemplateExpression or SyntaxKind.AsExpression or SyntaxKind.TypeAssertionExpression
                    or SyntaxKind.SatisfiesExpression or SyntaxKind.NonNullExpression or SyntaxKind.ParenthesizedExpression
                    or SyntaxKind.FunctionExpression or SyntaxKind.ClassExpression or SyntaxKind.ArrowFunction or SyntaxKind.VoidExpression
                    or SyntaxKind.DeleteExpression or SyntaxKind.TypeOfExpression or SyntaxKind.PrefixUnaryExpression or SyntaxKind.PostfixUnaryExpression
                    or SyntaxKind.BinaryExpression or SyntaxKind.ConditionalExpression or SyntaxKind.SpreadElement or SyntaxKind.TemplateExpression
                    or SyntaxKind.OmittedExpression or SyntaxKind.JsxElement or SyntaxKind.JsxSelfClosingElement or SyntaxKind.JsxFragment
                    or SyntaxKind.YieldExpression or SyntaxKind.AwaitExpression:
                    return true;
                case SyntaxKind.MetaProperty:
                    return node.Parent is not CallExpressionNode { Expression: MetaPropertyNode meta }
                        || meta != node || meta.KeywordToken != SyntaxKind.ImportKeyword || meta.Name is not IdentifierNode { Text: "defer" };
                case SyntaxKind.ExpressionWithTypeArguments:
                    return node.Parent is not HeritageClauseNode;
                case SyntaxKind.QualifiedName:
                    while (node.Parent is QualifiedNameNode)
                        node = node.Parent;
                    return NameExpression(node);
                case SyntaxKind.PrivateIdentifier:
                    return node.Parent is BinaryExpressionNode binary
                        && binary.Left == node
                        && binary.OperatorToken?.Kind == SyntaxKind.InKeyword;
                case SyntaxKind.Identifier:
                    if (NameExpression(node))
                        return true;
                    break;
                case SyntaxKind.NumericLiteral or SyntaxKind.BigIntLiteral or SyntaxKind.StringLiteral
                    or SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.ThisKeyword:
                    break;
                default:
                    return false;
            }
            var parent = node.Parent;
            if (parent is null)
                return false;
            switch (parent.Kind)
            {
                case SyntaxKind.VariableDeclaration or SyntaxKind.Parameter or SyntaxKind.PropertyDeclaration
                    or SyntaxKind.PropertySignature
                    or SyntaxKind.EnumMember or SyntaxKind.PropertyAssignment or SyntaxKind.BindingElement:
                    return parent is IInitializedNode initialized && initialized.Initializer == node;
                case SyntaxKind.ExpressionStatement or SyntaxKind.IfStatement or SyntaxKind.DoStatement or SyntaxKind.WhileStatement
                    or SyntaxKind.ReturnStatement or SyntaxKind.WithStatement or SyntaxKind.SwitchStatement or SyntaxKind.CaseClause
                    or SyntaxKind.DefaultClause or SyntaxKind.ThrowStatement or SyntaxKind.TypeAssertionExpression or SyntaxKind.AsExpression
                    or SyntaxKind.TemplateSpan or SyntaxKind.ComputedPropertyName or SyntaxKind.SatisfiesExpression:
                    return Expression(parent) == node;
                case SyntaxKind.ForStatement:
                    var loop = (ForStatementNode)parent;
                    return loop.Initializer == node && node is not VariableDeclarationListNode
                        || loop.Condition == node
                        || loop.Incrementor == node;
                case SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement:
                    var iteration = (ForInOrOfStatementNode)parent;
                    return iteration.Initializer == node && node is not VariableDeclarationListNode || iteration.Expression == node;
                case SyntaxKind.Decorator or SyntaxKind.JsxExpression or SyntaxKind.JsxSpreadAttribute or SyntaxKind.SpreadAssignment:
                    return true;
                case SyntaxKind.ExpressionWithTypeArguments:
                    return ((ExpressionWithTypeArgumentsNode)parent).Expression == node
                        && !(parent.Parent is HeritageClauseNode clause
                            && (!SemanticSyntax.ClassLike(clause.Parent) || clause.Token == SyntaxKind.ImplementsKeyword)
                            || parent.Parent?.Kind is SyntaxKind.JSDocImplementsTag or SyntaxKind.JSDocAugmentsTag);
                case SyntaxKind.ShorthandPropertyAssignment:
                    return ((ShorthandPropertyAssignmentNode)parent).ObjectAssignmentInitializer == node;
                default:
                    node = parent;
                    break;
            }
        }
    }

    private static bool NameExpression(SyntaxNode node) => node.Parent?.Kind is SyntaxKind.TypeQuery or SyntaxKind.JSDocLink
        or SyntaxKind.JSDocLinkCode or SyntaxKind.JSDocLinkPlain or SyntaxKind.JSDocNameReference
        || node.Parent switch
        {
            JsxOpeningElementNode parent => parent.TagName == node,
            JsxClosingElementNode parent => parent.TagName == node,
            JsxSelfClosingElementNode parent => parent.TagName == node,
            _ => false
        };

    private static SyntaxNode? Expression(SyntaxNode node) => node switch
    {
        ExpressionStatementNode n => n.Expression,
        IfStatementNode n => n.Expression,
        DoStatementNode n => n.Expression,
        WhileStatementNode n => n.Expression,
        ReturnStatementNode n => n.Expression,
        WithStatementNode n => n.Expression,
        SwitchStatementNode n => n.Expression,
        CaseOrDefaultClauseNode n => n.Expression,
        ThrowStatementNode n => n.Expression,
        TypeAssertionNode n => n.Expression,
        AsExpressionNode n => n.Expression,
        TemplateSpanNode n => n.Expression,
        ComputedPropertyNameNode n => n.Expression,
        SatisfiesExpressionNode n => n.Expression,
        _ => null
    };
}
