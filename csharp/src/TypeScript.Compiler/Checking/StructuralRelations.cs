using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal interface IStructuralRelationHost
{
    /// <summary>
    /// The property symbol's already resolved type, or null when it has not been resolved yet. Used by
    /// the identical-shape fast path, which must not force any resolution of its own.
    /// </summary>
    Type? ResolvedSymbolType(Symbol symbol);

    ValueTask<Ternary?> GenericTupleRelationAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation);

    Type GlobalObject { get; }

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<Type> ReturnTypeAsync(Signature signature, CancellationToken cancellation);

    ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, Func<ValueTask<Ternary>>? structuralFallback, CancellationToken cancellation);

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

    ValueTask<Type?> BestMatchingTypeAsync(RelationOperation operation, Type source, UnionType target, CancellationToken cancellation);

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
        await Task.CompletedTask.ConfigureAwait(CompilationCapture.StackGuard());
        cancellation.ThrowIfCancellationRequested();
        if (source is TypeParameter && await constraints.ConstraintAsync(source, cancellation).ConfigureAwait(false) == target)
            return Ternary.True;
        target = await NormalizedTargetAsync(source, target, cancellation).ConfigureAwait(false);
        if (source == target)
            return Ternary.True;
        if (operation.Kind == RelationKind.Comparable
            && (target.Flags & TypeFlags.Never) == 0
            && await SimpleProbeAsync(operation, target, source, cancellation, report: false).ConfigureAwait(false)
            || await SimpleProbeAsync(operation, source, target, cancellation, report: true).ConfigureAwait(false))
            return Ternary.True;
        if (((source.Flags | target.Flags) & TypeFlags.StructuredOrInstantiable) == 0)
            return Ternary.False;
        // Identical shapes are the common case in structural checking (a fresh object literal against
        // the declared object type it is checked against), and the full algorithm would only rediscover
        // that every property matches. When both member tables are already resolved and agree exactly,
        // the answer is True without entering the session, the variance pass or the property walk.
        if (operation.Kind is RelationKind.Assignable or RelationKind.Comparable && IdenticalShapeProbed(source, target))
            return Ternary.True;
        if ((intersection & IntersectionState.Target) == 0
            && (source.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)) == (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)
            && await host.ExcessPropertiesAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false))
            return Ternary.False;
        if (await WeakSourceCheckAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false))
        {
            if (operation.ReportErrors)
            {
                bool callable = false;
                foreach (bool construct in new[] { false, true })
                {
                    var signatures = await host.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false);
                    if (signatures.Count != 0 && await operation.CompareWithoutErrorsAsync(
                        await host.ReturnTypeAsync(signatures[0], cancellation).ConfigureAwait(false), target, RecursionFlags.Source,
                        cancellation: cancellation).ConfigureAwait(false) != Ternary.False)
                    {
                        callable = true;
                        break;
                    }
                }
                operation.ExplainArguments(
                    callable
                        ? DiagnosticCode.ValueOfType0HasNoPropertiesInCommonWithType1DidYouMeanToCallIt
                        : DiagnosticCode.Type0HasNoPropertiesInCommonWithType1,
                    source,
                    target);
            }
            return Ternary.False;
        }
        bool skip = source is UnionType su && su.Types.Count < 4 && target is not UnionType
            || target is UnionType tu && tu.Types.Count < 4 && (source.Flags & TypeFlags.StructuredOrInstantiable) == 0;
        if (skip)
        {
            var unionMark = CompilationCapture.Mark();
            try { return await UnionIntersectionAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false); }
            finally { CompilationCapture.Report(AllocationProbes.RelUnion, unionMark); }
        }
        var recurseMark = CompilationCapture.Mark();
        try
        {
            return await operation.RecursiveAsync(
                source,
                target,
                recursion,
                intersection,
                (Relations: this, Operation: operation, Source: source, Target: target, Intersection: intersection, Cancellation: cancellation),
                static state => state.Relations.StructuredAsync(state.Operation, state.Source, state.Target, state.Intersection, state.Cancellation),
                cancellation).ConfigureAwait(false);
        }
        finally { CompilationCapture.Report(AllocationProbes.RelRecurse, recurseMark); }
    }

    // Probe frames for the pieces of the relation entry point. Attribution only: the enclosing
    // checkerRelated frame keeps its inclusive bytes, and these split its exclusive remainder.
    private async ValueTask<Type> NormalizedTargetAsync(Type source, Type target, CancellationToken cancellation)
    {
        var mark = CompilationCapture.Mark();
        try { return await normalization.RelationTargetAsync(source, target, cancellation).ConfigureAwait(false); }
        finally { CompilationCapture.Report(AllocationProbes.RelNormalize, mark); }
    }

    private static async ValueTask<bool> SimpleProbeAsync(RelationOperation operation, Type left, Type right,
        CancellationToken cancellation, bool report)
    {
        var mark = CompilationCapture.Mark();
        try { return await operation.SimpleAsync(left, right, cancellation, report).ConfigureAwait(false); }
        finally { CompilationCapture.Report(AllocationProbes.RelSimple, mark); }
    }

    private bool IdenticalShapeProbed(Type source, Type target)
    {
        var mark = CompilationCapture.Mark();
        try { return IdenticalShape(source, target); }
        finally { CompilationCapture.Report(AllocationProbes.RelIdentical, mark); }
    }

    // The weak-type condition, extracted so its own probe frame can be reported without changing
    // the short-circuit order of the original inline expression.
    private async ValueTask<bool> WeakSourceCheckAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, CancellationToken cancellation)
    {
        var mark = CompilationCapture.Mark();
        try
        {
            return (operation.Kind != RelationKind.Comparable || (source.Flags & TypeFlags.Unit) != 0)
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
                    cancellation).ConfigureAwait(false);
        }
        finally { CompilationCapture.Report(AllocationProbes.RelWeak, mark); }
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
                result = await operation.CompareWithoutErrorsAsync(
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
            && !HasNonInferrableOrTarget(sourceParts, target))
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
        var previousExplanation = operation.Explanation;
        if (source is UnionOrIntersectionType || target is UnionOrIntersectionType)
        {
            var result = await UnionIntersectionAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            if (!((source.Flags & TypeFlags.Instantiable) != 0 || source is ObjectType && target is UnionType
                || source is IntersectionType && (target.Flags & (TypeFlags.Object | TypeFlags.Union | TypeFlags.Instantiable)) != 0))
                return Ternary.False;
        }
        if (await VarianceAsync(operation, source, target, intersection, previousExplanation, cancellation).ConfigureAwait(false) is { } variance)
            return variance;
        return await AfterVarianceAsync(operation, source, target, intersection, previousExplanation, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// True when two anonymous object types have the same properties: equal counts, every source
    /// property present in the target with a reference-identical resolved type and the same
    /// optionality, and no index, call or construct signatures on either side. Under those conditions
    /// every check the full algorithm would run (property relation, excess properties, index
    /// signatures, call signatures, weak-type) succeeds, so the comparison is True.
    /// Only resolved member tables are consulted, so nothing is forced into resolution here.
    /// </summary>
    private bool IdenticalShape(Type source, Type target)
    {
        if (source is not ObjectType sourceObject || target is not ObjectType targetObject)
            return false;
        if (source is TypeReference || target is TypeReference)
            return false;
        const ObjectFlags unsupported = ObjectFlags.JsxAttributes | ObjectFlags.Mapped | ObjectFlags.Reference | ObjectFlags.Tuple;
        if (((sourceObject.ObjectFlags | targetObject.ObjectFlags) & unsupported) != 0)
            return false;
        if ((sourceObject.ObjectFlags & ObjectFlags.MembersResolved) == 0 || (targetObject.ObjectFlags & ObjectFlags.MembersResolved) == 0)
            return false;
        if (sourceObject.Properties is not { Count: > 0 } sourceProperties || targetObject.Properties is not { Count: > 0 } targetProperties
            || sourceProperties.Count != targetProperties.Count)
            return false;
        if (sourceObject.IndexInfos.Count != 0 || sourceObject.CallSignatures.Count != 0 || sourceObject.ConstructSignatures.Count != 0
            || targetObject.IndexInfos.Count != 0 || targetObject.CallSignatures.Count != 0 || targetObject.ConstructSignatures.Count != 0)
            return false;
        foreach (var property in sourceProperties)
        {
            Symbol? match = null;
            foreach (var candidate in targetProperties)
                if (candidate.Name == property.Name)
                {
                    match = candidate;
                    break;
                }
            if (match is null)
                return false;
            if ((match.Flags & SymbolFlags.Optional) != (property.Flags & SymbolFlags.Optional)
                || (match.CheckFlags & CheckFlags.Partial) != (property.CheckFlags & CheckFlags.Partial))
                return false;
            if (host.ResolvedSymbolType(property) is not { } sourceType || host.ResolvedSymbolType(match) is not { } targetType
                || !ReferenceEquals(sourceType, targetType))
                return false;
        }
        return true;
    }

    private static bool HasNonInferrableOrTarget(IntersectionType source, Type target)
    {
        for (int i = 0; i < source.Types.Count; i++)
            if (source.Types[i] == target || (source.Types[i].ObjectFlags & ObjectFlags.NonInferrableType) != 0)
                return true;
        return false;
    }

    private ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, RelationExplanation? previousExplanation, CancellationToken cancellation) =>
        operation.ReportErrors
            ? VarianceWithFallbackAsync(operation, source, target, intersection, previousExplanation, cancellation)
            : host.VarianceAsync(operation, source, target, intersection, null, cancellation);

    // Keep the diagnostic fallback's closure off the ordinary relation path.
    private ValueTask<Ternary?> VarianceWithFallbackAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, RelationExplanation? previousExplanation, CancellationToken cancellation) =>
        host.VarianceAsync(operation, source, target, intersection,
            () => AfterVarianceAsync(operation, source, target, intersection, previousExplanation, cancellation), cancellation);

    private async ValueTask<Ternary> AfterVarianceAsync(RelationOperation operation, Type source, Type target,
        IntersectionState intersection, RelationExplanation? previousExplanation, CancellationToken cancellation)
    {
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
        var originalSource = source;
        source = await views.ApparentAsync(source, cancellation).ConfigureAwait(false);
        if (source != originalSource && await VarianceAsync(operation, source, target, intersection, previousExplanation, cancellation).ConfigureAwait(false) is { } apparentVariance)
            return apparentVariance;
        if (await host.ArrayRelationAsync(operation, source, target, intersection, cancellation).ConfigureAwait(false) is { } array)
            return array;
        if (operation.Kind is RelationKind.Subtype or RelationKind.StrictSubtype && (target.ObjectFlags & ObjectFlags.FreshLiteral) != 0
            && await views.EmptyObjectAsync(
                target,
                cancellation).ConfigureAwait(false) && !await views.EmptyObjectAsync(source, cancellation).ConfigureAwait(false))
            return Ternary.False;
        if ((source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && target is ObjectType)
        {
            var result = operation.ReportErrors && (primitive || operation.Explanation != previousExplanation)
                ? await operation.WithoutErrorsAsync(
                    () => MembersAsync(operation, source, target, primitive, intersection, cancellation)).ConfigureAwait(false)
                : await MembersAsync(operation, source, target, primitive, intersection, cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
        }
        if ((source.Flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && target is UnionType union)
        {
            var filtered = algebra.Filter(
                union,
                t => (t.Flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.Substitution)) != 0);
            if (filtered is UnionType objectUnion)
                return await operation.WithoutErrorsAsync(
                    () => host.DiscriminatedAsync(operation, source, objectUnion, cancellation)).ConfigureAwait(false);
        }
        return Ternary.False;
    }

    private async ValueTask<Ternary> MembersAsync(RelationOperation operation, Type source, Type target, bool primitive,
        IntersectionState intersection, CancellationToken cancellation)
    {
        var result = await objects.PropertiesAsync(operation, source, target, false, intersection, cancellation).ConfigureAwait(false);
        if (result != Ternary.False)
            result &= await host.SignatureRelationAsync(operation, source, target, false, intersection, cancellation).ConfigureAwait(false);
        if (result != Ternary.False)
            result &= await host.SignatureRelationAsync(operation, source, target, true, intersection, cancellation).ConfigureAwait(false);
        if (result != Ternary.False)
            result &= await objects.IndexesAsync(operation, source, target, primitive, intersection, cancellation).ConfigureAwait(false);
        return result;
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
            && await host.BestMatchingTypeAsync(operation, source, target, cancellation).ConfigureAwait(false) is { } best)
            await operation.CompareAsync(source, best, RecursionFlags.Target, intersection, cancellation).ConfigureAwait(false);
        return Ternary.False;
    }
}
