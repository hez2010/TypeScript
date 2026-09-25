using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal static class QuerySyntax
{
    internal static bool RightSide(SyntaxNode node) => node.Parent is QualifiedNameNode qualified && qualified.Right == node
        || node.Parent is PropertyAccessExpressionNode access && access.Name == node
        || node.Parent is MetaPropertyNode meta && meta.Name == node;

    internal static bool TypeDeclaration(SyntaxNode node) => node.Kind is K.TypeParameter or K.ClassDeclaration
        or K.InterfaceDeclaration or K.TypeAliasDeclaration or K.JSTypeAliasDeclaration or K.EnumDeclaration
        || node is ImportClauseNode && SemanticSyntax.TypeOnly(node)
        || node is ImportSpecifierNode or ExportSpecifierNode && node.Parent?.Parent is { } owner && SemanticSyntax.TypeOnly(owner);

    internal static bool ImportOrExportAssignment(SyntaxNode node)
    {
        while (node.Parent is QualifiedNameNode parent)
            node = parent;
        return node.Parent is ImportEqualsDeclarationNode import && import.ModuleReference == node
            || node.Parent is ExportAssignmentNode export && export.Expression == node;
    }

    internal static bool PartOfType(SyntaxNode node)
    {
        if (node.Kind is >= K.FirstTypeNode and <= K.LastTypeNode)
            return true;
        return node.Kind switch
        {
            K.AnyKeyword or K.UnknownKeyword or K.NumberKeyword or K.BigIntKeyword or K.StringKeyword
                or K.BooleanKeyword or K.SymbolKeyword or K.ObjectKeyword or K.UndefinedKeyword or K.NullKeyword or K.NeverKeyword => true,
            K.VoidKeyword => node.Parent is not VoidExpressionNode,
            K.ExpressionWithTypeArguments => TypeHeritage(node),
            K.TypeParameter => node.Parent is MappedTypeNode or InferTypeNode,
            K.Identifier => TypeInParent(RightSide(node) ? node.Parent! : node),
            K.QualifiedName or K.PropertyAccessExpression or K.ThisKeyword => TypeInParent(node),
            _ => false
        };
    }

    private static bool TypeHeritage(SyntaxNode node) => node.Parent is HeritageClauseNode clause
        && (!SemanticSyntax.ClassLike(clause.Parent) || clause.Token == K.ImplementsKeyword)
        || node.Parent?.Kind is K.JSDocImplementsTag or K.JSDocAugmentsTag;

    private static bool TypeInParent(SyntaxNode node)
    {
        var parent = node.Parent;
        if (parent is TypeQueryNode)
            return false;
        if (parent is ImportTypeNode import)
            return !import.IsTypeOf;
        if (parent?.Kind is >= K.FirstTypeNode and <= K.LastTypeNode)
            return true;
        return parent switch
        {
            ExpressionWithTypeArgumentsNode => TypeHeritage(parent),
            TypeParameterDeclarationNode parameter => parameter.Constraint == node,
            VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                or FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or ConstructorDeclarationNode
                or MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                or CallSignatureDeclarationNode or ConstructSignatureDeclarationNode or IndexSignatureDeclarationNode or TypeAssertionNode
                => ((ITypedNode)parent).Type == node,
            CallExpressionNode call => call.TypeArguments?.Contains(node) == true,
            NewExpressionNode call => call.TypeArguments?.Contains(node) == true,
            TaggedTemplateExpressionNode tag => tag.TypeArguments?.Contains(node) == true,
            _ => false
        };
    }

    internal static bool Expression(SyntaxNode node)
    {
        while (true)
        {
            switch (node.Kind)
            {
                case K.SuperKeyword or K.NullKeyword or K.TrueKeyword or K.FalseKeyword or K.RegularExpressionLiteral
                    or K.ArrayLiteralExpression or K.ObjectLiteralExpression or K.PropertyAccessExpression or K.ElementAccessExpression
                    or K.CallExpression or K.NewExpression or K.TaggedTemplateExpression or K.AsExpression or K.TypeAssertionExpression
                    or K.SatisfiesExpression or K.NonNullExpression or K.ParenthesizedExpression or K.FunctionExpression
                    or K.ClassExpression or K.ArrowFunction or K.VoidExpression or K.DeleteExpression or K.TypeOfExpression
                    or K.PrefixUnaryExpression or K.PostfixUnaryExpression or K.BinaryExpression or K.ConditionalExpression
                    or K.SpreadElement or K.TemplateExpression or K.OmittedExpression or K.JsxElement or K.JsxSelfClosingElement
                    or K.JsxFragment or K.YieldExpression or K.AwaitExpression:
                    return true;
                case K.MetaProperty:
                    return node.Parent is not CallExpressionNode { Expression: MetaPropertyNode { KeywordToken: K.ImportKeyword, Name.Text: "defer" } };
                case K.ExpressionWithTypeArguments:
                    return node.Parent is not HeritageClauseNode;
                case K.QualifiedName:
                    while (node.Parent is QualifiedNameNode qualified)
                        node = qualified;
                    return NameExpression(node);
                case K.PrivateIdentifier:
                    return node.Parent is BinaryExpressionNode binary && binary.Left == node && binary.OperatorToken?.Kind == K.InKeyword;
                case K.Identifier:
                    if (NameExpression(node))
                        return true;
                    break;
                case K.NumericLiteral or K.BigIntLiteral or K.StringLiteral or K.NoSubstitutionTemplateLiteral or K.ThisKeyword:
                    break;
                default:
                    return false;
            }
            var parent = node.Parent;
            switch (parent)
            {
                case VariableDeclarationNode or ParameterDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                    or EnumMemberNode or PropertyAssignmentNode or BindingElementNode:
                    return parent is IInitializedNode initialized && initialized.Initializer == node;
                case ExpressionStatementNode n:
                    return n.Expression == node;
                case IfStatementNode n:
                    return n.Expression == node;
                case DoStatementNode n:
                    return n.Expression == node;
                case WhileStatementNode n:
                    return n.Expression == node;
                case ReturnStatementNode n:
                    return n.Expression == node;
                case WithStatementNode n:
                    return n.Expression == node;
                case SwitchStatementNode n:
                    return n.Expression == node;
                case CaseOrDefaultClauseNode n:
                    return n.Expression == node;
                case ThrowStatementNode n:
                    return n.Expression == node;
                case TypeAssertionNode n:
                    return n.Expression == node;
                case AsExpressionNode n:
                    return n.Expression == node;
                case TemplateSpanNode n:
                    return n.Expression == node;
                case ComputedPropertyNameNode n:
                    return n.Expression == node;
                case SatisfiesExpressionNode n:
                    return n.Expression == node;
                case ForStatementNode n:
                    return n.Initializer == node && node is not VariableDeclarationListNode
                    || n.Condition == node || n.Incrementor == node;
                case ForInOrOfStatementNode n:
                    return n.Initializer == node && node is not VariableDeclarationListNode || n.Expression == node;
                case DecoratorNode or JsxExpressionNode or JsxSpreadAttributeNode or SpreadAssignmentNode:
                    return true;
                case ExpressionWithTypeArgumentsNode n:
                    return n.Expression == node && !PartOfType(n);
                case ShorthandPropertyAssignmentNode n:
                    return n.ObjectAssignmentInitializer == node;
                case null:
                    return false;
                default:
                    node = parent;
                    break;
            }
        }
    }

    private static bool NameExpression(SyntaxNode node) => node.Parent?.Kind is K.TypeQuery or K.JSDocLink
        or K.JSDocLinkCode or K.JSDocLinkPlain or K.JSDocNameReference
        || node.Parent is JsxOpeningElementNode opening && opening.TagName == node
        || node.Parent is JsxClosingElementNode closing && closing.TagName == node
        || node.Parent is JsxSelfClosingElementNode element && element.TagName == node;

    internal static SyntaxNode Reparsed(SyntaxNode node)
    {
        if ((node.Flags & (NodeFlags.JSDoc | NodeFlags.Reparsed)) != NodeFlags.JSDoc
            || SemanticSyntax.Source(node)?.ReparsedClones is not { Count: > 0 } clones)
            return node;
        int low = 0, high = clones.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            var candidate = clones[middle];
            if (candidate.Pos < node.Pos || candidate.Pos == node.Pos && candidate.End < node.End)
                low = middle + 1;
            else
                high = middle;
        }
        if (low == clones.Count || clones[low].Pos != node.Pos || clones[low].End != node.End)
            low = Math.Max(0, low - 1);
        SyntaxNode? current = clones[low];
        while (current is not null && current.Pos <= node.Pos && current.End >= node.End)
        {
            if (current.Kind == node.Kind && current.Pos == node.Pos && current.End == node.End)
                return current;
            SyntaxNode? child = null;
            for (int i = 0; i < current.ChildCount; i++)
                if (current.GetChild(i) is { } next && next.Pos <= node.Pos && next.End >= node.End)
                {
                    child = next;
                    break;
                }
            current = child;
        }
        return node;
    }
}
