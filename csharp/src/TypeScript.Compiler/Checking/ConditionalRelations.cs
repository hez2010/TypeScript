namespace TypeScript.Compiler.Checking;

internal interface IConditionalRelationHost
{
    ValueTask<TypeMapper> InferConditionalRelationAsync(IReadOnlyList<TypeParameter> parameters, Type source, Type target,
        RelationOperation operation, CancellationToken cancellation);
}

internal sealed class ConditionalRelations(TypeContext context, TypeInstantiation instantiation, TypeConstraints constraints,
    ObjectInstantiation objects, TypeRelations relations, TypeRecursion recursion, IConditionalRelationHost host)
{
    internal async ValueTask<Ternary?> TargetAsync(RelationOperation operation, Type source, ConditionalType target,
        IntersectionState intersection, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (await recursion.IsDeeplyNestedAsync(target, operation.Session.TargetStack, 10, cancellation).ConfigureAwait(false))
            return Ternary.Maybe;
        if (target.Root.InferTypeParameters is not null
            || await DistributionDependentAsync(target.Root, cancellation).ConfigureAwait(false)
            || source is ConditionalType conditional && conditional.Root == target.Root)
            return null;
        bool skipTrue = !await relations.RelatedAsync(
            await instantiation.PermissiveAsync(target.CheckType, cancellation).ConfigureAwait(false),
            await instantiation.PermissiveAsync(target.ExtendsType, cancellation).ConfigureAwait(false),
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);
        bool skipFalse = !skipTrue && await relations.RelatedAsync(
            await instantiation.RestrictiveAsync(target.CheckType, cancellation).ConfigureAwait(false),
            await instantiation.RestrictiveAsync(target.ExtendsType, cancellation).ConfigureAwait(false),
            RelationKind.Assignable,
            cancellation).ConfigureAwait(false);
        var result = skipTrue ? Ternary.True : await operation.CompareWithoutErrorsAsync(source,
            await constraints.ConditionalTrueAsync(target, cancellation: cancellation).ConfigureAwait(false),
            RecursionFlags.Target,
            intersection,
            cancellation).ConfigureAwait(false);
        if (result != Ternary.False)
        {
            result &= skipFalse ? Ternary.True : await operation.CompareWithoutErrorsAsync(source,
                await constraints.ConditionalFalseAsync(target, cancellation).ConfigureAwait(false),
                RecursionFlags.Target,
                intersection,
                cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        return null;
    }

    internal async ValueTask<Ternary> SourceAsync(RelationOperation operation, ConditionalType source, Type target,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (await recursion.IsDeeplyNestedAsync(source, operation.Session.SourceStack, 10, cancellation).ConfigureAwait(false))
            return Ternary.Maybe;
        if (target is ConditionalType conditional)
        {
            var sourceExtends = source.ExtendsType;
            TypeMapper? mapper = null;
            if (source.Root.InferTypeParameters is { Count: > 0 } parameters)
            {
                mapper = await host.InferConditionalRelationAsync(
                    parameters,
                    conditional.ExtendsType,
                    sourceExtends,
                    operation,
                    cancellation).ConfigureAwait(false);
                sourceExtends = await InstantiateAsync(sourceExtends, mapper, cancellation).ConfigureAwait(false);
            }
            if (await relations.RelatedAsync(
                sourceExtends,
                conditional.ExtendsType,
                RelationKind.Identity,
                cancellation).ConfigureAwait(false)
                && (await operation.CompareWithoutErrorsAsync(
                    source.CheckType,
                    conditional.CheckType,
                    cancellation: cancellation).ConfigureAwait(false) != Ternary.False
                    || await operation.CompareWithoutErrorsAsync(
                        conditional.CheckType,
                        source.CheckType,
                        cancellation: cancellation).ConfigureAwait(false) != Ternary.False))
            {
                var result = await operation.CompareAsync(
                    await InstantiateAsync(
                        await constraints.ConditionalTrueAsync(source, cancellation: cancellation).ConfigureAwait(false),
                        mapper,
                        cancellation).ConfigureAwait(false),
                    await constraints.ConditionalTrueAsync(conditional, cancellation: cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    result &= await operation.CompareAsync(
                        await constraints.ConditionalFalseAsync(source, cancellation).ConfigureAwait(false),
                        await constraints.ConditionalFalseAsync(conditional, cancellation).ConfigureAwait(false),
                        cancellation: cancellation).ConfigureAwait(false);
                if (result != Ternary.False)
                    return result;
            }
        }
        var defaultConstraint = await constraints.DefaultConditionalConstraintAsync(source, cancellation).ConfigureAwait(false);
        var constrained = await operation.CompareAsync(
            defaultConstraint,
            target,
            RecursionFlags.Source,
            cancellation: cancellation).ConfigureAwait(false);
        if (constrained != Ternary.False)
            return constrained;
        if (target is not ConditionalType && await constraints.HasNonCircularConstraintAsync(source, cancellation).ConfigureAwait(false)
            && await constraints.DistributiveConstraintAsync(source, cancellation).ConfigureAwait(false) is { } distributed)
            return await operation.CompareAsync(
                distributed,
                target,
                RecursionFlags.Source,
                cancellation: cancellation).ConfigureAwait(false);
        return Ternary.False;
    }

    private async ValueTask<Type> InstantiateAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
        => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Conditional relation instantiation returned no type");

    private async ValueTask<bool> DistributionDependentAsync(ConditionalRoot root, CancellationToken cancellation)
        => root.IsDistributive && (await objects.PossiblyReferencedAsync(
            (TypeParameter)root.CheckType,
            root.Node.TrueType!,
            cancellation).ConfigureAwait(false)
            || await objects.PossiblyReferencedAsync(
                (TypeParameter)root.CheckType,
                root.Node.FalseType!,
                cancellation).ConfigureAwait(false));
}
