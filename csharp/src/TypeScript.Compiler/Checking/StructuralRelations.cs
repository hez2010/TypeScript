using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IStructuralRelationHost
{
    ValueTask<Ternary?> GenericTupleRelationAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation);

    Type GlobalObject { get; }

    ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation);

    ValueTask<Ternary?> AdvancedRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation);

    ValueTask<Ternary?> ArrayRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation);

    ValueTask<Ternary> SignatureRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        bool construct,
        IntersectionState intersection,
        CancellationToken cancellation);

    ValueTask<Type?> MatchingConstituentAsync(UnionType target, Type source, CancellationToken cancellation);

    ValueTask<Type?> BestMatchingTypeAsync(Type source, UnionType target, CancellationToken cancellation);

    ValueTask<Ternary> DiscriminatedAsync(RelationOperation operation, Type source, UnionType target, CancellationToken cancellation);

    ValueTask<bool> ExcessPropertiesAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation);

    ValueTask<Type> RegularObjectAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> EffectiveIntersectionConstraintAsync(IReadOnlyList<Type> types, bool targetUnion, CancellationToken cancellation);

    ValueTask<bool> EmptyArrayAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> IsGenericObjectAsync(Type type, CancellationToken cancellation);
}

internal sealed class StructuralRelations(TypeContext context, TypeAlgebra algebra, TypeNormalization normalization, TypeViews views,
    TypeConstraints constraints, TypeProperties properties, ObjectRelations objects, MappedTypes mapped, IStructuralRelationHost host)
{
    internal async ValueTask<Ternary> RelatedAsync(RelationOperation operation, Type source, Type target, RecursionFlags recursion,
        IntersectionState intersection, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (source is TypeParameter && await constraints.ConstraintAsync(source, cancellation).ConfigureAwait(false) == target)
            return Ternary.True;
        if ((source.Flags & TypeFlags.DefinitelyNonNullable) != 0 && target is UnionType nullable)
        {
            var parts = nullable.Types;
            Type? candidate = parts.Count == 2 && (parts[0].Flags & TypeFlags.Nullable) != 0 ? parts[1]
                : parts.Count == 3 && (parts[0].Flags & TypeFlags.Nullable) != 0 && (parts[1].Flags & TypeFlags.Nullable) != 0
                    ? parts[2]
                    : null;
            if (candidate is not null && (candidate.Flags & TypeFlags.Nullable) == 0)
            {
                target = await normalization.GetAsync(candidate, true, cancellation).ConfigureAwait(false);
                if (source == target)
                    return Ternary.True;
            }
        }
        if (operation.Kind == RelationKind.Comparable
            && (target.Flags & TypeFlags.Never) == 0
            && await operation.SimpleAsync(target, source, cancellation).ConfigureAwait(false)
            || await operation.SimpleAsync(source, target, cancellation).ConfigureAwait(false))
            return Ternary.True;
        if (((source.Flags | target.Flags) & TypeFlags.StructuredOrInstantiable) == 0)
            return Ternary.False;
        if ((intersection & IntersectionState.Target) == 0
            && (source.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)) == (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)
            && await host.ExcessPropertiesAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false))
            return Ternary.False;
        if ((operation.Kind != RelationKind.Comparable || (source.Flags & TypeFlags.Unit) != 0)
            && (intersection & IntersectionState.Target) == 0
            && (source.Flags & (TypeFlags.Primitive | TypeFlags.Object | TypeFlags.Intersection)) != 0 && source != host.GlobalObject
            && (target.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && await objects.WeakAsync(
                target,
                cancellation).ConfigureAwait(false)
            && ((await properties.GetAsync(source, cancellation).ConfigureAwait(false)).Count > 0
                || await objects.CallableAsync(source, cancellation).ConfigureAwait(false))
            && !await objects.CommonPropertiesAsync(
                source,
                target,
                (source.ObjectFlags & ObjectFlags.JsxAttributes) != 0,
                cancellation).ConfigureAwait(false))
            return Ternary.False;
        bool skip = source is UnionType su && su.Types.Count < 4 && target is not UnionType
            || target is UnionType tu && tu.Types.Count < 4 && (source.Flags & TypeFlags.StructuredOrInstantiable) == 0;
        return skip ? await UnionIntersectionAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false)
            : await operation.RecursiveAsync(
                source,
                target,
                recursion,
                intersection,
                () => StructuredAsync(operation, source, target, intersection, cancellation),
                cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Ternary> StructuredAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        var result = await WorkerAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false);
        if (result == Ternary.False && (source is IntersectionType || source is TypeParameter && target is UnionType))
        {
            var constraint = await host.EffectiveIntersectionConstraintAsync(
                source is IntersectionType i ? i.Types : [source],
                target is UnionType,
                cancellation).ConfigureAwait(false);
            if (constraint is not null && (constraint is UnionType u ? u.Types.All(t => t != source) : constraint != source))
                result = await operation.CompareAsync(
                    constraint,
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false);
        }
        if (result != Ternary.False && (intersection & IntersectionState.Target) == 0 && target is IntersectionType
            && !await host.IsGenericObjectAsync(
                target,
                cancellation).ConfigureAwait(false) && (source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
        {
            result &= await objects.PropertiesAsync(operation, source, target, false, 0, cancellation).ConfigureAwait(false);
            if (result != Ternary.False
                && (source.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)) == (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral))
                result &= await objects.IndexesAsync(operation, source, target, false, 0, cancellation).ConfigureAwait(false);
        }
        // Optional properties of a source intersection require a second check even
        // if an individual constituent was sufficient for the first comparison.
        else if (result != Ternary.False
            && target is ObjectType
            && !(target is MappedType gm && await mapped.IsGenericAsync(gm, cancellation).ConfigureAwait(false))
            && !objects.ArrayOrTuple(target) && source is IntersectionType sourceParts
            && ((await views.ApparentAsync(source, cancellation).ConfigureAwait(false)).Flags & TypeFlags.StructuredType) != 0
            && !sourceParts.Types.Any(t => t == target || (t.ObjectFlags & ObjectFlags.NonInferrableType) != 0))
            result &= await objects.PropertiesAsync(operation, source, target, true, intersection, cancellation).ConfigureAwait(false);
        return result;
    }

    private async ValueTask<Ternary> WorkerAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        if (source is UnionOrIntersectionType || target is UnionOrIntersectionType)
        {
            var result = await UnionIntersectionAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            if (!((source.Flags & TypeFlags.Instantiable) != 0 || source is ObjectType && target is UnionType
                || source is IntersectionType && (target.Flags & (TypeFlags.Object | TypeFlags.Union | TypeFlags.Instantiable)) != 0))
                return Ternary.False;
        }
        if (await host.VarianceAsync(operation, source, target, cancellation).ConfigureAwait(false) is { } variance)
            return variance;
        if (await host.GenericTupleRelationAsync(operation, source, target, cancellation).ConfigureAwait(false) is { } tuple)
            return tuple;
        if ((source.Flags & TypeFlags.Instantiable) != 0 && !(source is TemplateLiteralType && target is ObjectType)
            || (target.Flags & TypeFlags.Instantiable) != 0
            || source is MappedType sm && await mapped.IsGenericAsync(sm, cancellation).ConfigureAwait(false)
            || target is MappedType tm && await mapped.IsGenericAsync(tm, cancellation).ConfigureAwait(false))
        {
            if (await host.AdvancedRelationAsync(
                operation,
                source,
                target,
                intersection,
                cancellation).ConfigureAwait(false) is { } advanced)
                return advanced;
        }
        bool primitive = (source.Flags & TypeFlags.Primitive) != 0;
        source = await views.ApparentAsync(source, cancellation).ConfigureAwait(false);
        if (await host.ArrayRelationAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false) is { } array)
            return array;
        if (operation.Kind is RelationKind.Subtype or RelationKind.StrictSubtype && (target.ObjectFlags & ObjectFlags.FreshLiteral) != 0
            && await views.EmptyObjectAsync(
                target,
                cancellation).ConfigureAwait(false) && !await views.EmptyObjectAsync(source, cancellation).ConfigureAwait(false))
            return Ternary.False;
        if ((source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && target is ObjectType)
        {
            var result = await objects.PropertiesAsync(operation, source, target, false, intersection, cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                result &= await host.SignatureRelationAsync(
                    operation,
                    source,
                    target,
                    false,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                result &= await host.SignatureRelationAsync(
                    operation,
                    source,
                    target,
                    true,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                result &= await objects.IndexesAsync(
                    operation,
                    source,
                    target,
                    primitive,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        if ((source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && target is UnionType union)
        {
            var filtered = algebra.Filter(
                union,
                t => (t.Flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.Substitution)) != 0);
            if (filtered is UnionType objectUnion)
                return await host.DiscriminatedAsync(operation, source, objectUnion, cancellation).ConfigureAwait(false);
        }
        return Ternary.False;
    }

    private async ValueTask<Ternary> UnionIntersectionAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        if (source is UnionType union)
        {
            if (target is UnionType targetUnion)
            {
                if (union.Origin is IntersectionType origin && target.Alias is not null && origin.Types.Contains(target)
                    || targetUnion.Origin is UnionType targetOrigin && source.Alias is not null && targetOrigin.Types.Contains(source))
                    return Ternary.True;
            }
            return operation.Kind == RelationKind.Comparable ? await SomeAsync(
                operation,
                union,
                target,
                intersection,
                cancellation).ConfigureAwait(false)
                : await EachAsync(operation, union, target, intersection, cancellation).ConfigureAwait(false);
        }
        if (target is UnionType targetTypes)
            return await ToSomeAsync(
                operation,
                await host.RegularObjectAsync(source, cancellation).ConfigureAwait(false),
                targetTypes,
                intersection,
                cancellation).ConfigureAwait(false);
        if (target is IntersectionType targetIntersection)
        {
            Ternary result = Ternary.True;
            foreach (var part in targetIntersection.Types)
            {
                var next = await operation.CompareAsync(
                    source,
                    part,
                    RecursionFlags.Target,
                    IntersectionState.Target,
                    cancellation).ConfigureAwait(false);
                if (next == Ternary.False)
                    return next;
                result &= next;
            }
            return result;
        }
        if (source is not IntersectionType sourceIntersection)
            return Ternary.False;
        if (operation.Kind == RelationKind.Comparable && (target.Flags & TypeFlags.Primitive) != 0)
        {
            var parts = new Type[sourceIntersection.Types.Count];
            bool changed = false;
            for (int i = 0; i < parts.Length; i++)
            {
                var part = sourceIntersection.Types[i];
                parts[i] = (part.Flags & TypeFlags.Instantiable) != 0
                    ? await constraints.BaseConstraintAsync(part, cancellation).ConfigureAwait(false) ?? context.UnknownType
                    : part;
                changed |= parts[i] != part;
            }
            if (changed)
            {
                source = await algebra.IntersectionAsync(parts, cancellation: cancellation).ConfigureAwait(false);
                if ((source.Flags & TypeFlags.Never) != 0)
                    return Ternary.False;
                if (source is not IntersectionType)
                {
                    var result = await operation.CompareWithoutErrorsAsync(
                        source,
                        target,
                        RecursionFlags.Source,
                        cancellation: cancellation).ConfigureAwait(false);
                    return result != Ternary.False
                        ? result
                        : await operation.CompareWithoutErrorsAsync(
                            target,
                            source,
                            RecursionFlags.Source,
                            cancellation: cancellation).ConfigureAwait(false);
                }
            }
        }
        return await SomeAsync(
            operation,
            (UnionOrIntersectionType)source,
            target,
            IntersectionState.Source,
            cancellation).ConfigureAwait(false);
    }

    private static async ValueTask<Ternary> SomeAsync(
        RelationOperation operation,
        UnionOrIntersectionType source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        if (source is UnionType && source.Types.Contains(target))
            return Ternary.True;
        for (int i = 0; i < source.Types.Count; i++)
        {
            var result = source is UnionType && (source.Flags & TypeFlags.Primitive) == 0 && i == source.Types.Count - 1
                ? await operation.CompareAsync(
                    source.Types[i],
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false)
                : await operation.CompareWithoutErrorsAsync(
                    source.Types[i],
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        return Ternary.False;
    }

    private async ValueTask<Ternary> EachAsync(
        RelationOperation operation,
        UnionType source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        Type stripped = target;
        if (target is UnionType tu && (source.Types[0].Flags & TypeFlags.Undefined) == 0 && (tu.Types[0].Flags & TypeFlags.Undefined) != 0)
            stripped = algebra.Filter(target, t => (t.Flags & TypeFlags.Undefined) == 0);
        Ternary result = Ternary.True;
        for (int i = 0; i < source.Types.Count; i++)
        {
            if (stripped is UnionType union && source.Types.Count >= union.Types.Count && source.Types.Count % union.Types.Count == 0)
            {
                var quick = await operation.CompareWithoutErrorsAsync(
                    source.Types[i],
                    union.Types[i % union.Types.Count],
                    RecursionFlags.Both,
                    intersection,
                    cancellation).ConfigureAwait(false);
                if (quick != Ternary.False)
                {
                    result &= quick;
                    continue;
                }
            }
            var related = (source.Flags & TypeFlags.Primitive) == 0
                ? await operation.CompareAsync(
                    source.Types[i],
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false)
                : await operation.CompareWithoutErrorsAsync(
                    source.Types[i],
                    target,
                    RecursionFlags.Source,
                    intersection,
                    cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
                return related;
            result &= related;
        }
        return result;
    }

    private async ValueTask<Ternary> ToSomeAsync(
        RelationOperation operation,
        Type source,
        UnionType target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        if (target.Types.Contains(source))
            return Ternary.True;
        if (operation.Kind != RelationKind.Comparable
            && (target.ObjectFlags & ObjectFlags.PrimitiveUnion) != 0
            && (source.Flags & TypeFlags.EnumLiteral) == 0
            && ((source.Flags & (TypeFlags.StringLiteral | TypeFlags.BooleanLiteral | TypeFlags.BigIntLiteral)) != 0
                || operation.Kind is RelationKind.Subtype or RelationKind.StrictSubtype && (source.Flags & TypeFlags.NumberLiteral) != 0))
        {
            var literal = (LiteralType)source;
            var alternate = source == literal.RegularType ? literal.FreshType : literal.RegularType;
            var primitive = (source.Flags & TypeFlags.StringLiteral) != 0 ? context.StringType : (source.Flags & TypeFlags.NumberLiteral) != 0 ? context.NumberType
                : (source.Flags & TypeFlags.BigIntLiteral) != 0 ? context.BigIntType : null;
            return primitive is not null && target.Types.Contains(primitive) || alternate is not null && target.Types.Contains(alternate)
                ? Ternary.True
                : Ternary.False;
        }
        if (await host.MatchingConstituentAsync(target, source, cancellation).ConfigureAwait(false) is { } match)
        {
            var result = await operation.CompareWithoutErrorsAsync(
                source,
                match,
                RecursionFlags.Target,
                intersection,
                cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        foreach (var part in target.Types)
        {
            var result = await operation.CompareWithoutErrorsAsync(
                source,
                part,
                RecursionFlags.Target,
                intersection,
                cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        if (operation.ReportErrors && ((source.Flags | target.Flags) & TypeFlags.Primitive) == 0
            && await host.BestMatchingTypeAsync(source, target, cancellation).ConfigureAwait(false) is { } best)
            await operation.CompareAsync(source, best, RecursionFlags.Target, intersection, cancellation).ConfigureAwait(false);
        return Ternary.False;
    }
}
