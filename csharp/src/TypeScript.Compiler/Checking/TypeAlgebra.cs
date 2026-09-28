using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TypeScript.Compiler.Binding;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

// These operations require the checker's relations, member resolution and
// constraints. There is deliberately no default implementation of those services.
internal interface ITypeAlgebraHost
{
    ValueTask<Type?> GetBaseConstraintAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> IsSubtypeAsync(Type source, Type target, bool strict, CancellationToken cancellation);

    ValueTask<bool> IsDerivedFromAsync(Type source, Type target, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Symbol>> GetPropertiesAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> GetTypeOfSymbolAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type?> GetPropertyTypeAsync(Type type, TextSlice name, CancellationToken cancellation);

    ValueTask<bool> IsEmptyAnonymousObjectAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> IsEmptyObjectAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> IsGenericIndexAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> MatchesPatternAsync(Type literal, Type pattern, CancellationToken cancellation);

    void ReportComplexity(TextSlice operation, long size);
}

internal sealed partial class TypeAlgebra(TypeContext context, TypeOrder order, ITypeAlgebraHost host)
{
    internal TypeOrder Order => order;
    private readonly Dictionary<(uint First, uint Second, UnionReduction Reduction, TypeCacheKey Alias), Type> unionPairs = [];
    private readonly Dictionary<(TypeCacheKey Types, IntersectionFlags Flags), Type> intersections = [];
    private readonly Dictionary<TypeCacheKey, Type[]> subtypeReductions = [];

    private void RequireOwned(IReadOnlyList<Type> types, TypeAlias? alias = null)
    {
        foreach (var type in types)
            context.RequireOwned(type);
        if (alias is not null)
            foreach (var type in alias.TypeArguments)
                context.RequireOwned(type);
    }

    internal async ValueTask<Type> UnionAsync(IReadOnlyList<Type> types, UnionReduction reduction = UnionReduction.Literal,
        TypeAlias? alias = null, Type? origin = null, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        RequireOwned(types, alias);
        if (origin is not null)
            context.RequireOwned(origin);
        if (types.Count == 0)
            return context.NeverType;
        if (types.Count == 1)
            return types[0];
        if (types.Count == 2 && origin is null && (types[0] is UnionType || types[1] is UnionType))
        {
            uint first = types[0].Id, second = types[1].Id;
            if (first > second)
                (first, second) = (second, first);
            var key = (first, second, reduction, alias is null ? default : TypeCacheKey.Union([], null, alias));
            if (unionPairs.TryGetValue(key, out var cached))
                return cached;
            var result = await UnionWorker(types, reduction, alias, null, cancellation).ConfigureAwait(false);
            unionPairs[key] = result;
            return result;
        }
        return await UnionWorker(types, reduction, alias, origin, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> UnionWorker(IReadOnlyList<Type> types, UnionReduction reduction, TypeAlias? alias,
        Type? origin, CancellationToken cancellation)
    {
        var (set, includes) = AddUnionTypes(types, cancellation);
        if (reduction != UnionReduction.None)
        {
            if ((includes & F.AnyOrUnknown) != 0)
                return (includes & F.Any) == 0 ? context.UnknownType : IncludedAny(includes);
            if ((includes & F.Undefined) != 0 && set.Count >= 2 && set[0] == context.UndefinedType && set[1] == context.MissingType)
                set.RemoveAt(1);
            if ((includes & (F.Enum | F.Literal | F.UniqueESSymbol | F.TemplateLiteral | F.StringMapping)) != 0
                || (includes & (F.Void | F.Undefined)) == (F.Void | F.Undefined))
            {
                for (int i = set.Count - 1; i >= 0; i--)
                {
                    var type = set[i];
                    var flags = type.Flags;
                    if ((flags & (F.StringLiteral | F.TemplateLiteral | F.StringMapping)) != 0 && (includes & F.String) != 0
                        || (flags & F.NumberLiteral) != 0 && (includes & F.Number) != 0
                        || (flags & F.BigIntLiteral) != 0 && (includes & F.BigInt) != 0
                        || (flags & F.UniqueESSymbol) != 0 && (includes & F.ESSymbol) != 0
                        || reduction == UnionReduction.Subtype && (flags & F.Undefined) != 0 && (includes & F.Void) != 0
                        || type is LiteralType literal && literal.IsFreshLiteral && set.BinarySearch(literal.RegularType, order) >= 0)
                        set.RemoveAt(i);
                }
            }
            if ((includes & F.StringLiteral) != 0 && (includes & (F.TemplateLiteral | F.StringMapping)) != 0)
            {
                var patterns = set.Where(IsPatternLiteral).ToArray();
                for (int i = set.Count - 1; i >= 0; i--)
                    if ((set[i].Flags & F.StringLiteral) != 0)
                        foreach (var pattern in patterns)
                            if (await host.MatchesPatternAsync(set[i], pattern, cancellation).ConfigureAwait(false))
                            {
                                set.RemoveAt(i);
                                break;
                            }
            }
            if ((includes & F.IncludesConstrainedTypeVariable) != 0)
                await RemoveConstrainedVariables(set, cancellation).ConfigureAwait(false);
            if (reduction == UnionReduction.Subtype)
            {
                var reduced = await RemoveSubtypes(set, (includes & F.Object) != 0, cancellation).ConfigureAwait(false);
                if (reduced is null)
                    return context.ErrorType;
                set = reduced;
            }
            if (set.Count == 0)
            {
                bool regular = (includes & F.IncludesNonWideningType) != 0;
                if ((includes & F.Null) != 0)
                    return regular ? context.NullType : context.NullWideningType;
                if ((includes & F.Undefined) != 0)
                    return regular ? context.UndefinedType : context.UndefinedWideningType;
                return context.NeverType;
            }
        }
        if (origin is null && (includes & F.Union) != 0)
        {
            var named = NamedUnions(types, cancellation);
            var covered = new HashSet<Type>(named.SelectMany(t => t.Types));
            var remaining = set.Where(t => !covered.Contains(t)).ToList();
            if (alias is null && named.Count == 1 && remaining.Count == 0)
                return named[0];
            if (named.Sum(t => (long)t.Types.Count) + remaining.Count == set.Count)
            {
                foreach (var type in named)
                    Insert(remaining, type);
                origin = context.NewUnionType(remaining.ToArray());
            }
        }
        var objectFlags = (includes & F.NotPrimitiveUnion) == 0 ? O.PrimitiveUnion : O.None;
        if ((includes & F.Intersection) != 0)
            objectFlags |= O.ContainsIntersections;
        return context.GetUnionFromSortedTypes(CollectionsMarshal.AsSpan(set), objectFlags, alias, origin);
    }

    private (List<Type> Types, F Includes) AddUnionTypes(IReadOnlyList<Type> source, CancellationToken cancellation)
    {
        var types = new List<Type>(source.Count);
        F includes = 0;
        // Union constituents are already sorted. Adding another type (often
        // undefined) or union only needs a merge, not another structural sort.
        if (source.Count == 2 && (source[0] is UnionType || source[1] is UnionType))
        {
            var left = source[0] as UnionType;
            var right = source[1] as UnionType;
            if (left is { Alias: not null } or { Origin: not null } || right is { Alias: not null } or { Origin: not null })
                includes |= F.Union;
            int leftCount = left?.Types.Count ?? 1, rightCount = right?.Types.Count ?? 1;
            types.EnsureCapacity(leftCount + rightCount);
            int i = 0, j = 0;
            while (i < leftCount && j < rightCount)
            {
                cancellation.ThrowIfCancellationRequested();
                var a = left is null ? source[0] : left.Types[i];
                var b = right is null ? source[1] : right.Types[j];
                int comparison = order.Compare(a, b);
                Add(comparison <= 0 ? a : b);
                if (comparison <= 0) i++;
                if (comparison >= 0) j++;
            }
            for (; i < leftCount; i++) Add(left is null ? source[0] : left.Types[i]);
            for (; j < rightCount; j++) Add(right is null ? source[1] : right.Types[j]);
            return (types, includes);
        }
        Type? previous = null;
        foreach (var type in source)
        {
            cancellation.ThrowIfCancellationRequested();
            if (type == previous)
                continue;
            if (type is UnionType union)
            {
                if (union.Alias is not null || union.Origin is not null)
                    includes |= F.Union;
                foreach (var constituent in union.Types)
                    Add(constituent);
            }
            else
                Add(type);
            previous = type;
        }
        // Flattened unions frequently arrive in canonical order already. Avoid
        // sorting them again: structural type comparison can traverse whole types.
        for (int i = 1; i < types.Count; i++)
            if (order.Compare(types[i - 1], types[i]) > 0)
            {
                types.Sort(order);
                break;
            }
        if (types.Count > 1)
        {
            int unique = 1;
            for (int i = 1; i < types.Count; i++)
                if (types[i] != types[unique - 1])
                    types[unique++] = types[i];
            types.RemoveRange(unique, types.Count - unique);
        }
        return (types, includes);

        void Add(Type type)
        {
            F flags = type.Flags;
            if ((flags & F.Never) != 0)
                return;
            includes |= flags & F.IncludesMask;
            if ((flags & F.Instantiable) != 0)
                includes |= F.IncludesInstantiable;
            if (IsConstrainedVariable(type))
                includes |= F.IncludesConstrainedTypeVariable;
            if (type == context.WildcardType)
                includes |= F.IncludesWildcard;
            if (IsError(type))
                includes |= F.IncludesError;
            if (!context.StrictNullChecks && (flags & F.Nullable) != 0)
            {
                if ((type.ObjectFlags & O.ContainsWideningType) == 0)
                    includes |= F.IncludesNonWideningType;
            }
            else
                types.Add(type);
        }
    }

    private static List<UnionType> NamedUnions(IReadOnlyList<Type> types, CancellationToken cancellation)
    {
        var result = new List<UnionType>();
        var seen = new HashSet<UnionType>();
        var pending = new Stack<Type>(types.Reverse());
        while (pending.TryPop(out var type))
        {
            cancellation.ThrowIfCancellationRequested();
            if (type is not UnionType union)
                continue;
            if (union.Alias is not null || union.Origin is not null and not UnionType)
            {
                if (seen.Add(union))
                    result.Add(union);
            }
            else if (union.Origin is UnionType origin)
                for (int i = origin.Types.Count - 1; i >= 0; i--)
                    pending.Push(origin.Types[i]);
        }
        return result;
    }

    private bool Insert(List<Type> types, Type type)
    {
        int index = types.BinarySearch(type, order);
        if (index >= 0)
            return false;
        types.Insert(~index, type);
        return true;
    }

    private bool IsError(Type type) => type == context.ErrorType || (type.Flags & F.Any) != 0 && type.Alias is not null;

    private Type IncludedAny(F includes) => (includes & F.IncludesWildcard) != 0 ? context.WildcardType
            : (includes & F.IncludesError) != 0 ? context.ErrorType : context.AnyType;

    private static bool IsConstrainedVariable(Type type) =>
        type is IntersectionType && (type.ObjectFlags & O.IsConstrainedTypeVariable) != 0;

    private static (Type Variable, Type Primitive) ConstrainedParts(Type type)
    {
        var types = ((IntersectionType)type).Types;
        return (types[0].Flags & F.TypeVariable) != 0 ? (types[0], types[1]) : (types[1], types[0]);
    }

    private async ValueTask RemoveConstrainedVariables(List<Type> types, CancellationToken cancellation)
    {
        var variables = types.Where(IsConstrainedVariable).Select(t => ConstrainedParts(t).Variable).Distinct().ToArray();
        foreach (var variable in variables)
        {
            cancellation.ThrowIfCancellationRequested();
            var primitives = new HashSet<Type>(types.Where(IsConstrainedVariable).Select(ConstrainedParts)
                .Where(p => p.Variable == variable).Select(p => p.Primitive));
            var constraint = await host.GetBaseConstraintAsync(variable, cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Constrained intersection has no base constraint");
            if (Every(constraint, primitives.Contains))
            {
                types.RemoveAll(t => IsConstrainedVariable(t) && ConstrainedParts(t) is var parts
                    && parts.Variable == variable && primitives.Contains(parts.Primitive));
                Insert(types, variable);
            }
        }
    }

    private async ValueTask<List<Type>?> RemoveSubtypes(List<Type> types, bool hasObjects, CancellationToken cancellation)
    {
        if (types.Count < 2)
            return types;
        var key = new TypeCacheKey(CollectionsMarshal.AsSpan(types));
        if (subtypeReductions.TryGetValue(key, out var cached))
            return new(cached);
        bool hasEmptyObject = false;
        if (hasObjects)
            foreach (var type in types)
                if ((type.Flags & F.Object) != 0 && await host.IsEmptyObjectAsync(type, cancellation).ConfigureAwait(false))
                {
                    hasEmptyObject = true;
                    break;
                }
        int length = types.Count, count = 0;
        for (int i = types.Count - 1; i >= 0; i--)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = types[i];
            if (!hasEmptyObject && (source.Flags & F.StructuredOrInstantiable) == 0)
                continue;
            if (source is TypeParameter && await host.GetBaseConstraintAsync(source, cancellation).ConfigureAwait(false) is UnionType)
            {
                var others = types.Select(t => t == source ? context.NeverType : t).ToArray();
                if (await host.IsSubtypeAsync(
                    source,
                    await UnionAsync(others, cancellation: cancellation).ConfigureAwait(false),
                    true,
                    cancellation).ConfigureAwait(false))
                    types.RemoveAt(i);
                continue;
            }
            Symbol? property = null;
            Type? propertyType = null;
            if ((source.Flags & (F.Object | F.Intersection | F.InstantiableNonPrimitive)) != 0)
                foreach (var candidate in await host.GetPropertiesAsync(source, cancellation).ConfigureAwait(false))
                {
                    var type = await host.GetTypeOfSymbolAsync(candidate, cancellation).ConfigureAwait(false);
                    if (type.IsUnit)
                    {
                        property = candidate;
                        propertyType = await RegularTypeAsync(type, cancellation).ConfigureAwait(false);
                        break;
                    }
                }
            foreach (var target in types)
            {
                cancellation.ThrowIfCancellationRequested();
                if (source == target)
                    continue;
                if (count == 100_000)
                {
                    long estimated = (long)(count / (length - i)) * length;
                    if (estimated > 1_000_000)
                    {
                        host.ReportComplexity("removeSubtypes", estimated);
                        return null;
                    }
                }
                count++;
                if (property is not null && (target.Flags & (F.Object | F.Intersection | F.InstantiableNonPrimitive)) != 0)
                {
                    var targetProperty = await host.GetPropertyTypeAsync(target, property.Name, cancellation).ConfigureAwait(false);
                    if (targetProperty is not null && targetProperty.IsUnit
                        && await RegularTypeAsync(targetProperty, cancellation).ConfigureAwait(false) != propertyType)
                        continue;
                }
                if ((source == context.EmptyObjectType || source == context.UnknownEmptyObjectType) && target.Symbol is not null
                    && await EmptyAnonymous(target, cancellation).ConfigureAwait(false))
                    continue;
                if (await host.IsSubtypeAsync(source, target, true, cancellation).ConfigureAwait(false)
                    && ((Target(source).ObjectFlags & O.Class) == 0 || (Target(target).ObjectFlags & O.Class) == 0
                        || await host.IsDerivedFromAsync(source, target, cancellation).ConfigureAwait(false)))
                {
                    types.RemoveAt(i);
                    break;
                }
            }
        }
        subtypeReductions[key] = types.ToArray();
        return types;
    }

    private static Type Target(Type type) => (type.ObjectFlags & O.Reference) != 0 ? ((TypeReference)type).ReferencedType : type;

    private ValueTask<bool> EmptyAnonymous(Type type, CancellationToken cancellation) => (type.ObjectFlags & O.Anonymous) != 0
            ? host.IsEmptyAnonymousObjectAsync(type, cancellation) : ValueTask.FromResult(false);

    private static bool Every(Type type, Func<Type, bool> predicate) =>
        type is UnionType union ? union.Types.All(predicate) : predicate(type);

    internal async ValueTask<Type> RegularTypeAsync(Type type, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        if (type is LiteralType literal)
            return literal.RegularType;
        if (type is not UnionType union)
            return type;
        if (union.RegularType is not null)
            return union.RegularType;
        var result = await MapAsync(
            type,
            async t => await RegularTypeAsync(t, cancellation).ConfigureAwait(false),
            cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Regular literal mapping removed all constituents");
        return union.RegularType = result;
    }

    internal async ValueTask<Type?> MapAsync(Type type, Func<Type, ValueTask<Type?>> map, bool noReductions = false,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & F.Never) != 0)
            return type;
        if (type is not UnionType union)
        {
            var single = await map(type).ConfigureAwait(false);
            if (single is not null)
                context.RequireOwned(single);
            return single;
        }
        var source = union.Origin is UnionType origin ? origin.Types : union.Types;
        var result = new List<Type>();
        bool changed = false;
        foreach (var constituent in source)
        {
            cancellation.ThrowIfCancellationRequested();
            var mapped = constituent is UnionType ? await MapAsync(constituent, map, noReductions, cancellation).ConfigureAwait(false)
                : await map(constituent).ConfigureAwait(false);
            changed |= mapped != constituent;
            if (mapped is not null)
            {
                context.RequireOwned(mapped);
                result.Add(mapped);
            }
        }
        return !changed ? type : result.Count == 0 ? null
            : await UnionAsync(
                result,
                noReductions ? UnionReduction.None : UnionReduction.Literal,
                cancellation: cancellation).ConfigureAwait(false);
    }

    internal Type Filter(Type type, Func<Type, bool> predicate)
    {
        context.RequireOwned(type);
        if (type is not UnionType union)
            return (type.Flags & F.Never) != 0 || predicate(type) ? type : context.NeverType;
        var filtered = union.Types.Where(predicate).ToArray();
        if (filtered.Length == union.Types.Count)
            return type;
        Type? origin = null;
        if (union.Origin is UnionType oldOrigin)
        {
            var originTypes = oldOrigin.Types.Where(t => t is UnionType || predicate(t)).ToArray();
            if (oldOrigin.Types.Count - originTypes.Length == union.Types.Count - filtered.Length)
            {
                if (originTypes.Length == 1)
                    return originTypes[0];
                origin = context.NewUnionType(originTypes);
            }
        }
        return context.GetUnionFromSortedTypes(filtered, union.ObjectFlags & (O.PrimitiveUnion | O.ContainsIntersections), origin: origin);
    }

    internal async ValueTask<Type> FilterAsync(Type type, Func<Type, ValueTask<bool>> predicate, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is not UnionType union)
            return (type.Flags & F.Never) != 0 || await predicate(type).ConfigureAwait(false) ? type : context.NeverType;
        List<Type> filtered = [];
        foreach (var part in union.Types)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await predicate(part).ConfigureAwait(false))
                filtered.Add(part);
        }
        if (filtered.Count == union.Types.Count)
            return type;
        Type? origin = null;
        if (union.Origin is UnionType oldOrigin)
        {
            List<Type> originTypes = [];
            foreach (var part in oldOrigin.Types)
                if (part is UnionType || await predicate(part).ConfigureAwait(false))
                    originTypes.Add(part);
            if (oldOrigin.Types.Count - originTypes.Count == union.Types.Count - filtered.Count)
            {
                if (originTypes.Count == 1)
                    return originTypes[0];
                origin = context.NewUnionType(originTypes.ToArray());
            }
        }
        return context.GetUnionFromSortedTypes(
            filtered.ToArray(),
            union.ObjectFlags & (O.PrimitiveUnion | O.ContainsIntersections),
            origin: origin);
    }

    internal static long CrossProductSize(IReadOnlyList<Type> types)
    {
        long size = 1;
        foreach (var type in types)
        {
            if (type is UnionType union)
            {
                if (union.Types.Count > 0 && size > long.MaxValue / union.Types.Count)
                    return long.MaxValue;
                size *= union.Types.Count;
            }
            else if ((type.Flags & F.Never) != 0)
                return 0;
        }
        return size;
    }

    internal bool CheckCrossProduct(IReadOnlyList<Type> types)
    {
        long count = CrossProductSize(types);
        if (count < 100_000)
            return true;
        host.ReportComplexity("crossProduct", count);
        return false;
    }
}
