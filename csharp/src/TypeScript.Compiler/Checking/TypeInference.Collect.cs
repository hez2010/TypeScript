using System.Runtime.CompilerServices;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    private sealed class State(InferenceContext inference, Type source, Type target, InferencePriority priority, bool contravariant)
    {
        internal InferenceContext Context { get; } = inference;
        internal Type OriginalSource { get; } = source;
        internal Type OriginalTarget { get; } = target;
        internal InferencePriority Priority { get; set; } = priority;
        internal InferencePriority InferencePriority { get; set; } = InferencePriority.MaxValue;
        internal bool Contravariant { get; set; } = contravariant;
        internal bool Bivariant { get; set; }
        internal Type? PropagationType { get; set; }
        internal ExpandingFlags Expanding { get; set; }
        internal Dictionary<(Type, Type), InferencePriority> Visited { get; } = [];
        internal List<Type> Sources { get; } = [];
        internal List<Type> Targets { get; } = [];
    }

    internal async ValueTask InferAsync(InferenceContext inference, Type source, Type target, InferencePriority priority = 0,
        bool contravariant = false, CancellationToken cancellation = default)
    {
        context.RequireOwned(source);
        context.RequireOwned(target);
        await inference.RunAsync(async () =>
        {
            await FromAsync(new(inference, source, target, priority, contravariant), source, target, cancellation).ConfigureAwait(false);
            return true;
        }, cancellation).ConfigureAwait(false);
    }

    private async ValueTask FromAsync(State state, Type source, Type target, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (!await variables.CouldContainAsync(target, cancellation).ConfigureAwait(false) || NoInfer(target))
            return;
        if (source == context.WildcardType || source == context.BlockedStringType)
        {
            var previous = state.PropagationType;
            state.PropagationType = source;
            try
            {
                await FromAsync(state, target, target, cancellation).ConfigureAwait(false);
            }
            finally
            {
                state.PropagationType = previous;
            }
            return;
        }
        if (source.Alias is { } sourceAlias && target.Alias is { } targetAlias && sourceAlias.Symbol == targetAlias.Symbol)
        {
            if (sourceAlias.TypeArguments.Count != 0 || targetAlias.TypeArguments.Count != 0)
            {
                var args = await host.AliasInferenceArgumentsAsync(sourceAlias, targetAlias, cancellation).ConfigureAwait(false);
                await ArgumentsAsync(state, args.Source, args.Target, args.Variances, cancellation).ConfigureAwait(false);
            }
            return;
        }
        if (source == target && source is UnionOrIntersectionType same)
        {
            foreach (var part in same.Types)
                await FromAsync(state, part, part, cancellation).ConfigureAwait(false);
            return;
        }
        if (target is UnionType unionTarget)
        {
            var (firstSources, firstTargets) = await MatchAsync(
                state,
                Parts(source),
                unionTarget.Types,
                true,
                false,
                cancellation).ConfigureAwait(false);
            var (sources, targets) = await MatchAsync(state, firstSources, firstTargets, false, true, cancellation).ConfigureAwait(false);
            if (targets.Count == 0)
                return;
            target = await algebra.UnionAsync(targets, cancellation: cancellation).ConfigureAwait(false);
            if (sources.Count == 0)
            {
                await PriorityAsync(state, source, target, InferencePriority.NakedTypeVariable, cancellation).ConfigureAwait(false);
                return;
            }
            source = await algebra.UnionAsync(sources, cancellation: cancellation).ConfigureAwait(false);
        }
        else if (target is IntersectionType intersection
            && !await AllNonGenericObjectsAsync(intersection.Types, cancellation).ConfigureAwait(false)
            && source is not UnionType)
        {
            var (sources, targets) = await MatchAsync(
                state,
                source is IntersectionType sourceIntersection ? sourceIntersection.Types : [source],
                intersection.Types, false, false, cancellation).ConfigureAwait(false);
            if (sources.Count == 0 || targets.Count == 0)
                return;
            source = await algebra.IntersectionAsync(sources, cancellation: cancellation).ConfigureAwait(false);
            target = await algebra.IntersectionAsync(targets, cancellation: cancellation).ConfigureAwait(false);
        }
        if (target is IndexedAccessType or SubstitutionType)
        {
            if (NoInfer(target))
                return;
            target = await mapped.ActualVariableAsync(target, cancellation).ConfigureAwait(false);
        }
        if ((target.Flags & TypeFlags.TypeVariable) != 0)
        {
            if (Blocked(source))
                return;
            if (Info(state, target) is { } info)
            {
                if ((source.ObjectFlags & ObjectFlags.NonInferrableType) != 0 || source == context.NonInferrableAnyType)
                    return;
                if (!info.IsFixed)
                {
                    var candidate = state.PropagationType ?? source;
                    if (candidate == context.BlockedStringType)
                        return;
                    if (state.Priority < info.Priority)
                    {
                        info.Candidates = [];
                        info.ContraCandidates = [];
                        info.TopLevel = true;
                        info.Priority = state.Priority;
                    }
                    if (state.Priority == info.Priority)
                    {
                        var candidates = state.Contravariant && !state.Bivariant ? info.ContraCandidates : info.Candidates;
                        if (!candidates.Contains(candidate))
                        {
                            candidates.Add(candidate);
                            state.Context.ClearCached();
                        }
                    }
                    if ((state.Priority & InferencePriority.ReturnType) == 0 && target is TypeParameter && info.TopLevel
                        && !await TopLevelAsync(state.OriginalTarget, target, cancellation).ConfigureAwait(false))
                    {
                        info.TopLevel = false;
                        state.Context.ClearCached();
                    }
                }
                state.InferencePriority = Min(state.InferencePriority, state.Priority);
                return;
            }
            var simplified = await host.SimplifyAsync(target, false, cancellation).ConfigureAwait(false);
            if (simplified != target)
                await FromAsync(state, source, simplified, cancellation).ConfigureAwait(false);
            else if (target is IndexedAccessType access)
            {
                var index = await host.SimplifyAsync(access.IndexType, false, cancellation).ConfigureAwait(false);
                if ((index.Flags & TypeFlags.Instantiable) != 0)
                {
                    var objectType = await host.SimplifyAsync(access.ObjectType, false, cancellation).ConfigureAwait(false);
                    var distributed = await indexed.DistributeIndexAsync(objectType, index, false, cancellation).ConfigureAwait(false);
                    if (distributed is not null && distributed != target)
                        await FromAsync(state, source, distributed, cancellation).ConfigureAwait(false);
                }
            }
        }
        if (source is TypeReference sourceReference && target is TypeReference targetReference
            && (source.ObjectFlags & target.ObjectFlags & ObjectFlags.Reference) != 0
            && (sourceReference.Target == targetReference.Target || host.IsArray(source) && host.IsArray(target))
            && !(sourceReference.Node is not null && targetReference.Node is not null))
            await ArgumentsAsync(state, await host.TypeArgumentsAsync(sourceReference, cancellation).ConfigureAwait(false),
                await host.TypeArgumentsAsync(targetReference, cancellation).ConfigureAwait(false),
                await host.InferenceVariancesAsync((TypeReference)sourceReference.Target!, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
        else if (source is IndexType sourceIndex && target is IndexType targetIndex)
            await ContraAsync(state, sourceIndex.Target, targetIndex.Target, cancellation).ConfigureAwait(false);
        else if ((Literal(source) || (source.Flags & TypeFlags.String) != 0) && target is IndexType targetKeys)
            await PriorityAsync(state, await host.EmptyInferenceObjectAsync(source, cancellation).ConfigureAwait(false), targetKeys.Target,
                InferencePriority.LiteralKeyof, cancellation, contravariant: true).ConfigureAwait(false);
        else if (source is IndexedAccessType sourceAccess && target is IndexedAccessType targetAccess)
        {
            await FromAsync(state, sourceAccess.ObjectType, targetAccess.ObjectType, cancellation).ConfigureAwait(false);
            await FromAsync(state, sourceAccess.IndexType, targetAccess.IndexType, cancellation).ConfigureAwait(false);
        }
        else if (source is StringMappingType sourceMapping && target is StringMappingType targetMapping)
        {
            if (source.Symbol == target.Symbol)
                await FromAsync(state, sourceMapping.Target, targetMapping.Target, cancellation).ConfigureAwait(false);
        }
        else if (source is SubstitutionType substitution)
        {
            await FromAsync(state, substitution.BaseType, target, cancellation).ConfigureAwait(false);
            var substitute = NoInfer(source) ? substitution.BaseType
                : await algebra.IntersectionAsync(
                    [substitution.Constraint, substitution.BaseType],
                    cancellation: cancellation).ConfigureAwait(false);
            await PriorityAsync(state, substitute, target, InferencePriority.SubstituteSource, cancellation).ConfigureAwait(false);
        }
        else if (target is ConditionalType conditional)
            await OnceAsync(
                state,
                source,
                target,
                () => ConditionalAsync(state, source, conditional, cancellation),
                cancellation).ConfigureAwait(false);
        else if (target is UnionOrIntersectionType composite)
            await MultipleAsync(state, source, composite.Types, composite.Flags, cancellation).ConfigureAwait(false);
        else if (source is UnionType union)
        {
            foreach (var part in union.Types)
                await FromAsync(state, part, target, cancellation).ConfigureAwait(false);
        }
        else if (target is TemplateLiteralType template)
            await TemplateAsync(state, source, template, cancellation).ConfigureAwait(false);
        else
        {
            source = await views.ReducedAsync(source, cancellation).ConfigureAwait(false);
            if (source is MappedType sourceMap && target is MappedType targetMap
                && await mapped.IsGenericAsync(sourceMap, cancellation).ConfigureAwait(false)
                && await mapped.IsGenericAsync(targetMap, cancellation).ConfigureAwait(false))
                await OnceAsync(
                    state,
                    source,
                    target,
                    () => GenericMappedAsync(state, sourceMap, targetMap, cancellation),
                    cancellation).ConfigureAwait(false);
            if (!((state.Priority & InferencePriority.NoConstraints) != 0
                && (source.Flags & (TypeFlags.Intersection | TypeFlags.Instantiable)) != 0))
            {
                var apparent = await views.ApparentAsync(source, cancellation).ConfigureAwait(false);
                if (apparent != source && (apparent.Flags & (TypeFlags.Object | TypeFlags.Intersection)) == 0)
                {
                    await FromAsync(state, apparent, target, cancellation).ConfigureAwait(false);
                    return;
                }
                source = apparent;
            }
            if ((source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
                await OnceAsync(
                    state,
                    source,
                    target,
                    () => ObjectsAsync(state, source, target, cancellation),
                    cancellation).ConfigureAwait(false);
        }
    }

    private async ValueTask OnceAsync(State state, Type source, Type target, Func<ValueTask> action, CancellationToken cancellation)
    {
        var key = (source, target);
        if (state.Visited.TryGetValue(key, out var previous))
        {
            state.InferencePriority = Min(state.InferencePriority, previous);
            return;
        }
        state.Visited[key] = InferencePriority.Circularity;
        var priority = state.InferencePriority;
        var expanding = state.Expanding;
        state.InferencePriority = InferencePriority.MaxValue;
        state.Sources.Add(source);
        state.Targets.Add(target);
        try
        {
            if (await recursion.IsDeeplyNestedAsync(source, state.Sources, 2, cancellation).ConfigureAwait(false))
                state.Expanding |= ExpandingFlags.Source;
            if (await recursion.IsDeeplyNestedAsync(target, state.Targets, 2, cancellation).ConfigureAwait(false))
                state.Expanding |= ExpandingFlags.Target;
            if (state.Expanding != ExpandingFlags.Both)
                await action().ConfigureAwait(false);
            else
                state.InferencePriority = InferencePriority.Circularity;
            state.Visited[key] = state.InferencePriority;
        }
        finally
        {
            state.Targets.RemoveAt(state.Targets.Count - 1);
            state.Sources.RemoveAt(state.Sources.Count - 1);
            state.Expanding = expanding;
            state.InferencePriority = Min(state.InferencePriority, priority);
        }
    }

    private async ValueTask ArgumentsAsync(State state, IReadOnlyList<Type> sources, IReadOnlyList<Type> targets,
        IReadOnlyList<VarianceFlags> variances, CancellationToken cancellation)
    {
        for (int i = 0; i < Math.Min(sources.Count, targets.Count); i++)
            if (i < variances.Count && (variances[i] & VarianceFlags.VarianceMask) == VarianceFlags.Contravariant)
                await ContraAsync(state, sources[i], targets[i], cancellation).ConfigureAwait(false);
            else
                await FromAsync(state, sources[i], targets[i], cancellation).ConfigureAwait(false);
    }

    private async ValueTask PriorityAsync(State state, Type source, Type target, InferencePriority priority,
        CancellationToken cancellation, bool contravariant = false)
    {
        var previous = state.Priority;
        state.Priority |= priority;
        try
        {
            if (contravariant)
                await ContraAsync(state, source, target, cancellation).ConfigureAwait(false);
            else
                await FromAsync(state, source, target, cancellation).ConfigureAwait(false);
        }
        finally
        {
            state.Priority = previous;
        }
    }

    private async ValueTask ContraAsync(State state, Type source, Type target, CancellationToken cancellation)
    {
        state.Contravariant = !state.Contravariant;
        try
        {
            await FromAsync(state, source, target, cancellation).ConfigureAwait(false);
        }
        finally
        {
            state.Contravariant = !state.Contravariant;
        }
    }

    private static InferenceInfo? Info(State state, Type type) => (type.Flags & TypeFlags.TypeVariable) != 0
        ? state.Context.Inferences.FirstOrDefault(i => i.Parameter == NonDistributed(type)) : null;

    private bool Blocked(Type type) => type.Symbol?.Declarations.Any(host.SkipDirectInference) == true;

    private static Type NonDistributed(Type type) => type is TypeParameter parameter ? parameter.NonDistributed : type;

    private static bool NoInfer(Type type) =>
        type is SubstitutionType substitution && (substitution.Constraint.Flags & TypeFlags.Unknown) != 0;

    private static IReadOnlyList<Type> Parts(Type type) => type is UnionType union ? union.Types : [type];

    private static bool Literal(Type type) => (type.Flags & TypeFlags.Boolean) != 0 || (type.Flags & TypeFlags.Unit) != 0
            || type is UnionType union
                && (union.ObjectFlags & ObjectFlags.PrimitiveUnion) != 0
                && union.Types.All(t => (t.Flags & TypeFlags.Unit) != 0);

    private static InferencePriority Min(InferencePriority left, InferencePriority right) =>
        (InferencePriority)Math.Min((int)left, (int)right);

    private async ValueTask<bool> AllNonGenericObjectsAsync(IReadOnlyList<Type> types, CancellationToken cancellation)
    {
        foreach (var type in types)
            if ((type.Flags & TypeFlags.Object) == 0
                || (await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) != 0)
                return false;
        return true;
    }
}
