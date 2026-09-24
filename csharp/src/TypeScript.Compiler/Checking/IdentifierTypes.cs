using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IIdentifierTypeHost
{
    Type AutoArray { get; }
    bool NoImplicitAny { get; }

    bool IsReadonly(Symbol symbol);

    bool ContextualBindingPattern(SyntaxNode pattern);

    ValueTask<Type> ThisExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask MarkIdentifierAsync(IdentifierNode node, CancellationToken cancellation);

    ValueTask CheckDeprecatedAsync(IdentifierNode node, Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> NarrowedSymbolAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation);

    ValueTask<Type> NarrowableReferenceAsync(Type type, SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> DeclarationInitializerAsync(SyntaxNode declaration, CheckMode mode, CancellationToken cancellation);

    void IdentifierError(SyntaxNode node, int code, Symbol symbol, Type? type = null);

    void CircularInitializer(Symbol symbol);
}

internal sealed class IdentifierTypes(TypeContext context, CheckerLinks links, CheckerSymbols symbols, ReferenceSymbols references,
    AliasResolver aliases, SymbolTypes values, TypeAlgebra algebra, TypeWidening widening, TypeFactQueries facts,
    TypeResolutionStack resolutions, AssignmentMarks assignments, FlowTypes flows, IIdentifierTypeHost host)
{
    internal async ValueTask<Type> CheckAsync(IdentifierNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (FlowReferences.ThisInQuery(node))
            return await host.ThisExpressionAsync(node, cancellation).ConfigureAwait(false);
        var symbol = references.Resolve(node, cancellation);
        if (symbol == symbols.UnknownSymbol)
            return context.ErrorType;
        if (symbol == symbols.ArgumentsSymbol)
        {
            if (PropertyInitializerOrStaticBlock(node, true))
            {
                host.IdentifierError(node, 2815, symbol);
                return context.ErrorType;
            }
            return await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
        }
        if (MarkAlias(node))
            await host.MarkIdentifierAsync(node, cancellation).ConfigureAwait(false);
        var exported = symbols.ExportedValue(symbol)!;
        var target = await aliases.WithDeprecationAsync(exported, node, cancellation).ConfigureAwait(false);
        if (target.Declarations.Count != 0)
            await host.CheckDeprecatedAsync(node, target, cancellation).ConfigureAwait(false);
        var declaration = exported.ValueDeclaration;
        var immediateDeclaration = declaration;
        if (declaration is BindingElementNode && host.ContextualBindingPattern(declaration.Parent!)
            && DeclarationOrder.Ancestor(node, p => p == declaration.Parent) is not null)
            return context.NonInferrableAnyType;
        var type = await host.NarrowedSymbolAsync(exported, node, cancellation).ConfigureAwait(false);
        int assignmentKind = ReferenceSyntax.AssignmentKind(node);
        if (assignmentKind != 0)
        {
            if ((exported.Flags & SymbolFlags.Variable) == 0
                && !((node.Flags & NodeFlags.JavaScriptFile) != 0 && (exported.Flags & SymbolFlags.ValueModule) != 0))
            {
                int code = (exported.Flags & SymbolFlags.Enum) != 0 ? 2628 : (exported.Flags & SymbolFlags.Class) != 0 ? 2629
                    : (exported.Flags & SymbolFlags.Module) != 0 ? 2631 : (exported.Flags & SymbolFlags.Function) != 0 ? 2630
                    : (exported.Flags & SymbolFlags.Alias) != 0 ? 2632 : 2539;
                host.IdentifierError(node, code, symbol);
                return context.ErrorType;
            }
            if (host.IsReadonly(exported))
            {
                host.IdentifierError(node, (exported.Flags & SymbolFlags.Variable) != 0 ? 2588 : 2540, symbol);
                return context.ErrorType;
            }
        }
        bool alias = (exported.Flags & SymbolFlags.Alias) != 0;
        if ((exported.Flags & SymbolFlags.Variable) != 0)
        {
            if (assignmentKind == 1)
                return FlowTypes.CompoundLike(node) ? await widening.LiteralBaseAsync(type, cancellation).ConfigureAwait(false) : type;
        }
        else if (alias)
            declaration = AliasResolver.Declaration(symbol);
        else
            return type;
        if (declaration is null)
            return type;
        type = await host.NarrowableReferenceAsync(type, node, mode, cancellation).ConfigureAwait(false);
        bool parameter = SemanticSyntax.RootDeclaration(declaration) is ParameterDeclarationNode;
        var declarationContainer = Container(declaration);
        var flowContainer = Container(node);
        bool outer = flowContainer != declarationContainer;
        bool spreadTarget = node.Parent is SpreadAssignmentNode && node.Parent.Parent is { } pattern && DestructuringTarget(pattern);
        bool automatic = type == context.AutoType || type == host.AutoArray;
        bool automaticNonNull = automatic && node.Parent is NonNullExpressionNode;
        while (flowContainer != declarationContainer && ClosedFunction(flowContainer)
            && (AssignmentMarks.Constant(exported) && type != host.AutoArray
                || assignments.ParameterOrMutableLocal(exported)
                    && await assignments.PastLastAsync(exported, node, cancellation).ConfigureAwait(false)))
            flowContainer = Container(flowContainer!);
        bool neverInitialized = immediateDeclaration is VariableDeclarationNode { Initializer: null, ExclamationToken: null } variable
            && variable.Parent?.Parent is not ForInOrOfStatementNode && assignments.MutableLocal(variable)
            && !await assignments.DefiniteAsync(symbol, cancellation).ConfigureAwait(false);
        bool assumeInitialized = parameter || alias || outer && !neverInitialized || spreadTarget
            || (symbol.Flags & SymbolFlags.ModuleExports) != 0 || SameBinding(node, declaration)
            || !automatic && (!context.StrictNullChecks || (type.Flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void)) != 0
                || DeclarationOrder.InTypeQuery(node) || DeclarationOrder.AmbientOrType(node) || node.Parent is ExportSpecifierNode)
            || node.Parent is NonNullExpressionNode || declaration is VariableDeclarationNode { ExclamationToken: not null }
            || (declaration.Flags & NodeFlags.Ambient) != 0;
        Type initial;
        if (automaticNonNull)
            initial = context.UndefinedType;
        else if (assumeInitialized && parameter)
            initial = await RemoveOptionalityAsync(type, declaration, cancellation).ConfigureAwait(false);
        else if (assumeInitialized)
            initial = type;
        else if (automatic)
            initial = context.UndefinedType;
        else
            initial = await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
        var flowType = await flows.GetAsync(node, type, initial, flowContainer, cancellation: cancellation).ConfigureAwait(false);
        if (automaticNonNull)
            flowType = await facts.NonNullableAsync(flowType, cancellation).ConfigureAwait(false);
        if (!await flows.EvolvingTargetAsync(node, cancellation).ConfigureAwait(false) && automatic)
        {
            if (flowType == context.AutoType || flowType == host.AutoArray)
            {
                if (host.NoImplicitAny)
                {
                    host.IdentifierError(SemanticSyntax.Name(declaration)!, 7034, symbol, flowType);
                    host.IdentifierError(node, 7005, symbol, flowType);
                }
                return flows.ConvertAuto(flowType);
            }
        }
        else if (!assumeInitialized && !ContainsUndefined(type) && ContainsUndefined(flowType))
        {
            host.IdentifierError(node, 2454, symbol);
            return type;
        }
        return assignmentKind != 0 ? await widening.LiteralBaseAsync(flowType, cancellation).ConfigureAwait(false) : flowType;
    }

    internal async ValueTask<Type> RemoveOptionalityAsync(Type type, SyntaxNode declaration, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        return context.StrictNullChecks && declaration is ParameterDeclarationNode { Initializer: not null }
            && await facts.GetAsync(type, TypeFacts.IsUndefined, cancellation).ConfigureAwait(false) != 0
            && !await InitializerUndefinedAsync(declaration, cancellation).ConfigureAwait(false)
                ? await facts.FilterAsync(type, TypeFacts.NEUndefined, cancellation).ConfigureAwait(false) : type;
    }

    private async ValueTask<bool> InitializerUndefinedAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        var data = links.Nodes.Get(declaration);
        if ((data.Flags & NodeCheckFlags.InitializerIsUndefinedComputed) == 0)
        {
            if (!resolutions.Push(declaration, TypeSystemPropertyName.InitializerIsUndefined))
            {
                host.CircularInitializer(symbols.Declaration(declaration)!);
                return true;
            }
            bool active = true;
            try
            {
                bool undefined = await facts.GetAsync(
                    await host.DeclarationInitializerAsync(declaration, 0, cancellation).ConfigureAwait(false),
                    TypeFacts.IsUndefined, cancellation).ConfigureAwait(false) != 0;
                bool resolved = resolutions.Pop();
                active = false;
                if (!resolved)
                {
                    host.CircularInitializer(symbols.Declaration(declaration)!);
                    return true;
                }
                cancellation.ThrowIfCancellationRequested();
                if ((data.Flags & NodeCheckFlags.InitializerIsUndefinedComputed) == 0)
                    data.Flags |= NodeCheckFlags.InitializerIsUndefinedComputed | (undefined ? NodeCheckFlags.InitializerIsUndefined : 0);
            }
            finally
            {
                if (active)
                    resolutions.Pop();
            }
        }
        return (data.Flags & NodeCheckFlags.InitializerIsUndefined) != 0;
    }

    internal static SyntaxNode? Container(SyntaxNode node) => DeclarationOrder.Ancestor(node.Parent,
        n => n is IFunctionSignature && !SemanticSyntax.ImmediatelyInvoked(n)
            || n is ModuleBlockNode or SourceFileNode or PropertyDeclarationNode);

    internal static bool DestructuringTarget(SyntaxNode node) => node.Parent is BinaryExpressionNode binary && binary.Left == node
        || node.Parent is ForInOrOfStatementNode { Kind: SyntaxKind.ForOfStatement } loop && loop.Initializer == node;

    private static bool ClosedFunction(SyntaxNode? node) => node is FunctionExpressionNode or ArrowFunctionNode
        || node is MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
            && node.Parent is ObjectLiteralExpressionNode or ClassExpressionNode;

    private static bool ContainsUndefined(Type type) =>
        ((type is UnionType union ? union.Types[0] : type).Flags & TypeFlags.Undefined) != 0;

    private static bool SameBinding(SyntaxNode node, SyntaxNode declaration)
        => declaration is BindingElementNode && DeclarationOrder.Ancestor(node, n => n is BindingElementNode) is { } binding
            && SemanticSyntax.RootDeclaration(binding) == SemanticSyntax.RootDeclaration(declaration);

    internal static bool PropertyInitializerOrStaticBlock(SyntaxNode node, bool ignoreArrows)
    {
        for (var current = node; current is not null; current = current.Parent)
            switch (current)
            {
                case PropertyDeclarationNode or ClassStaticBlockDeclarationNode:
                    return true;
                case TypeQueryNode or JsxClosingElementNode:
                    return false;
                case ArrowFunctionNode when !ignoreArrows:
                    return false;
                case BlockNode { Parent: not ArrowFunctionNode } when SemanticSyntax.FunctionDeclarationLike(current.Parent):
                    return false;
            }
        return false;
    }

    private static bool MarkAlias(IdentifierNode node)
        => !(node.Parent is PropertyAccessExpressionNode property && property.Expression == node)
            && node.Parent is not ExportSpecifierNode { IsTypeOnly: true }
            && node.Parent?.Parent?.Parent is not ExportDeclarationNode { IsTypeOnly: true };
}
