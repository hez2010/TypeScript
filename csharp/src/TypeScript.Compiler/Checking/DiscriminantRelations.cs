using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class DiscriminantRelations(TypeContext context, TypeAlgebra algebra, TypeProperties properties, SymbolTypes values,
    MappedTypes mapped, ObjectRelations objects, IStructuralRelationHost host)
{
    internal async ValueTask<bool> PropertyAsync(UnionType type, string name, CancellationToken cancellation = default)
    {
        var property = await properties.CachedPropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
        if (property is null || (property.CheckFlags & CheckFlags.SyntheticProperty) == 0)
            return false;
        if ((property.CheckFlags & CheckFlags.IsDiscriminantComputed) == 0)
        {
            var old = property.CheckFlags & (CheckFlags.IsDiscriminantComputed | CheckFlags.IsDiscriminant);
            property.CheckFlags |= CheckFlags.IsDiscriminantComputed;
            try
            {
                if ((property.CheckFlags & CheckFlags.NonUniformAndLiteral) == CheckFlags.NonUniformAndLiteral
                    && await mapped.GenericFlagsAsync(
                        await values.GetAsync(property, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false) == 0)
                    property.CheckFlags |= CheckFlags.IsDiscriminant;
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                property.CheckFlags = property.CheckFlags & ~(CheckFlags.IsDiscriminantComputed | CheckFlags.IsDiscriminant) | old;
                throw;
            }
        }
        return (property.CheckFlags & CheckFlags.IsDiscriminant) != 0;
    }

    internal async ValueTask<Type?> MatchAsync(UnionType target, Type source, CancellationToken cancellation = default)
    {
        string name = await KeyAsync(target, cancellation).ConfigureAwait(false);
        if (name.Length == 0)
            return null;
        var property = await properties.PropertyAsync(source, name, cancellation: cancellation).ConfigureAwait(false);
        if (property is null)
            return null;
        var key = await algebra.RegularTypeAsync(
            await values.GetAsync(property, cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        var match = target.ConstituentMap!.GetValueOrDefault(key);
        return match == context.UnknownType ? null : match;
    }

    internal async ValueTask<string> KeyAsync(UnionType type, CancellationToken cancellation)
    {
        const string missing = Symbol.InternalPrefix + "missing";
        if (type.KeyPropertyName is null or "")
        {
            var types = type.Types;
            string? candidate = null;
            Dictionary<Type, Type>? map = null;
            if (types.Count >= 10
                && (type.ObjectFlags & ObjectFlags.PrimitiveUnion) == 0
                && types.Count(t => (t.Flags & (TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) != 0) >= 10)
            {
                foreach (var part in types)
                {
                    if ((part.Flags & (TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) == 0)
                        continue;
                    foreach (var property in await properties.GetAsync(part, cancellation).ConfigureAwait(false))
                        if (((await values.GetAsync(property, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Unit) != 0)
                        {
                            candidate = property.Name;
                            break;
                        }
                    if (candidate is not null)
                        break;
                }
                if (candidate is not null)
                    map = await MapAsync(types, candidate, cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            type.KeyPropertyName = map is null ? missing : candidate;
            type.ConstituentMap = map;
        }
        return type.KeyPropertyName == missing ? "" : type.KeyPropertyName!;
    }

    private async ValueTask<Dictionary<Type, Type>?> MapAsync(IReadOnlyList<Type> types, string name, CancellationToken cancellation)
    {
        var result = new Dictionary<Type, Type>();
        int count = 0;
        foreach (var type in types)
        {
            if ((type.Flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.InstantiableNonPrimitive)) == 0)
                continue;
            var property = await properties.PropertyAsync(type, name, cancellation: cancellation).ConfigureAwait(false);
            if (property is null)
                return null;
            var discriminant = await values.GetAsync(property, cancellation).ConfigureAwait(false);
            if (!Literal(discriminant))
                return null;
            bool duplicate = false;
            foreach (var part in discriminant is UnionType union ? union.Types : [discriminant])
            {
                var key = await algebra.RegularTypeAsync(part, cancellation).ConfigureAwait(false);
                if (!result.TryGetValue(key, out var existing))
                    result.Add(key, type);
                else if (existing != context.UnknownType)
                {
                    result[key] = context.UnknownType;
                    duplicate = true;
                }
            }
            if (!duplicate)
                count++;
        }
        return count >= 10 && count * 2 >= types.Count ? result : null;
    }

    internal async ValueTask<Ternary> RelatedAsync(
        RelationOperation operation,
        Type source,
        UnionType target,
        CancellationToken cancellation = default)
    {
        var sourceProperties = new List<Symbol>();
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
            if (await PropertyAsync(target, property.Name, cancellation).ConfigureAwait(false))
                sourceProperties.Add(property);
        if (sourceProperties.Count == 0)
            return Ternary.False;
        int combinations = 1;
        foreach (var property in sourceProperties)
        {
            var type = values.NonMissing(
                await values.GetAsync(property, cancellation).ConfigureAwait(false),
                (property.Flags & SymbolFlags.Optional) != 0);
            int count = (type.Flags & TypeFlags.Never) != 0 ? 0 : type is UnionType union ? union.Types.Count : 1;
            if (count == 0 || count > 25 / combinations)
                return Ternary.False;
            combinations *= count;
        }
        var candidates = new IReadOnlyList<Type>[sourceProperties.Count];
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < candidates.Length; i++)
        {
            var property = sourceProperties[i];
            var type = values.NonMissing(
                await values.GetAsync(property, cancellation).ConfigureAwait(false),
                (property.Flags & SymbolFlags.Optional) != 0);
            candidates[i] = type is UnionType union ? union.Types : [type];
            excluded.Add(property.Name);
        }
        var matches = new List<Type>();
        for (int combination = 0; combination < combinations; combination++)
        {
            var parts = new Type[candidates.Length];
            int remaining = combination;
            for (int j = parts.Length - 1; j >= 0; j--)
            {
                parts[j] = candidates[j][remaining % candidates[j].Count];
                remaining /= candidates[j].Count;
            }
            bool found = false;
            foreach (var type in target.Types)
            {
                bool match = true;
                for (int i = 0; i < sourceProperties.Count; i++)
                {
                    var sourceProperty = sourceProperties[i];
                    var targetProperty = await properties.PropertyAsync(
                        type,
                        sourceProperty.Name,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (targetProperty is null)
                    {
                        match = false;
                        break;
                    }
                    if (sourceProperty != targetProperty
                        && await objects.PropertyAsync(
                            operation,
                            source,
                            type,
                            sourceProperty,
                            targetProperty,
                            0,
                            cancellation,
                            parts[i],
                            context.StrictNullChecks || operation.Kind == RelationKind.Comparable).ConfigureAwait(false) == Ternary.False)
                    {
                        match = false;
                        break;
                    }
                }
                if (!match)
                    continue;
                if (!matches.Contains(type))
                    matches.Add(type);
                found = true;
            }
            if (!found)
                return Ternary.False;
        }
        Ternary result = Ternary.True;
        foreach (var match in matches)
        {
            result &= await objects.PropertiesAsync(operation, source, match, false, 0, cancellation, excluded).ConfigureAwait(false);
            if (result != Ternary.False)
                result &= await host.SignatureRelationAsync(operation, source, match, false, 0, cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                result &= await host.SignatureRelationAsync(operation, source, match, true, 0, cancellation).ConfigureAwait(false);
            if (result != Ternary.False && !(source is TypeReference { Target: TupleType } && match is TypeReference { Target: TupleType }))
                result &= await objects.IndexesAsync(operation, source, match, false, 0, cancellation).ConfigureAwait(false);
            if (result == Ternary.False)
                return result;
        }
        return result;
    }

    internal static bool Literal(Type type) =>
        (type.Flags & TypeFlags.Boolean) != 0
            || (type is UnionType union
                ? (type.Flags & TypeFlags.EnumLiteral) != 0 || union.Types.All(t => (t.Flags & TypeFlags.Unit) != 0)
                : (type.Flags & TypeFlags.Unit) != 0);
}
