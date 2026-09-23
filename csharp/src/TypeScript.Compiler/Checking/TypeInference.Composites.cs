namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    private async ValueTask<(IReadOnlyList<Type> Sources, IReadOnlyList<Type> Targets)> MatchAsync(State state,
        IReadOnlyList<Type> sources, IReadOnlyList<Type> targets, bool baseIdentity, bool closely, CancellationToken cancellation)
    {
        var matchedSources = new List<Type>();
        var matchedTargets = new List<Type>();
        async ValueTask<bool> Matches(Type source, Type target)
        {
            if (closely)
                return source is ObjectType && target is ObjectType && source.Symbol is not null && source.Symbol == target.Symbol
                    || source.Alias is { TypeArguments.Count: > 0 } a && target.Alias is { } b && a.Symbol == b.Symbol;
            if (baseIdentity && target == context.MissingType)
                return source == target;
            return await relations.RelatedAsync(source, target, RelationKind.Identity, cancellation).ConfigureAwait(false)
                || baseIdentity && ((target.Flags & TypeFlags.String) != 0 && (source.Flags & TypeFlags.StringLiteral) != 0
                    || (target.Flags & TypeFlags.Number) != 0 && (source.Flags & TypeFlags.NumberLiteral) != 0);
        }
        foreach (var target in targets)
            foreach (var source in sources)
                if (await Matches(source, target).ConfigureAwait(false))
                {
                    if (!closely)
                        await FromAsync(state, source, target, cancellation).ConfigureAwait(false);
                    if (!matchedSources.Contains(source))
                        matchedSources.Add(source);
                    if (!matchedTargets.Contains(target))
                        matchedTargets.Add(target);
                }
        if (closely)
        {
            var depths = new Dictionary<Type, int>();
            foreach (var type in matchedTargets)
                depths[type] = await DepthAsync(type, 3, cancellation).ConfigureAwait(false);
            matchedTargets.Sort(
                (left, right) => depths[left] != depths[right] ? depths[right].CompareTo(depths[left]) : order.Compare(left, right));
            foreach (var target in matchedTargets)
                foreach (var source in matchedSources)
                    if (await Matches(source, target).ConfigureAwait(false))
                        await FromAsync(state, source, target, cancellation).ConfigureAwait(false);
        }
        return (matchedSources.Count == 0 ? sources : sources.Where(t => !matchedSources.Contains(t)).ToArray(),
            matchedTargets.Count == 0 ? targets : targets.Where(t => !matchedTargets.Contains(t)).ToArray());
    }

    private async ValueTask<int> DepthAsync(Type type, int remaining, CancellationToken cancellation)
    {
        var pending = new Stack<(Type Type, int Remaining, int Depth)>();
        pending.Push((type, remaining, 0));
        int result = 0;
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            result = Math.Max(result, item.Depth);
            if (item.Remaining == 0)
                continue;
            IReadOnlyList<Type>? args = item.Type.Alias?.TypeArguments;
            if (args is not { Count: > 0 } && item.Type is TypeReference reference && (reference.ObjectFlags & ObjectFlags.Reference) != 0)
                args = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
            if (args is { Count: > 0 })
                foreach (var argument in args)
                    pending.Push((argument, item.Remaining - 1, item.Depth + 1));
            else if (item.Type is UnionOrIntersectionType composite)
                foreach (var part in composite.Types)
                    pending.Push((part, item.Remaining, item.Depth));
        }
        return result;
    }

    private async ValueTask MultipleAsync(
        State state,
        Type source,
        IReadOnlyList<Type> targets,
        TypeFlags flags,
        CancellationToken cancellation)
    {
        int variableCount = 0;
        if ((flags & TypeFlags.Union) != 0)
        {
            Type? naked = null;
            var sources = Parts(source);
            var matched = new bool[sources.Count];
            bool circular = false;
            foreach (var target in targets)
            {
                if (Info(state, target) is not null)
                {
                    naked = target;
                    variableCount++;
                }
                else
                    for (int i = 0; i < sources.Count; i++)
                    {
                        var priority = state.InferencePriority;
                        state.InferencePriority = InferencePriority.MaxValue;
                        try
                        {
                            await FromAsync(state, sources[i], target, cancellation).ConfigureAwait(false);
                            if (state.InferencePriority == state.Priority)
                                matched[i] = true;
                            circular |= state.InferencePriority == InferencePriority.Circularity;
                        }
                        finally
                        {
                            state.InferencePriority = Min(state.InferencePriority, priority);
                        }
                    }
            }
            if (variableCount == 0)
            {
                Type? variable = null;
                foreach (var target in targets)
                {
                    if (target is not IntersectionType intersection)
                        return;
                    var candidate = intersection.Types.FirstOrDefault(t => Info(state, t) is not null);
                    if (candidate is null || variable is not null && variable != candidate)
                        return;
                    variable = candidate;
                }
                if (variable is not null)
                    await PriorityAsync(state, source, variable, InferencePriority.NakedTypeVariable, cancellation).ConfigureAwait(false);
                return;
            }
            if (variableCount == 1 && !circular)
            {
                var unmatched = sources.Where((_, i) => !matched[i]).ToArray();
                if (unmatched.Length != 0)
                {
                    await FromAsync(
                        state,
                        await algebra.UnionAsync(unmatched, cancellation: cancellation).ConfigureAwait(false),
                        naked!,
                        cancellation).ConfigureAwait(false);
                    return;
                }
            }
        }
        else
            foreach (var target in targets)
                if (Info(state, target) is not null)
                    variableCount++;
                else
                    await FromAsync(state, source, target, cancellation).ConfigureAwait(false);
        if ((flags & TypeFlags.Intersection) != 0 ? variableCount == 1 : variableCount > 0)
            foreach (var target in targets)
                if (Info(state, target) is not null)
                    await PriorityAsync(state, source, target, InferencePriority.NakedTypeVariable, cancellation).ConfigureAwait(false);
    }

    private async ValueTask ConditionalAsync(State state, Type source, ConditionalType target, CancellationToken cancellation)
    {
        if (source is ConditionalType conditional)
        {
            await FromAsync(state, NonDistributed(conditional.CheckType), target.CheckType, cancellation).ConfigureAwait(false);
            await FromAsync(state, NonDistributed(conditional.ExtendsType), target.ExtendsType, cancellation).ConfigureAwait(false);
            await FromAsync(
                state,
                NonDistributed(await constraints.ConditionalTrueAsync(conditional, cancellation: cancellation).ConfigureAwait(false)),
                await constraints.ConditionalTrueAsync(target, cancellation: cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            await FromAsync(state, NonDistributed(await constraints.ConditionalFalseAsync(conditional, cancellation).ConfigureAwait(false)),
                await constraints.ConditionalFalseAsync(target, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        }
        else
        {
            var targets = new[] { await constraints.ConditionalTrueAsync(target, cancellation: cancellation).ConfigureAwait(false),
                await constraints.ConditionalFalseAsync(target, cancellation).ConfigureAwait(false) };
            var priority = state.Priority;
            if (state.Contravariant)
                state.Priority |= InferencePriority.ContravariantConditional;
            try
            {
                await MultipleAsync(state, source, targets, target.Flags, cancellation).ConfigureAwait(false);
            }
            finally
            {
                state.Priority = priority;
            }
        }
    }

    private async ValueTask GenericMappedAsync(State state, MappedType source, MappedType target, CancellationToken cancellation)
    {
        await FromAsync(state, await mapped.ConstraintAsync(source, cancellation).ConfigureAwait(false),
            await mapped.ConstraintAsync(target, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        await FromAsync(state, await mapped.TemplateAsync(source, cancellation).ConfigureAwait(false),
            await mapped.TemplateAsync(target, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        var sourceName = await mapped.NameAsync(source, cancellation).ConfigureAwait(false);
        var targetName = await mapped.NameAsync(target, cancellation).ConfigureAwait(false);
        if (sourceName is not null && targetName is not null)
            await FromAsync(state, sourceName, targetName, cancellation).ConfigureAwait(false);
    }

    internal ValueTask<bool> TopLevelAsync(Type type, Type parameter, CancellationToken cancellation = default)
        => TopLevelAsync(type, parameter, 0, cancellation);

    private async ValueTask<bool> TopLevelAsync(Type type, Type parameter, int depth, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var item))
        {
            cancellation.ThrowIfCancellationRequested();
            if (item == parameter)
                return true;
            if (item is UnionOrIntersectionType composite)
            {
                for (int i = composite.Types.Count - 1; i >= 0; i--)
                    pending.Push(composite.Types[i]);
            }
            else if (depth < 3 && item is ConditionalType conditional)
            {
                // Keep branch evaluation lazy and in source order.
                if (await TopLevelAsync(
                    await constraints.ConditionalTrueAsync(conditional, cancellation: cancellation).ConfigureAwait(false),
                    parameter,
                    depth + 1,
                    cancellation).ConfigureAwait(false)
                    || await TopLevelAsync(
                        await constraints.ConditionalFalseAsync(conditional, cancellation).ConfigureAwait(false),
                        parameter,
                        depth + 1,
                        cancellation).ConfigureAwait(false))
                    return true;
            }
        }
        return false;
    }

}
