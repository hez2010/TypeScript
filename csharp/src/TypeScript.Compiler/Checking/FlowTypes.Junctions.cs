using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowTypes
{
    private async ValueTask<FlowType> BranchAsync(FlowState state, IReadOnlyList<FlowNode> antecedents, CancellationToken cancellation)
    {
        List<Type> types = [];
        bool subtypeReduction = false, incomplete = false;
        FlowNode? bypass = null;
        foreach (var antecedent in antecedents)
        {
            if (bypass is null && (antecedent.Flags & FlowFlags.SwitchClause) != 0 && antecedent.ClauseStart == antecedent.ClauseEnd)
            {
                bypass = antecedent;
                continue;
            }
            var type = await AtAsync(state, antecedent, cancellation).ConfigureAwait(false);
            if (type.Type == state.Declared && state.Declared == state.Initial)
                return new(type.Type);
            if (!types.Contains(type.Type))
                types.Add(type.Type);
            if (!await SubsetAsync(type.Type, state.Initial, cancellation).ConfigureAwait(false))
                subtypeReduction = true;
            incomplete |= type.Incomplete;
        }
        if (bypass is not null)
        {
            var type = await AtAsync(state, bypass, cancellation).ConfigureAwait(false);
            if ((type.Type.Flags & TypeFlags.Never) == 0 && !types.Contains(type.Type)
                && !await host.ExhaustiveAsync(bypass.Node!, cancellation).ConfigureAwait(false))
            {
                if (type.Type == state.Declared && state.Declared == state.Initial)
                    return new(type.Type);
                types.Add(type.Type);
                if (!await SubsetAsync(type.Type, state.Initial, cancellation).ConfigureAwait(false))
                    subtypeReduction = true;
                incomplete |= type.Incomplete;
            }
        }
        return Result(await UnionAsync(state, types, subtypeReduction, cancellation).ConfigureAwait(false), incomplete);
    }

    private async ValueTask<Type> UnionAsync(
        FlowState state,
        IReadOnlyList<Type> types,
        bool subtypeReduction,
        CancellationToken cancellation)
    {
        if (types.Any(t => t is EvolvingArrayType) && types.All(t => t is EvolvingArrayType || (t.Flags & TypeFlags.Never) != 0))
            return Evolving(
                await algebra.UnionAsync(
                types.Select(t => t is EvolvingArrayType array ? array.ElementType! : context.NeverType).ToArray(),
                cancellation: cancellation).ConfigureAwait(false));
        var finalized = new Type[types.Count];
        for (int i = 0; i < types.Count; i++)
            finalized[i] = await FinalizeAsync(types[i], cancellation).ConfigureAwait(false);
        var result = await algebra.UnionAsync(finalized, subtypeReduction ? UnionReduction.Subtype : UnionReduction.Literal,
            cancellation: cancellation).ConfigureAwait(false);
        if (result == context.UnknownUnionType)
            result = context.UnknownType;
        return result != state.Declared && result is UnionType union && state.Declared is UnionType declared
            && union.Types.SequenceEqual(declared.Types) ? declared : result;
    }

    private async ValueTask<FlowType> LoopAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        if (!state.KeyComputed)
        {
            state.Key = await host.ReferenceKeyAsync(state, cancellation).ConfigureAwait(false);
            state.KeyComputed = true;
        }
        if (state.Key is null)
            return new(state.Declared);
        var key = (flow, state.Key.Value);
        if (loops.TryGetValue(key, out var cached))
            return new(cached);
        foreach (var loop in loopStack)
            if (loop.Key == key && loop.Types.Count != 0)
                return Result(await UnionAsync(state, loop.Types, false, cancellation).ConfigureAwait(false), true);
        List<Type> types = [];
        bool subtypeReduction = false;
        FlowType? first = null;
        foreach (var antecedent in flow.Antecedents)
        {
            FlowType type;
            if (first is null)
            {
                type = await AtAsync(state, antecedent, cancellation).ConfigureAwait(false);
                first = type;
            }
            else
            {
                var activeStack = loopStack;
                activeStack.Add((key, types));
                var savedCache = ExpressionCache;
                ExpressionCache = null;
                try
                {
                    type = await AtAsync(state, antecedent, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    ExpressionCache = savedCache;
                    activeStack.RemoveAt(activeStack.Count - 1);
                }
                if (loops.TryGetValue(key, out cached))
                    return new(cached);
            }
            if (!types.Contains(type.Type))
                types.Add(type.Type);
            if (!await SubsetAsync(type.Type, state.Initial, cancellation).ConfigureAwait(false))
                subtypeReduction = true;
            if (type.Type == state.Declared)
                break;
        }
        var result = await UnionAsync(state, types, subtypeReduction, cancellation).ConfigureAwait(false);
        if (first is { Incomplete: true })
            return Result(result, true);
        cancellation.ThrowIfCancellationRequested();
        loops[key] = result;
        return new(result);
    }

    internal async ValueTask<bool> SubsetAsync(Type source, Type target, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source == target || (source.Flags & TypeFlags.Never) != 0)
            return true;
        if (target is not UnionType union)
            return false;
        if (source is UnionType sourceUnion)
            return sourceUnion.Types.All(union.Types.Contains);
        return (source.Flags & TypeFlags.EnumLike) != 0 && await host.EnumBaseAsync(source, cancellation).ConfigureAwait(false) == target
            || union.Types.Contains(source);
    }
}
