using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    private readonly Dictionary<Signature, Signature> baseSignatures = [];

    private async ValueTask ObjectsAsync(State state, Type source, Type target, CancellationToken cancellation)
    {
        if (source is TypeReference sr
            && target is TypeReference tr
            && (source.ObjectFlags & target.ObjectFlags & ObjectFlags.Reference) != 0
            && (sr.Target == tr.Target || host.IsArray(source) && host.IsArray(target)))
        {
            await ArgumentsAsync(state, await host.TypeArgumentsAsync(sr, cancellation).ConfigureAwait(false),
                await host.TypeArgumentsAsync(tr, cancellation).ConfigureAwait(false),
                await host.InferenceVariancesAsync((TypeReference)sr.Target!, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false);
            return;
        }
        if (source is MappedType sourceMap && target is MappedType targetMap
            && await mapped.IsGenericAsync(sourceMap, cancellation).ConfigureAwait(false)
            && await mapped.IsGenericAsync(targetMap, cancellation).ConfigureAwait(false))
            await GenericMappedAsync(state, sourceMap, targetMap, cancellation).ConfigureAwait(false);
        if (target is MappedType mapping && mapping.Declaration!.NameType is null
            && await ToMappedAsync(
                state,
                source,
                mapping,
                await mapped.ConstraintAsync(mapping, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false))
            return;
        if (await host.DefinitelyUnrelatedAsync(source, target, cancellation).ConfigureAwait(false))
            return;
        if (host.IsArray(source) || source is TypeReference { Target: TupleType })
        {
            if (target is TypeReference { Target: TupleType tuple })
            {
                await TupleAsync(state, (TypeReference)source, (TypeReference)target, tuple, cancellation).ConfigureAwait(false);
                return;
            }
            if (host.IsArray(target))
            {
                await IndexesAsync(state, source, target, cancellation).ConfigureAwait(false);
                return;
            }
        }
        foreach (var property in await host.ObjectPropertiesAsync(target, cancellation).ConfigureAwait(false))
            if (await host.PropertyAsync(source, property.Name, cancellation).ConfigureAwait(false) is { } sourceProperty
                && !sourceProperty.Declarations.Any(host.SkipDirectInference))
                await FromAsync(
                    state,
                    symbols.NonMissing(
                        await host.SymbolTypeAsync(sourceProperty, cancellation).ConfigureAwait(false),
                        (sourceProperty.Flags & SymbolFlags.Optional) != 0),
                    symbols.NonMissing(
                        await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false),
                        (property.Flags & SymbolFlags.Optional) != 0),
                    cancellation).ConfigureAwait(false);
        foreach (bool construct in new[] { false, true })
        {
            var sourceSignatures = await host.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false);
            if (sourceSignatures.Count == 0)
                continue;
            var targetSignatures = await host.SignaturesAsync(target, construct, cancellation).ConfigureAwait(false);
            for (int i = 0; i < targetSignatures.Count; i++)
                await SignatureAsync(
                    state,
                    await BaseSignatureAsync(
                        sourceSignatures[Math.Max(sourceSignatures.Count - targetSignatures.Count + i, 0)],
                        cancellation).ConfigureAwait(false),
                    await signatureRelations.ErasedAsync(targetSignatures[i], cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
        }
        await IndexesAsync(state, source, target, cancellation).ConfigureAwait(false);
    }

    private async ValueTask TupleAsync(
        State state,
        TypeReference source,
        TypeReference target,
        TupleType tuple,
        CancellationToken cancellation)
    {
        var sourceArgs = await host.TypeArgumentsAsync(source, cancellation).ConfigureAwait(false);
        var targetArgs = await host.TypeArgumentsAsync(target, cancellation).ConfigureAwait(false);
        var sourceTuple = source.Target as TupleType;
        int sourceArity = sourceTuple?.ElementInfos.Count ?? sourceArgs.Count;
        int targetArity = tuple.ElementInfos.Count;
        var infos = tuple.ElementInfos;
        if (sourceTuple is not null && sourceArity == targetArity
            && infos.Select(
                (e, i) => (e.Flags & ElementFlags.Variable) == (sourceTuple.ElementInfos[i].Flags & ElementFlags.Variable)).All(v => v))
        {
            for (int i = 0; i < targetArity; i++)
                await FromAsync(state, sourceArgs[i], targetArgs[i], cancellation).ConfigureAwait(false);
            return;
        }
        int start = sourceTuple is null ? 0 : Math.Min(sourceTuple.FixedLength, tuple.FixedLength);
        int end = sourceTuple is not null && (tuple.CombinedFlags & ElementFlags.Variable) != 0
            ? Math.Min(EndFixed(sourceTuple), EndFixed(tuple))
            : 0;
        for (int i = 0; i < start; i++)
            await FromAsync(state, sourceArgs[i], targetArgs[i], cancellation).ConfigureAwait(false);
        if (sourceTuple is null || sourceArity - start - end == 1 && (sourceTuple.ElementInfos[start].Flags & ElementFlags.Rest) != 0)
        {
            var rest = sourceArgs[start];
            for (int i = start; i < targetArity - end; i++)
                await FromAsync(
                    state,
                    (infos[i].Flags & ElementFlags.Variadic) != 0
                        ? await tuples.ArrayAsync(rest, cancellation: cancellation).ConfigureAwait(false)
                        : rest,
                    targetArgs[i], cancellation).ConfigureAwait(false);
        }
        else
        {
            int middle = targetArity - start - end;
            if (middle == 2)
            {
                if ((infos[start].Flags & infos[start + 1].Flags & ElementFlags.Variadic) != 0)
                {
                    if (Info(state, targetArgs[start]) is { ImpliedArity: >= 0 } info)
                    {
                        await FromAsync(
                            state,
                            await tuples.SliceAsync(
                                source,
                                start,
                                end + sourceArity - info.ImpliedArity,
                                cancellation).ConfigureAwait(false),
                            targetArgs[start],
                            cancellation).ConfigureAwait(false);
                        await FromAsync(
                            state,
                            await tuples.SliceAsync(source, start + info.ImpliedArity, end, cancellation).ConfigureAwait(false),
                            targetArgs[start + 1],
                            cancellation).ConfigureAwait(false);
                    }
                }
                else if ((infos[start].Flags & ElementFlags.Variadic) != 0 && (infos[start + 1].Flags & ElementFlags.Rest) != 0)
                {
                    if (Info(state, targetArgs[start]) is { } info
                        && await constraints.BaseConstraintAsync(
                            info.Parameter,
                            cancellation).ConfigureAwait(false) is TypeReference { Target: TupleType bound }
                        && (bound.CombinedFlags & ElementFlags.Variable) == 0)
                    {
                        await FromAsync(
                            state,
                            await tuples.SliceAsync(
                                source,
                                start,
                                sourceArity - start - bound.FixedLength,
                                cancellation).ConfigureAwait(false),
                            targetArgs[start],
                            cancellation).ConfigureAwait(false);
                        if (await tuples.SliceElementAsync(
                            source,
                            start + bound.FixedLength,
                            end,
                            cancellation: cancellation).ConfigureAwait(false) is { } rest)
                            await FromAsync(state, rest, targetArgs[start + 1], cancellation).ConfigureAwait(false);
                    }
                }
                else if ((infos[start].Flags & ElementFlags.Rest) != 0 && (infos[start + 1].Flags & ElementFlags.Variadic) != 0)
                {
                    if (Info(state, targetArgs[start + 1]) is { } info
                        && await constraints.BaseConstraintAsync(
                            info.Parameter,
                            cancellation).ConfigureAwait(false) is TypeReference { Target: TupleType bound }
                        && (bound.CombinedFlags & ElementFlags.Variable) == 0)
                    {
                        int endIndex = sourceArity - EndFixed(tuple), startIndex = endIndex - bound.FixedLength;
                        if (startIndex >= start)
                        {
                            var trailing = await tuples.CreateAsync(sourceArgs.Skip(startIndex).Take(endIndex - startIndex).ToArray(),
                                sourceTuple.ElementInfos.Skip(startIndex).Take(endIndex - startIndex).ToArray(),
                                cancellation: cancellation).ConfigureAwait(false);
                            if (await tuples.SliceElementAsync(
                                source,
                                start,
                                end + bound.FixedLength,
                                cancellation: cancellation).ConfigureAwait(false) is { } rest)
                                await FromAsync(state, rest, targetArgs[start], cancellation).ConfigureAwait(false);
                            await FromAsync(state, trailing, targetArgs[start + 1], cancellation).ConfigureAwait(false);
                        }
                    }
                }
            }
            else if (middle == 1 && (infos[start].Flags & ElementFlags.Variadic) != 0)
                await PriorityAsync(
                    state,
                    await tuples.SliceAsync(source, start, end, cancellation).ConfigureAwait(false),
                    targetArgs[start],
                    (infos[targetArity - 1].Flags & ElementFlags.Optional) != 0 ? InferencePriority.SpeculativeTuple : 0,
                    cancellation).ConfigureAwait(false);
            else if (middle == 1 && (infos[start].Flags & ElementFlags.Rest) != 0
                && await tuples.SliceElementAsync(source, start, end, cancellation: cancellation).ConfigureAwait(false) is { } rest)
                await FromAsync(state, rest, targetArgs[start], cancellation).ConfigureAwait(false);
        }
        for (int i = 0; i < end; i++)
            await FromAsync(state, sourceArgs[sourceArity - i - 1], targetArgs[targetArity - i - 1], cancellation).ConfigureAwait(false);
    }

    private async ValueTask SignatureAsync(State state, Signature source, Signature target, CancellationToken cancellation)
    {
        if ((source.Flags & SignatureFlags.IsNonInferrable) == 0)
        {
            bool bivariant = state.Bivariant;
            state.Bivariant |= target.Declaration?.Kind is SyntaxKind.MethodDeclaration or SyntaxKind.MethodSignature
                or SyntaxKind.Constructor;
            try
            {
                await ApplyParametersAsync(
                    source,
                    target,
                    (s, t) => host.StrictFunctionTypes || (state.Priority & InferencePriority.AlwaysStrict) != 0
                    ? ContraAsync(state, s, t, cancellation) : FromAsync(state, s, t, cancellation), cancellation).ConfigureAwait(false);
            }
            finally
            {
                state.Bivariant = bivariant;
            }
        }
        await ApplyReturnsAsync(source, target, (s, t) => FromAsync(state, s, t, cancellation), cancellation).ConfigureAwait(false);
    }

    internal async ValueTask ApplyParametersAsync(
        Signature source,
        Signature target,
        Func<Type, Type, ValueTask> callback,
        CancellationToken cancellation = default)
    {
        int sourceCount = await parameters.CountAsync(source, cancellation).ConfigureAwait(false);
        int targetCount = await parameters.CountAsync(target, cancellation).ConfigureAwait(false);
        var sourceRest = await parameters.EffectiveRestAsync(source, cancellation).ConfigureAwait(false);
        var targetRest = await parameters.EffectiveRestAsync(target, cancellation).ConfigureAwait(false);
        int targetFixed = targetCount - (targetRest is null ? 0 : 1);
        int count = sourceRest is null ? Math.Min(sourceCount, targetFixed) : targetFixed;
        if (await parameters.ThisAsync(source, cancellation).ConfigureAwait(false) is { } sourceThis
            && await parameters.ThisAsync(target, cancellation).ConfigureAwait(false) is { } targetThis)
            await callback(sourceThis, targetThis).ConfigureAwait(false);
        for (int i = 0; i < count; i++)
            await callback(
                await parameters.AtAsync(source, i, cancellation).ConfigureAwait(false),
                await parameters.AtAsync(target, i, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        if (targetRest is not null)
        {
            bool readOnly = await host.ConstTypeVariableAsync(targetRest, cancellation).ConfigureAwait(false);
            if (readOnly)
                foreach (var part in Parts(targetRest))
                    if (await host.MutableArrayLikeAsync(part, cancellation).ConfigureAwait(false))
                    {
                        readOnly = false;
                        break;
                    }
            await callback(
                await parameters.RestAtAsync(source, count, readOnly, cancellation).ConfigureAwait(false),
                targetRest).ConfigureAwait(false);
        }
    }

    internal async ValueTask ApplyReturnsAsync(
        Signature source,
        Signature target,
        Func<Type, Type, ValueTask> callback,
        CancellationToken cancellation = default)
    {
        if (await signatures.PredicateAsync(target, cancellation).ConfigureAwait(false) is { } targetPredicate
            && await signatures.PredicateAsync(source, cancellation).ConfigureAwait(false) is { } sourcePredicate
            && sourcePredicate.Kind == targetPredicate.Kind && sourcePredicate.ParameterIndex == targetPredicate.ParameterIndex
            && sourcePredicate.Type is { } sourceType && targetPredicate.Type is { } targetType)
        {
            await callback(sourceType, targetType).ConfigureAwait(false);
            return;
        }
        var result = await signatures.ReturnAsync(target, cancellation).ConfigureAwait(false);
        if (await variables.CouldContainAsync(result, cancellation).ConfigureAwait(false))
            await callback(await signatures.ReturnAsync(source, cancellation).ConfigureAwait(false), result).ConfigureAwait(false);
    }

    internal async ValueTask<Signature> BaseSignatureAsync(Signature signature, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        if (signature.TypeParameters.Count == 0)
            return signature;
        if (baseSignatures.TryGetValue(signature, out var cached))
            return cached;
        var bounds = new Type[signature.TypeParameters.Count];
        for (int i = 0; i < bounds.Length; i++)
            bounds[i] = await constraints.ParameterConstraintAsync(
                (TypeParameter)signature.TypeParameters[i],
                cancellation).ConfigureAwait(false) ?? context.UnknownType;
        var mapper = TypeMapper.Create(signature.TypeParameters.ToArray(), bounds);
        IReadOnlyList<Type> types = await instantiation.TypesAsync(signature.TypeParameters, mapper, cancellation).ConfigureAwait(false);
        for (int i = 1; i < bounds.Length; i++)
            types = await instantiation.TypesAsync(types, mapper, cancellation).ConfigureAwait(false);
        types = await instantiation.TypesAsync(
            types,
            TypeMapper.ToSingle(signature.TypeParameters.ToArray(), context.AnyType),
            cancellation).ConfigureAwait(false);
        var result = await instantiation.SignatureAsync(
            signature,
            TypeMapper.Create(signature.TypeParameters.ToArray(), types.ToArray()),
            true,
            cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return baseSignatures[signature] = result;
    }

    private async ValueTask IndexesAsync(State state, Type source, Type target, CancellationToken cancellation)
    {
        var priority = (source.ObjectFlags & target.ObjectFlags & ObjectFlags.Mapped) != 0 ? InferencePriority.HomomorphicMappedType : 0;
        var targetIndexes = await host.IndexesAsync(target, cancellation).ConfigureAwait(false);
        if (await host.InferableIndexAsync(source, cancellation).ConfigureAwait(false))
            foreach (var targetIndex in targetIndexes)
            {
                var types = new List<Type>();
                foreach (var property in await host.PropertiesAsync(source, cancellation).ConfigureAwait(false))
                    if (await indexes.ApplicableTypeAsync(
                        await keys.PropertyAsync(
                            property,
                            TypeFlags.StringOrNumberLiteralOrUnique,
                            false,
                            cancellation).ConfigureAwait(false),
                        targetIndex.KeyType,
                        cancellation).ConfigureAwait(false))
                    {
                        var type = await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false);
                        if ((property.Flags & SymbolFlags.Optional) != 0)
                            type = context.ExactOptionalPropertyTypes ? algebra.Filter(type, t => t != context.MissingType)
                                : await host.NonUndefinedAsync(type, cancellation).ConfigureAwait(false);
                        types.Add(type);
                    }
                foreach (var index in await host.IndexesAsync(source, cancellation).ConfigureAwait(false))
                    if (await indexes.ApplicableTypeAsync(index.KeyType, targetIndex.KeyType, cancellation).ConfigureAwait(false))
                        types.Add(index.ValueType);
                if (types.Count != 0)
                    await PriorityAsync(
                        state,
                        await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false),
                        targetIndex.ValueType,
                        priority,
                        cancellation).ConfigureAwait(false);
            }
        foreach (var targetIndex in targetIndexes)
            if (await indexes.ApplicableAsync(
                await host.IndexesAsync(source, cancellation).ConfigureAwait(false),
                targetIndex.KeyType,
                cancellation).ConfigureAwait(false) is { } sourceIndex)
                await PriorityAsync(state, sourceIndex.ValueType, targetIndex.ValueType, priority, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> ToMappedAsync(
        State state,
        Type source,
        MappedType target,
        Type constraint,
        CancellationToken cancellation)
    {
        if (constraint is UnionOrIntersectionType composite)
        {
            bool result = false;
            foreach (var part in composite.Types)
                result = await ToMappedAsync(state, source, target, part, cancellation).ConfigureAwait(false) || result;
            return result;
        }
        if (constraint is IndexType index)
        {
            if (Info(state, index.Target) is { IsFixed: false } info && !Blocked(source)
                && await host.ReverseMappedInferenceAsync(source, target, index, cancellation).ConfigureAwait(false) is { } inferred)
                await PriorityAsync(state, inferred, info.Parameter, (source.ObjectFlags & ObjectFlags.NonInferrableType) != 0
                    ? InferencePriority.PartialHomomorphicMappedType : InferencePriority.HomomorphicMappedType,
                    cancellation).ConfigureAwait(false);
            return true;
        }
        if (constraint is TypeParameter)
        {
            await PriorityAsync(
                state,
                await keys.GetAsync(
                    source,
                    host.HasInferencePattern(source) ? IndexFlags.NoIndexSignatures : 0,
                    cancellation).ConfigureAwait(false),
                constraint, InferencePriority.MappedTypeConstraint, cancellation).ConfigureAwait(false);
            if (await constraints.ConstraintAsync(constraint, cancellation).ConfigureAwait(false) is { } extended
                && await ToMappedAsync(state, source, target, extended, cancellation).ConfigureAwait(false))
                return true;
            var types = new List<Type>();
            foreach (var property in await host.PropertiesAsync(source, cancellation).ConfigureAwait(false))
                types.Add(await host.SymbolTypeAsync(property, cancellation).ConfigureAwait(false));
            foreach (var sourceIndex in await host.IndexesAsync(source, cancellation).ConfigureAwait(false))
                types.Add(sourceIndex != host.EnumNumberIndex ? sourceIndex.ValueType : context.NeverType);
            await FromAsync(state, await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false),
                await mapped.TemplateAsync(target, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
            return true;
        }
        return false;
    }

    private static int EndFixed(TupleType tuple) =>
        tuple.ElementInfos.Reverse().TakeWhile(i => (i.Flags & ElementFlags.Fixed) != 0).Count();
}
