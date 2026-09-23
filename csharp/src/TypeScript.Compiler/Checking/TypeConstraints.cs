using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal interface ITypeConstraintHost
{
    ValueTask<Type> SimplifyAsync(Type type, bool writing, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> InferredParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation);

    ValueTask<Type?> InstantiateAsync(Type? type, TypeMapper? mapper, CancellationToken cancellation);

    ValueTask<bool> IsGenericMappedAsync(MappedType type, CancellationToken cancellation);

    ValueTask<Type?> MappedNameTypeAsync(MappedType type, CancellationToken cancellation);

    bool HasKeyofConstraint(MappedType type);

    ValueTask<Type> MappedIndexTypeAsync(MappedType type, CancellationToken cancellation);

    ValueTask<bool> IsMappedGenericAccessAsync(IndexedAccessType type, CancellationToken cancellation);

    ValueTask<Type> SubstituteMappedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation);

    ValueTask<Type?> IndexedAccessAsync(Type objectType, Type indexType, AccessFlags flags, CancellationToken cancellation);

    bool IsRestrictiveInstantiation(ConditionalType type);

    ValueTask<Type> InstantiateConditionalAsync(ConditionalType type, TypeMapper mapper, bool forConstraint, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    bool IsArrayType(Type type);

    ValueTask<Type> CreateTupleAsync(
        IReadOnlyList<Type> elements,
        IReadOnlyList<TupleElementInfo> infos,
        bool isReadonly,
        CancellationToken cancellation);

    void CircularConstraint(TypeParameter parameter, SyntaxNode declaration);
}

// Constraint state is separate from declaration syntax. This resolver retains
// the reference's semantic recursion budgets and only publishes completed caches.
internal sealed class TypeConstraints(TypeContext context, TypeAlgebra algebra, TypeResolutionStack resolutions,
    TypeRecursion recursion, ITypeConstraintHost host)
{
    private int conditionalDepth;

    internal static bool IsGenericTuple(Type type) => type is TypeReference { Target: TupleType tuple }
        && (tuple.CombinedFlags & ElementFlags.Variadic) != 0;

    internal async ValueTask<Type?> BaseConstraintAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & (F.InstantiableNonPrimitive | F.UnionOrIntersection | F.TemplateLiteral | F.StringMapping | F.Index)) == 0
            && !IsGenericTuple(type))
            return null;
        var result = await ResolvedBaseAsync(type, [], cancellation).ConfigureAwait(false);
        return IsAbsent(result) ? null : result;
    }

    internal async ValueTask<Type> BaseConstraintOrTypeAsync(Type type, CancellationToken cancellation = default)
        => await BaseConstraintAsync(type, cancellation).ConfigureAwait(false) ?? type;

    internal async ValueTask<bool> HasNonCircularConstraintAsync(Type type, CancellationToken cancellation = default)
        => await ResolvedBaseAsync(type, [], cancellation).ConfigureAwait(false) != context.CircularConstraintType;

    private bool IsAbsent(Type type) => type == context.NoConstraintType || type == context.CircularConstraintType;

    internal ValueTask<Type> ResolvedBaseConstraintAsync(Type type, CancellationToken cancellation = default)
        => ResolvedBaseAsync(type, [], cancellation);

    internal async ValueTask<Type?> ConstraintAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is TypeParameter parameter)
            return await ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        if (type is IndexedAccessType indexed)
            return await HasNonCircularConstraintAsync(type, cancellation).ConfigureAwait(false)
                ? await IndexedConstraintAsync(indexed, cancellation).ConfigureAwait(false) : null;
        if (type is ConditionalType conditional)
            return await HasNonCircularConstraintAsync(type, cancellation).ConfigureAwait(false)
                ? await ConditionalConstraintAsync(conditional, cancellation).ConfigureAwait(false) : null;
        return await BaseConstraintAsync(type, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type?> SimplifiedOrConstraintAsync(Type type, CancellationToken cancellation)
    {
        var simplified = await host.SimplifyAsync(type, false, cancellation).ConfigureAwait(false);
        return simplified != type ? simplified : await ConstraintAsync(type, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type?> IndexedConstraintAsync(IndexedAccessType type, CancellationToken cancellation)
    {
        if (await host.IsMappedGenericAccessAsync(type, cancellation).ConfigureAwait(false))
            return await host.SubstituteMappedAccessAsync(type.ObjectType, type.IndexType, cancellation).ConfigureAwait(false);
        var index = await SimplifiedOrConstraintAsync(type.IndexType, cancellation).ConfigureAwait(false);
        if (index is not null && index != type.IndexType
            && await host.IndexedAccessAsync(type.ObjectType, index, type.AccessFlags, cancellation).ConfigureAwait(false) is { } result)
            return result;
        var objectType = await SimplifiedOrConstraintAsync(type.ObjectType, cancellation).ConfigureAwait(false);
        return objectType is not null && objectType != type.ObjectType
            ? await host.IndexedAccessAsync(objectType, type.IndexType, type.AccessFlags, cancellation).ConfigureAwait(false) : null;
    }

    internal async ValueTask<Type> ConditionalTrueAsync(
        ConditionalType type,
        bool inferred = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (!inferred)
            return type.ResolvedTrueType ??= await ConditionalBranchAsync(
                type.Root.Node.TrueType!,
                type.Mapper,
                cancellation).ConfigureAwait(false);
        return type.ResolvedInferredTrueType ??= type.CombinedMapper is null
            ? await ConditionalTrueAsync(type, false, cancellation).ConfigureAwait(false)
            : await ConditionalBranchAsync(type.Root.Node.TrueType!, type.CombinedMapper, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> ConditionalFalseAsync(ConditionalType type, CancellationToken cancellation = default)
        =>
            type.ResolvedFalseType ??= await ConditionalBranchAsync(
                type.Root.Node.FalseType!,
                type.Mapper,
                cancellation).ConfigureAwait(false);

    private async ValueTask<Type> ConditionalBranchAsync(SyntaxNode node, TypeMapper? mapper, CancellationToken cancellation)
        => await host.InstantiateAsync(
            await host.TypeFromNodeAsync(node, cancellation).ConfigureAwait(false),
            mapper,
            cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Conditional branch instantiation returned no type");

    internal async ValueTask<Type> ConditionalConstraintAsync(ConditionalType type, CancellationToken cancellation = default)
    {
        var distributed = await DistributiveConstraintAsync(type, cancellation).ConfigureAwait(false);
        if (distributed is not null)
            return distributed;
        if (type.ResolvedDefaultConstraint is null)
        {
            var trueType = await ConditionalTrueAsync(type, true, cancellation).ConfigureAwait(false);
            var falseType = await ConditionalFalseAsync(type, cancellation).ConfigureAwait(false);
            type.ResolvedDefaultConstraint = (trueType.Flags & F.Any) != 0 ? falseType : (falseType.Flags & F.Any) != 0 ? trueType
                : await algebra.UnionAsync([trueType, falseType], cancellation: cancellation).ConfigureAwait(false);
        }
        return type.ResolvedDefaultConstraint;
    }

    private async ValueTask<Type?> DistributiveConstraintAsync(ConditionalType type, CancellationToken cancellation)
    {
        if (type.ResolvedConstraintOfDistributive is null)
        {
            if (type.Root.IsDistributive && !host.IsRestrictiveInstantiation(type))
            {
                Type? constraint = await host.SimplifyAsync(type.CheckType, false, cancellation).ConfigureAwait(false);
                if (constraint == type.CheckType)
                    constraint = await ConstraintAsync(constraint, cancellation).ConfigureAwait(false);
                if (constraint is not null && constraint != type.CheckType)
                {
                    var mapper = TypeMapper.Prepend(type.Root.CheckType, constraint, type.Mapper);
                    var instantiated = await host.InstantiateConditionalAsync(type, mapper, true, cancellation).ConfigureAwait(false);
                    if ((instantiated.Flags & F.Never) == 0)
                        return type.ResolvedConstraintOfDistributive = instantiated;
                }
            }
            type.ResolvedConstraintOfDistributive = context.NoConstraintType;
        }
        return type.ResolvedConstraintOfDistributive == context.NoConstraintType ? null : type.ResolvedConstraintOfDistributive;
    }

    private async ValueTask<Type> ResolvedBaseAsync(Type type, IReadOnlyList<RecursionIdentity> stack, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is not ConstrainedType constrained)
            return type;
        if (constrained.ResolvedBaseConstraint is { } cached)
            return cached;
        if (!resolutions.Push(type, TypeSystemPropertyName.ResolvedBaseConstraint))
            return context.CircularConstraintType;
        Type? constraint = null;
        bool nonCircular;
        try
        {
            var identity = await recursion.IdentityAsync(type, cancellation).ConfigureAwait(false);
            // These are the reference's constraint exploration budgets, not native stack limits.
            if (stack.Count < 10 || stack.Count < 50 && !stack.Contains(identity))
            {
                var next = new RecursionIdentity[stack.Count + 1];
                for (int i = 0; i < stack.Count; i++)
                    next[i] = stack[i];
                next[^1] = identity;
                var simplified = await host.SimplifyAsync(type, false, cancellation).ConfigureAwait(false);
                constraint = await ComputeBaseAsync(simplified, next, cancellation).ConfigureAwait(false);
            }
        }
        finally
        {
            nonCircular = resolutions.Pop();
        }
        if (!nonCircular)
        {
            if (type is TypeParameter parameter && ConstraintDeclaration(parameter) is { } declaration)
                host.CircularConstraint(parameter, declaration);
            constraint = context.CircularConstraintType;
        }
        constraint ??= context.NoConstraintType;
        context.RequireOwned(constraint);
        return constrained.ResolvedBaseConstraint ??= constraint;
    }

    private async ValueTask<Type?> NextBaseAsync(Type? type, IReadOnlyList<RecursionIdentity> stack, CancellationToken cancellation)
    {
        if (type is null)
            return null;
        var result = await ResolvedBaseAsync(type, stack, cancellation).ConfigureAwait(false);
        return IsAbsent(result) ? null : result;
    }

    private async ValueTask<Type?> ComputeBaseAsync(Type type, IReadOnlyList<RecursionIdentity> stack, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        switch (type)
        {
            case TypeParameter parameter:
                var parameterConstraint = await ParameterConstraintWorkerAsync(parameter, cancellation).ConfigureAwait(false);
                return parameter.IsThisType
                    ? parameterConstraint
                    : await NextBaseAsync(parameterConstraint, stack, cancellation).ConfigureAwait(false);
            case UnionOrIntersectionType composite:
                var constraints = new List<Type>(composite.Types.Count);
                bool different = false;
                foreach (var part in composite.Types)
                {
                    var constraint = await NextBaseAsync(part, stack, cancellation).ConfigureAwait(false);
                    different |= constraint != part;
                    if (constraint is not null)
                        constraints.Add(constraint);
                }
                if (!different)
                    return type;
                if (type is UnionType && constraints.Count == composite.Types.Count)
                    return await algebra.UnionAsync(constraints, cancellation: cancellation).ConfigureAwait(false);
                if (type is IntersectionType && constraints.Count != 0)
                    return await algebra.IntersectionAsync(constraints, cancellation: cancellation).ConfigureAwait(false);
                return null;
            case IndexType index:
                if (index.Target is MappedType mapped && await host.IsGenericMappedAsync(mapped, cancellation).ConfigureAwait(false)
                    && await host.MappedNameTypeAsync(
                        mapped,
                        cancellation).ConfigureAwait(false) is not null && !host.HasKeyofConstraint(mapped))
                    return await NextBaseAsync(
                        await host.MappedIndexTypeAsync(mapped, cancellation).ConfigureAwait(false),
                        stack,
                        cancellation).ConfigureAwait(false);
                return context.StringNumberSymbolType;
            case TemplateLiteralType template:
                var parts = new List<Type>(template.Types.Count);
                foreach (var part in template.Types)
                    if (await NextBaseAsync(part, stack, cancellation).ConfigureAwait(false) is { } constraint)
                        parts.Add(constraint);
                return parts.Count == template.Types.Count
                    ? await algebra.TemplateAsync(template.Texts, parts, cancellation).ConfigureAwait(false)
                    : context.StringType;
            case StringMappingType mapping:
                var mappedConstraint = await NextBaseAsync(mapping.Target, stack, cancellation).ConfigureAwait(false);
                return mappedConstraint is not null && mappedConstraint != mapping.Target
                    ? await algebra.StringMappingAsync(
                        mapping.Symbol!,
                        mappedConstraint,
                        cancellation).ConfigureAwait(false) : context.StringType;
            case IndexedAccessType indexed:
                if (await host.IsMappedGenericAccessAsync(indexed, cancellation).ConfigureAwait(false))
                    return await NextBaseAsync(
                        await host.SubstituteMappedAccessAsync(indexed.ObjectType, indexed.IndexType, cancellation).ConfigureAwait(false),
                        stack,
                        cancellation).ConfigureAwait(false);
                var objectConstraint = await NextBaseAsync(indexed.ObjectType, stack, cancellation).ConfigureAwait(false);
                var indexConstraint = await NextBaseAsync(indexed.IndexType, stack, cancellation).ConfigureAwait(false);
                return objectConstraint is null || indexConstraint is null ? null
                    : await NextBaseAsync(
                        await host.IndexedAccessAsync(
                            objectConstraint,
                            indexConstraint,
                            indexed.AccessFlags,
                            cancellation).ConfigureAwait(false),
                        stack,
                        cancellation).ConfigureAwait(false);
            case ConditionalType conditional:
                if (conditionalDepth >= 100)
                    return null;
                Type? conditionalConstraint;
                conditionalDepth++;
                try
                {
                    conditionalConstraint = await ConditionalConstraintAsync(conditional, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    conditionalDepth--;
                }
                return await NextBaseAsync(conditionalConstraint, stack, cancellation).ConfigureAwait(false);
            case SubstitutionType substitution:
                var substituted = (substitution.Constraint.Flags & F.Unknown) != 0 ? substitution.BaseType
                    : await algebra.IntersectionAsync(
                        [substitution.Constraint, substitution.BaseType],
                        cancellation: cancellation).ConfigureAwait(false);
                return await NextBaseAsync(substituted, stack, cancellation).ConfigureAwait(false);
            case TypeReference reference when IsGenericTuple(type):
                var tuple = (TupleType)reference.ReferencedType;
                var elements = (await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false)).ToArray();
                for (int i = 0; i < elements.Length; i++)
                    if (elements[i] is TypeParameter && (tuple.ElementInfos[i].Flags & ElementFlags.Variadic) != 0)
                    {
                        var constraint = await NextBaseAsync(elements[i], stack, cancellation).ConfigureAwait(false);
                        if (constraint is not null && constraint != elements[i] && ArrayOrFixedTuple(constraint))
                            elements[i] = constraint;
                    }
                return await host.CreateTupleAsync(elements, tuple.ElementInfos, tuple.IsReadonly, cancellation).ConfigureAwait(false);
            default:
                return type;
        }
    }

    private bool ArrayOrFixedTuple(Type type)
    {
        if (type is UnionType union)
            return union.Types.All(ArrayOrFixedTuple);
        return (host.IsArrayType(type) || type is TypeReference { Target: TupleType }) && !IsGenericTuple(type);
    }

    internal static SyntaxNode? ConstraintDeclaration(TypeParameter parameter)
        =>
            parameter.Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().Select(d => d.Constraint).FirstOrDefault(n => n is not null);

    internal async ValueTask<Type?> ParameterConstraintAsync(TypeParameter parameter, CancellationToken cancellation = default)
        => await HasNonCircularConstraintAsync(parameter, cancellation).ConfigureAwait(false)
            ? await ParameterConstraintWorkerAsync(parameter, cancellation).ConfigureAwait(false) : null;

    private async ValueTask<Type?> ParameterConstraintWorkerAsync(TypeParameter parameter, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (parameter.Constraint is null)
        {
            Type? constraint;
            if (parameter.Target is { } target)
                constraint = await host.InstantiateAsync(
                    await ParameterConstraintAsync(target, cancellation).ConfigureAwait(false),
                    parameter.Mapper,
                    cancellation).ConfigureAwait(false);
            else if (ConstraintDeclaration(parameter) is { } declaration)
            {
                constraint = await host.TypeFromNodeAsync(declaration, cancellation).ConfigureAwait(false);
                if ((constraint.Flags & F.Any) != 0 && constraint != context.ErrorType && constraint.Alias is null)
                    constraint = declaration.Parent?.Parent is MappedTypeNode ? context.StringNumberSymbolType : context.UnknownType;
            }
            else
                constraint = await host.InferredParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false);
            constraint ??= context.NoConstraintType;
            context.RequireOwned(constraint);
            parameter.Constraint = constraint;
        }
        return parameter.Constraint == context.NoConstraintType ? null : parameter.Constraint;
    }

    internal async ValueTask<Type?> DefaultAsync(TypeParameter parameter, CancellationToken cancellation = default)
    {
        var result = await ResolvedDefaultAsync(parameter, cancellation).ConfigureAwait(false);
        return IsAbsent(result) ? null : result;
    }

    internal async ValueTask<Type> ResolvedDefaultAsync(TypeParameter parameter, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(parameter);
        if (parameter.ResolvedDefaultType is null)
        {
            if (parameter.Target is { } target)
            {
                var targetDefault = await ResolvedDefaultAsync(target, cancellation).ConfigureAwait(false);
                parameter.ResolvedDefaultType = await host.InstantiateAsync(
                    targetDefault,
                    parameter.Mapper,
                    cancellation).ConfigureAwait(false)
                    ?? context.NoConstraintType;
            }
            else
            {
                parameter.ResolvedDefaultType = context.ResolvingDefaultType;
                try
                {
                    var declaration = parameter.Symbol?.Declarations.OfType<TypeParameterDeclarationNode>().Select(d => d.DefaultType).FirstOrDefault(n => n is not null);
                    var result = declaration is null
                        ? context.NoConstraintType
                        : await host.TypeFromNodeAsync(declaration, cancellation).ConfigureAwait(false);
                    context.RequireOwned(result);
                    if (parameter.ResolvedDefaultType == context.ResolvingDefaultType)
                        parameter.ResolvedDefaultType = result;
                }
                catch
                {
                    parameter.ResolvedDefaultType = null;
                    throw;
                }
            }
        }
        else if (parameter.ResolvedDefaultType == context.ResolvingDefaultType)
            parameter.ResolvedDefaultType = context.CircularConstraintType;
        return parameter.ResolvedDefaultType;
    }

    internal async ValueTask<IReadOnlyList<Type>> FillMissingArgumentsAsync(
        IReadOnlyList<Type> arguments,
        IReadOnlyList<TypeParameter> parameters,
        bool javaScriptImplicitAny,
        Func<Type, Type, CancellationToken, ValueTask<bool>> identical,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        foreach (var argument in arguments)
            context.RequireOwned(argument);
        foreach (var parameter in parameters)
            context.RequireOwned(parameter);
        if (parameters.Count == 0)
            return [];
        if (!javaScriptImplicitAny && arguments.Count >= parameters.Count)
            return arguments;
        var result = new Type[parameters.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = i < arguments.Count ? arguments[i] : context.ErrorType;
        Type fallback = javaScriptImplicitAny ? context.AnyType : context.UnknownType;
        for (int i = arguments.Count; i < parameters.Count; i++)
        {
            var defaultType = await DefaultAsync(parameters[i], cancellation).ConfigureAwait(false);
            if (javaScriptImplicitAny && defaultType is not null
                && (await identical(defaultType, context.UnknownType, cancellation).ConfigureAwait(false)
                    || await identical(defaultType, context.EmptyObjectType, cancellation).ConfigureAwait(false)))
                defaultType = context.AnyType;
            result[i] = defaultType is null ? fallback
                : await host.InstantiateAsync(
                    defaultType,
                    TypeMapper.Create(parameters.ToArray(), result),
                    cancellation).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Type argument default instantiation returned no type");
        }
        return Array.AsReadOnly(result);
    }
}
