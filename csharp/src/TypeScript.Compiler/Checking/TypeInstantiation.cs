using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal interface ITypeInstantiationHost
{
    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> NormalizedReferenceAsync(InterfaceType target, IReadOnlyList<Type> arguments, CancellationToken cancellation);

    ValueTask<Type> ObjectInstantiationAsync(ObjectType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation);

    ValueTask<Type?> InferReverseMappedAsync(Type source, MappedType mapped, IndexType constraint, CancellationToken cancellation);

    ValueTask<Type> IndexTypeAsync(Type target, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, AccessFlags flags, TypeAlias? alias, CancellationToken cancellation);

    ValueTask<Type> ConditionalInstantiationAsync(ConditionalType type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation);

    ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> IsAssignableAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<bool> IsEmptyAnonymousAsync(Type type, CancellationToken cancellation);

    void InstantiationLimit(int depth, int count);
}

// An exclusive checker owns this engine. Active-mapper caches are scoped to one
// instantiation chain; only completed types are cached and failures unwind scopes.
internal sealed partial class TypeInstantiation(TypeContext context, TypeAlgebra algebra, CheckerLinks links, ITypeInstantiationHost host)
{
    private readonly TypeVariables variables = new(host.TypeArgumentsAsync);
    private readonly List<(TypeMapper Mapper, InstantiationCache Cache)> active = [];
    private readonly Stack<InstantiationCache> availableCaches = [];

    private sealed class InstantiationCache
    {
        private readonly Dictionary<uint, Type> types = [];
        private Dictionary<TypeCacheKey, Type>? aliases;

        internal bool TryGet(uint type, TypeCacheKey? alias, out Type? result)
        {
            if (alias is not { } key)
                return types.TryGetValue(type, out result);
            result = null;
            return aliases is not null && aliases.TryGetValue(key, out result);
        }

        internal void Set(uint type, TypeCacheKey? alias, Type result)
        {
            if (alias is { } key)
                (aliases ??= [])[key] = result;
            else
                types[type] = result;
        }

        internal void Clear()
        {
            types.Clear();
            aliases?.Clear();
        }
    }
    private int depth, count;
    private readonly Dictionary<Type, Type> restrictiveTypes = [];
    private readonly Dictionary<Type, Type> permissiveTypes = [];
    private readonly Dictionary<TypeParameter, TypeParameter> restrictiveParameters = [];
    private TypeMapper? restrictiveMapper, permissiveMapper;
    internal TypeMapper PermissiveMapper =>
        permissiveMapper ??= TypeMapper.Function(type => type is TypeParameter ? context.WildcardType : type);
    private TypeMapper RestrictiveMapper => restrictiveMapper ??= TypeMapper.Function(RestrictiveParameter);
    internal long TotalCount { get; private set; }
    internal int Depth => depth;
    internal int Count => count;
    internal int ActiveMappers => active.Count;

    internal bool IsRestrictive(Type type) => restrictiveTypes.TryGetValue(type, out var result) && result == type;

    internal void ResetStatementCount()
    {
        if (depth != 0)
            throw new InvalidOperationException("Cannot reset the instantiation budget inside an active instantiation");
        count = 0;
    }

    internal void ResetExpressionCount() => count = 0;

    internal void ClearActiveCaches()
    {
        foreach (var entry in active)
            entry.Cache.Clear();
    }

    internal async ValueTask<Type?> InstantiateAsync(
        Type? type,
        TypeMapper? mapper,
        TypeAlias? alias = null,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (type is null)
            return null;
        context.RequireOwned(type);
        if (alias is not null)
            foreach (var argument in alias.TypeArguments)
                context.RequireOwned(argument);
        if (mapper is null)
            return type;
        bool affected = await variables.CouldContainAsync(type, cancellation).ConfigureAwait(false);
        if (!affected && type.Alias is { } existingAlias)
            foreach (var argument in existingAlias.TypeArguments)
                if (await variables.CouldContainAsync(argument, cancellation).ConfigureAwait(false))
                {
                    affected = true;
                    break;
                }
        if (!affected)
            return type;
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (depth == 100 || count >= 5_000_000)
        {
            host.InstantiationLimit(depth, count);
            return context.ErrorType;
        }
        int index = active.Count - 1;
        while (index >= 0 && active[index].Mapper != mapper)
            index--;
        bool ownsScope = index < 0;
        InstantiationCache cache;
        if (ownsScope)
        {
            cache = availableCaches.TryPop(out var reusable) ? reusable : new();
            active.Add((mapper, cache));
        }
        else
            cache = active[index].Cache;
        // A newly opened scope has an empty cache and never stores its own result.
        // Alias keys still assign the symbol's lazy identity in reference order.
        TypeCacheKey? key = alias is null ? null : TypeCacheKey.Union([type], null, alias);
        try
        {
            if (!ownsScope && cache.TryGet(type.Id, key, out var cached))
                return cached;
            TotalCount++;
            count++;
            depth++;
            Type result;
            try
            {
                result = await WorkerAsync(type, mapper, alias, cancellation).ConfigureAwait(false);
            }
            finally
            {
                depth--;
            }
            context.RequireOwned(result);
            if (!ownsScope)
                cache.Set(type.Id, key, result);
            return result;
        }
        finally
        {
            if (ownsScope)
            {
                active.RemoveAt(active.Count - 1);
                cache.Clear();
                availableCaches.Push(cache);
            }
        }
    }

    private async ValueTask<Type> RequiredAsync(Type type, TypeMapper mapper, CancellationToken cancellation)
        => await InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Instantiation of a present type returned no type");

    internal TypeMapper Combine(TypeMapper? first, TypeMapper second)
        => TypeMapper.CombineAsync(first, second, RequiredAsync);

    private async ValueTask<Type> WorkerAsync(Type type, TypeMapper mapper, TypeAlias? alias, CancellationToken cancellation)
    {
        switch (type)
        {
            case TypeParameter:
                return await mapper.MapTypeAsync(type, cancellation).ConfigureAwait(false);
            case ObjectType objectType:
                if ((type.ObjectFlags & (O.Reference | O.Anonymous | O.Mapped)) == 0)
                    return type;
                if ((type.ObjectFlags & O.Reference) != 0 && type is TypeReference { Node: null } reference)
                {
                    var arguments = reference.ResolvedTypeArguments ?? throw new InvalidOperationException("Non-deferred reference has unresolved arguments");
                    var instantiated = await TypesAsync(arguments, mapper, cancellation).ConfigureAwait(false);
                    return ReferenceEquals(arguments, instantiated) ? type
                        : await host.NormalizedReferenceAsync(
                            (InterfaceType)reference.ReferencedType,
                            instantiated,
                            cancellation).ConfigureAwait(false);
                }
                if (type is ReverseMappedType reverse)
                {
                    var innerMapped = await RequiredAsync(reverse.MappedType!, mapper, cancellation).ConfigureAwait(false);
                    if (innerMapped is not MappedType mapped)
                        return type;
                    var innerIndex = await RequiredAsync(reverse.ConstraintType!, mapper, cancellation).ConfigureAwait(false);
                    if (innerIndex is not IndexType index)
                        return type;
                    var source = await RequiredAsync(reverse.Source!, mapper, cancellation).ConfigureAwait(false);
                    return await host.InferReverseMappedAsync(source, mapped, index, cancellation).ConfigureAwait(false) ?? type;
                }
                return await host.ObjectInstantiationAsync(objectType, mapper, alias, cancellation).ConfigureAwait(false);
            case UnionOrIntersectionType composite:
                var sourceType = type is UnionType { Origin: UnionOrIntersectionType origin } ? origin : composite;
                var mappedTypes = await TypesAsync(sourceType.Types, mapper, cancellation).ConfigureAwait(false);
                if (ReferenceEquals(mappedTypes, sourceType.Types) && alias?.Symbol == type.Alias?.Symbol)
                    return type;
                alias ??= await AliasAsync(type.Alias, mapper, cancellation).ConfigureAwait(false);
                return sourceType is IntersectionType
                    ? await algebra.IntersectionAsync(mappedTypes, alias: alias, cancellation: cancellation).ConfigureAwait(false)
                    : await algebra.UnionAsync(mappedTypes, alias: alias, cancellation: cancellation).ConfigureAwait(false);
            case IndexType index:
                return await host.IndexTypeAsync(
                    await RequiredAsync(index.Target, mapper, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            case IndexedAccessType indexed:
                alias ??= await AliasAsync(type.Alias, mapper, cancellation).ConfigureAwait(false);
                var objectArgument = await RequiredAsync(indexed.ObjectType, mapper, cancellation).ConfigureAwait(false);
                var indexArgument = await RequiredAsync(indexed.IndexType, mapper, cancellation).ConfigureAwait(false);
                return await host.IndexedAccessAsync(
                    objectArgument,
                    indexArgument,
                    indexed.AccessFlags,
                    alias,
                    cancellation).ConfigureAwait(false);
            case TemplateLiteralType template:
                return await algebra.TemplateAsync(
                    template.Texts,
                    await TypesAsync(template.Types, mapper, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            case StringMappingType mapping:
                return await algebra.StringMappingAsync(
                    mapping.Symbol!,
                    await RequiredAsync(mapping.Target, mapper, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
            case ConditionalType conditional:
                return await host.ConditionalInstantiationAsync(
                    conditional,
                    Combine(conditional.Mapper, mapper),
                    alias,
                    cancellation).ConfigureAwait(false);
            case SubstitutionType substitution:
                var newBase = await RequiredAsync(substitution.BaseType, mapper, cancellation).ConfigureAwait(false);
                if ((substitution.Constraint.Flags & F.Unknown) != 0)
                    return await NoInferAsync(newBase, cancellation).ConfigureAwait(false);
                var constraint = await RequiredAsync(substitution.Constraint, mapper, cancellation).ConfigureAwait(false);
                if ((newBase.Flags & F.TypeVariable) != 0 && await host.IsGenericTypeAsync(constraint, cancellation).ConfigureAwait(false))
                    return context.GetSubstitutionType(newBase, constraint);
                if ((constraint.Flags & F.AnyOrUnknown) != 0)
                    return newBase;
                var restrictiveBase = await RestrictiveAsync(newBase, cancellation).ConfigureAwait(false);
                var restrictiveConstraint = await RestrictiveAsync(constraint, cancellation).ConfigureAwait(false);
                if (await host.IsAssignableAsync(restrictiveBase, restrictiveConstraint, cancellation).ConfigureAwait(false))
                    return newBase;
                return (newBase.Flags & F.TypeVariable) != 0 ? context.GetSubstitutionType(newBase, constraint)
                    : await algebra.IntersectionAsync([constraint, newBase], cancellation: cancellation).ConfigureAwait(false);
            default:
                return type;
        }
    }

    internal async ValueTask<IReadOnlyList<Type>> TypesAsync(
        IReadOnlyList<Type> types,
        TypeMapper mapper,
        CancellationToken cancellation = default)
    {
        Type[]? result = null;
        for (int i = 0; i < types.Count; i++)
        {
            var mapped = await RequiredAsync(types[i], mapper, cancellation).ConfigureAwait(false);
            if (mapped != types[i])
                result ??= types.ToArray();
            if (result is not null)
                result[i] = mapped;
        }
        return result is null ? types : Array.AsReadOnly(result);
    }

    internal async ValueTask<TypeAlias?> AliasAsync(TypeAlias? alias, TypeMapper mapper, CancellationToken cancellation = default)
        =>
            alias is null
                ? null
                : context.CreateAlias(
                    alias.Symbol,
                    (await TypesAsync(alias.TypeArguments, mapper, cancellation).ConfigureAwait(false)).ToArray());

    internal async ValueTask<Type> RestrictiveAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & (F.Primitive | F.AnyOrUnknown | F.Never)) != 0)
            return type;
        if (restrictiveTypes.TryGetValue(type, out var cached))
            return cached;
        var result = await RequiredAsync(type, RestrictiveMapper, cancellation).ConfigureAwait(false);
        restrictiveTypes[type] = result;
        restrictiveTypes[result] = result;
        return result;
    }

    internal async ValueTask<Type> PermissiveAsync(Type type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & (F.Primitive | F.AnyOrUnknown | F.Never)) != 0)
            return type;
        if (permissiveTypes.TryGetValue(type, out var cached))
            return cached;
        var result = await RequiredAsync(type, PermissiveMapper, cancellation).ConfigureAwait(false);
        permissiveTypes[type] = result;
        return result;
    }

    private Type RestrictiveParameter(Type type)
    {
        if (type is not TypeParameter parameter)
            return type;
        if (parameter.Constraint is null && TypeConstraints.ConstraintDeclaration(parameter) is null
            || parameter.Constraint == context.NoConstraintType)
            return type;
        if (!restrictiveParameters.TryGetValue(parameter, out var result))
            restrictiveParameters.Add(parameter, result = context.NewTypeParameter(parameter.Symbol));
        result.Constraint = context.NoConstraintType;
        return result;
    }

    internal async ValueTask<Type> NoInferAsync(Type type, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is UnionOrIntersectionType composite)
            {
                foreach (var part in composite.Types)
                    pending.Push(part);
                continue;
            }
            if (current is SubstitutionType substitution)
            {
                if ((substitution.Constraint.Flags & F.Unknown) == 0)
                    pending.Push(substitution.BaseType);
                continue;
            }
            if ((current.Flags & F.Object) != 0 && !await host.IsEmptyAnonymousAsync(current, cancellation).ConfigureAwait(false)
                || (current.Flags & (F.Instantiable & ~F.Substitution)) != 0 && !TypeAlgebra.IsPatternLiteral(current))
                return context.GetOrCreateSubstitutionType(type, context.UnknownType);
        }
        return type;
    }
}
