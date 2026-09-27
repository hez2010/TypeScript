namespace TypeScript.Compiler.Checking;

internal sealed class BestMatchingTypes(TypeDiscrimination discrimination, TypeRelations relations, TypeAlgebra algebra,
    TypeKeys keys, IArrayLiteralHost arrays, ICallResolutionHost calls)
{
    internal async ValueTask<Type?> GetAsync(Type source, UnionType target, CancellationToken cancellation = default,
        Func<Type, Type, ValueTask<bool>>? related = null)
    {
        if (await discrimination.MatchAsync(
            source,
            target,
            related ?? ((s, t) => relations.RelatedAsync(s, t, RelationKind.Assignable, cancellation)),
            cancellation).ConfigureAwait(false) is { } match)
            return match;
        if ((source.ObjectFlags & (ObjectFlags.Reference | ObjectFlags.Anonymous)) != 0)
            foreach (var part in target.Types)
            {
                if ((part.Flags & TypeFlags.Object) == 0)
                    continue;
                var overlap = source.ObjectFlags & part.ObjectFlags;
                if ((overlap & ObjectFlags.Reference) != 0 && ((TypeReference)source).Target == ((TypeReference)part).Target)
                    return part;
                if ((overlap & ObjectFlags.Anonymous) != 0 && source.Alias is not null && part.Alias?.Symbol == source.Alias.Symbol)
                    return part;
            }
        if ((source.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
        {
            bool anyArray = false;
            foreach (var part in target.Types)
                if (await arrays.ArrayLikeAsync(part, cancellation).ConfigureAwait(false))
                {
                    anyArray = true;
                    break;
                }
            if (anyArray)
                foreach (var part in target.Types)
                    if (!await arrays.ArrayLikeAsync(part, cancellation).ConfigureAwait(false))
                        return part;
        }
        foreach (bool construct in new[] { false, true })
            if ((await calls.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false)).Count != 0)
                foreach (var part in target.Types)
                    if ((await calls.SignaturesAsync(part, construct, cancellation).ConfigureAwait(false)).Count != 0)
                        return part;
        Type? best = null;
        int count = 0;
        if ((source.Flags & (TypeFlags.Primitive | TypeFlags.InstantiablePrimitive)) == 0)
            foreach (var part in target.Types)
            {
                if ((part.Flags & (TypeFlags.Primitive | TypeFlags.InstantiablePrimitive)) != 0)
                    continue;
                var overlap = await algebra.IntersectionAsync(
                    [await keys.GetAsync(source, cancellation: cancellation).ConfigureAwait(false),
                    await keys.GetAsync(part, cancellation: cancellation).ConfigureAwait(false)],
                    cancellation: cancellation).ConfigureAwait(false);
                if ((overlap.Flags & TypeFlags.Index) != 0)
                    return part;
                if ((overlap.Flags & TypeFlags.Unit) != 0 || overlap is UnionType)
                {
                    int length = overlap is UnionType union ? union.Types.Count(t => (t.Flags & TypeFlags.Unit) != 0) : 1;
                    if (length >= count)
                    {
                        best = part;
                        count = length;
                    }
                }
            }
        return best;
    }
}
