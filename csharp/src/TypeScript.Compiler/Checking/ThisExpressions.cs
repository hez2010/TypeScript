using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IThisExpressionHost
{
    bool NoImplicitThis { get; }
    bool LegacyDecorators { get; }
    int TargetYear { get; }

    FlowNode? FlowOf(SyntaxNode node);

    ValueTask<Signature?> FullSignatureAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> ContextualThisAsync(SyntaxNode node, CancellationToken cancellation);

    void ThisError(SyntaxNode node, DiagnosticCode code, SyntaxNode? related = null);
}

internal sealed class ThisExpressions(TypeContext context, CheckerLinks links, CheckerSymbols symbols, SymbolTypes values,
    DeclaredTypes declared, Signatures signatures, SignatureParameters parameters, ClassBases classes, BaseTypes bases,
    FlowTypes flows, IThisExpressionHost host)
{
    internal async ValueTask<Type> ThisAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var container = MissingNamePrefixes.ThisContainer(node, true, true);
        bool captured = false, computed = false;
        if (container is ConstructorDeclarationNode)
            await BeforeSuperAsync(
                node,
                container,
                DiagnosticCode.XSuperMustBeCalledBeforeAccessingThisInTheConstructorOfADerivedClass,
                cancellation).ConfigureAwait(false);
        while (true)
        {
            if (container is ArrowFunctionNode)
            {
                container = MissingNamePrefixes.ThisContainer(container, false, !computed);
                captured = true;
            }
            if (container is ComputedPropertyNameNode)
            {
                container = MissingNamePrefixes.ThisContainer(container, !captured, false);
                computed = true;
                continue;
            }
            break;
        }
        if (container is PropertyDeclarationNode { Initializer: { } initializer }
            && SemanticSyntax.IsStatic(container)
            && host.LegacyDecorators
            && initializer.Pos <= node.Pos && node.Pos <= initializer.End
            && container.Parent is IModifiedNode { Modifiers: { } modifiers } && modifiers.Any(m => m is DecoratorNode))
            host.ThisError(node, DiagnosticCode.CannotUseThisInAStaticPropertyInitializerOfADecoratedClass);
        if (computed)
            host.ThisError(node, DiagnosticCode.XThisCannotBeReferencedInAComputedPropertyName);
        else if (container is ModuleDeclarationNode)
            host.ThisError(node, DiagnosticCode.XThisCannotBeReferencedInAModuleOrNamespaceBody);
        else if (container is EnumDeclarationNode)
            host.ThisError(node, DiagnosticCode.XThisCannotBeReferencedInCurrentLocation);
        var type = await AtAsync(node, true, container, cancellation).ConfigureAwait(false);
        if (host.NoImplicitThis)
        {
            var global = await values.GetAsync(symbols.GlobalThisSymbol, cancellation).ConfigureAwait(false);
            if (type == global && captured)
                host.ThisError(node, DiagnosticCode.TheContainingArrowFunctionCapturesTheGlobalValueOfThis);
            else if (type is null)
            {
                SyntaxNode? related = null;
                if (container is not SourceFileNode
                    && await AtAsync(container, cancellation: cancellation).ConfigureAwait(false) is { } outer
                    && outer != global)
                    related = container;
                host.ThisError(node, DiagnosticCode.XThisImplicitlyHasTypeAnyBecauseItDoesNotHaveATypeAnnotation, related);
            }
        }
        return type ?? context.AnyType;
    }

    internal async ValueTask<Type?> AtAsync(
        SyntaxNode node,
        bool includeGlobal = true,
        SyntaxNode? container = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        container ??= MissingNamePrefixes.ThisContainer(node, false, false);
        if (container is IFunctionSignature signatureNode && (!ParameterInitializer(node) || ThisParameter(signatureNode) is not null))
        {
            var signature = await host.FullSignatureAsync(container, cancellation).ConfigureAwait(false)
                ?? await signatures.FromDeclarationAsync(container, cancellation).ConfigureAwait(false);
            var type = await parameters.ThisAsync(signature, cancellation).ConfigureAwait(false)
                ?? await host.ContextualThisAsync(container, cancellation).ConfigureAwait(false);
            if (type is not null)
                return await flows.GetAsync(node, type, cancellation: cancellation).ConfigureAwait(false);
        }
        if (SemanticSyntax.ClassLike(container.Parent))
        {
            var symbol = symbols.Declaration(container.Parent!)!;
            var type = SemanticSyntax.IsStatic(container) ? await values.GetAsync(symbol, cancellation).ConfigureAwait(false)
                : ((InterfaceType)await declared.GetAsync(symbol, cancellation).ConfigureAwait(false)).ThisType!;
            return await flows.GetAsync(node, type, cancellation: cancellation).ConfigureAwait(false);
        }
        if (container is SourceFileNode file)
        {
            if (file.ExternalModuleIndicator is not null)
                return context.UndefinedType;
            if (includeGlobal)
                return await values.GetAsync(symbols.GlobalThisSymbol, cancellation).ConfigureAwait(false);
        }
        return null;
    }

    internal async ValueTask<Type> SuperAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        bool call = node.Parent is CallExpressionNode invocation && invocation.Expression == node;
        var immediate = SuperContainer(node);
        var container = immediate;
        if (!call)
            while (container is ArrowFunctionNode)
                container = SuperContainer(container);
        bool legal = call ? container is ConstructorDeclarationNode
            : container is not null && (SemanticSyntax.ClassLike(container.Parent) || container.Parent is ObjectLiteralExpressionNode)
                && (SemanticSyntax.IsStatic(container)
                    ? container is MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode
                        or SetAccessorDeclarationNode or PropertyDeclarationNode or ClassStaticBlockDeclarationNode
                    : container is MethodDeclarationNode or MethodSignatureDeclarationNode or GetAccessorDeclarationNode
                        or SetAccessorDeclarationNode or PropertyDeclarationNode or PropertySignatureDeclarationNode
                        or ConstructorDeclarationNode);
        if (!legal)
        {
            bool computed = false;
            for (var current = node; current is not null && current != container; current = current.Parent)
                if (current is ComputedPropertyNameNode)
                {
                    computed = true;
                    break;
                }
            host.ThisError(
                node,
                computed ? DiagnosticCode.XSuperCannotBeReferencedInAComputedPropertyName : call ? DiagnosticCode.SuperCallsAreNotPermittedOutsideConstructorsOrInNestedFunctionsInsideConstructors : container?.Parent is not { } parent
                || !(SemanticSyntax.ClassLike(parent)
                    || parent is ObjectLiteralExpressionNode) ? DiagnosticCode.XSuperCanOnlyBeReferencedInMembersOfDerivedClassesOrObjectLiteralExpressions : DiagnosticCode.XSuperPropertyAccessIsPermittedOnlyInAConstructorMemberFunctionOrMemberAccessorOfADerivedClass);
            return context.ErrorType;
        }
        if (!call && immediate is ConstructorDeclarationNode)
            await BeforeSuperAsync(
                node,
                container!,
                DiagnosticCode.XSuperMustBeCalledBeforeAccessingAPropertyOfSuperInTheConstructorOfADerivedClass,
                cancellation).ConfigureAwait(false);
        if (container!.Parent is ObjectLiteralExpressionNode)
            return context.AnyType;
        var classType = (InterfaceType)await declared.GetAsync(symbols.Declaration(container.Parent!)!, cancellation).ConfigureAwait(false);
        if (ClassBases.BaseNode(classType) is null)
        {
            host.ThisError(node, DiagnosticCode.XSuperCanOnlyBeReferencedInADerivedClass);
            return context.ErrorType;
        }
        if (await classes.ConstructorAsync(classType, cancellation).ConfigureAwait(false) == context.NullWideningType)
            return call ? context.ErrorType : context.NullWideningType;
        var baseTypes = await bases.GetAsync(classType, cancellation).ConfigureAwait(false);
        if (baseTypes.Count == 0)
            return context.ErrorType;
        if (container is ConstructorDeclarationNode)
            for (var current = node; current is not null && !SemanticSyntax.FunctionDeclarationLike(current); current = current.Parent)
                if (current is ParameterDeclarationNode && current.Parent == container)
                {
                    host.ThisError(node, DiagnosticCode.XSuperCannotBeReferencedInConstructorArguments);
                    return context.ErrorType;
                }
        if (SemanticSyntax.IsStatic(container) || call)
        {
            if (!call && host.TargetYear <= 2021 && container is PropertyDeclarationNode or ClassStaticBlockDeclarationNode)
                for (var current = DeclarationOrder.BlockContainer(node.Parent!); current is not null; current = DeclarationOrder.BlockContainer(current))
                    if (current is not SourceFileNode || symbols.Binding(current)?.IsModule == true)
                        links.Nodes.Get(current).Flags |= NodeCheckFlags.ContainsSuperPropertyInStaticInitializer;
            return await classes.ConstructorAsync(classType, cancellation).ConfigureAwait(false);
        }
        return await bases.WithThisAsync(baseTypes[0], classType.ThisType, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask BeforeSuperAsync(SyntaxNode node, SyntaxNode container, DiagnosticCode code, CancellationToken cancellation)
    {
        var type = (InterfaceType)await declared.GetAsync(symbols.Declaration(container.Parent!)!, cancellation).ConfigureAwait(false);
        if (ClassBases.BaseNode(type) is not null
            && await classes.ConstructorAsync(type, cancellation).ConfigureAwait(false) != context.NullWideningType
            && host.FlowOf(node) is { } flow && !await flows.Reachability.PostSuperAsync(
                flow,
                cancellation: cancellation).ConfigureAwait(false))
            host.ThisError(node, code);
    }

    private static ParameterDeclarationNode? ThisParameter(IFunctionSignature function)
        =>
            function.Parameters is { Count: > 0 } parameters
                && parameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text.Span: var matchedText } } parameter && matchedText.SequenceEqual("this"u8)
                ? parameter
                : null;

    internal static bool ParameterInitializer(SyntaxNode node)
    {
        bool binding = false;
        while (node.Parent is not null && node.Parent is not IFunctionSignature)
        {
            if (node.Parent is ParameterDeclarationNode parameter && (binding || parameter.Initializer == node))
                return true;
            if (node.Parent is BindingElementNode element && element.Initializer == node)
                binding = true;
            node = node.Parent;
        }
        return false;
    }

    private static SyntaxNode? SuperContainer(SyntaxNode node)
    {
        while (node.Parent is { } parent)
        {
            node = parent;
            switch (node)
            {
                case ComputedPropertyNameNode:
                    node = node.Parent!;
                    break;
                case FunctionDeclarationNode or FunctionExpressionNode or ArrowFunctionNode or PropertyDeclarationNode
                    or PropertySignatureDeclarationNode
                    or MethodDeclarationNode or MethodSignatureDeclarationNode or ConstructorDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode
                    or ClassStaticBlockDeclarationNode:
                    return node;
                case DecoratorNode:
                    if (node.Parent is ParameterDeclarationNode && SemanticSyntax.ClassElement(node.Parent.Parent))
                        node = node.Parent.Parent!;
                    else if (SemanticSyntax.ClassElement(node.Parent))
                        node = node.Parent!;
                    break;
            }
        }
        return null;
    }
}
