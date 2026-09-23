namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    internal async ValueTask<Type> CovariantAsync(InferenceInfo info, Signature signature, CancellationToken cancellation = default)
    {
        IReadOnlyList<Type> candidates = info.Candidates;
        if (candidates.Count > 1)
        {
            bool LiteralObject(Type type) => (type.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.ArrayLiteral)) != 0;
            var literals = candidates.Where(LiteralObject).ToArray();
            if (literals.Length != 0)
                candidates =
                    [
                        .. candidates.Where(t => !LiteralObject(t)),
                        await algebra.UnionAsync(literals, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false)
                    ];
        }
        bool primitive = await PrimitiveConstraintAsync((TypeParameter)info.Parameter, cancellation).ConfigureAwait(false)
            || await ConstVariableAsync(info.Parameter, cancellation).ConfigureAwait(false);
        bool widenLiterals = !primitive
            && info.TopLevel
            && (info.IsFixed || !await ReturnTopLevelAsync(signature, info.Parameter, cancellation).ConfigureAwait(false));
        if (primitive || widenLiterals)
        {
            var result = new Type[candidates.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = primitive ? await algebra.RegularTypeAsync(candidates[i], cancellation).ConfigureAwait(false)
                    : await widening.LiteralAsync(candidates[i], cancellation).ConfigureAwait(false);
            candidates = result;
        }
        var inferred = (info.Priority & InferencePriority.PriorityImpliesCombination) != 0
            ? await algebra.UnionAsync(candidates, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false)
            : await CommonSupertypeAsync(candidates, cancellation).ConfigureAwait(false);
        return await widening.GetAsync(inferred, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> PrimitiveConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
    {
        var bound = await constraints.ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false);
        if (bound is null)
            return false;
        if (bound is ConditionalType conditional)
            bound = await constraints.DefaultConditionalConstraintAsync(conditional, cancellation).ConfigureAwait(false);
        var pending = new Stack<Type>();
        pending.Push(bound);
        while (pending.TryPop(out var type))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((type.Flags & (TypeFlags.Primitive | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
                return true;
            if (type is UnionOrIntersectionType composite)
                foreach (var part in composite.Types)
                    pending.Push(part);
        }
        return false;
    }

    private async ValueTask<bool> ReturnTopLevelAsync(Signature signature, Type parameter, CancellationToken cancellation)
    {
        if (await signatures.PredicateAsync(signature, cancellation).ConfigureAwait(false) is { } predicate)
            return predicate.Type is { } type && await TopLevelAsync(type, parameter, cancellation).ConfigureAwait(false);
        return await TopLevelAsync(
            await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false),
            parameter,
            cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> CommonSupertypeAsync(IReadOnlyList<Type> types, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (types.Count == 1)
            return types[0];
        var primary = types.Select(
            t => context.StrictNullChecks ? algebra.Filter(t, p => (p.Flags & TypeFlags.Nullable) == 0) : t).ToArray();
        Type? baseType = null;
        bool literals = true;
        foreach (var type in primary)
        {
            if ((type.Flags & TypeFlags.Never) != 0)
                continue;
            var next = await widening.LiteralBaseAsync(type, cancellation).ConfigureAwait(false);
            baseType ??= next;
            if (next == type || next != baseType)
            {
                literals = false;
                break;
            }
        }
        Type candidate;
        if (literals)
            candidate = await algebra.UnionAsync(primary, cancellation: cancellation).ConfigureAwait(false);
        else
        {
            candidate = await LeftmostAsync(primary, RelationKind.StrictSubtype, cancellation).ConfigureAwait(false);
            bool strict = true;
            foreach (var type in primary)
                if (type != candidate
                    && !await relations.RelatedAsync(type, candidate, RelationKind.StrictSubtype, cancellation).ConfigureAwait(false))
                {
                    strict = false;
                    break;
                }
            if (!strict)
                candidate = await LeftmostAsync(primary, RelationKind.Subtype, cancellation).ConfigureAwait(false);
        }
        if (primary.SequenceEqual(types))
            return candidate;
        var nullable = TypeFlags.None;
        var pending = new Stack<Type>(types);
        while (pending.TryPop(out var type))
            if (type is UnionType union)
                foreach (var part in union.Types)
                    pending.Push(part);
            else
                nullable |= type.Flags & TypeFlags.Nullable;
        var results = new List<Type> { candidate };
        if ((nullable & TypeFlags.Undefined) != 0)
            results.Add(context.UndefinedType);
        if ((nullable & TypeFlags.Null) != 0)
            results.Add(context.NullType);
        return await algebra.UnionAsync(results, cancellation: cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Type> LeftmostAsync(IReadOnlyList<Type> types, RelationKind relation, CancellationToken cancellation)
    {
        var candidate = types[0];
        foreach (var type in types.Skip(1))
            if (await relations.RelatedAsync(candidate, type, relation, cancellation).ConfigureAwait(false))
                candidate = type;
        return candidate;
    }
}
