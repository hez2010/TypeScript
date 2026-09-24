using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IClassBaseHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<Type?> ArrayElementAsync(Type type, CancellationToken cancellation);

    void ClassBaseError(SyntaxNode node, int code, Type type);
}

internal sealed class ClassBases(TypeContext context, DeclaredTypes declared, TypeReferences references, BaseTypes bases,
    TypeViews views, TypeConstraints constraints, TypeInstantiation instantiation, SignatureInstantiation signatureInstantiation,
    Signatures signatures, SignatureParameters parameters, StructuredMembers members, TypeResolutionStack resolutions,
    TypeRelations relations, IClassBaseHost host)
{
    internal async ValueTask<Type> ConstructorAsync(InterfaceType type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type.ResolvedBaseConstructorType is { } cached)
            return cached;
        var node = BaseNode(type);
        if (node is null)
            return type.ResolvedBaseConstructorType = context.UndefinedType;
        if (!resolutions.Push(type, TypeSystemPropertyName.ResolvedBaseConstructorType))
            return context.ErrorType;
        bool active = true;
        try
        {
            var constructor = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
            if ((constructor.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
                await members.ResolveAsync((StructuredType)constructor, cancellation).ConfigureAwait(false);
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved)
            {
                host.ClassBaseError(type.Symbol!.ValueDeclaration!, 2506, type);
                return type.ResolvedBaseConstructorType ??= context.ErrorType;
            }
            if ((constructor.Flags & TypeFlags.Any) == 0 && constructor != context.NullWideningType
                && !await IsConstructorAsync(constructor, cancellation).ConfigureAwait(false))
            {
                host.ClassBaseError(node.Expression!, 2507, constructor);
                if (constructor is TypeParameter parameter)
                {
                    var constraint = await constraints.ConstraintAsync(parameter, cancellation).ConfigureAwait(false);
                    if (constraint is not null)
                    {
                        var constructors = await host.SignaturesAsync(constraint, true, cancellation).ConfigureAwait(false);
                        if (constructors.Count != 0)
                            await signatures.ReturnAsync(constructors[0], cancellation).ConfigureAwait(false);
                    }
                }
                return type.ResolvedBaseConstructorType ??= context.ErrorType;
            }
            cancellation.ThrowIfCancellationRequested();
            return type.ResolvedBaseConstructorType ??= constructor;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    internal async ValueTask<IReadOnlyList<Type>> GetAsync(InterfaceType type, CancellationToken cancellation = default)
    {
        var constructor = await views.ApparentAsync(
            await ConstructorAsync(type, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        if ((constructor.Flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.Any)) == 0)
            return [];
        var node = BaseNode(type)!;
        Type? original = constructor.Symbol is { } symbol ? await declared.GetAsync(symbol, cancellation).ConfigureAwait(false) : null;
        Type baseType;
        if (constructor.Symbol is { Flags: var flags } && (flags & SymbolFlags.Class) != 0
            && await OuterAppliedAsync((InterfaceType)original!, cancellation).ConfigureAwait(false))
            baseType = await references.ClassReferenceAsync(node, constructor.Symbol, cancellation).ConfigureAwait(false);
        else if ((constructor.Flags & TypeFlags.Any) != 0)
            baseType = constructor;
        else
        {
            var constructors = await ConstructorsAsync(constructor, node, cancellation).ConfigureAwait(false);
            if (constructors.Count == 0)
            {
                host.ClassBaseError(node.Expression!, 2508, constructor);
                return [];
            }
            baseType = await signatures.ReturnAsync(constructors[0], cancellation).ConfigureAwait(false);
        }
        if (baseType == context.ErrorType || (baseType.Flags & TypeFlags.Any) != 0 && baseType.Alias is not null)
            return [];
        var reduced = await views.ReducedAsync(baseType, cancellation).ConfigureAwait(false);
        if (!await bases.ValidAsync(reduced, cancellation).ConfigureAwait(false))
        {
            host.ClassBaseError(node.Expression!, 2509, reduced);
            return [];
        }
        if (type == reduced || await bases.HasBaseAsync(reduced, type, cancellation).ConfigureAwait(false))
        {
            host.ClassBaseError(type.Symbol!.ValueDeclaration!, 2310, type);
            return [];
        }
        return [reduced];
    }

    private async ValueTask<bool> OuterAppliedAsync(InterfaceType type, CancellationToken cancellation)
        => type.OuterTypeParameterCount == 0 || type.AllTypeParameters[type.OuterTypeParameterCount - 1].Symbol
            != (await references.TypeArgumentsAsync(type, cancellation).ConfigureAwait(false))[type.OuterTypeParameterCount - 1].Symbol;

    private async ValueTask<bool> IsConstructorAsync(Type type, CancellationToken cancellation)
    {
        if ((await host.SignaturesAsync(type, true, cancellation).ConfigureAwait(false)).Count != 0)
            return true;
        if ((type.Flags & TypeFlags.TypeVariable) == 0
            || await constraints.BaseConstraintAsync(type, cancellation).ConfigureAwait(false) is not { } constraint)
            return false;
        var constructors = await host.SignaturesAsync(constraint, true, cancellation).ConfigureAwait(false);
        if (constructors is not [var signature]
            || signature.TypeParameters.Count != 0
            || signature.Parameters.Count != 1
            || !signature.HasRestParameter)
            return false;
        var parameter = await parameters.ParameterAsync(signature.Parameters[0], cancellation).ConfigureAwait(false);
        return (parameter.Flags & TypeFlags.Any) != 0
            || await host.ArrayElementAsync(parameter, cancellation).ConfigureAwait(false) == context.AnyType;
    }

    private async ValueTask<IReadOnlyList<Signature>> ConstructorsAsync(
        Type type,
        ExpressionWithTypeArgumentsNode node,
        CancellationToken cancellation)
    {
        int count = node.TypeArguments?.Count ?? 0;
        var constructors = (await host.SignaturesAsync(type, true, cancellation).ConfigureAwait(false))
            .Where(s => count >= TypeReferences.Minimum(s.TypeParameters) && count <= s.TypeParameters.Count).ToArray();
        var arguments = await ArgumentsAsync(node, cancellation).ConfigureAwait(false);
        for (int i = 0; i < constructors.Length; i++)
            if (constructors[i].TypeParameters.Count != 0)
                constructors[i] = await signatureInstantiation.GetAsync(
                    constructors[i],
                    arguments,
                    (node.Flags & NodeFlags.JavaScriptFile) != 0,
                    cancellation: cancellation).ConfigureAwait(false);
        return constructors;
    }

    internal async ValueTask<IReadOnlyList<Signature>> DefaultsAsync(InterfaceType type, CancellationToken cancellation = default)
    {
        var constructor = await ConstructorAsync(type, cancellation).ConfigureAwait(false);
        var baseSignatures = await host.SignaturesAsync(constructor, true, cancellation).ConfigureAwait(false);
        var declaration = type.Symbol!.Declarations.FirstOrDefault(SemanticSyntax.ClassLike);
        bool isAbstract = declaration is not null && SemanticSyntax.HasModifier(declaration, SyntaxKind.AbstractKeyword);
        var locals = type.AllTypeParameters.Skip(type.OuterTypeParameterCount).Take(
            type.AllTypeParameters.Count - type.OuterTypeParameterCount - (type.ThisType is null ? 0 : 1)).Cast<TypeParameter>().ToArray();
        if (baseSignatures.Count == 0)
            return [context.NewSignature(
                SignatureFlags.Construct | (isAbstract ? SignatureFlags.Abstract : 0),
                null,
                locals,
                null,
                [],
                type,
                null,
                0)];
        bool javaScript = declaration is not null && (declaration.Flags & NodeFlags.JavaScriptFile) != 0;
        var arguments = await ArgumentsAsync(BaseNode(type)!, cancellation).ConfigureAwait(false);
        List<Signature> result = [];
        foreach (var signature in baseSignatures)
        {
            if (!javaScript
                && (arguments.Count < TypeReferences.Minimum(signature.TypeParameters) || arguments.Count > signature.TypeParameters.Count))
                continue;
            Signature instantiated;
            if (signature.TypeParameters.Count != 0)
            {
                var filled = await constraints.FillMissingArgumentsAsync(arguments, signature.TypeParameters, javaScript,
                    (s, t, token) => relations.RelatedAsync(s, t, RelationKind.Identity, token), cancellation).ConfigureAwait(false);
                instantiated = await instantiation.SignatureAsync(signature,
                    TypeMapper.Create(
                        (await signatureInstantiation.ParametersAsync(signature, cancellation).ConfigureAwait(false)).ToArray(),
                        filled.ToArray()),
                    true,
                    cancellation).ConfigureAwait(false);
            }
            else
                instantiated = context.CloneSignature(signature);
            instantiated.TypeParameters = locals;
            instantiated.ResolvedReturnType = type;
            instantiated.Flags = isAbstract ? instantiated.Flags | SignatureFlags.Abstract : instantiated.Flags & ~SignatureFlags.Abstract;
            result.Add(instantiated);
        }
        return result;
    }

    private async ValueTask<IReadOnlyList<Type>> ArgumentsAsync(ExpressionWithTypeArgumentsNode node, CancellationToken cancellation)
    {
        if (node.TypeArguments is null)
            return [];
        var result = new Type[node.TypeArguments.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = await host.TypeFromNodeAsync(node.TypeArguments[i], cancellation).ConfigureAwait(false);
        return result;
    }

    internal static ExpressionWithTypeArgumentsNode? BaseNode(InterfaceType type)
    {
        var declaration = type.Symbol?.Declarations.FirstOrDefault(SemanticSyntax.ClassLike);
        var clauses = declaration switch
        {
            ClassDeclarationNode c => c.HeritageClauses,
            ClassExpressionNode c => c.HeritageClauses,
            _ => null
        };
        return clauses?.OfType<HeritageClauseNode>().FirstOrDefault(h => h.Token == SyntaxKind.ExtendsKeyword)?.Types?.FirstOrDefault() as ExpressionWithTypeArgumentsNode;
    }
}
