using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeKeyHost
{
    ValueTask<IReadOnlyList<Symbol>> PropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation);

    CheckFlags AccessFlags(Symbol symbol, bool write);

    IndexInfo EnumNumberIndex { get; }
}

// keyof preserves origins separately from the literal keys. Generic intersections
// are evaluated with a unique literal substitution before deciding to defer them.
internal sealed class TypeKeys(TypeContext context, TypeAlgebra algebra, TypeViews views,
    TypeInstantiation instantiation, MappedTypes mapped, MappedMembers members, ITypeKeyHost host)
{
    private readonly Dictionary<(Type, TypeFlags, bool, bool), Type> propertyTypes = [];
    private readonly TypeMapper uniqueLiterals = TypeMapper.Function(t => t is TypeParameter ? context.UniqueLiteralType : t);

    internal async ValueTask<Type> GetAsync(Type type, IndexFlags flags = 0, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        type = await views.ReducedAsync(type, cancellation).ConfigureAwait(false);
        if (type is SubstitutionType substitution && (substitution.Constraint.Flags & TypeFlags.Unknown) != 0)
            return await instantiation.NoInferAsync(
                await GetAsync(substitution.BaseType, flags, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        if (await ShouldDeferAsync(type, flags, cancellation).ConfigureAwait(false))
            return context.GetIndexTypeForGenericType(type, flags);
        if (type is UnionOrIntersectionType composite)
        {
            var keys = new Type[composite.Types.Count];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = await GetAsync(composite.Types[i], flags, cancellation).ConfigureAwait(false);
            return type is UnionType ? await algebra.IntersectionAsync(keys, cancellation: cancellation).ConfigureAwait(false)
                : await algebra.UnionAsync(keys, cancellation: cancellation).ConfigureAwait(false);
        }
        if (type is MappedType mapping)
            return await MappedAsync(mapping, flags, cancellation).ConfigureAwait(false);
        if (type == context.WildcardType)
            return type;
        if ((type.Flags & TypeFlags.Unknown) != 0)
            return context.NeverType;
        if ((type.Flags & (TypeFlags.Any | TypeFlags.Never)) != 0)
            return context.StringNumberSymbolType;
        var include = ((flags & IndexFlags.NoIndexSignatures) != 0 ? TypeFlags.StringLiteral : TypeFlags.StringLike)
            | ((flags & IndexFlags.StringsOnly) != 0 ? 0 : TypeFlags.NumberLike | TypeFlags.ESSymbolLike);
        return await FromPropertiesAsync(type, include, flags == 0, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<bool> ShouldDeferAsync(Type type, IndexFlags flags = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & TypeFlags.InstantiableNonPrimitive) != 0 || TypeConstraints.IsGenericTuple(type)
            || type is MappedType mapping && await mapped.IsGenericAsync(mapping, cancellation).ConfigureAwait(false)
                && await mapped.NameAsync(mapping, cancellation).ConfigureAwait(false) is not null
            || type is UnionType && (flags & IndexFlags.NoReducibleCheck) == 0
                && await GenericReducibleAsync(type, cancellation).ConfigureAwait(false))
            return true;
        if (type is IntersectionType intersection && Includes(type, TypeFlags.Instantiable))
            foreach (var part in intersection.Types)
                if (await views.EmptyAnonymousAsync(part, cancellation).ConfigureAwait(false))
                    return true;
        return false;
    }

    internal async ValueTask<bool> GenericReducibleAsync(Type type, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is UnionType union && (union.ObjectFlags & ObjectFlags.ContainsIntersections) != 0)
            {
                for (int i = union.Types.Count - 1; i >= 0; i--)
                    pending.Push(union.Types[i]);
            }
            else if (current is IntersectionType intersection)
            {
                var filled = intersection.UniqueLiteralFilledInstantiation;
                if (filled is null)
                {
                    filled = await instantiation.InstantiateAsync(
                        intersection,
                        uniqueLiterals,
                        cancellation: cancellation).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("Intersection instantiation returned no type");
                    cancellation.ThrowIfCancellationRequested();
                    intersection.UniqueLiteralFilledInstantiation = filled;
                }
                if (await views.ReducedAsync(filled, cancellation).ConfigureAwait(false) != filled)
                    return true;
            }
        }
        return false;
    }

    internal async ValueTask<Type> FromPropertiesAsync(Type type, TypeFlags include, bool includeOrigin,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var key = (type, include, includeOrigin, (type.ObjectFlags & ObjectFlags.UnresolvedMembers) != 0);
        if (propertyTypes.TryGetValue(key, out var cached))
            return cached;
        Type? origin = includeOrigin && (type.ObjectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0
            || type.Alias is not null
            ? new IndexType(context, type, 0) : null;
        var properties = await host.PropertiesAsync(type, cancellation).ConfigureAwait(false);
        var indexes = await host.IndexesAsync(type, cancellation).ConfigureAwait(false);
        var keys = new List<Type>();
        foreach (var property in properties)
            keys.Add(await PropertyAsync(property, include, false, cancellation).ConfigureAwait(false));
        foreach (var index in indexes)
        {
            cancellation.ThrowIfCancellationRequested();
            if (index != host.EnumNumberIndex && KeyIncluded(index.KeyType, include))
                keys.Add(
                    index.KeyType == context.StringType && (include & TypeFlags.Number) != 0 ? context.StringOrNumberType : index.KeyType);
        }
        var result = await algebra.UnionAsync(keys, origin: origin, cancellation: cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        propertyTypes[key] = result;
        return result;
    }

    internal async ValueTask<Type> PropertyAsync(Symbol property, TypeFlags include, bool includeNonPublic,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!includeNonPublic && (host.AccessFlags(property, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)) != 0)
            return context.NeverType;
        var type = await host.PropertyNameTypeAsync(property, cancellation).ConfigureAwait(false);
        context.RequireOwned(type);
        return (type.Flags & include) != 0 ? type : context.NeverType;
    }

    private async ValueTask<Type> MappedAsync(MappedType type, IndexFlags flags, CancellationToken cancellation)
    {
        var parameter = await mapped.ParameterAsync(type, cancellation).ConfigureAwait(false);
        var constraint = await mapped.ConstraintAsync(type, cancellation).ConfigureAwait(false);
        var name = await mapped.NameAsync((MappedType?)type.Target ?? type, cancellation).ConfigureAwait(false);
        if (name is null && (flags & IndexFlags.NoIndexSignatures) == 0)
            return constraint;
        var keys = new List<Type>();
        async ValueTask AddAsync(Type key)
        {
            var result = name is null ? key : await instantiation.InstantiateAsync(name,
                TypeMapper.Append(type.Mapper, parameter, key), cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Mapped name instantiation returned no type");
            keys.Add(result == context.StringType ? context.StringOrNumberType : result);
        }
        if ((await mapped.GenericFlagsAsync(constraint, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) != 0)
        {
            if (MappedMembers.HasKeyofConstraint(type))
                return context.GetIndexTypeForGenericType(type, flags);
            foreach (var part in Parts(constraint))
                await AddAsync(part).ConfigureAwait(false);
        }
        else if (MappedMembers.HasKeyofConstraint(type))
        {
            var modifiers = await views.ApparentAsync(
                await members.ModifiersTypeAsync(type, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            foreach (var property in await host.PropertiesAsync(modifiers, cancellation).ConfigureAwait(false))
                await AddAsync(
                    await PropertyAsync(
                        property,
                        TypeFlags.StringOrNumberLiteralOrUnique,
                        false,
                        cancellation).ConfigureAwait(false)).ConfigureAwait(false);
            if ((modifiers.Flags & TypeFlags.Any) != 0)
                await AddAsync(context.StringType).ConfigureAwait(false);
            else
                foreach (var index in await host.IndexesAsync(modifiers, cancellation).ConfigureAwait(false))
                    if ((flags & IndexFlags.StringsOnly) == 0
                        || (index.KeyType.Flags & (TypeFlags.String | TypeFlags.TemplateLiteral)) != 0)
                        await AddAsync(index.KeyType).ConfigureAwait(false);
        }
        else
            foreach (var part in Parts(await members.LowerBoundAsync(constraint, cancellation).ConfigureAwait(false)))
                await AddAsync(part).ConfigureAwait(false);
        var result = await algebra.UnionAsync(keys, cancellation: cancellation).ConfigureAwait(false);
        if ((flags & IndexFlags.NoIndexSignatures) != 0)
            result = algebra.Filter(result, t => (t.Flags & (TypeFlags.Any | TypeFlags.String)) == 0);
        return result is UnionType union && constraint is UnionType original && union.Types.SequenceEqual(original.Types)
            ? constraint
            : result;
    }

    private static IReadOnlyList<Type> Parts(Type type) => type is UnionType union ? union.Types : [type];

    internal static bool KeyIncluded(Type type, TypeFlags flags) => Includes(type, flags, intersectionsOnly: true);

    private static bool Includes(Type type, TypeFlags flags, bool intersectionsOnly = false)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            if ((current.Flags & flags) != 0)
                return true;
            if (current is UnionOrIntersectionType composite && (!intersectionsOnly || current is IntersectionType))
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return false;
    }
}
