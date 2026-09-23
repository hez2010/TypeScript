using System.Runtime.CompilerServices;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeAlgebra
{
    internal async ValueTask<Type> IntersectionAsync(IReadOnlyList<Type> types, IntersectionFlags flags = 0,
        TypeAlias? alias = null, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        RequireOwned(types, alias);
        var (set, includes) = await AddIntersectionTypes(types, cancellation).ConfigureAwait(false);
        if ((includes & F.Never) != 0)
            return set.Contains(context.SilentNeverType) ? context.SilentNeverType : context.NeverType;
        if (context.StrictNullChecks
            && (includes & F.Nullable) != 0
            && (includes & (F.Object | F.NonPrimitive | F.IncludesEmptyObject)) != 0
            || Disjoint(includes, F.NonPrimitive) || Disjoint(includes, F.StringLike) || Disjoint(includes, F.NumberLike)
            || Disjoint(includes, F.BigIntLike) || Disjoint(includes, F.ESSymbolLike) || Disjoint(includes, F.VoidLike))
            return context.NeverType;
        if ((includes & (F.TemplateLiteral | F.StringMapping)) != 0 && (includes & F.StringLiteral) != 0)
        {
            var literals = set.Where(t => (t.Flags & F.StringLiteral) != 0).ToArray();
            for (int i = set.Count - 1; i >= 0; i--)
            {
                var pattern = set[i];
                if ((pattern.Flags & (F.TemplateLiteral | F.StringMapping)) == 0)
                    continue;
                foreach (var literal in literals)
                {
                    if (await host.IsSubtypeAsync(literal, pattern, false, cancellation).ConfigureAwait(false))
                    {
                        set.RemoveAt(i);
                        break;
                    }
                    if (IsPatternLiteral(pattern))
                        return context.NeverType;
                }
            }
        }
        if ((includes & F.Any) != 0)
            return IncludedAny(includes);
        if (!context.StrictNullChecks && (includes & F.Nullable) != 0)
            return (includes & F.IncludesEmptyObject) != 0 ? context.NeverType
                : (includes & F.Undefined) != 0 ? context.UndefinedType : context.NullType;
        bool removeSupertypes = (includes & F.String) != 0 && (includes & (F.StringLiteral | F.TemplateLiteral | F.StringMapping)) != 0
            || (includes & (F.Number | F.NumberLiteral)) == (F.Number | F.NumberLiteral)
            || (includes & (F.BigInt | F.BigIntLiteral)) == (F.BigInt | F.BigIntLiteral)
            || (includes & (F.ESSymbol | F.UniqueESSymbol)) == (F.ESSymbol | F.UniqueESSymbol)
            || (includes & (F.Void | F.Undefined)) == (F.Void | F.Undefined)
            || (includes & F.IncludesEmptyObject) != 0 && (includes & F.DefinitelyNonNullable) != 0;
        if (removeSupertypes && (flags & IntersectionFlags.NoSupertypeReduction) == 0)
            for (int i = set.Count - 1; i >= 0; i--)
            {
                var type = set[i];
                if ((type.Flags & F.String) != 0 && (includes & (F.StringLiteral | F.TemplateLiteral | F.StringMapping)) != 0
                    || (type.Flags & F.Number) != 0 && (includes & F.NumberLiteral) != 0
                    || (type.Flags & F.BigInt) != 0 && (includes & F.BigIntLiteral) != 0
                    || (type.Flags & F.ESSymbol) != 0 && (includes & F.UniqueESSymbol) != 0
                    || (type.Flags & F.Void) != 0 && (includes & F.Undefined) != 0
                    || (includes & F.DefinitelyNonNullable) != 0 && await EmptyAnonymous(type, cancellation).ConfigureAwait(false))
                    set.RemoveAt(i);
            }
        if ((includes & F.IncludesMissingType) != 0)
            set[set.IndexOf(context.UndefinedType)] = context.MissingType;
        if (set.Count == 0)
            return context.UnknownType;
        if (set.Count == 1)
            return set[0];
        O objectFlags = 0;
        if (set.Count == 2 && (flags & IntersectionFlags.NoConstraintReduction) == 0)
        {
            int variableIndex = (set[0].Flags & F.TypeVariable) != 0 ? 0 : 1;
            var variable = set[variableIndex];
            var primitive = set[1 - variableIndex];
            if ((variable.Flags & F.TypeVariable) != 0 && ((primitive.Flags & (F.Primitive | F.NonPrimitive)) != 0
                && !IsGenericStringLike(primitive)
                || (includes & F.IncludesEmptyObject) != 0))
            {
                var constraint = await host.GetBaseConstraintAsync(variable, cancellation).ConfigureAwait(false);
                if (constraint is not null && await PrimitiveOrEmptyConstraint(constraint, cancellation).ConfigureAwait(false))
                {
                    if (await host.IsSubtypeAsync(constraint, primitive, true, cancellation).ConfigureAwait(false))
                        return variable;
                    bool someSubtype = false;
                    if (constraint is UnionType union)
                        foreach (var constituent in union.Types)
                            if (await host.IsSubtypeAsync(constituent, primitive, true, cancellation).ConfigureAwait(false))
                            {
                                someSubtype = true;
                                break;
                            }
                    if (!someSubtype && !await host.IsSubtypeAsync(primitive, constraint, true, cancellation).ConfigureAwait(false))
                        return context.NeverType;
                    objectFlags = O.IsConstrainedTypeVariable;
                }
            }
        }
        // NoSupertypeReduction has already affected the constituent set. Only
        // NoConstraintReduction distinguishes cache entries, and suppresses the alias key.
        var cacheFlags = flags & IntersectionFlags.NoConstraintReduction;
        var key = (TypeCacheKey.Union(set.ToArray(), null, cacheFlags == 0 ? alias : null), cacheFlags);
        if (intersections.TryGetValue(key, out var existing))
            return existing;
        Type result;
        if ((includes & F.Union) == 0)
        {
            result = context.NewIntersectionType(set.ToArray(), objectFlags | TypeContext.PropagatingFlags(types.ToArray(), F.Nullable));
            result.Alias = alias;
        }
        else if (IntersectPrimitiveUnions(set))
            result = await IntersectionAsync(set, flags, alias, cancellation).ConfigureAwait(false);
        else if (set.All(t => t is UnionType union && (union.Types[0].Flags & F.Undefined) != 0))
        {
            var undefined = set.Any(ContainsMissing) ? context.MissingType : context.UndefinedType;
            var filtered = set.Select(t => Filter(t, t => (t.Flags & F.Undefined) == 0)).ToArray();
            var intersection = await IntersectionAsync(filtered, flags, cancellation: cancellation).ConfigureAwait(false);
            result = await UnionAsync([intersection, undefined], alias: alias, cancellation: cancellation).ConfigureAwait(false);
        }
        else if (set.All(t => t is UnionType union && ((union.Types[0].Flags & F.Null) != 0 || (union.Types[1].Flags & F.Null) != 0)))
        {
            var filtered = set.Select(t => Filter(t, t => (t.Flags & F.Null) == 0)).ToArray();
            var intersection = await IntersectionAsync(filtered, flags, cancellation: cancellation).ConfigureAwait(false);
            result = await UnionAsync([intersection, context.NullType], alias: alias, cancellation: cancellation).ConfigureAwait(false);
        }
        else if (set.Count >= 3 && types.Count > 2)
        {
            int middle = set.Count / 2;
            var left = await IntersectionAsync(set.GetRange(0, middle), flags, cancellation: cancellation).ConfigureAwait(false);
            var right = await IntersectionAsync(
                set.GetRange(middle, set.Count - middle),
                flags,
                cancellation: cancellation).ConfigureAwait(false);
            result = await IntersectionAsync([left, right], flags, alias, cancellation).ConfigureAwait(false);
        }
        else
        {
            if (!CheckCrossProduct(set))
                return context.ErrorType;
            var constituents = await CrossProductIntersections(set, flags, cancellation).ConfigureAwait(false);
            Type? origin = constituents.Any(t => t is IntersectionType) && ConstituentCount(constituents) > ConstituentCount(set)
                ? context.NewIntersectionType(set.ToArray()) : null;
            result = await UnionAsync(constituents, alias: alias, origin: origin, cancellation: cancellation).ConfigureAwait(false);
        }
        intersections[key] = result;
        return result;
    }

    private static bool Disjoint(F includes, F domain) => (includes & domain) != 0 && (includes & (F.DisjointDomains & ~domain)) != 0;

    private bool ContainsMissing(Type type) =>
        type == context.MissingType || type is UnionType union && union.Types[0] == context.MissingType;

    private async ValueTask<bool> PrimitiveOrEmptyConstraint(Type type, CancellationToken cancellation)
    {
        var types = type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type];
        foreach (var constituent in types)
            if ((constituent.Flags & (F.Primitive | F.NonPrimitive)) == 0
                && !await EmptyAnonymous(constituent, cancellation).ConfigureAwait(false))
                return false;
        return true;
    }

    private async ValueTask<(List<Type> Types, F Includes)> AddIntersectionTypes(IReadOnlyList<Type> source, CancellationToken cancellation)
    {
        var types = new List<Type>(source.Count);
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(source.Reverse());
        F includes = 0;
        while (pending.TryPop(out var original))
        {
            cancellation.ThrowIfCancellationRequested();
            var type = await RegularTypeAsync(original, cancellation).ConfigureAwait(false);
            var flags = type.Flags;
            if (type is IntersectionType intersection)
            {
                for (int i = intersection.Types.Count - 1; i >= 0; i--)
                    pending.Push(intersection.Types[i]);
                continue;
            }
            if (await EmptyAnonymous(type, cancellation).ConfigureAwait(false))
            {
                if ((includes & F.IncludesEmptyObject) == 0)
                {
                    includes |= F.IncludesEmptyObject;
                    seen.Add(type);
                    types.Add(type);
                }
                continue;
            }
            if ((flags & F.AnyOrUnknown) != 0)
            {
                if (type == context.WildcardType)
                    includes |= F.IncludesWildcard;
                if (IsError(type))
                    includes |= F.IncludesError;
            }
            else if (context.StrictNullChecks || (flags & F.Nullable) == 0)
            {
                if (type == context.MissingType)
                {
                    includes |= F.IncludesMissingType;
                    type = context.UndefinedType;
                }
                if (seen.Add(type))
                {
                    if (type.IsUnit && (includes & F.Unit) != 0)
                        includes |= F.NonPrimitive;
                    types.Add(type);
                }
            }
            includes |= flags & F.IncludesMask;
        }
        return (types, includes);
    }

    private bool IntersectPrimitiveUnions(List<Type> types)
    {
        int index = types.FindIndex(t => (t.ObjectFlags & O.PrimitiveUnion) != 0);
        if (index < 0)
            return false;
        var unions = new List<UnionType> { (UnionType)types[index] };
        for (int i = index + 1; i < types.Count;)
        {
            if ((types[i].ObjectFlags & O.PrimitiveUnion) != 0)
            {
                unions.Add((UnionType)types[i]);
                types.RemoveAt(i);
            }
            else
                i++;
        }
        if (unions.Count == 1)
            return false;
        var seen = new HashSet<Type>();
        var result = new List<Type>();
        foreach (var union in unions)
            foreach (var type in union.Types)
                if (seen.Add(type) && unions.All(u => UnionContains(u, type, true)))
                {
                    if (type == context.UndefinedType && result.Count != 0 && result[0] == context.MissingType)
                        continue;
                    if (type == context.MissingType && result.Count != 0 && result[0] == context.UndefinedType)
                        result[0] = context.MissingType;
                    else
                        Insert(result, type);
                }
        types[index] = context.GetUnionFromSortedTypes(result.ToArray(), O.PrimitiveUnion);
        return true;
    }

    internal bool UnionContains(UnionType union, Type type, bool matchSymbol)
    {
        if (union.Types.Contains(type))
            return true;
        if (type == context.MissingType)
            return union.Types.Contains(context.UndefinedType);
        if (type == context.UndefinedType)
            return union.Types.Contains(context.MissingType);
        Type? primitive = (type.Flags & F.StringLiteral) != 0 ? context.StringType
            : (type.Flags & (F.Enum | F.NumberLiteral)) != 0 ? context.NumberType
            : (type.Flags & F.BigIntLiteral) != 0 ? context.BigIntType
            : (type.Flags & F.UniqueESSymbol) != 0 && matchSymbol ? context.ESSymbolType : null;
        return primitive is not null && union.Types.Contains(primitive);
    }

    private async ValueTask<List<Type>> CrossProductIntersections(
        IReadOnlyList<Type> types,
        IntersectionFlags flags,
        CancellationToken cancellation)
    {
        long count = CrossProductSize(types);
        var result = new List<Type>();
        for (long i = 0; i < count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var constituents = types.ToArray();
            long n = i;
            for (int j = types.Count - 1; j >= 0; j--)
                if (types[j] is UnionType union)
                {
                    constituents[j] = union.Types[(int)(n % union.Types.Count)];
                    n /= union.Types.Count;
                }
            var intersection = await IntersectionAsync(constituents, flags, cancellation: cancellation).ConfigureAwait(false);
            if ((intersection.Flags & F.Never) == 0)
                result.Add(intersection);
        }
        return result;
    }

    private static long ConstituentCount(IReadOnlyList<Type> types)
    {
        long count = 0;
        var pending = new Stack<Type>(types);
        while (pending.TryPop(out var type))
        {
            if (type is not UnionOrIntersectionType composite || type.Alias is not null)
                count++;
            else if (type is UnionType { Origin: { } origin })
                pending.Push(origin);
            else
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return count;
    }

    internal static bool IsPatternLiteral(Type type) => Pattern(type, false);

    internal static bool IsPatternPlaceholder(Type type) => Pattern(type, true);

    private static bool IsGenericStringLike(Type type) =>
        (type.Flags & (F.TemplateLiteral | F.StringMapping)) != 0 && !IsPatternLiteral(type);

    // Evaluate the mutually recursive pattern predicates without an input-shaped
    // native call chain. False is a definitive result, not a relation-cache state.
    private static bool Pattern(Type root, bool placeholder)
    {
        var values = new Dictionary<(Type, bool), bool>();
        var pending = new Stack<(Type Type, bool Placeholder, bool Finish)>();
        pending.Push((root, placeholder, false));
        while (pending.TryPop(out var frame))
        {
            var type = frame.Type;
            var key = (type, frame.Placeholder);
            if (values.ContainsKey(key))
                continue;
            if (frame.Placeholder && type is IntersectionType intersection)
            {
                if (!frame.Finish)
                {
                    pending.Push((type, true, true));
                    foreach (var part in intersection.Types)
                        if ((part.Flags & (F.Literal | F.Nullable)) == 0)
                            pending.Push((part, true, false));
                }
                else
                {
                    bool seen = false, valid = true;
                    foreach (var part in intersection.Types)
                    {
                        if ((part.Flags & (F.Literal | F.Nullable)) != 0 || values[(part, true)])
                            seen = true;
                        else if ((part.Flags & F.Object) == 0)
                        {
                            valid = false;
                            break;
                        }
                    }
                    values[key] = seen && valid;
                }
            }
            else if (frame.Placeholder && (type.Flags & (F.Any | F.String | F.Number | F.BigInt)) != 0)
                values[key] = true;
            else if (type is TemplateLiteralType template)
            {
                if (!frame.Finish)
                {
                    pending.Push((type, frame.Placeholder, true));
                    foreach (var part in template.Types)
                        pending.Push((part, true, false));
                }
                else
                    values[key] = template.Types.All(t => values[(t, true)]);
            }
            else if (type is StringMappingType mapping)
            {
                if (!frame.Finish)
                {
                    pending.Push((type, frame.Placeholder, true));
                    pending.Push((mapping.Target, true, false));
                }
                else
                    values[key] = values[(mapping.Target, true)];
            }
            else
                values[key] = false;
        }
        return values[(root, placeholder)];
    }
}
