using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Syntax;
using static TypeScript.Compiler.Binding.SemanticSyntax;

namespace TypeScript.Compiler.Checking;

internal interface IDeclarationOrderHost
{
    ValueTask<bool> InitializedInStaticBlocksAsync(PropertyDeclarationNode declaration, SyntaxNode usage,
        SyntaxNode initializer, CancellationToken cancellation);
}

internal sealed class DeclarationOrder(CompilerOptions options, IDeclarationOrderHost host)
{
    internal bool StandardClassFields => options.Boolean("useDefineForClassFields") != false && options.EmitTargetYear >= 2022;
    private bool LegacyDecorators => options.Boolean("experimentalDecorators") == true;

    internal async ValueTask<bool> BeforeUseAsync(SyntaxNode declaration, SyntaxNode usage, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var container = BlockContainer(declaration);
        if (Source(declaration) != Source(usage) || (usage.Flags & NodeFlags.JSDoc) != 0 || InTypeQuery(usage) || AmbientOrType(usage))
            return true;
        if (declaration.Pos <= usage.Pos && !(declaration is PropertyDeclarationNode { Initializer: null } property
            && usage.Parent is PropertyAccessExpressionNode { Expression.Kind: SyntaxKind.ThisKeyword }
            && property.PostfixToken?.Kind != SyntaxKind.ExclamationToken))
        {
            switch (declaration)
            {
                case BindingElementNode:
                    var element = Ancestor(usage, n => n is BindingElementNode);
                    if (element is not null)
                        return Ancestor(element.Parent, n => n is BindingElementNode)
                            != Ancestor(declaration.Parent, n => n is BindingElementNode) || declaration.Pos < element.Pos;
                    return await BeforeUseAsync(Ancestor(declaration.Parent, n => n is VariableDeclarationNode)
                        ?? throw new InvalidOperationException("Binding element has no variable declaration"),
                        usage,
                        cancellation).ConfigureAwait(false);
                case VariableDeclarationNode:
                    return !ImmediatelyUsedInInitializer(declaration, usage, container);
                case ClassDeclarationNode or ClassExpressionNode:
                    var current = usage;
                    while (current is not null && current != declaration)
                    {
                        if (current is ComputedPropertyNameNode && current.Parent?.Parent == declaration
                            || !LegacyDecorators && current is DecoratorNode && (current.Parent == declaration
                                || current.Parent is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                                    or PropertyDeclarationNode
                                    && current.Parent.Parent == declaration
                                || current.Parent is ParameterDeclarationNode && current.Parent.Parent?.Parent == declaration))
                            break;
                        current = current.Parent;
                    }
                    if (current is null || current == declaration)
                        return true;
                    if (!LegacyDecorators && current is DecoratorNode)
                    {
                        var node = usage;
                        while (node is not null && node != current)
                        {
                            if (node is IFunctionSignature && !ImmediatelyInvoked(node))
                                break;
                            node = node.Parent;
                        }
                        return node is not null && node != current;
                    }
                    return false;
                case PropertyDeclarationNode:
                    return !PropertyImmediatelyReferenced(declaration, usage, false);
                case ParameterDeclarationNode when ParameterProperty(declaration):
                    return !(StandardClassFields && ContainingClass(declaration) == ContainingClass(usage)
                        && await DeferredAsync(usage, declaration, container, cancellation).ConfigureAwait(false));
            }
            return true;
        }
        if (usage.Parent is ExportSpecifierNode or ExportAssignmentNode { IsExportEquals: true }
            || usage is ExportAssignmentNode { IsExportEquals: true })
            return true;
        if (await DeferredAsync(usage, declaration, container, cancellation).ConfigureAwait(false))
            return !(StandardClassFields && ContainingClass(declaration) is not null
                && (declaration is PropertyDeclarationNode || ParameterProperty(declaration)))
                || !PropertyImmediatelyReferenced(declaration, usage, true);
        return false;
    }

    private async ValueTask<bool> DeferredAsync(
        SyntaxNode usage,
        SyntaxNode declaration,
        SyntaxNode? container,
        CancellationToken cancellation)
    {
        // Decorator relocation resumes at an outer ancestor, so traversal remains iterative.
        for (var current = usage; current is not null; current = current.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (current == container)
                return false;
            if (current is IFunctionSignature)
            {
                if (!ImmediatelyInvoked(current))
                    return true;
                continue;
            }
            if (current is ClassStaticBlockDeclarationNode)
            {
                if (declaration.Pos < usage.Pos)
                    return true;
                continue;
            }
            if (current.Parent is PropertyDeclarationNode property && property.Initializer == current)
            {
                if (IsStatic(property))
                {
                    if (declaration is MethodDeclarationNode)
                        return true;
                    if (declaration is PropertyDeclarationNode { Name: IdentifierNode or PrivateIdentifierNode } declaredProperty
                        && ContainingClass(usage) == ContainingClass(declaration)
                        && await host.InitializedInStaticBlocksAsync(declaredProperty, usage, current, cancellation).ConfigureAwait(false))
                        return true;
                }
                else if (declaration is not PropertyDeclarationNode || IsStatic(declaration)
                    || ContainingClass(usage) != ContainingClass(declaration))
                    return true;
            }
            if (current.Parent is DecoratorNode decorator && decorator.Expression == current)
            {
                var relocated = decorator.Parent switch
                {
                    ParameterDeclarationNode => decorator.Parent.Parent?.Parent,
                    MethodDeclarationNode => decorator.Parent.Parent,
                    _ => null
                };
                if (relocated is not null)
                {
                    // The reference restarts this search with the relocated usage position.
                    usage = relocated;
                    if (usage == container)
                        return false;
                    current = decorator.Parent is ParameterDeclarationNode ? decorator.Parent.Parent! : decorator.Parent!;
                }
            }
        }
        return false;
    }

    internal static bool InTypeQuery(SyntaxNode? node)
    {
        while (node is IdentifierNode or QualifiedNameNode)
            node = node.Parent;
        return node is TypeQueryNode;
    }

    internal static bool AmbientOrType(SyntaxNode node) => (node.Flags & NodeFlags.Ambient) != 0
        || Ancestor(node, n => n is InterfaceDeclarationNode or TypeAliasDeclarationNode or TypeLiteralNode) is not null;

    internal static SyntaxNode? Ancestor(SyntaxNode? node, Func<SyntaxNode, bool> predicate)
    {
        for (; node is not null; node = node.Parent)
            if (predicate(node))
                return node;
        return null;
    }

    internal static SyntaxNode? ContainingClass(SyntaxNode node) => Ancestor(node.Parent, ClassLike);

    internal static bool ParameterProperty(SyntaxNode node) => node is ParameterDeclarationNode { Parent: ConstructorDeclarationNode }
        && (HasModifier(node, SyntaxKind.PublicKeyword) || HasModifier(node, SyntaxKind.ProtectedKeyword)
            || HasModifier(node, SyntaxKind.PrivateKeyword) || HasModifier(node, SyntaxKind.ReadonlyKeyword)
            || HasModifier(node, SyntaxKind.OverrideKeyword));

    internal static bool BlockScoped(SyntaxNode node)
    {
        var root = RootDeclaration(node);
        return ((node.Flags | root.Flags | (root.Parent?.Flags ?? 0)) & NodeFlags.BlockScoped) != 0
            || root is VariableDeclarationNode { Parent: CatchClauseNode };
    }

    internal static SyntaxNode? BlockContainer(SyntaxNode node) => Ancestor(node.Parent, n => n.Kind is
        SyntaxKind.SourceFile or SyntaxKind.CaseBlock or SyntaxKind.CatchClause or SyntaxKind.ModuleDeclaration
        or SyntaxKind.ForStatement or SyntaxKind.ForInStatement or SyntaxKind.ForOfStatement or SyntaxKind.Constructor
        or SyntaxKind.MethodDeclaration or SyntaxKind.GetAccessor or SyntaxKind.SetAccessor or SyntaxKind.FunctionDeclaration
        or SyntaxKind.FunctionExpression or SyntaxKind.ArrowFunction or SyntaxKind.PropertyDeclaration or SyntaxKind.ClassStaticBlockDeclaration
        || n is BlockNode && n.Parent is not (IFunctionSignature or ClassStaticBlockDeclarationNode));

    private static bool ImmediatelyUsedInInitializer(SyntaxNode declaration, SyntaxNode usage, SyntaxNode? container)
    {
        var statement = declaration.Parent?.Parent;
        if (statement is VariableStatementNode or ForStatementNode || statement?.Kind == SyntaxKind.ForOfStatement)
            if (SameScopeDescendant(usage, declaration, container))
                return true;
        return statement is ForInOrOfStatementNode loop && SameScopeDescendant(usage, loop.Expression, container);
    }

    internal static bool SameScopeDescendant(SyntaxNode initial, SyntaxNode? parent, SyntaxNode? stop)
    {
        if (parent is null)
            return false;
        for (var node = initial; node is not null; node = node.Parent)
        {
            if (node == parent)
                return true;
            if (node == stop || node is IFunctionSignature
                && (!ImmediatelyInvoked(node) || Generator(node) || HasModifier(node, SyntaxKind.AsyncKeyword)))
                return false;
        }
        return false;
    }

    internal static bool PropertyImmediatelyReferenced(SyntaxNode declaration, SyntaxNode usage, bool stopAtAnyProperty)
    {
        if (usage.End > declaration.End)
            return false;
        for (var node = usage; node is not null && node != declaration; node = node.Parent)
        {
            if (node is ArrowFunctionNode)
                return false;
            if (node is PropertyDeclarationNode)
                return stopAtAnyProperty && (declaration is PropertyDeclarationNode && node.Parent == declaration.Parent
                    || ParameterProperty(declaration) && node.Parent == declaration.Parent?.Parent);
            if (node is BlockNode { Parent: MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode })
                return false;
        }
        return true;
    }
}
