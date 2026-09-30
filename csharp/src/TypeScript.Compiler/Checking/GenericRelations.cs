using TypeScript.Compiler.Text;
using System.Globalization;

namespace TypeScript.Compiler.Checking;

// Generic targets are examined before source constraints. This ordering keeps
// relationships between mapped keys and their original type parameters intact.
internal sealed class GenericRelations(TypeContext context, TypeAlgebra algebra, TypeConstraints constraints,
    TypeKeys keys, IndexedTypes indexed, MappedTypes mapped, MappedMembers members, TypeInstantiation instantiation,
    TypeVariance variance, TypeViews views, BaseTypes bases, Func<bool, Type> arrayTarget)
{
    internal async ValueTask<Ternary?> TargetAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (target is TypeParameter && source is MappedType mapping && mapping.Declaration!.NameType is null
            && await operation.CompareWithoutErrorsAsync(await keys.GetAsync(target, cancellation: cancellation).ConfigureAwait(false),
                await mapped.ConstraintAsync(mapping, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false) != Ternary.False
            && (MappedTypes.Modifiers(mapping) & MappedTypeModifiers.IncludeOptional) == 0)
        {
            var template = await mapped.TemplateAsync(mapping, cancellation).ConfigureAwait(false);
            var access = await indexed.GetAsync(
                target,
                await mapped.ParameterAsync(mapping, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false);
            var result = await operation.CompareAsync(template, access, cancellation: cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        if (target is IndexedAccessType targetAccess)
        {
            RelationExplanation? directExplanation = null;
            if (source is IndexedAccessType sourceAccess)
            {
                var result = await operation.CompareAsync(
                    sourceAccess.ObjectType,
                    targetAccess.ObjectType,
                    cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    result &= await operation.CompareAsync(
                        sourceAccess.IndexType,
                        targetAccess.IndexType,
                        cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    return result;
                directExplanation = operation.Explanation;
            }
            if (operation.Kind is RelationKind.Assignable or RelationKind.Comparable)
            {
                var objectType = await constraints.BaseConstraintOrTypeAsync(targetAccess.ObjectType, cancellation).ConfigureAwait(false);
                var indexType = await constraints.BaseConstraintOrTypeAsync(targetAccess.IndexType, cancellation).ConfigureAwait(false);
                if ((await mapped.GenericFlagsAsync(objectType, cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericObjectType) == 0
                    && (await mapped.GenericFlagsAsync(
                        indexType,
                        cancellation).ConfigureAwait(false) & ObjectFlags.IsGenericIndexType) == 0)
                {
                    var flags = AccessFlags.Writing | (objectType != targetAccess.ObjectType ? AccessFlags.NoIndexSignatures : 0);
                    if (await indexed.TryGetAsync(
                        objectType,
                        indexType,
                        flags,
                        cancellation: cancellation).ConfigureAwait(false) is { } constraint)
                    {
                        var result = await operation.CompareAsync(
                            source,
                            constraint,
                            RecursionFlags.Target,
                            intersection,
                            cancellation).ConfigureAwait(false);
                        if (result != Ternary.False)
                            return result;
                        if (directExplanation is not null && operation.Explanation is { } constraintExplanation
                            && Depth(directExplanation) <= Depth(constraintExplanation))
                            operation.RestoreExplanation(directExplanation);
                    }
                }
            }
        }
        else if (target is IndexType targetIndex)
        {
            if (source is IndexType sourceIndex)
            {
                var result = await operation.CompareWithoutErrorsAsync(
                    targetIndex.Target,
                    sourceIndex.Target,
                    cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    return result;
            }
            if (targetIndex.Target is TypeReference { Target: TupleType tuple })
            {
                var known = new List<Type>();
                for (int i = 0; i < tuple.FixedLength; i++)
                    known.Add(context.GetStringLiteralType(Utf8String.Format(i)));
                known.Add(await keys.GetAsync(arrayTarget(tuple.IsReadonly), cancellation: cancellation).ConfigureAwait(false));
                var result = await operation.CompareAsync(source,
                    await algebra.UnionAsync(known, cancellation: cancellation).ConfigureAwait(false),
                    RecursionFlags.Target,
                    cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    return result;
            }
            else if (await constraints.SimplifiedOrConstraintAsync(
                targetIndex.Target,
                cancellation).ConfigureAwait(false) is { } constraint)
            {
                var targetKeys = await keys.GetAsync(
                    constraint,
                    targetIndex.IndexFlags | IndexFlags.NoReducibleCheck,
                    cancellation).ConfigureAwait(false);
                if (await operation.CompareAsync(
                    source,
                    targetKeys,
                    RecursionFlags.Target,
                    cancellation: cancellation).ConfigureAwait(false) == Ternary.True)
                    return Ternary.True;
            }
            else if (targetIndex.Target is MappedType targetMap
                && await mapped.IsGenericAsync(targetMap, cancellation).ConfigureAwait(false))
            {
                var name = await mapped.NameAsync(targetMap, cancellation).ConfigureAwait(false);
                var bound = await mapped.ConstraintAsync(targetMap, cancellation).ConfigureAwait(false);
                var targetKeys = name is not null && MappedMembers.HasKeyofConstraint(targetMap)
                    ? await algebra.UnionAsync(
                        [await members.ApparentKeysAsync(name, targetMap, cancellation).ConfigureAwait(false), name],
                        cancellation: cancellation).ConfigureAwait(false)
                    : name ?? bound;
                if (await operation.CompareAsync(
                    source,
                    targetKeys,
                    RecursionFlags.Target,
                    cancellation: cancellation).ConfigureAwait(false) == Ternary.True)
                    return Ternary.True;
            }
        }
        else if (target is MappedType targetMap && await mapped.IsGenericAsync(targetMap, cancellation).ConfigureAwait(false))
        {
            bool remapped = targetMap.Declaration!.NameType is not null;
            var template = await mapped.TemplateAsync(targetMap, cancellation).ConfigureAwait(false);
            var modifiers = MappedTypes.Modifiers(targetMap);
            if ((modifiers & MappedTypeModifiers.ExcludeOptional) == 0)
            {
                if (!remapped && template is IndexedAccessType access && access.ObjectType == source
                    && access.IndexType == await mapped.ParameterAsync(targetMap, cancellation).ConfigureAwait(false))
                    return Ternary.True;
                if (!(source is MappedType sourceMap && await mapped.IsGenericAsync(sourceMap, cancellation).ConfigureAwait(false)))
                {
                    var previousExplanation = operation.Explanation;
                    var targetKeys = remapped ? (await mapped.NameAsync(targetMap, cancellation).ConfigureAwait(false))!
                        : await mapped.ConstraintAsync(targetMap, cancellation).ConfigureAwait(false);
                    var sourceKeys = await keys.GetAsync(source, IndexFlags.NoIndexSignatures, cancellation).ConfigureAwait(false);
                    bool optional = (modifiers & MappedTypeModifiers.IncludeOptional) != 0;
                    var filtered = optional
                        ? await algebra.IntersectionAsync([targetKeys, sourceKeys], cancellation: cancellation).ConfigureAwait(false)
                        : null;
                    if (optional && (filtered!.Flags & TypeFlags.Never) == 0
                        || !optional
                            && await operation.CompareWithoutErrorsAsync(
                                targetKeys,
                                sourceKeys,
                                cancellation: cancellation).ConfigureAwait(false) != Ternary.False)
                    {
                        var parameter = await mapped.ParameterAsync(targetMap, cancellation).ConfigureAwait(false);
                        var nonNullable = algebra.Filter(template, t => (t.Flags & ~TypeFlags.Nullable) != 0);
                        Ternary result;
                        if (!remapped && nonNullable is IndexedAccessType indexedTemplate && indexedTemplate.IndexType == parameter)
                            result = await operation.CompareAsync(
                                source,
                                indexedTemplate.ObjectType,
                                RecursionFlags.Target,
                                cancellation: cancellation).ConfigureAwait(false);
                        else
                        {
                            Type index = remapped ? filtered ?? targetKeys : filtered is not null
                                ? await algebra.IntersectionAsync(
                                    [filtered, parameter],
                                    cancellation: cancellation).ConfigureAwait(false) : parameter;
                            var sourceValue = await indexed.GetAsync(source, index, cancellation: cancellation).ConfigureAwait(false);
                            result = await operation.CompareAsync(sourceValue, template, cancellation: cancellation).ConfigureAwait(false);
                        }
                        if (result != Ternary.False)
                            return result;
                    }
                    operation.RestoreExplanation(previousExplanation);
                }
            }
        }
        return null;

        static int Depth(RelationExplanation? explanation)
        {
            int depth = 0;
            for (; explanation is not null; explanation = explanation.Next)
                depth++;
            return depth;
        }
    }

    internal async ValueTask<Ternary?> SourceAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (source is IndexedAccessType access)
        {
            if (target is IndexedAccessType)
                return Ternary.False;
            var constraint = await constraints.ConstraintAsync(source, cancellation).ConfigureAwait(false) ?? context.UnknownType;
            var result = await operation.CompareWithoutErrorsAsync(
                constraint,
                target,
                RecursionFlags.Source,
                intersection,
                cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            var withThis = await bases.WithThisAsync(constraint, source, cancellation: cancellation).ConfigureAwait(false);
            result = constraint == context.UnknownType
                ? await operation.CompareWithoutErrorsAsync(
                    withThis,
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false)
                : await operation.CompareContinuingAsync(
                    withThis,
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            if (await indexed.IsMappedGenericAccessAsync(access, cancellation).ConfigureAwait(false)
                && await constraints.ConstraintAsync(access.IndexType, cancellation).ConfigureAwait(false) is { } indexConstraint)
                return await operation.CompareContinuingAsync(
                    await indexed.GetAsync(access.ObjectType, indexConstraint, cancellation: cancellation).ConfigureAwait(false),
                    target, RecursionFlags.Source, cancellation: cancellation).ConfigureAwait(false);
            return Ternary.False;
        }
        if (source is IndexType index)
        {
            bool deferredMap = await keys.ShouldDeferAsync(index.Target, index.IndexFlags, cancellation).ConfigureAwait(false)
                && index.Target is MappedType;
            var result = deferredMap
                ? await operation.CompareWithoutErrorsAsync(context.StringNumberSymbolType, target, RecursionFlags.Source,
                    cancellation: cancellation).ConfigureAwait(false)
                : await operation.CompareContinuingAsync(context.StringNumberSymbolType, target, RecursionFlags.Source,
                    cancellation: cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            if (deferredMap)
            {
                var mapping = (MappedType)index.Target;
                var name = await mapped.NameAsync(mapping, cancellation).ConfigureAwait(false);
                var sourceKeys = name is not null && MappedMembers.HasKeyofConstraint(mapping)
                    ? await members.ApparentKeysAsync(name, mapping, cancellation).ConfigureAwait(false)
                    : name ?? await mapped.ConstraintAsync(mapping, cancellation).ConfigureAwait(false);
                return await operation.CompareContinuingAsync(
                    sourceKeys,
                    target,
                    RecursionFlags.Source,
                    cancellation: cancellation).ConfigureAwait(false);
            }
            return Ternary.False;
        }
        if ((source.Flags & (TypeFlags.TypeVariable | TypeFlags.Index | TypeFlags.Conditional | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) == 0)
        {
            if (operation.Kind is not (RelationKind.Subtype or RelationKind.StrictSubtype)
                && target is MappedType partial && (MappedTypes.Modifiers(partial) & MappedTypeModifiers.IncludeOptional) != 0
                && await views.EmptyObjectAsync(source, cancellation).ConfigureAwait(false))
                return Ternary.True;
            if (target is MappedType targetMap && await mapped.IsGenericAsync(targetMap, cancellation).ConfigureAwait(false))
                return source is MappedType sourceMap && await mapped.IsGenericAsync(sourceMap, cancellation).ConfigureAwait(false)
                    ? await MappedAsync(operation, sourceMap, targetMap, cancellation).ConfigureAwait(false) : Ternary.False;
        }
        return null;
    }

    internal async ValueTask<Ternary> MappedAsync(RelationOperation operation, MappedType source, MappedType target,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        bool modifiers = operation.Kind == RelationKind.Comparable
            || operation.Kind == RelationKind.Identity && MappedTypes.Modifiers(source) == MappedTypes.Modifiers(target)
            || operation.Kind != RelationKind.Identity
                && await members.CombinedOptionalityAsync(
                    source,
                    cancellation).ConfigureAwait(false) <= await members.CombinedOptionalityAsync(
                        target,
                        cancellation).ConfigureAwait(false);
        if (!modifiers)
            return Ternary.False;
        var targetConstraint = await mapped.ConstraintAsync(target, cancellation).ConfigureAwait(false);
        var sourceConstraint = await variance.ReportTypeAsync(await mapped.ConstraintAsync(source, cancellation).ConfigureAwait(false),
            await members.CombinedOptionalityAsync(source, cancellation).ConfigureAwait(false) < 0, cancellation).ConfigureAwait(false);
        var result = await operation.CompareAsync(targetConstraint, sourceConstraint, cancellation: cancellation).ConfigureAwait(false);
        if (result == Ternary.False)
            return result;
        var mapper = TypeMapper.Create(
            [await mapped.ParameterAsync(source, cancellation).ConfigureAwait(false)],
            [await mapped.ParameterAsync(target, cancellation).ConfigureAwait(false)]);
        var sourceName = await instantiation.InstantiateAsync(
            await mapped.NameAsync(source, cancellation).ConfigureAwait(false),
            mapper,
            cancellation: cancellation).ConfigureAwait(false);
        var targetName = await instantiation.InstantiateAsync(
            await mapped.NameAsync(target, cancellation).ConfigureAwait(false),
            mapper,
            cancellation: cancellation).ConfigureAwait(false);
        if (sourceName != targetName)
            return Ternary.False;
        var sourceTemplate = await instantiation.InstantiateAsync(
            await mapped.TemplateAsync(source, cancellation).ConfigureAwait(false),
            mapper,
            cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Mapped template instantiation returned no type");
        return result & await operation.CompareAsync(
            sourceTemplate,
            await mapped.TemplateAsync(target, cancellation).ConfigureAwait(false),
            cancellation: cancellation).ConfigureAwait(false);
    }
}
