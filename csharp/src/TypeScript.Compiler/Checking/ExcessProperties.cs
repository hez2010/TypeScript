using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface IExcessPropertyHost
{
    bool NoImplicitAny { get; }
    Type GlobalObject { get; }

    ValueTask<IndexInfo?> ApplicableIndexAsync(Type type, string name, CancellationToken cancellation);

    ValueTask<IReadOnlyList<IndexInfo>> IndexesAsync(Type type, CancellationToken cancellation);
}

internal sealed class ExcessProperties(TypeContext context, TypeAlgebra algebra, TypeViews views, TypeProperties properties,
    SymbolTypes values, TypePredicates predicates, TypeDiscrimination discrimination, IExcessPropertyHost host)
{
    internal async ValueTask<bool> HasAsync(RelationOperation operation, Type source, Type target, CancellationToken cancellation = default)
    {
        if (!Target(target) || !host.NoImplicitAny && (target.ObjectFlags & ObjectFlags.JSLiteral) != 0)
            return false;
        bool jsx = (source.ObjectFlags & ObjectFlags.JsxAttributes) != 0;
        if (operation.Kind is RelationKind.Assignable or RelationKind.Comparable
            && (target == host.GlobalObject || target is UnionType objectUnion && objectUnion.Types.Contains(host.GlobalObject)
                || !jsx && await views.EmptyObjectAsync(target, cancellation).ConfigureAwait(false)))
            return false;
        var reduced = target;
        IReadOnlyList<Type>? checkTypes = null;
        if (target is UnionType union)
        {
            reduced = await discrimination.MatchAsync(
                source,
                union,
                operation,
                cancellation).ConfigureAwait(false) ?? FilterPrimitives(union);
            checkTypes = reduced is UnionType reducedUnion ? reducedUnion.Types : [reduced];
        }
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
        {
            if (property.ValueDeclaration is null || source.Symbol?.ValueDeclaration is null
                || property.ValueDeclaration.Parent != source.Symbol.ValueDeclaration || jsx && property.Name.Contains('-'))
                continue;
            if (!await KnownAsync(reduced, property.Name, jsx, cancellation).ConfigureAwait(false))
                return true;
            if (checkTypes is not null)
            {
                var types = new List<Type>();
                foreach (var part in checkTypes)
                {
                    var apparent = await views.ApparentAsync(part, cancellation).ConfigureAwait(false);
                    var member = await properties.PropertyAsync(apparent, property.Name, cancellation: cancellation).ConfigureAwait(false);
                    types.Add(member is not null ? await values.GetAsync(member, cancellation).ConfigureAwait(false)
                        : (await host.ApplicableIndexAsync(
                            apparent,
                            property.Name,
                            cancellation).ConfigureAwait(false))?.ValueType ?? context.UndefinedType);
                }
                if (await operation.CompareAsync(await values.GetAsync(property, cancellation).ConfigureAwait(false),
                    await algebra.UnionAsync(types, cancellation: cancellation).ConfigureAwait(false),
                    cancellation: cancellation).ConfigureAwait(false) == Ternary.False)
                    return true;
            }
        }
        return false;
    }

    private Type FilterPrimitives(UnionType target)
    {
        if (predicates.Maybe(target, TypeFlags.NonPrimitive))
        {
            var filtered = algebra.Filter(target, t => (t.Flags & TypeFlags.Primitive) == 0);
            if ((filtered.Flags & TypeFlags.Never) == 0)
                return filtered;
        }
        return target;
    }

    internal async ValueTask<(Symbol Property, Type Target)?> UnknownPropertyAsync(
        Type source,
        Type target,
        RelationKind kind,
        TypeRelations relations,
        CancellationToken cancellation = default)
    {
        if ((source.ObjectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)) != (ObjectFlags.ObjectLiteral | ObjectFlags.FreshLiteral)
            || !Target(target) || !host.NoImplicitAny && (target.ObjectFlags & ObjectFlags.JSLiteral) != 0)
            return null;
        bool jsx = (source.ObjectFlags & ObjectFlags.JsxAttributes) != 0;
        if (kind is RelationKind.Assignable or RelationKind.Comparable
            && (target == host.GlobalObject || target is UnionType objectUnion && objectUnion.Types.Contains(host.GlobalObject)
                || !jsx && await views.EmptyObjectAsync(target, cancellation).ConfigureAwait(false)))
            return null;
        if (target is UnionType union)
            target = await discrimination.MatchAsync(
                source,
                union,
                (s, t) => relations.RelatedAsync(s, t, kind, cancellation),
                cancellation).ConfigureAwait(false)
                ?? FilterPrimitives(union);
        foreach (var property in await properties.GetAsync(source, cancellation).ConfigureAwait(false))
            if (property.ValueDeclaration is not null && source.Symbol?.ValueDeclaration is not null
                && property.ValueDeclaration.Parent == source.Symbol.ValueDeclaration && !(jsx && property.Name.Contains('-'))
                && !await KnownAsync(target, property.Name, jsx, cancellation).ConfigureAwait(false))
                return (property, await algebra.FilterAsync(
                    target,
                    part => ValueTask.FromResult(Target(part)),
                    cancellation).ConfigureAwait(false));
        return null;
    }

    private async ValueTask<bool> KnownAsync(Type type, string name, bool jsx, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if ((type.Flags & TypeFlags.Object) != 0
            && (await properties.ObjectPropertyAsync(type, name, cancellation).ConfigureAwait(false) is not null
                || await host.ApplicableIndexAsync(type, name, cancellation).ConfigureAwait(false) is not null
                || name.StartsWith(Symbol.InternalPrefix + "@", StringComparison.Ordinal)
                    && (await host.IndexesAsync(type, cancellation).ConfigureAwait(false)).Any(i => i.KeyType == context.StringType)
                || jsx && name.Contains('-')))
            return true;
        if (type is SubstitutionType substitution)
            return await KnownAsync(substitution.BaseType, name, jsx, cancellation).ConfigureAwait(false);
        if (type is UnionOrIntersectionType composite && Target(type))
            foreach (var part in composite.Types)
                if (await KnownAsync(part, name, jsx, cancellation).ConfigureAwait(false))
                    return true;
        return false;
    }

    private static bool Target(Type type)
    {
        var pending = new Stack<(Type Type, bool Visited)>();
        var results = new Dictionary<Type, bool>();
        pending.Push((type, false));
        while (pending.TryPop(out var next))
        {
            var current = next.Type;
            if (results.ContainsKey(current))
                continue;
            if ((current.Flags & TypeFlags.Object) != 0
                && (current.ObjectFlags & ObjectFlags.ObjectLiteralPatternWithComputedProperties) == 0
                || (current.Flags & TypeFlags.NonPrimitive) != 0)
            {
                results[current] = true;
                continue;
            }
            if (!next.Visited)
            {
                pending.Push((current, true));
                if (current is SubstitutionType substitution)
                    pending.Push((substitution.BaseType, false));
                else if (current is UnionOrIntersectionType composite)
                    foreach (var part in composite.Types)
                        pending.Push((part, false));
            }
            else
                results[current] = current switch
                {
                    SubstitutionType substitution => results[substitution.BaseType],
                    UnionType union => union.Types.Any(t => results[t]),
                    IntersectionType intersection => intersection.Types.All(t => results[t]),
                    _ => false
                };
        }
        return results[type];
    }
}
