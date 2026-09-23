using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Binding;

internal static class SemanticSyntax
{
    internal static SyntaxNode? Name(SyntaxNode? node) => (node as INamedNode)?.Name;

    internal static bool HasModifier(SyntaxNode node, K kind) =>
        node is IModifiedNode { Modifiers: { } list } && list.Any(n => n.Kind == kind);

    internal static bool ClassLike(SyntaxNode? node) => node?.Kind is K.ClassDeclaration or K.ClassExpression;

    internal static bool FunctionDeclarationLike(SyntaxNode? node) => node?.Kind is K.FunctionDeclaration or K.MethodDeclaration
            or K.Constructor or K.GetAccessor or K.SetAccessor or K.FunctionExpression or K.ArrowFunction;

    internal static bool ClassElement(SyntaxNode? node) => node?.Kind is K.Constructor or K.PropertyDeclaration or K.MethodDeclaration
            or K.GetAccessor or K.SetAccessor or K.IndexSignature or K.ClassStaticBlockDeclaration or K.SemicolonClassElement;

    internal static bool IsStatic(SyntaxNode node) =>
        ClassElement(node) && HasModifier(node, K.StaticKeyword) || node is ClassStaticBlockDeclarationNode;

    internal static bool TypeNode(SyntaxNode node) => node.Kind is >= K.FirstTypeNode and <= K.LastTypeNode
            or K.AnyKeyword or K.UnknownKeyword or K.NumberKeyword or K.BigIntKeyword or K.ObjectKeyword or K.BooleanKeyword
            or K.StringKeyword or K.SymbolKeyword or K.VoidKeyword or K.UndefinedKeyword or K.NeverKeyword or K.IntrinsicKeyword
            or K.ExpressionWithTypeArguments or K.JSDocAllType or K.JSDocNullableType or K.JSDocNonNullableType
            or K.JSDocOptionalType or K.JSDocVariadicType;

    internal static SyntaxNode? Body(SyntaxNode node) => node switch
    {
        FunctionDeclarationNode n => n.Body,
        FunctionExpressionNode n => n.Body,
        ArrowFunctionNode n => n.Body,
        MethodDeclarationNode n => n.Body,
        ConstructorDeclarationNode n => n.Body,
        GetAccessorDeclarationNode n => n.Body,
        SetAccessorDeclarationNode n => n.Body,
        ClassStaticBlockDeclarationNode n => n.Body,
        ModuleDeclarationNode n => n.Body,
        _ => null
    };

    internal static SourceFileNode? Source(SyntaxNode? node)
    {
        while (node is not null && node is not SourceFileNode)
            node = node.Parent;
        return node as SourceFileNode;
    }

    internal static SyntaxNode RootDeclaration(SyntaxNode node)
    {
        while (node is BindingElementNode)
            node = node.Parent?.Parent ?? throw new InvalidOperationException("Binding element has no declaration");
        return node;
    }

    internal static SyntaxNode? DeclarationContainer(SyntaxNode node)
    {
        node = RootDeclaration(node);
        while (node.Kind is K.VariableDeclaration or K.VariableDeclarationList or K.ImportSpecifier or K.NamedImports
            or K.NamespaceImport or K.ImportClause)
            node = node.Parent ?? throw new InvalidOperationException("Declaration has no container");
        return node.Parent;
    }

    internal static bool ConstAssertion(SyntaxNode node) => node.Kind is K.AsExpression or K.TypeAssertionExpression
        && node is ITypedNode { Type: TypeReferenceNode { TypeName: IdentifierNode { Text: "const" }, TypeArguments: null or { Count: 0 } } };

    internal static bool RequireCall(SyntaxNode? node) => node is CallExpressionNode
    { Expression: IdentifierNode { Text: "require" }, Arguments.Count: 1 };

    internal static bool ImmediatelyInvoked(SyntaxNode node)
    {
        if (node.Kind is not (K.FunctionExpression or K.ArrowFunction))
            return false;
        while (node.Parent is ParenthesizedExpressionNode parent)
            node = parent;
        return node.Parent is CallExpressionNode call && call.Expression == node;
    }

    internal static bool Generator(SyntaxNode node) => node is FunctionExpressionNode { AsteriskToken: not null }
        or FunctionDeclarationNode { AsteriskToken: not null } or MethodDeclarationNode { AsteriskToken: not null };

    internal static bool TypeOnly(SyntaxNode node) => node switch
    {
        ImportClauseNode n => n.PhaseModifier == K.TypeKeyword,
        ImportEqualsDeclarationNode n => n.IsTypeOnly,
        ImportSpecifierNode n => n.IsTypeOnly,
        ExportSpecifierNode n => n.IsTypeOnly,
        ExportDeclarationNode n => n.IsTypeOnly,
        _ => false
    };
}
