using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

internal static class DecoratorSyntax
{
    internal static NodeList? Members(SyntaxNode? node) => node switch
    {
        ClassDeclarationNode n => n.Members, ClassExpressionNode n => n.Members, _ => null
    };
    internal static SyntaxNode? Body(SyntaxNode node) => node switch
    {
        ConstructorDeclarationNode n => n.Body, MethodDeclarationNode n => n.Body,
        GetAccessorDeclarationNode n => n.Body, SetAccessorDeclarationNode n => n.Body,
        FunctionDeclarationNode n => n.Body, FunctionExpressionNode n => n.Body, ArrowFunctionNode n => n.Body, _ => null
    };
    internal static ConstructorDeclarationNode? Constructor(SyntaxNode node) => Members(node)?.OfType<ConstructorDeclarationNode>().FirstOrDefault(n => n.Body is not null);
    internal static bool HasDecorators(SyntaxNode? node) => node?.ModifierList?.Any(n => n is DecoratorNode) == true;
    internal static bool ThisParameter(SyntaxNode node) => node.DeclarationName is IdentifierNode { Text: var text } && text == "this"u8;
    internal static bool Static(SyntaxNode node) => SemanticSyntax.HasModifier(node, K.StaticKeyword);

    internal static bool CanDecorate(bool legacy, SyntaxNode node, SyntaxNode? parent, SyntaxNode? grandparent = null)
    {
        if (legacy && node.DeclarationName is PrivateIdentifierNode) return false;
        return node.Kind switch
        {
            K.ClassDeclaration => true,
            K.ClassExpression => !legacy,
            K.PropertyDeclaration => parent is not null && (legacy ? parent is ClassDeclarationNode
                : parent is ClassDeclarationNode or ClassExpressionNode && !SemanticSyntax.HasModifier(node, K.AbstractKeyword) && !SemanticSyntax.HasModifier(node, K.DeclareKeyword)),
            K.GetAccessor or K.SetAccessor or K.MethodDeclaration => parent is not null && Body(node) is not null
                && (legacy ? parent is ClassDeclarationNode : parent is ClassDeclarationNode or ClassExpressionNode),
            K.Parameter => legacy && parent is ConstructorDeclarationNode or MethodDeclarationNode or SetAccessorDeclarationNode
                && Body(parent) is not null && !ThisParameter(node) && grandparent is ClassDeclarationNode,
            _ => false
        };
    }
    internal static bool Decorated(bool legacy, SyntaxNode node, SyntaxNode? parent, SyntaxNode? grandparent = null) => HasDecorators(node) && CanDecorate(legacy, node, parent, grandparent);
    internal static bool ChildDecorated(bool legacy, SyntaxNode node, SyntaxNode? parent = null)
    {
        if (node is ClassDeclarationNode or ClassExpressionNode)
            return Members(node)?.Any(n => Decorated(legacy, n, node, parent) || ChildDecorated(legacy, n, node)) == true;
        if (node is ConstructorDeclarationNode or MethodDeclarationNode or SetAccessorDeclarationNode)
            return ((IFunctionSignature)node).Parameters?.Any(n => Decorated(legacy, n, node, parent)) == true;
        return false;
    }
    internal static bool ClassDecorated(bool legacy, SyntaxNode node) => Decorated(legacy, node, null)
        || Constructor(node) is { } constructor && ChildDecorated(legacy, constructor, node);

    internal static bool ElementDecorated(bool legacy, SyntaxNode node, SyntaxNode parent)
    {
        NodeList? parameters = null;
        if (node is GetAccessorDeclarationNode or SetAccessorDeclarationNode)
        {
            var accessors = Accessors(Members(parent), node);
            var first = HasDecorators(accessors.First) ? accessors.First : HasDecorators(accessors.Second) ? accessors.Second : null;
            if (node != first) return false;
            parameters = accessors.Setter?.Parameters;
        }
        else if (node is MethodDeclarationNode method) parameters = method.Parameters;
        return Decorated(legacy, node, parent) || parameters?.Any(n => !ThisParameter(n) && Decorated(legacy, n, node, parent)) == true;
    }

    internal readonly record struct AccessorGroup(SyntaxNode First, SyntaxNode? Second, GetAccessorDeclarationNode? Getter, SetAccessorDeclarationNode? Setter);
    internal static AccessorGroup Accessors(NodeList? members, SyntaxNode accessor)
    {
        var name = PropertyText(accessor.DeclarationName);
        var other = name is null ? null : members?.FirstOrDefault(n => n != accessor && n.Kind != accessor.Kind
            && n is GetAccessorDeclarationNode or SetAccessorDeclarationNode && Static(n) == Static(accessor) && PropertyText(n.DeclarationName) == name);
        bool otherFirst = other is not null && other.Pos < accessor.Pos;
        return new(otherFirst ? other! : accessor, otherFirst ? accessor : other,
            accessor as GetAccessorDeclarationNode ?? other as GetAccessorDeclarationNode,
            accessor as SetAccessorDeclarationNode ?? other as SetAccessorDeclarationNode);
    }
    internal static Utf8String? PropertyText(SyntaxNode? name)
    {
        if (name is ComputedPropertyNameNode computed)
        {
            name = computed.Expression;
            if (name is PrefixUnaryExpressionNode { Operand: NumericLiteralNode number } unary && unary.Operator is K.PlusToken or K.MinusToken)
                return unary.Operator == K.MinusToken ? Utf8String.Concat("-"u8, number.Text) : number.Text;
            if (name is not (StringLiteralNode or NumericLiteralNode) && name?.Kind != K.NoSubstitutionTemplateLiteral)
                return null;
        }
        return name switch
        {
            IdentifierNode id => id.Text, PrivateIdentifierNode id => id.Text,
            StringLiteralNode literal => literal.Text, NumericLiteralNode literal => literal.Text,
            NoSubstitutionTemplateLiteralNode literal => literal.Text,
            _ => null
        };
    }
}
