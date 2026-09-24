using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker : IStructuralRelationHost, IObjectRelationHost, ITypeVarianceHost, ISignatureAssignabilityHost, ITypeFactHost
{
    internal ObjectRelations ObjectRelations { get; }
    internal StructuralRelations Structural { get; }
    internal RelationSupport RelationSupport { get; }
    internal TypeVariance Variances { get; }
    internal SignatureAssignability SignatureAssignability { get; }
    internal TypeFactQueries Facts { get; }
    internal DiscriminantRelations Discriminants { get; }
    internal TemplateMatching Templates { get; }
    internal GenericRelations Generics { get; }
    public bool StrictFunctionTypes => program.Symbols.Program.Configuration.Options.StrictOption("strictFunctionTypes");

    public bool IsReadonlyArray(Type type) => Instantiation.IsReadonlyArrayType(type);

    public async ValueTask<Ternary?> GenericTupleRelationAsync(RelationOperation operation, Type source, Type target,
        CancellationToken cancellation)
    {
        if (source is TypeReference { Target: TupleType { ElementInfos.Count: 1, IsReadonly: false } }
            && TypeConstraints.IsGenericTuple(source))
        {
            var result = await operation.CompareAsync((await References.TypeArgumentsAsync((TypeReference)source, cancellation))[0],
                target, RecursionFlags.Source, cancellation: cancellation);
            if (result != Ternary.False)
                return result;
        }
        if (target is TypeReference { Target: TupleType { ElementInfos.Count: 1 } targetTuple }
            && TypeConstraints.IsGenericTuple(target))
        {
            bool mutable = targetTuple.IsReadonly;
            if (!mutable)
            {
                var constraint = await Instantiation.Constraints.BaseConstraintOrTypeAsync(source, cancellation);
                mutable = constraint is TypeReference { Target: TupleType { IsReadonly: false } }
                    || IsArray(constraint) && !IsReadonlyArray(constraint);
            }
            if (mutable)
            {
                var result = await operation.CompareAsync(source,
                    (await References.TypeArgumentsAsync((TypeReference)target, cancellation))[0], RecursionFlags.Target,
                    cancellation: cancellation);
                if (result != Ternary.False)
                    return result;
            }
        }
        return null;
    }

    public ValueTask<bool> IsGenericTypeAsync(Type type, CancellationToken cancellation) =>
        Instantiation.IsGenericTypeAsync(type, cancellation);

    public async ValueTask<bool> IsGenericObjectAsync(Type type, CancellationToken cancellation)
            => (await Instantiation.Mapped.GenericFlagsAsync(type, cancellation) & ObjectFlags.IsGenericObjectType) != 0;

    public ValueTask ReportUnreliableAsync(Type type, CancellationToken cancellation) => Variances.ReportAsync(type, false, cancellation);

    public ValueTask<bool> ValidOverrideAsync(Symbol source, Symbol target, CancellationToken cancellation) =>
        RelationSupport.ValidOverrideAsync(source, target, cancellation);

    public ValueTask<Type?> EffectiveIntersectionConstraintAsync(
        IReadOnlyList<Type> types,
        bool targetUnion,
        CancellationToken cancellation)
            => RelationSupport.EffectiveConstraintAsync(types, targetUnion, cancellation);

    public async ValueTask<bool> EmptyArrayAsync(Type type, CancellationToken cancellation)
            =>
                await ArrayElementAsync(type, cancellation) is { } element
                    && element == (context.StrictNullChecks ? context.ImplicitNeverType : context.UndefinedWideningType);

    public ValueTask<Ternary> SignatureRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        bool construct,
        IntersectionState intersection,
        CancellationToken cancellation)
            => SignatureAssignability.OfTypesAsync(operation, source, target, construct, intersection, cancellation);

    public async ValueTask<Ternary?> ArrayRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        var sources = source is UnionType u ? u.Types : [source];
        if (IsArray(target) && (IsReadonlyArray(target) && sources.All(ObjectRelations.ArrayOrTuple)
            || sources.All(t => t is TypeReference { Target: TupleType { IsReadonly: false } })))
        {
            if (operation.Kind == RelationKind.Identity)
                return Ternary.False;
            var s = (await IndexesAsync(
                source,
                cancellation)).FirstOrDefault(i => i.KeyType == context.NumberType)?.ValueType ?? context.AnyType;
            var t = (await IndexesAsync(
                target,
                cancellation)).FirstOrDefault(i => i.KeyType == context.NumberType)?.ValueType ?? context.AnyType;
            return await operation.CompareAsync(s, t, intersection: intersection, cancellation: cancellation);
        }
        if (TypeConstraints.IsGenericTuple(source)
            && target is TypeReference { Target: TupleType }
            && !TypeConstraints.IsGenericTuple(target))
        {
            var constraint = await Instantiation.Constraints.BaseConstraintOrTypeAsync(source, cancellation);
            if (constraint != source)
                return await operation.CompareAsync(constraint, target, RecursionFlags.Source, cancellation: cancellation);
        }
        return null;
    }

    public async ValueTask<Ternary?> AdvancedRelationAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        if (await Generics.TargetAsync(operation, source, target, intersection, cancellation) is { } genericTarget)
            return genericTarget;
        if (target is ConditionalType conditionalTarget
            && await ConditionalRelations.TargetAsync(
                operation,
                source,
                conditionalTarget,
                intersection,
                cancellation) is { } conditionalResult)
            return conditionalResult;
        if (target is TemplateLiteralType template)
        {
            if (source is TemplateLiteralType sourceTemplate)
            {
                if (operation.Kind == RelationKind.Comparable)
                    return TemplateMatching.Unrelated(sourceTemplate, template) ? Ternary.False : Ternary.True;
                await Variances.ReportAsync(source, false, cancellation);
            }
            if (await Templates.MatchesAsync(source, template, operation, cancellation))
                return Ternary.True;
        }
        else if (target is StringMappingType
            && source is not StringMappingType
            && await Templates.MemberAsync(source, target, cancellation))
            return Ternary.True;
        if (await RelationSupport.TypeParameterAsync(operation, source, target, intersection, cancellation) is { } parameter)
            return parameter;
        if (await Generics.SourceAsync(operation, source, target, intersection, cancellation) is { } genericSource)
            return genericSource;
        if (source is ConditionalType conditionalSource)
            return await ConditionalRelations.SourceAsync(operation, conditionalSource, target, cancellation);
        if (source is TemplateLiteralType && target is not ObjectType && target is not TemplateLiteralType)
        {
            var constraint = await Instantiation.Constraints.BaseConstraintAsync(source, cancellation);
            if (constraint is not null && constraint != source)
                return await operation.CompareAsync(constraint, target, RecursionFlags.Source, cancellation: cancellation);
        }
        if (source is StringMappingType mapping)
        {
            if (target is StringMappingType other)
                return mapping.Symbol == other.Symbol
                    ? await operation.CompareAsync(mapping.Target, other.Target, cancellation: cancellation)
                    : Ternary.False;
            if (await Instantiation.Constraints.BaseConstraintAsync(source, cancellation) is { } constraint)
                return await operation.CompareAsync(constraint, target, RecursionFlags.Source, cancellation: cancellation);
        }
        return source is MappedType ? null : Ternary.False;
    }

    public ValueTask<Type?> MatchingConstituentAsync(UnionType target, Type source, CancellationToken cancellation)
            => Discriminants.MatchAsync(target, source, cancellation);

    public ValueTask<Type?> BestMatchingTypeAsync(Type source, UnionType target, CancellationToken cancellation)
        => BestMatchingTypes.GetAsync(source, target, cancellation);

    public ValueTask<Ternary> DiscriminatedAsync(RelationOperation operation, Type source, UnionType target, CancellationToken cancellation)
            => Discriminants.RelatedAsync(operation, source, target, cancellation);

    public ValueTask<bool> ExcessPropertiesAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation)
            => ExcessProperties.HasAsync(operation, source, target, cancellation);

    public ValueTask<Type> RegularObjectAsync(Type type, CancellationToken cancellation)
            => ObjectLiterals.RegularAsync(type, cancellation);

    public ValueTask<Type> NonUndefinedAsync(Type type, CancellationToken cancellation)
            => Facts.FilterAsync(type, TypeFacts.NEUndefined, cancellation);

    public ValueTask<Type> NonNullableAsync(Type type, CancellationToken cancellation)
            => Facts.NonNullableAsync(type, cancellation);

    public async ValueTask<bool> SameNullableFactsAsync(Type source, Type target, CancellationToken cancellation)
            =>
                await Facts.GetAsync(
                    source,
                    TypeFacts.IsUndefinedOrNull,
                    cancellation) == await Facts.GetAsync(target, TypeFacts.IsUndefinedOrNull, cancellation);

    public ValueTask<bool> SubtypeAsync(Type source, Type target, CancellationToken cancellation)
            => Relations.RelatedAsync(source, target, RelationKind.Subtype, cancellation);

    public async ValueTask<Type> NonNullableInstantiationAsync(Type type, CancellationToken cancellation)
    {
        if (program.Symbols.Globals.TryGetValue("NonNullable", out var symbol) && (symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            await Declared.GetAsync(symbol, cancellation);
            if (links.TypeAliases.Get(symbol).TypeParameters?.Count == 1)
                return await References.AliasInstantiationAsync(symbol, [type], cancellation: cancellation);
        }
        return await Algebra.IntersectionAsync([type, context.EmptyObjectType], cancellation: cancellation);
    }

    public ValueTask<Signature> ContextualInstantiationAsync(
        Signature source,
        Signature target,
        RelationOperation operation,
        CancellationToken cancellation)
        =>
            Inference.ContextualSignatureAsync(
                source,
                target,
                compare: (s, t, token) => operation.CompareAsync(s, t, cancellation: token),
                cancellation: cancellation);
}
