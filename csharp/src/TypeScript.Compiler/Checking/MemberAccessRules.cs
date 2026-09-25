using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IMemberAccessHost
{
    bool NoImplicitAny { get; }
    bool UseDefineForClassFields { get; }

    bool IsReadonly(Symbol symbol);

    ValueTask<SyntaxNode?> ConstructorPropertyAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask MemberErrorAsync(
        SyntaxNode node,
        int code,
        Symbol symbol,
        CancellationToken cancellation,
        Type? type = null,
        Type? enclosing = null);
}

internal sealed class MemberAccessRules(CheckerSymbols symbols, CheckerLinks links, ReferenceSymbols references,
    DeclaredTypes declared, TypeProperties properties, BaseTypes bases, DeclarationOrder order, IMemberAccessHost host)
{
    internal bool ReadonlyAssignment(SyntaxNode node, Symbol symbol, int assignment, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (assignment == 0)
            return false;
        var receiver = FlowReferences.Receiver(node);
        if (receiver is not null && SkipParentheses(receiver) is IdentifierNode identifier
            && (references.Resolve(identifier, cancellation).Flags & SymbolFlags.ModuleExports) != 0)
            return false;
        if (host.IsReadonly(symbol))
        {
            if ((symbol.Flags & SymbolFlags.Property) != 0 && receiver?.Kind == SyntaxKind.ThisKeyword)
            {
                var constructor = IdentifierTypes.Container(node);
                if (constructor is not ConstructorDeclarationNode)
                    return true;
                if (symbol.ValueDeclaration is { } declaration)
                    return !(constructor.Parent == declaration.Parent || constructor == declaration.Parent
                        || declaration is BinaryExpressionNode
                            && (symbol.Parent?.ValueDeclaration == constructor.Parent || symbol.Parent?.ValueDeclaration == constructor));
            }
            return true;
        }
        if (receiver is not null && SkipParentheses(receiver) is IdentifierNode imported)
        {
            var owner = references.Resolve(imported, cancellation);
            return (owner.Flags & SymbolFlags.Alias) != 0 && AliasResolver.Declaration(owner) is NamespaceImportNode;
        }
        return false;
    }

    internal async ValueTask<bool> AutoConstructorAsync(SyntaxNode node, Symbol property, CancellationToken cancellation = default)
    {
        var constructor = await host.ConstructorPropertyAsync(property, cancellation).ConfigureAwait(false);
        if (constructor is null && FlowReferences.Receiver(node)?.Kind == SyntaxKind.ThisKeyword && AutoTyped(property))
            foreach (var declaration in property.Declarations)
                if (MissingNamePrefixes.ThisContainer(declaration, false, false) is ConstructorDeclarationNode found)
                {
                    constructor = found;
                    break;
                }
        return MissingNamePrefixes.ThisContainer(node, true, false) == constructor;
    }

    internal bool AutoTyped(Symbol property) =>
        property.ValueDeclaration is PropertyDeclarationNode { Type: null, Initializer: null } && host.NoImplicitAny;

    internal async ValueTask BeforeDeclarationAsync(
        Symbol property,
        SyntaxNode node,
        SyntaxNode name,
        CancellationToken cancellation = default)
    {
        var declaration = property.ValueDeclaration;
        if (declaration is null || SemanticSyntax.Source(node)?.IsDeclarationFile == true)
            return;
        if (IdentifierTypes.PropertyInitializerOrStaticBlock(node, false)
            && !(declaration is PropertyDeclarationNode { PostfixToken.Kind: SyntaxKind.QuestionToken }
                && !SemanticSyntax.HasModifier(declaration, SyntaxKind.AccessorKeyword))
            && !(node is PropertyAccessExpressionNode or ElementAccessExpressionNode
                && FlowReferences.Receiver(node) is PropertyAccessExpressionNode or ElementAccessExpressionNode)
            && !await order.BeforeUseAsync(declaration, name, cancellation).ConfigureAwait(false)
            && !(declaration is MethodDeclarationNode && SemanticSyntax.IsStatic(declaration))
            && (host.UseDefineForClassFields || !await AncestorPropertyAsync(property, cancellation).ConfigureAwait(false)))
            await host.MemberErrorAsync(name, 2729, property, cancellation).ConfigureAwait(false);
        else if (declaration is ClassDeclarationNode && node.Parent is not TypeReferenceNode && (declaration.Flags & NodeFlags.Ambient) == 0
            && !await order.BeforeUseAsync(declaration, name, cancellation).ConfigureAwait(false))
            await host.MemberErrorAsync(name, 2449, property, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> AncestorPropertyAsync(Symbol property, CancellationToken cancellation)
    {
        if (property.Parent is not { } parent || (parent.Flags & SymbolFlags.Class) == 0)
            return false;
        var type = (InterfaceType)await declared.GetAsync(parent, cancellation).ConfigureAwait(false);
        var baseTypes = await bases.GetAsync(type, cancellation).ConfigureAwait(false);
        return baseTypes.Count != 0
            && await properties.PropertyAsync(
                baseTypes[0],
                property.Name,
                cancellation: cancellation).ConfigureAwait(false) is { ValueDeclaration: not null };
    }

    internal void MarkReferenced(
        Symbol property,
        SyntaxNode node,
        SyntaxNode receiver,
        Symbol? parent,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((property.Flags & SymbolFlags.ClassMember) == 0 || property.ValueDeclaration is not { } declaration
            || !(SemanticSyntax.HasModifier(declaration, SyntaxKind.PrivateKeyword)
                || SemanticSyntax.Name(declaration) is PrivateIdentifierNode))
            return;
        if (ReferenceSyntax.AccessKind(node) == 1 && (property.Flags & SymbolFlags.SetAccessor) == 0)
            return;
        bool self = receiver.Kind == SyntaxKind.ThisKeyword;
        if (!self && parent is not null && Semantics.ConstantEvaluator.EntityName(receiver))
        {
            var first = receiver;
            while (first is PropertyAccessExpressionNode access)
                first = access.Expression!;
            self = first is IdentifierNode identifier && references.Resolve(identifier, cancellation) == parent;
        }
        if (self && DeclarationOrder.Ancestor(node, SemanticSyntax.FunctionDeclarationLike) is { } function
            && symbols.Binding(function)?.Get(function)?.Symbol == property)
            return;
        var target = (property.CheckFlags & CheckFlags.Instantiated) != 0 ? links.Values.Get(property).Target! : property;
        symbols.MarkReferenced(target, SymbolFlags.All);
    }

    internal static SyntaxNode SkipParentheses(SyntaxNode node)
    {
        while (node is ParenthesizedExpressionNode parentheses)
            node = parentheses.Expression!;
        return node;
    }
}
