using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal interface IObjectRelationHost
{
    bool IsArray(Type type);

    bool IsReadonlyArray(Type type);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, Type key, CancellationToken cancellation);

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, TextSlice name, CancellationToken cancellation);

    ValueTask<Type> PropertyNameTypeAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<Type> NonUndefinedAsync(Type type, CancellationToken cancellation);

    ValueTask<bool> ValidOverrideAsync(Symbol source, Symbol target, CancellationToken cancellation);

    ValueTask<Type?> DeclaringClassAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<bool> EmptyArrayAsync(Type type, CancellationToken cancellation);

    CheckFlags AccessFlags(Symbol symbol, bool write);

    bool IsReadonly(Symbol symbol);
}

internal sealed class ObjectRelations(TypeContext context, TypeAlgebra algebra, StructuredMembers members, TypeProperties properties,
    SymbolTypes values, IndexSignatures indexes, TypeReferences references, TupleTypes tuples, MappedTypes mapped, IObjectRelationHost host)
{
    internal bool ArrayOrTuple(Type type) => host.IsArray(type) || type is TypeReference { Target: TupleType };

    internal async ValueTask<bool> WeakAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type is ObjectType obj)
        {
            var resolved = await members.ResolveAsync(obj, cancellation).ConfigureAwait(false);
            return resolved.CallSignatures.Count == 0 && resolved.ConstructSignatures.Count == 0 && resolved.IndexInfos.Count == 0
                && resolved.Properties is { Count: > 0 } list && list.All(p => (p.Flags & SymbolFlags.Optional) != 0);
        }
        if (type is SubstitutionType substitution)
            return await WeakAsync(substitution.BaseType, cancellation).ConfigureAwait(false);
        if (type is IntersectionType intersection)
        {
            foreach (var part in intersection.Types)
                if (!await WeakAsync(part, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        return false;
    }

    internal async ValueTask<bool> CallableAsync(Type type, CancellationToken cancellation = default)
        => (await host.SignaturesAsync(type, false, cancellation).ConfigureAwait(false)).Count != 0
            || (await host.SignaturesAsync(type, true, cancellation).ConfigureAwait(false)).Count != 0;

    internal async ValueTask<bool> CommonPropertiesAsync(Type source, Type target, bool jsx, CancellationToken cancellation = default)
    {
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
            if (await KnownAsync(target, property.Name, jsx, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    internal async ValueTask<bool> KnownAsync(Type type, TextSlice name, bool jsx = false, CancellationToken cancellation = default)
    {
        var pending = new Stack<Type>();
        pending.Push(type);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is ObjectType)
            {
                if (await properties.ObjectPropertyAsync(current, name, cancellation).ConfigureAwait(false) is not null
                    || await host.ApplicableIndexAsync(current, name, cancellation).ConfigureAwait(false) is not null
                    || name.Span.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal)
                        && (await host.IndexesAsync(current, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.StringType)
                    || jsx && name.Span.Contains('-'))
                    return true;
            }
            else if (current is SubstitutionType substitution)
                pending.Push(substitution.BaseType);
            else if (current is UnionOrIntersectionType composite && await ExcessTargetAsync(current, cancellation).ConfigureAwait(false))
                for (int i = composite.Types.Count - 1; i >= 0; i--)
                    pending.Push(composite.Types[i]);
        }
        return false;
    }

    internal static async ValueTask<bool> ExcessTargetAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type is ObjectType)
            return (type.ObjectFlags & ObjectFlags.ObjectLiteralPatternWithComputedProperties) == 0;
        if ((type.Flags & TypeFlags.NonPrimitive) != 0)
            return true;
        if (type is SubstitutionType substitution)
            return await ExcessTargetAsync(substitution.BaseType, cancellation).ConfigureAwait(false);
        if (type is UnionOrIntersectionType composite)
        {
            foreach (var part in composite.Types)
            {
                bool result = await ExcessTargetAsync(part, cancellation).ConfigureAwait(false);
                if (type is UnionType && result)
                    return true;
                if (type is IntersectionType && !result)
                    return false;
            }
            return type is IntersectionType;
        }
        return false;
    }

    internal async ValueTask<Ternary> PropertiesAsync(RelationOperation operation, Type source, Type target, bool optionalsOnly,
        IntersectionState intersection, CancellationToken cancellation = default, IReadOnlySet<TextSlice>? excluded = null)
    {
        if (target is TypeReference { Target: TupleType targetTuple })
        {
            if (ArrayOrTuple(source))
                return await TupleAsync(
                    operation,
                    (TypeReference)source,
                    (TypeReference)target,
                    intersection,
                    cancellation,
                    excluded).ConfigureAwait(false);
            if ((targetTuple.CombinedFlags & ElementFlags.Variable) != 0)
                return Ternary.False;
        }
        bool requireOptional = operation.Kind is RelationKind.Subtype or RelationKind.StrictSubtype
            && (source.ObjectFlags & ObjectFlags.ObjectLiteral) == 0
            && !await host.EmptyArrayAsync(source, cancellation).ConfigureAwait(false) && source is not TypeReference { Target: TupleType };
        var targetProperties = await properties.GetAsync(target, cancellation).ConfigureAwait(false);
        List<Symbol>? missing = null;
        foreach (var property in targetProperties)
        {
            if (property.ValueDeclaration is { } d && SemanticSyntax.IsStatic(d) && (d as INamedNode)?.Name is PrivateIdentifierNode)
                continue;
            if ((requireOptional || (property.Flags & SymbolFlags.Optional) == 0 && (property.CheckFlags & CheckFlags.Partial) == 0)
                && await properties.PropertyAsync(source, property.Name, cancellation: cancellation).ConfigureAwait(false) is null)
            {
                if (!operation.ReportErrors)
                    return Ternary.False;
                (missing ??= []).Add(property);
            }
        }
        if (missing is not null)
        {
            if (SemanticSyntax.Name(missing[0].ValueDeclaration) is PrivateIdentifierNode privateName
                && source.Symbol is { } sourceSymbol && (sourceSymbol.Flags & SymbolFlags.Class) != 0
                && await properties.PropertyAsync(
                    source,
                    PrivateAccess.Name(sourceSymbol, privateName.Text),
                    cancellation: cancellation).ConfigureAwait(false) is not null
                && target.Symbol is { } targetSymbol)
            {
                operation.ExplainArguments(
                    DiagnosticCode.Property0InType1RefersToADifferentMemberThatCannotBeAccessedFromWithinType2,
                    privateName.Text,
                    sourceSymbol,
                    targetSymbol);
                return Ternary.False;
            }
            if (!await CallableAsync(source, cancellation).ConfigureAwait(false)
                || (await properties.GetAsync(source, cancellation).ConfigureAwait(false)).Count != 0
                || (await host.SignaturesAsync(source, false, cancellation).ConfigureAwait(false)).Count != 0
                    && (await host.SignaturesAsync(target, false, cancellation).ConfigureAwait(false)).Count != 0
                || (await host.SignaturesAsync(source, true, cancellation).ConfigureAwait(false)).Count != 0
                    && (await host.SignaturesAsync(target, true, cancellation).ConfigureAwait(false)).Count != 0)
            {
                if (missing.Count == 1)
                    operation.Explain(DiagnosticCode.Property0IsMissingInType1ButRequiredInType2, source, target, missing[0]);
                else if (ArrayLikeMissingProperties(source, target))
                {
                    if (missing.Count > 5)
                        operation.ExplainArguments(
                            DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2And3More,
                            source,
                            target,
                            missing.Take(4).ToArray(),
                            missing.Count - 4);
                    else
                        operation.ExplainArguments(
                            DiagnosticCode.Type0IsMissingTheFollowingPropertiesFromType1Colon2,
                            source,
                            target,
                            missing);
                }
            }
            return Ternary.False;
        }
        if ((target.ObjectFlags & ObjectFlags.ObjectLiteral) != 0)
            foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
                if (excluded?.Contains(property.Name) != true
                    && await properties.ObjectPropertyAsync(target, property.Name, cancellation).ConfigureAwait(false) is null)
                    return Ternary.False;
        Ternary result = Ternary.True;
        foreach (var targetProperty in targetProperties)
        {
            if (excluded?.Contains(targetProperty.Name) == true
                || (targetProperty.Flags & SymbolFlags.Prototype) != 0
                || optionalsOnly && (targetProperty.Flags & SymbolFlags.Optional) == 0)
                continue;
            var sourceProperty = await properties.PropertyAsync(
                source,
                targetProperty.Name,
                cancellation: cancellation).ConfigureAwait(false);
            if (sourceProperty is null || sourceProperty == targetProperty)
                continue;
            var related = await PropertyAsync(
                operation,
                source,
                target,
                sourceProperty,
                targetProperty,
                intersection,
                cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
                return related;
            result &= related;
        }
        return result;
    }

    internal async ValueTask<Ternary> PropertyAsync(
        RelationOperation operation,
        Type sourceObject,
        Type targetObject,
        Symbol source,
        Symbol target,
        IntersectionState intersection,
        CancellationToken cancellation,
        Type? sourceOverride = null,
        bool? skipOptional = null)
    {
        var sourceFlags = host.AccessFlags(source, false);
        var targetFlags = host.AccessFlags(target, false);
        if (((sourceFlags | targetFlags) & CheckFlags.ContainsPrivate) != 0)
        {
            if (source.ValueDeclaration != target.ValueDeclaration)
            {
                if (operation.ReportErrors)
                {
                    if ((sourceFlags & targetFlags & CheckFlags.ContainsPrivate) != 0)
                        operation.ExplainArguments(DiagnosticCode.TypesHaveSeparateDeclarationsOfAPrivateProperty0, target);
                    else
                        operation.ExplainArguments(DiagnosticCode.Property0IsPrivateInType1ButNotInType2, target,
                            (sourceFlags & CheckFlags.ContainsPrivate) != 0 ? sourceObject : targetObject,
                            (sourceFlags & CheckFlags.ContainsPrivate) != 0 ? targetObject : sourceObject);
                }
                return Ternary.False;
            }
        }
        else if ((targetFlags & CheckFlags.ContainsProtected) != 0)
        {
            if (!await host.ValidOverrideAsync(source, target, cancellation).ConfigureAwait(false))
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.Property0IsProtectedButType1IsNotAClassDerivedFrom2, target,
                        await host.DeclaringClassAsync(source, cancellation).ConfigureAwait(false) ?? sourceObject,
                        await host.DeclaringClassAsync(target, cancellation).ConfigureAwait(false) ?? targetObject);
                return Ternary.False;
            }
        }
        else if ((sourceFlags & CheckFlags.ContainsProtected) != 0)
        {
            if (operation.ReportErrors)
                operation.ExplainArguments(DiagnosticCode.Property0IsProtectedInType1ButPublicInType2, target, sourceObject, targetObject);
            return Ternary.False;
        }
        if (operation.Kind == RelationKind.StrictSubtype && host.IsReadonly(source) && !host.IsReadonly(target))
            return Ternary.False;
        var targetType = values.NonMissing(
            await values.GetAsync(target, cancellation).ConfigureAwait(false),
            (target.Flags & SymbolFlags.Optional) != 0);
        if (context.StrictNullChecks && (target.CheckFlags & CheckFlags.Partial) != 0)
            targetType = await algebra.UnionAsync([targetType, context.UndefinedType], cancellation: cancellation).ConfigureAwait(false);
        Ternary related = Ternary.True;
        if ((targetType.Flags & (operation.Kind == RelationKind.StrictSubtype ? TypeFlags.Any : TypeFlags.AnyOrUnknown)) == 0)
        {
            var sourceType = sourceOverride ?? values.NonMissing(
                await values.GetAsync(source, cancellation).ConfigureAwait(false),
                (source.Flags & SymbolFlags.Optional) != 0);
            related = await operation.CompareAsync(
                sourceType,
                targetType,
                intersection: intersection,
                cancellation: cancellation).ConfigureAwait(false);
        }
        if (related == Ternary.False)
        {
            operation.Explain(DiagnosticCode.TypesOfProperty0AreIncompatible, property: target);
            return related;
        }
        if (!(skipOptional ?? operation.Kind == RelationKind.Comparable)
            && (source.Flags & SymbolFlags.Optional) != 0
            && (target.Flags & SymbolFlags.ClassMember) != 0
            && (target.Flags & SymbolFlags.Optional) == 0)
        {
            if (operation.ReportErrors)
                operation.ExplainArguments(DiagnosticCode.Property0IsOptionalInType1ButRequiredInType2, target, sourceObject, targetObject);
            return Ternary.False;
        }
        return related;
    }

    private bool ArrayLikeMissingProperties(Type source, Type target)
    {
        bool mutable = target is TypeReference { Target: TupleType { IsReadonly: false } }
            || host.IsArray(target) && !host.IsReadonlyArray(target);
        if (source is TypeReference { Target: TupleType tuple })
            return !(tuple.IsReadonly && mutable) && ArrayOrTuple(target);
        if (host.IsReadonlyArray(source) && mutable)
            return false;
        return target is not TypeReference { Target: TupleType } || host.IsArray(source);
    }

    private async ValueTask<Ternary> TupleAsync(
        RelationOperation operation,
        TypeReference source,
        TypeReference target,
        IntersectionState intersection,
        CancellationToken cancellation,
        IReadOnlySet<TextSlice>? excluded)
    {
        var sourceTuple = source.Target as TupleType;
        var targetTuple = (TupleType)target.Target!;
        if (!targetTuple.IsReadonly && (host.IsReadonlyArray(source) || sourceTuple?.IsReadonly == true))
            return Ternary.False;
        int sourceArity = ((InterfaceType)source.Target!).AllTypeParameters.Count - (((InterfaceType)source.Target).ThisType is null
            ? 0
            : 1);
        int targetArity = targetTuple.ElementInfos.Count;
        bool sourceRest = sourceTuple is null || (sourceTuple.CombinedFlags & ElementFlags.Rest) != 0;
        bool targetRest = (targetTuple.CombinedFlags & ElementFlags.Rest) != 0, targetVariable = (targetTuple.CombinedFlags & ElementFlags.Variable) != 0;
        int sourceMinimum = sourceTuple?.MinLength ?? 0;
        if (!sourceRest && sourceArity < targetTuple.MinLength)
        {
            if (operation.ReportErrors)
                operation.ExplainArguments(DiagnosticCode.SourceHas0ElementSButTargetRequires1, sourceArity, targetTuple.MinLength);
            return Ternary.False;
        }
        if (!targetVariable && targetArity < sourceMinimum)
        {
            if (operation.ReportErrors)
                operation.ExplainArguments(DiagnosticCode.SourceHas0ElementSButTargetAllowsOnly1, sourceMinimum, targetArity);
            return Ternary.False;
        }
        if (!targetVariable && (sourceRest || targetArity < sourceArity))
        {
            if (operation.ReportErrors)
                operation.ExplainArguments(
                    sourceMinimum < targetTuple.MinLength
                        ? DiagnosticCode.TargetRequires0ElementSButSourceMayHaveFewer
                        : DiagnosticCode.TargetAllowsOnly0ElementSButSourceMayHaveMore,
                    sourceMinimum < targetTuple.MinLength ? targetTuple.MinLength : targetArity);
            return Ternary.False;
        }
        var sourceArguments = await references.TypeArgumentsAsync(source, cancellation).ConfigureAwait(false);
        var targetArguments = await references.TypeArgumentsAsync(target, cancellation).ConfigureAwait(false);
        int start = 0, end = 0;
        while (start < targetArity && (targetTuple.ElementInfos[start].Flags & ElementFlags.NonRest) != 0)
            start++;
        while (end < targetArity && (targetTuple.ElementInfos[targetArity - 1 - end].Flags & ElementFlags.NonRest) != 0)
            end++;
        Ternary result = Ternary.True;
        bool canExclude = excluded?.Count > 0;
        for (int i = 0; i < sourceArity; i++)
        {
            var sourceFlags = sourceTuple?.ElementInfos[i].Flags ?? ElementFlags.Rest;
            int fromEnd = sourceArity - 1 - i;
            int position;
            if (targetRest && i >= start)
                position = targetArity - 1 - Math.Min(fromEnd, end);
            else
            {
                if (i >= targetArity)
                {
                    if (operation.ReportErrors)
                        operation.ExplainArguments(DiagnosticCode.TargetAllowsOnly0ElementSButSourceMayHaveMore, targetArity);
                    return Ternary.False;
                }
                position = i;
            }
            var targetFlags = position >= 0 ? targetTuple.ElementInfos[position].Flags : 0;
            if ((targetFlags & ElementFlags.Variadic) != 0 && (sourceFlags & ElementFlags.Variadic) == 0)
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.SourceProvidesNoMatchForVariadicElementAtPosition0InTarget, position);
                return Ternary.False;
            }
            if ((sourceFlags & ElementFlags.Variadic) != 0 && (targetFlags & ElementFlags.Variable) == 0)
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(
                        DiagnosticCode.VariadicElementAtPosition0InSourceDoesNotMatchElementAtPosition1InTarget,
                        i,
                        position);
                return Ternary.False;
            }
            if ((targetFlags & ElementFlags.Required) != 0 && (sourceFlags & ElementFlags.Required) == 0)
            {
                if (operation.ReportErrors)
                    operation.ExplainArguments(DiagnosticCode.SourceProvidesNoMatchForRequiredElementAtPosition0InTarget, position);
                return Ternary.False;
            }
            if (canExclude)
            {
                if (((sourceFlags | targetFlags) & ElementFlags.Variable) != 0)
                    canExclude = false;
                if (canExclude && excluded!.Contains(TextSlice.Format(i)))
                    continue;
            }
            var sourceType = values.NonMissing(sourceArguments[i], (sourceFlags & targetFlags & ElementFlags.Optional) != 0);
            var targetType = targetArguments[position];
            var check = (sourceFlags & ElementFlags.Variadic) != 0 && (targetFlags & ElementFlags.Rest) != 0
                ? await tuples.ArrayAsync(
                    targetType,
                    cancellation: cancellation).ConfigureAwait(false) : values.NonMissing(
                        targetType,
                        (targetFlags & ElementFlags.Optional) != 0);
            var related = await operation.CompareAsync(
                sourceType,
                check,
                intersection: intersection,
                cancellation: cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
            {
                if (operation.ReportErrors && (sourceArity > 1 || targetArity > 1))
                {
                    if (targetRest && i >= start && sourceArity - 1 - i >= end && start != sourceArity - end - 1)
                        operation.ExplainArguments(
                            DiagnosticCode.TypeAtPositions0Through1InSourceIsNotCompatibleWithTypeAtPosition2InTarget,
                            start,
                            sourceArity - end - 1,
                            position);
                    else
                        operation.ExplainArguments(
                            DiagnosticCode.TypeAtPosition0InSourceIsNotCompatibleWithTypeAtPosition1InTarget,
                            i,
                            position);
                }
                return related;
            }
            result &= related;
        }
        return result;
    }

    internal async ValueTask<Ternary> IndexesAsync(
        RelationOperation operation,
        Type source,
        Type target,
        bool sourcePrimitive,
        IntersectionState intersection,
        CancellationToken cancellation = default)
    {
        var targetIndexes = await host.IndexesAsync(target, cancellation).ConfigureAwait(false);
        bool stringIndex = targetIndexes.Any(i => i.KeyType == context.StringType);
        Ternary result = Ternary.True;
        foreach (var index in targetIndexes)
        {
            Ternary related;
            if (operation.Kind != RelationKind.StrictSubtype
                && !sourcePrimitive
                && stringIndex
                && (index.ValueType.Flags & TypeFlags.Any) != 0)
                related = Ternary.True;
            else if (source is MappedType mappedSource
                && await mapped.IsGenericAsync(mappedSource, cancellation).ConfigureAwait(false)
                && stringIndex)
                related = await operation.CompareAsync(
                    await mapped.TemplateAsync(mappedSource, cancellation).ConfigureAwait(false),
                    index.ValueType,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
            else if (await host.ApplicableIndexAsync(source, index.KeyType, cancellation).ConfigureAwait(false) is { } sourceIndex)
            {
                related = await operation.CompareAsync(
                    sourceIndex.ValueType,
                    index.ValueType,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
                if (related == Ternary.False)
                    operation.Explain(
                        sourceIndex.KeyType == index.KeyType
                            ? DiagnosticCode.X0IndexSignaturesAreIncompatible
                            : DiagnosticCode.X0And1IndexSignaturesAreIncompatible,
                        sourceIndex.KeyType,
                        index.KeyType);
            }
            else if ((intersection & IntersectionState.Source) == 0
                && (operation.Kind != RelationKind.StrictSubtype || (source.ObjectFlags & ObjectFlags.FreshLiteral) != 0)
                && await InferableIndexAsync(source, cancellation).ConfigureAwait(false))
                related = await MembersToIndexAsync(operation, source, index, intersection, cancellation).ConfigureAwait(false);
            else
            {
                related = Ternary.False;
                operation.Explain(DiagnosticCode.IndexSignatureForType0IsMissingInType1, index.KeyType, source);
            }
            if (related == Ternary.False)
                return related;
            result &= related;
        }
        return result;
    }

    internal async ValueTask<bool> InferableIndexAsync(Type type, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(
            RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (type is IntersectionType intersection)
        {
            foreach (var part in intersection.Types)
                if (!await InferableIndexAsync(part, cancellation).ConfigureAwait(false))
                    return false;
            return true;
        }
        return type.Symbol is { } symbol
            && (symbol.Flags & (SymbolFlags.ObjectLiteral | SymbolFlags.TypeLiteral | SymbolFlags.Enum | SymbolFlags.ValueModule)) != 0
            && (symbol.Flags & SymbolFlags.Class) == 0 && !await CallableAsync(type, cancellation).ConfigureAwait(false)
            || (type.ObjectFlags & (ObjectFlags.JSLiteral | ObjectFlags.ObjectRestType)) != 0
            || type is ReverseMappedType reverse && await InferableIndexAsync(reverse.Source!, cancellation).ConfigureAwait(false);
    }

    private async ValueTask<Ternary> MembersToIndexAsync(
        RelationOperation operation,
        Type source,
        IndexInfo target,
        IntersectionState intersection,
        CancellationToken cancellation)
    {
        var list = source is IntersectionType composite ? await properties.CompositePropertiesAsync(
            composite,
            cancellation).ConfigureAwait(false)
            : source is ObjectType obj ? (await members.ResolveAsync(obj, cancellation).ConfigureAwait(false)).Properties ?? [] : [];
        Ternary result = Ternary.True;
        foreach (var property in list)
        {
            if ((source.ObjectFlags & ObjectFlags.JsxAttributes) != 0 && property.Name.Span.Contains('-'))
                continue;
            if (!await indexes.ApplicableTypeAsync(
                await host.PropertyNameTypeAsync(property, cancellation).ConfigureAwait(false),
                target.KeyType,
                cancellation).ConfigureAwait(false))
                continue;
            var type = values.NonMissing(
                await values.GetAsync(property, cancellation).ConfigureAwait(false),
                (property.Flags & SymbolFlags.Optional) != 0);
            if (!context.ExactOptionalPropertyTypes
                && (type.Flags & TypeFlags.Undefined) == 0
                && target.KeyType != context.NumberType
                && (property.Flags & SymbolFlags.Optional) != 0)
                type = await host.NonUndefinedAsync(type, cancellation).ConfigureAwait(false);
            var related = await operation.CompareAsync(
                type,
                target.ValueType,
                intersection: intersection,
                cancellation: cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
            {
                operation.Explain(DiagnosticCode.Property0IsIncompatibleWithIndexSignature, property: property);
                return related;
            }
            result &= related;
        }
        foreach (var info in await host.IndexesAsync(source, cancellation).ConfigureAwait(false))
            if (await indexes.ApplicableTypeAsync(info.KeyType, target.KeyType, cancellation).ConfigureAwait(false))
            {
                var related = await operation.CompareAsync(
                    info.ValueType,
                    target.ValueType,
                    intersection: intersection,
                    cancellation: cancellation).ConfigureAwait(false);
                if (related == Ternary.False)
                {
                    operation.Explain(
                        info.KeyType == target.KeyType
                            ? DiagnosticCode.X0IndexSignaturesAreIncompatible
                            : DiagnosticCode.X0And1IndexSignaturesAreIncompatible,
                        info.KeyType,
                        target.KeyType);
                    return related;
                }
                result &= related;
            }
        return result;
    }
}
