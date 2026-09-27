using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionContextHost
{
    bool NoImplicitAny { get; }

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> DeclarationInitializerAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type?> InvokedParameterAsync(ParameterDeclarationNode node, CallExpressionNode call, CancellationToken cancellation);

    ValueTask<Type?> GeneratorContextReturnAsync(SyntaxNode node, Type type, CancellationToken cancellation);
}

internal sealed class FunctionContexts(TypeContext context, CheckerLinks links, CheckerSymbols symbols,
    ExpressionContexts expressions, TypeAlgebra algebra, TypeConstraints constraints, TypeInstantiation instantiation,
    TypeRelations relations, TypeInference inference, Signatures signatures, SignatureParameters parameters,
    SignatureComparison comparison, SignatureComposition composition, SymbolTypes values, TypeWidening widening,
    VariableTypes variables, BindingTypes bindings, BindingPatterns patterns, AwaitedTypes awaited,
    TypeResolutionStack resolutions, IFunctionContextHost host)
{
    internal async ValueTask<Signature?> GetAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        if (!FunctionSyntax.Contextual(node))
            return null;
        var type = await expressions.ApparentAsync(node, ContextFlags.Signature, cancellation).ConfigureAwait(false);
        if (type is null)
            return null;
        if (type is not UnionType union)
            return await CallAsync(type, node, cancellation).ConfigureAwait(false);
        var found = new List<Signature>();
        foreach (var part in union.Types)
        {
            var signature = await CallAsync(part, node, cancellation).ConfigureAwait(false);
            if (signature is null)
                continue;
            if (found.Count != 0 && await comparison.CompareAsync(found[0], signature, ignoreThis: true, ignoreReturn: true,
                cancellation: cancellation).ConfigureAwait(false) == Ternary.False)
                return null;
            found.Add(signature);
        }
        if (found.Count == 0)
            return null;
        if (found.Count == 1)
            return found[0];
        var result = context.CloneSignature(found[0]);
        result.Composite = new(true, found.AsReadOnly());
        result.Target = null;
        result.Mapper = null;
        return result;
    }

    internal async ValueTask<Signature?> CallAsync(Type type, SyntaxNode node, CancellationToken cancellation = default)
    {
        var list = new List<Signature>();
        foreach (var signature in await host.SignaturesAsync(type, false, cancellation).ConfigureAwait(false))
            if (!await AritySmallerAsync(signature, node, cancellation).ConfigureAwait(false))
                list.Add(signature);
        if (list.Count == 1)
            return list[0];
        if (!host.NoImplicitAny)
            return null;
        Signature? combined = null;
        foreach (var signature in list)
        {
            if (combined is null || combined == signature)
                combined = signature;
            else if (await IdenticalParametersAsync(combined.TypeParameters, signature.TypeParameters, cancellation).ConfigureAwait(false))
                combined = await composition.CombineAsync(combined, signature, false, cancellation).ConfigureAwait(false);
            else
                return null;
        }
        return combined;
    }

    private async ValueTask<bool> IdenticalParametersAsync(
        IReadOnlyList<TypeParameter> source,
        IReadOnlyList<TypeParameter> target,
        CancellationToken cancellation)
    {
        if (source.Count != target.Count)
            return false;
        var mapper = TypeMapper.Create(target.Cast<Type>().ToArray(), source.Cast<Type>().ToArray());
        for (int i = 0; i < source.Count; i++)
            if (source[i] != target[i]
                && !await relations.RelatedAsync(
                    await constraints.ConstraintAsync(source[i], cancellation).ConfigureAwait(false) ?? context.UnknownType,
                    (await instantiation.InstantiateAsync(
                        await constraints.ConstraintAsync(target[i], cancellation).ConfigureAwait(false) ?? context.UnknownType,
                        mapper,
                        cancellation: cancellation).ConfigureAwait(false))!,
                    RelationKind.Identity,
                    cancellation).ConfigureAwait(false))
                return false;
        return true;
    }

    private async ValueTask<bool> AritySmallerAsync(Signature signature, SyntaxNode node, CancellationToken cancellation)
    {
        var declarations = ((IFunctionSignature)node).Parameters!;
        int count = 0;
        foreach (ParameterDeclarationNode parameter in declarations)
        {
            if (parameter.Initializer is not null || parameter.QuestionToken is not null || parameter.DotDotDotToken is not null)
                break;
            count++;
        }
        if (declarations.FirstOrDefault() is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } })
            count--;
        return !await parameters.HasRestAsync(signature, cancellation).ConfigureAwait(false)
            && await parameters.CountAsync(signature, cancellation).ConfigureAwait(false) < count;
    }

    internal async ValueTask<Type?> ParameterAsync(ParameterDeclarationNode parameter, CancellationToken cancellation = default)
    {
        var node = parameter.Parent!;
        if (!FunctionSyntax.Contextual(node) || !FunctionSyntax.Sensitive(node, symbols))
            return null;
        if (Signatures.Invoked(node) is { } call)
            return await host.InvokedParameterAsync(parameter, call, cancellation).ConfigureAwait(false);
        var signature = await GetAsync(node, cancellation).ConfigureAwait(false);
        if (signature is null)
            return null;
        var declarations = ((IFunctionSignature)node).Parameters!;
        int index = declarations.IndexOf(parameter)
            - (declarations.FirstOrDefault() is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } } ? 1 : 0);
        return parameter.DotDotDotToken is not null && declarations[^1] == parameter
            ? await parameters.RestAtAsync(signature, index, cancellation: cancellation).ConfigureAwait(false)
            : await parameters.TryAtAsync(signature, index, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type?> ReturnAsync(SyntaxNode node, ContextFlags flags = 0, CancellationToken cancellation = default)
    {
        var annotation = await signatures.AnnotationAsync(node, cancellation).ConfigureAwait(false);
        if (annotation is not null)
            return annotation;
        var signature = await GetAsync(node, cancellation).ConfigureAwait(false);
        if (signature is not null && !Resolving(signature))
        {
            var type = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
            if (SemanticSyntax.Generator(node))
                return await host.GeneratorContextReturnAsync(node, type, cancellation).ConfigureAwait(false);
            if (SemanticSyntax.HasModifier(node, SyntaxKind.AsyncKeyword))
                return await algebra.FilterAsync(
                    type,
                    async part => (part.Flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.InstantiableNonPrimitive)) != 0
                    || await awaited.OfPromiseAsync(part, cancellation: cancellation).ConfigureAwait(false) is not null,
                    cancellation).ConfigureAwait(false);
            return type;
        }
        return Signatures.Invoked(node) is { } call ? await expressions.GetAsync(call, flags, cancellation).ConfigureAwait(false) : null;
    }

    private bool Resolving(Signature signature)
    {
        var pending = new Stack<Signature>();
        pending.Push(signature);
        while (pending.TryPop(out var current))
        {
            if (current.Composite is { } composite)
                foreach (var part in composite.Signatures)
                    pending.Push(part);
            if (current.ResolvedReturnType is null && resolutions.FindCycleStart(current, TypeSystemPropertyName.ResolvedReturnType) >= 0)
                return true;
        }
        return false;
    }

    internal async ValueTask AssignAsync(Signature signature, Signature? contextual, CancellationToken cancellation = default)
    {
        var oldParameters = signature.TypeParameters;
        var oldThis = signature.ThisParameter;
        var changed = new Dictionary<Symbol, Type?>();
        try
        {
            if (contextual is null)
            {
                if (signature.ThisParameter is { } receiver)
                    await AssignParameterAsync(receiver, null).ConfigureAwait(false);
                foreach (var parameter in signature.Parameters)
                    await AssignParameterAsync(parameter, null).ConfigureAwait(false);
                return;
            }
            if (contextual.TypeParameters.Count != 0)
            {
                if (signature.TypeParameters.Count != 0)
                    return;
                signature.TypeParameters = contextual.TypeParameters;
            }
            if (contextual.ThisParameter is { } contextThis
                && (signature.ThisParameter is null || signature.ThisParameter.ValueDeclaration is ITypedNode { Type: null }))
            {
                signature.ThisParameter ??= widening.WithType(contextThis, null);
                await AssignParameterAsync(
                    signature.ThisParameter,
                    await values.GetAsync(contextThis, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
            }
            int length = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
            for (int i = 0; i < length; i++)
            {
                var parameter = signature.Parameters[i];
                var declaration = (ParameterDeclarationNode)parameter.ValueDeclaration!;
                if (declaration.Type is not null)
                    continue;
                var type = await parameters.TryAtAsync(contextual, i, cancellation).ConfigureAwait(false);
                if (type is not null && declaration.Initializer is not null)
                {
                    var initializer = await host.DeclarationInitializerAsync(declaration, 0, cancellation).ConfigureAwait(false);
                    if (!await relations.RelatedAsync(initializer, type, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                    {
                        initializer = await variables.WidenInitializerAsync(declaration, initializer, cancellation).ConfigureAwait(false);
                        if (await relations.RelatedAsync(type, initializer, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                            type = initializer;
                    }
                }
                await AssignParameterAsync(parameter, type).ConfigureAwait(false);
            }
            if (signature.HasRestParameter)
            {
                var parameter = signature.Parameters[^1];
                if (parameter.ValueDeclaration is ITypedNode { Type: null }
                    || parameter.ValueDeclaration is null && (parameter.CheckFlags & CheckFlags.DeferredType) != 0)
                    await AssignParameterAsync(
                        parameter,
                        await parameters.RestAtAsync(
                            contextual,
                            length,
                            cancellation: cancellation).ConfigureAwait(false)).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            signature.TypeParameters = oldParameters;
            signature.ThisParameter = oldThis;
            foreach (var (symbol, value) in changed)
                links.Values.Get(symbol).ResolvedType = value;
            throw;
        }

        void Set(Symbol symbol, Type type)
        {
            var data = links.Values.Get(symbol);
            changed.TryAdd(symbol, data.ResolvedType);
            data.ResolvedType = type;
        }
        async ValueTask AssignParameterAsync(Symbol parameter, Type? contextualType)
        {
            if (links.Values.Get(parameter).ResolvedType is not null)
                return;
            var declaration = parameter.ValueDeclaration;
            var type = contextualType ?? (declaration is not null
                ? await variables.GetAsync(
                    declaration,
                    true,
                    cancellation).ConfigureAwait(false) : await values.GetAsync(parameter, cancellation).ConfigureAwait(false));
            if (context.StrictNullChecks && declaration is ParameterDeclarationNode { Initializer: null, QuestionToken: not null })
                type = await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
            Set(parameter, type);
            if (declaration is INamedNode { Name: BindingPatternNode pattern })
            {
                if (type == context.UnknownType)
                    Set(parameter, type = await patterns.GetAsync(pattern, cancellation: cancellation).ConfigureAwait(false));
                var pending = new Stack<(BindingElementNode Element, Type Type)>();
                Push(pattern, type);
                while (pending.TryPop(out var item))
                {
                    var inferred = await bindings.FromParentAsync(
                        item.Element,
                        item.Type,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (item.Element.Name is IdentifierNode)
                        Set(symbols.Declaration(item.Element)!, inferred);
                    else if (item.Element.Name is BindingPatternNode nested)
                        Push(nested, inferred);
                }
                void Push(BindingPatternNode nested, Type parent)
                {
                    for (int i = nested.Elements!.Count - 1; i >= 0; i--)
                        if (nested.Elements[i] is BindingElementNode element)
                            pending.Push((element, parent));
                }
            }
        }
    }

    internal async ValueTask InferAnnotationsAsync(Signature signature, Signature contextual, InferenceContext inferenceContext,
        CancellationToken cancellation = default)
    {
        int length = signature.Parameters.Count - (signature.HasRestParameter ? 1 : 0);
        for (int i = 0; i < length; i++)
            if (signature.Parameters[i].ValueDeclaration is ITypedNode { Type: { } annotation } declaration)
            {
                var type = await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
                if (context.StrictNullChecks && VariableTypes.Optional((SyntaxNode)declaration))
                    type = await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
                await inference.InferAsync(
                    inferenceContext,
                    type,
                    await parameters.AtAsync(contextual, i, cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
            }
        if (signature.Declaration is ITypedNode { Type: { } returnNode })
            await inference.InferAsync(inferenceContext, await host.TypeFromNodeAsync(returnNode, cancellation).ConfigureAwait(false),
                await signatures.ReturnAsync(contextual, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false);
    }
}
