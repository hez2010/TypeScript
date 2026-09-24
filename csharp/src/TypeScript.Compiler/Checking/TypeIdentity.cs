using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ITypeIdentityHost
{
    ValueTask<Ternary?> VarianceAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation);

    ValueTask<Ternary> MappedRelationAsync(
        RelationOperation operation,
        MappedType source,
        MappedType target,
        CancellationToken cancellation);

    ValueTask<Type> ConditionalBranchAsync(ConditionalType type, bool whenTrue, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);

    CheckFlags AccessFlags(Symbol symbol, bool write);

    bool IsReadonly(Symbol symbol);
}

internal sealed class TypeIdentity(TypeContext context, CheckerLinks links, StructuredMembers members, TypeProperties properties,
    SymbolTypes values, SignatureComparison signatures, MappedTypes mapped, ITypeIdentityHost host)
{
    internal async ValueTask<Ternary> CompareAsync(
        RelationOperation operation,
        Type source,
        Type target,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        ValueTask<Ternary> Compare(Type s, Type t) => operation.CompareAsync(s, t, cancellation: cancellation);
        Ternary result;
        switch (source)
        {
            case UnionOrIntersectionType composite:
                result = await EachAsync(composite, (UnionOrIntersectionType)target).ConfigureAwait(false);
                return result == Ternary.False
                    ? result
                    : result & await EachAsync((UnionOrIntersectionType)target, composite).ConfigureAwait(false);
            case IndexType index:
                return await Compare(index.Target, ((IndexType)target).Target).ConfigureAwait(false);
            case IndexedAccessType indexed:
                var other = (IndexedAccessType)target;
                result = await Compare(indexed.ObjectType, other.ObjectType).ConfigureAwait(false);
                return result == Ternary.False ? result : result & await Compare(indexed.IndexType, other.IndexType).ConfigureAwait(false);
            case ConditionalType conditional:
                var otherConditional = (ConditionalType)target;
                if (conditional.Root.IsDistributive != otherConditional.Root.IsDistributive)
                    return Ternary.False;
                result = await Compare(conditional.CheckType, otherConditional.CheckType).ConfigureAwait(false);
                if (result != Ternary.False)
                    result &= await Compare(conditional.ExtendsType, otherConditional.ExtendsType).ConfigureAwait(false);
                if (result != Ternary.False)
                    result &= await Compare(
                        await host.ConditionalBranchAsync(conditional, true, cancellation).ConfigureAwait(false),
                        await host.ConditionalBranchAsync(
                            otherConditional,
                            true,
                            cancellation).ConfigureAwait(false)).ConfigureAwait(false);
                if (result != Ternary.False)
                    result &= await Compare(
                        await host.ConditionalBranchAsync(conditional, false, cancellation).ConfigureAwait(false),
                        await host.ConditionalBranchAsync(
                            otherConditional,
                            false,
                            cancellation).ConfigureAwait(false)).ConfigureAwait(false);
                return result;
            case SubstitutionType substitution:
                var otherSubstitution = (SubstitutionType)target;
                result = await Compare(substitution.BaseType, otherSubstitution.BaseType).ConfigureAwait(false);
                return result == Ternary.False
                    ? result
                    : result & await Compare(substitution.Constraint, otherSubstitution.Constraint).ConfigureAwait(false);
            case TemplateLiteralType template:
                var otherTemplate = (TemplateLiteralType)target;
                if (!template.Texts.SequenceEqual(otherTemplate.Texts))
                    return Ternary.False;
                result = Ternary.True;
                for (int i = 0; i < template.Types.Count; i++)
                {
                    result &= await Compare(template.Types[i], otherTemplate.Types[i]).ConfigureAwait(false);
                    if (result == Ternary.False)
                        break;
                }
                return result;
            case StringMappingType mapping:
                return mapping.Symbol == target.Symbol
                    ? await Compare(mapping.Target, ((StringMappingType)target).Target).ConfigureAwait(false)
                    : Ternary.False;
        }
        if (source is not ObjectType sourceObject || target is not ObjectType targetObject)
            return Ternary.False;
        if (await host.VarianceAsync(operation, source, target, cancellation).ConfigureAwait(false) is { } variance)
            return variance;
        if (source is MappedType mappedSource && await mapped.IsGenericAsync(mappedSource, cancellation).ConfigureAwait(false)
            || target is MappedType mappedTarget && await mapped.IsGenericAsync(mappedTarget, cancellation).ConfigureAwait(false))
            return source is MappedType first && target is MappedType second
                && await mapped.IsGenericAsync(first, cancellation).ConfigureAwait(false)
                && await mapped.IsGenericAsync(second, cancellation).ConfigureAwait(false)
                    ? await host.MappedRelationAsync(operation, first, second, cancellation).ConfigureAwait(false) : Ternary.False;
        var sourceProperties = (await members.ResolveAsync(sourceObject, cancellation).ConfigureAwait(false)).Properties ?? [];
        var targetProperties = (await members.ResolveAsync(targetObject, cancellation).ConfigureAwait(false)).Properties ?? [];
        if (sourceProperties.Count != targetProperties.Count)
            return Ternary.False;
        result = Ternary.True;
        foreach (var property in sourceProperties)
        {
            var other = await properties.ObjectPropertyAsync(target, property.Name, cancellation).ConfigureAwait(false);
            if (other is null)
                return Ternary.False;
            var related = await PropertyAsync(operation, property, other, cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
                return related;
            result &= related;
        }
        foreach (bool construct in new[] { false, true })
        {
            var sourceSignatures = await host.SignaturesAsync(source, construct, cancellation).ConfigureAwait(false);
            var targetSignatures = await host.SignaturesAsync(target, construct, cancellation).ConfigureAwait(false);
            if (sourceSignatures.Count != targetSignatures.Count)
                return Ternary.False;
            for (int i = 0; i < sourceSignatures.Count; i++)
            {
                var related = await signatures.CompareAsync(sourceSignatures[i], targetSignatures[i], cancellation: cancellation,
                    compareTypes: (s, t, token) => operation.CompareAsync(s, t, cancellation: token)).ConfigureAwait(false);
                if (related == Ternary.False)
                    return related;
                result &= related;
            }
        }
        var sourceIndexes = await host.IndexesAsync(source, cancellation).ConfigureAwait(false);
        var targetIndexes = await host.IndexesAsync(target, cancellation).ConfigureAwait(false);
        if (sourceIndexes.Count != targetIndexes.Count)
            return Ternary.False;
        foreach (var targetIndex in targetIndexes)
        {
            var sourceIndex = (await host.IndexesAsync(
                source,
                cancellation).ConfigureAwait(false)).FirstOrDefault(i => i.KeyType == targetIndex.KeyType);
            if (sourceIndex is null
                || await Compare(sourceIndex.ValueType, targetIndex.ValueType).ConfigureAwait(false) == Ternary.False
                || sourceIndex.IsReadonly != targetIndex.IsReadonly)
                return Ternary.False;
        }
        return result;

        async ValueTask<Ternary> EachAsync(UnionOrIntersectionType from, UnionOrIntersectionType to)
        {
            Ternary all = Ternary.True;
            foreach (var part in from.Types)
            {
                if (to is UnionType && to.Types.Contains(part))
                    continue;
                Ternary related = Ternary.False;
                foreach (var candidate in to.Types)
                {
                    related = await operation.CompareAsync(
                        part,
                        candidate,
                        RecursionFlags.Target,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (related != Ternary.False)
                        break;
                }
                if (related == Ternary.False)
                    return related;
                all &= related;
            }
            return all;
        }
    }

    private async ValueTask<Ternary> PropertyAsync(
        RelationOperation operation,
        Symbol source,
        Symbol target,
        CancellationToken cancellation)
        =>
            await PropertyAsync(
                source,
                target,
                (s, t, token) => operation.CompareAsync(s, t, cancellation: token),
                cancellation).ConfigureAwait(false);

    internal async ValueTask<Ternary> PropertyAsync(Symbol source, Symbol target,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>> compare, CancellationToken cancellation = default)
    {
        if (source == target)
            return Ternary.True;
        var access = host.AccessFlags(source, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected);
        if (access != (host.AccessFlags(target, false) & (CheckFlags.ContainsPrivate | CheckFlags.ContainsProtected)))
            return Ternary.False;
        if (access != 0 ? Target(source) != Target(target) : (source.Flags & SymbolFlags.Optional) != (target.Flags & SymbolFlags.Optional))
            return Ternary.False;
        if (host.IsReadonly(source) != host.IsReadonly(target))
            return Ternary.False;
        var sourceType = values.NonMissing(
            await values.GetAsync(source, cancellation).ConfigureAwait(false),
            (source.Flags & SymbolFlags.Optional) != 0);
        var targetType = values.NonMissing(
            await values.GetAsync(target, cancellation).ConfigureAwait(false),
            (target.Flags & SymbolFlags.Optional) != 0);
        return await compare(sourceType, targetType, cancellation).ConfigureAwait(false);
    }

    private Symbol? Target(Symbol symbol) => (symbol.CheckFlags & CheckFlags.Instantiated) != 0 ? links.Values.Get(symbol).Target : symbol;
}
