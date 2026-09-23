using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Checking;

internal interface ISignatureHost
{
    ValueTask<Signature?> FullSignatureAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Type?> ContextualTypeAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> BindableNameAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ReturnFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<TypePredicate?> PredicateFromBodyAsync(SyntaxNode declaration, CancellationToken cancellation);

    ValueTask<int> ParameterCountAsync(Signature signature, CancellationToken cancellation);

    void CircularReturn(Signature signature);
}

internal sealed class Signatures(TypeContext context, CheckerLinks links, CheckerSymbols symbols, TypeParameterScopes scopes,
    TypeInstantiation instantiation, TypeAlgebra algebra, TypeResolutionStack resolutions, ISignatureHost host)
{
    internal async ValueTask<Type> NonCircularReturnAsync(Signature signature, CancellationToken cancellation = default)
    {
        var pending = new Stack<Signature>();
        pending.Push(signature);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            RequireOwned(current);
            if (current.Composite is { } composite)
                foreach (var part in composite.Signatures)
                    pending.Push(part);
            if (current.ResolvedReturnType is null && resolutions.FindCycleStart(current, TypeSystemPropertyName.ResolvedReturnType) >= 0)
                return context.AnyType;
        }
        return await ReturnAsync(signature, cancellation).ConfigureAwait(false);
    }

    private readonly TypePredicate noPredicate = new(TypePredicateKind.Identifier, 0, "<<unresolved>>", context.AnyType);

    internal async ValueTask<IReadOnlyList<Signature>> OfSymbolAsync(Symbol? symbol, CancellationToken cancellation = default)
    {
        if (symbol is null)
            return [];
        var result = new List<Signature>();
        for (int i = 0; i < symbol.Declarations.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var declaration = symbol.Declarations[i];
            if (!FunctionLike(declaration))
                continue;
            if (i > 0 && SemanticSyntax.Body(declaration) is not null)
            {
                var previous = symbol.Declarations[i - 1];
                if (declaration.Parent == previous.Parent && declaration.Kind == previous.Kind
                    && (declaration.Pos == previous.End || (previous.Flags & NodeFlags.Reparsed) != 0))
                    continue;
            }
            result.Add(await host.FullSignatureAsync(declaration, cancellation).ConfigureAwait(false)
                ?? await FromDeclarationAsync(declaration, cancellation).ConfigureAwait(false));
        }
        return result.AsReadOnly();
    }

    internal async ValueTask<Signature> FromDeclarationAsync(SyntaxNode declaration, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var data = links.Signatures.Get(declaration);
        if (data.ResolvedSignature is { } cached)
            return cached;
        var parameters = new List<Symbol>();
        SignatureFlags flags = 0;
        Symbol? thisParameter = null;
        int minimum = 0;
        bool hasThis = false;
        var declarations = Parameters(declaration);
        var iife = Invoked(declaration);
        if (iife is null && (declaration.Flags & NodeFlags.JavaScriptFile) != 0
            && declaration.Kind is K.FunctionExpression or K.ArrowFunction or K.MethodDeclaration or K.GetAccessor
                or K.SetAccessor or K.FunctionDeclaration or K.Constructor
            && (declarations?.All(p => ((ITypedNode)p).Type is null) ?? true)
            && await host.ContextualTypeAsync(declaration, cancellation).ConfigureAwait(false) is null)
            flags |= SignatureFlags.IsUntypedSignatureInJSFile;
        for (int i = 0; i < (declarations?.Count ?? 0); i++)
        {
            var parameter = (ParameterDeclarationNode)declarations![i];
            var symbol = symbols.Binding(parameter)?.Get(parameter)?.Symbol
                ?? throw new InvalidOperationException("Signature parameter is unbound");
            if ((symbol.Flags & SymbolFlags.Property) != 0 && parameter.Name?.Kind is not (K.ObjectBindingPattern or K.ArrayBindingPattern))
                symbol = symbols.NameResolver(cancellation).Resolve(parameter, symbol.Name, SymbolFlags.Value)
                    ?? throw new InvalidOperationException("Parameter property has no parameter symbol");
            if (i == 0 && symbol.Name == "this")
            {
                hasThis = true;
                thisParameter = symbols.Binding(parameter)!.Get(parameter)!.Symbol;
            }
            else
                parameters.Add(symbol);
            if (parameter.Type is LiteralTypeNode)
                flags |= SignatureFlags.HasLiteralTypes;
            bool optional = parameter.QuestionToken is not null || parameter.Initializer is not null || parameter.DotDotDotToken is not null
                || iife is not null && parameters.Count > (iife.Arguments?.Count ?? 0) && parameter.Type is null;
            if (!optional)
                minimum = parameters.Count;
        }
        if (declaration.Kind is K.GetAccessor or K.SetAccessor && (!hasThis || thisParameter is null)
            && await host.BindableNameAsync(declaration, cancellation).ConfigureAwait(false))
        {
            var otherKind = declaration.Kind == K.GetAccessor ? K.SetAccessor : K.GetAccessor;
            var other = symbols.Declaration(declaration)?.Declarations.FirstOrDefault(n => n.Kind == otherKind);
            var otherParameters = other is null ? null : Parameters(other);
            int count = otherKind == K.GetAccessor ? 1 : 2;
            if (otherParameters?.Count == count
                && otherParameters[0] is ParameterDeclarationNode { Name: IdentifierNode { Text: "this" } } otherThis)
                thisParameter = symbols.Binding(otherThis)?.Get(otherThis)?.Symbol;
        }
        IReadOnlyList<TypeParameter> typeParameters;
        if (declaration is ConstructorDeclarationNode)
        {
            var type = await scopes.ClassOrInterfaceAsync(symbols.Declaration(declaration.Parent!)!, cancellation).ConfigureAwait(false);
            typeParameters = type.AllTypeParameters.Skip(type.OuterTypeParameterCount)
                .Take(type.AllTypeParameters.Count - type.OuterTypeParameterCount - 1).Cast<TypeParameter>().ToArray();
        }
        else
        {
            var full = await host.FullSignatureAsync(declaration, cancellation).ConfigureAwait(false);
            typeParameters = full?.TypeParameters ?? (declaration as IFunctionSignature)?.TypeParameters?.Select(n =>
                scopes.Parameter(symbols.Binding(n)!.Get(n)!.Symbol!)).Distinct().ToArray() ?? [];
        }
        if (declarations is { Count: > 0 } && declarations[^1] is ParameterDeclarationNode { DotDotDotToken: not null })
            flags |= SignatureFlags.HasRestParameter;
        if (declaration.Kind is K.Constructor or K.ConstructorType or K.ConstructSignature)
            flags |= SignatureFlags.Construct;
        if (declaration.Kind == K.ConstructorType && SemanticSyntax.HasModifier(declaration, K.AbstractKeyword)
            || declaration.Kind == K.Constructor && SemanticSyntax.HasModifier(declaration.Parent!, K.AbstractKeyword))
            flags |= SignatureFlags.Abstract;
        cancellation.ThrowIfCancellationRequested();
        return data.ResolvedSignature = context.NewSignature(
            flags,
            declaration,
            typeParameters.ToArray(),
            thisParameter,
            parameters.ToArray(),
            null,
            null,
            minimum);
    }

    internal async ValueTask<Type> ReturnAsync(Signature signature, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        RequireOwned(signature);
        if (signature.ResolvedReturnType is { } cached)
            return cached;
        if (!resolutions.Push(signature, TypeSystemPropertyName.ResolvedReturnType))
            return context.ErrorType;
        bool active = true;
        try
        {
            Type type;
            if (signature.Target is { } target)
                type = await InstantiateAsync(
                    await ReturnAsync(target, cancellation).ConfigureAwait(false),
                    signature.Mapper,
                    cancellation).ConfigureAwait(false);
            else if (signature.Composite is { } composite)
            {
                var types = new Type[composite.Signatures.Count];
                for (int i = 0; i < types.Length; i++)
                    types[i] = await ReturnAsync(composite.Signatures[i], cancellation).ConfigureAwait(false);
                type = composite.IsUnion ? await algebra.UnionAsync(
                    types,
                    UnionReduction.Subtype,
                    cancellation: cancellation).ConfigureAwait(false)
                    : await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
                type = await InstantiateAsync(type, signature.Mapper, cancellation).ConfigureAwait(false);
            }
            else
            {
                var declaration = signature.Declaration ?? throw new InvalidOperationException("Signature has no return source");
                var annotated = await AnnotationAsync(declaration, cancellation).ConfigureAwait(false);
                var body = SemanticSyntax.Body(declaration);
                type = annotated ?? (body is not null && !(body.Pos >= 0 && body.Pos == body.End)
                    ? await host.ReturnFromBodyAsync(declaration, cancellation).ConfigureAwait(false) : context.AnyType);
            }
            if ((signature.Flags & SignatureFlags.IsInnerCallChain) != 0)
            {
                if (context.StrictNullChecks)
                    type = await algebra.UnionAsync([type, context.OptionalType], cancellation: cancellation).ConfigureAwait(false);
            }
            else if ((signature.Flags & SignatureFlags.IsOuterCallChain) != 0)
                type = await algebra.UnionAsync([type, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
            context.RequireOwned(type);
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved)
            {
                host.CircularReturn(signature);
                type = context.AnyType;
            }
            cancellation.ThrowIfCancellationRequested();
            return signature.ResolvedReturnType ??= type;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    private async ValueTask<Type?> AnnotationAsync(SyntaxNode declaration, CancellationToken cancellation)
    {
        if (declaration is ConstructorDeclarationNode)
            return await scopes.ClassOrInterfaceAsync(symbols.Declaration(declaration.Parent!)!, cancellation).ConfigureAwait(false);
        if (declaration is ITypedNode { Type: { } type })
            return await host.TypeFromNodeAsync(type, cancellation).ConfigureAwait(false);
        if (declaration is GetAccessorDeclarationNode && await host.BindableNameAsync(declaration, cancellation).ConfigureAwait(false))
        {
            var setter = symbols.Declaration(declaration)?.Declarations.OfType<SetAccessorDeclarationNode>().FirstOrDefault();
            return SymbolTypes.Annotation(setter) is { } annotation
                ? await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false)
                : null;
        }
        var full = await host.FullSignatureAsync(declaration, cancellation).ConfigureAwait(false);
        return full is null ? null : await ReturnAsync(full, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<TypePredicate?> PredicateAsync(Signature signature, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        RequireOwned(signature);
        if (signature.ResolvedTypePredicate is null)
        {
            TypePredicate? result = null;
            try
            {
                if (signature.Target is { } target)
                {
                    var predicate = await PredicateAsync(target, cancellation).ConfigureAwait(false);
                    if (predicate is not null)
                    {
                        var type = await instantiation.InstantiateAsync(
                            predicate.Type,
                            signature.Mapper,
                            cancellation: cancellation).ConfigureAwait(false);
                        result = type == predicate.Type
                            ? predicate
                            : new(predicate.Kind, predicate.ParameterIndex, predicate.ParameterName, type);
                    }
                }
                else if (signature.Composite is { } composite)
                    result = await CompositePredicateAsync(composite, cancellation).ConfigureAwait(false);
                else if (signature.Declaration is { } declaration)
                {
                    if (declaration is ITypedNode { Type: { } node })
                    {
                        if (node is TypePredicateNode predicate)
                        {
                            var type = predicate.Type is null
                                ? null
                                : await host.TypeFromNodeAsync(predicate.Type, cancellation).ConfigureAwait(false);
                            bool assertion = predicate.AssertsModifier is not null;
                            if (predicate.ParameterName?.Kind == K.ThisType)
                                result = new(assertion ? TypePredicateKind.AssertsThis : TypePredicateKind.This, 0, "", type);
                            else
                            {
                                string name = ((IdentifierNode)predicate.ParameterName!).Text;
                                int index = Array.FindIndex(signature.Parameters.ToArray(), p => p.Name == name);
                                result = new(
                                    assertion ? TypePredicateKind.AssertsIdentifier : TypePredicateKind.Identifier,
                                    index,
                                    name,
                                    type);
                            }
                        }
                    }
                    else if (SemanticSyntax.FunctionDeclarationLike(declaration)
                        && (signature.ResolvedReturnType is null || (signature.ResolvedReturnType.Flags & TypeFlags.Boolean) != 0)
                        && await host.ParameterCountAsync(signature, cancellation).ConfigureAwait(false) > 0)
                    {
                        signature.ResolvedTypePredicate = noPredicate;
                        result = await host.PredicateFromBodyAsync(declaration, cancellation).ConfigureAwait(false);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                signature.ResolvedTypePredicate = result ?? noPredicate;
            }
            catch
            {
                if (signature.ResolvedTypePredicate == noPredicate)
                    signature.ResolvedTypePredicate = null;
                throw;
            }
        }
        return signature.ResolvedTypePredicate == noPredicate ? null : signature.ResolvedTypePredicate;
    }

    private async ValueTask<TypePredicate?> CompositePredicateAsync(CompositeSignature composite, CancellationToken cancellation)
    {
        TypePredicate? last = null;
        var types = new List<Type>();
        foreach (var signature in composite.Signatures)
        {
            var predicate = await PredicateAsync(signature, cancellation).ConfigureAwait(false);
            if (predicate is not null)
            {
                if (predicate.Kind is not (TypePredicateKind.This or TypePredicateKind.Identifier)
                    || last is not null && (last.Kind != predicate.Kind || last.ParameterIndex != predicate.ParameterIndex))
                    return null;
                last = predicate;
                types.Add(predicate.Type!);
            }
            else
            {
                var type = composite.IsUnion ? await ReturnAsync(signature, cancellation).ConfigureAwait(false) : null;
                if (type != context.FalseType && type != context.RegularFalseType)
                    return null;
            }
        }
        if (last is null)
            return null;
        var result = composite.IsUnion ? await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false)
            : await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false);
        return new(last.Kind, last.ParameterIndex, last.ParameterName, result);
    }

    private async ValueTask<Type> InstantiateAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
        => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Return type instantiation returned no type");

    private void RequireOwned(Signature signature)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
    }

    internal static NodeList? Parameters(SyntaxNode node) => node is IFunctionSignature signature ? signature.Parameters
        : node is IndexSignatureDeclarationNode index ? index.Parameters : null;

    internal static bool FunctionLike(SyntaxNode node) => SemanticSyntax.FunctionDeclarationLike(node)
            || node.Kind is K.MethodSignature or K.CallSignature or K.JSDocSignature or K.ConstructSignature or K.IndexSignature
                or K.FunctionType or K.ConstructorType;

    private static CallExpressionNode? Invoked(SyntaxNode node)
    {
        if (node is not (FunctionExpressionNode or ArrowFunctionNode))
            return null;
        while (node.Parent is ParenthesizedExpressionNode parent)
            node = parent;
        return node.Parent is CallExpressionNode call && call.Expression == node ? call : null;
    }
}
