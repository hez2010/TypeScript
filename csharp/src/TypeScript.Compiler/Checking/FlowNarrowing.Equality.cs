using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowNarrowing
{
    internal async ValueTask<Type> EqualityAsync(
        Type type,
        SyntaxKind op,
        SyntaxNode value,
        bool assumeTrue,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((type.Flags & TypeFlags.Any) != 0)
            return type;
        if (op is SyntaxKind.ExclamationEqualsToken or SyntaxKind.ExclamationEqualsEqualsToken)
            assumeTrue = !assumeTrue;
        var valueType = await host.ExpressionAsync(value, cancellation).ConfigureAwait(false);
        bool loose = op is SyntaxKind.EqualsEqualsToken or SyntaxKind.ExclamationEqualsToken;
        if ((valueType.Flags & TypeFlags.Nullable) != 0)
        {
            if (!context.StrictNullChecks)
                return type;
            var include = loose ? assumeTrue ? TypeFacts.EQUndefinedOrNull : TypeFacts.NEUndefinedOrNull
                : (valueType.Flags & TypeFlags.Null) != 0 ? assumeTrue ? TypeFacts.EQNull : TypeFacts.NENull
                : assumeTrue ? TypeFacts.EQUndefined : TypeFacts.NEUndefined;
            return await facts.AdjustAsync(type, include, cancellation).ConfigureAwait(false);
        }
        if (assumeTrue)
        {
            if (!loose && ((type.Flags & TypeFlags.Unknown) != 0 || await SomeEmptyAsync(type, cancellation).ConfigureAwait(false)))
            {
                if ((valueType.Flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0
                    || await views.EmptyAnonymousAsync(valueType, cancellation).ConfigureAwait(false))
                    return valueType;
                if ((valueType.Flags & TypeFlags.Object) != 0)
                    return context.NonPrimitiveType;
            }
            if (!loose && (valueType.Flags & TypeFlags.Primitive) != 0 && Uniform(type))
            {
                var regular = await algebra.RegularTypeAsync(valueType, cancellation).ConfigureAwait(false);
                if (algebra.UnionContains((UnionType)type, regular, false))
                    return regular;
            }
            var filtered = await algebra.FilterAsync(
                type,
                async part => await ComparableAsync(part, valueType, cancellation).ConfigureAwait(false)
                || loose && (part.Flags & (TypeFlags.Number | TypeFlags.String | TypeFlags.BooleanLiteral)) != 0
                    && (valueType.Flags & (TypeFlags.Number | TypeFlags.String | TypeFlags.Boolean)) != 0,
                cancellation).ConfigureAwait(false);
            return await ReplacePrimitivesAsync(filtered, valueType, cancellation).ConfigureAwait(false);
        }
        if (valueType.IsUnit)
        {
            if (Uniform(type))
            {
                var removed = Remove(type, await algebra.RegularTypeAsync(valueType, cancellation).ConfigureAwait(false));
                if (removed != type)
                    return removed;
            }
            return await algebra.FilterAsync(type, async part => !(await UnitLikeAsync(part, cancellation).ConfigureAwait(false)
                && await ComparableAsync(part, valueType, cancellation).ConfigureAwait(false)), cancellation).ConfigureAwait(false);
        }
        return type;
    }

    private async ValueTask<bool> SomeEmptyAsync(Type type, CancellationToken cancellation)
    {
        foreach (var part in type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type])
            if (await views.EmptyAnonymousAsync(part, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }

    private async ValueTask<bool> ComparableAsync(Type source, Type target, CancellationToken cancellation)
        => await relations.RelatedAsync(source, target, RelationKind.Comparable, cancellation).ConfigureAwait(false)
            || await relations.RelatedAsync(target, source, RelationKind.Comparable, cancellation).ConfigureAwait(false);

    private async ValueTask<bool> UnitLikeAsync(Type type, CancellationToken cancellation)
    {
        type = await constraints.BaseConstraintOrTypeAsync(type, cancellation).ConfigureAwait(false);
        return type is IntersectionType intersection ? intersection.Types.Any(t => t.IsUnit) : type.IsUnit;
    }

    internal bool Uniform(Type type)
    {
        if ((type.ObjectFlags & ObjectFlags.PrimitiveUnion) == 0)
            return false;
        if ((type.ObjectFlags & ObjectFlags.IsUniformEnumComputed) == 0)
        {
            Symbol? enumSymbol = null;
            bool literals = false, uniform = true;
            foreach (var part in ((UnionType)type).Types)
            {
                if ((part.Flags & TypeFlags.EnumLike) != 0)
                {
                    if (literals)
                    {
                        uniform = false;
                        break;
                    }
                    var parent = symbols.Parent(part.Symbol!);
                    if (enumSymbol is null)
                        enumSymbol = parent;
                    else if (enumSymbol != parent)
                    {
                        uniform = false;
                        break;
                    }
                }
                else if ((part.Flags & TypeFlags.StringOrNumberLiteral) != 0)
                {
                    if (enumSymbol is not null)
                    {
                        uniform = false;
                        break;
                    }
                    literals = true;
                }
            }
            type.ObjectFlags |= ObjectFlags.IsUniformEnumComputed | (uniform ? ObjectFlags.IsUniformEnum : 0);
        }
        return (type.ObjectFlags & ObjectFlags.IsUniformEnum) != 0;
    }

    private Type Remove(Type type, Type removed)
    {
        if (type is not UnionType union)
            return type == removed ? context.NeverType : type;
        if (union.Origin is UnionType origin && origin.Types.Contains(removed))
            return algebra.Filter(type, t => t != removed);
        if (!union.Types.Contains(removed))
            return type;
        return context.GetUnionFromSortedTypes(union.Types.Where(t => t != removed).ToArray(),
            union.ObjectFlags & (ObjectFlags.PrimitiveUnion | ObjectFlags.ContainsIntersections));
    }

    internal async ValueTask<Type> ReplacePrimitivesAsync(Type primitives, Type literals, CancellationToken cancellation = default)
    {
        if (!predicates.Maybe(primitives, TypeFlags.String | TypeFlags.TemplateLiteral | TypeFlags.Number | TypeFlags.BigInt, cancellation)
            || !predicates.Maybe(
                literals,
                TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping | TypeFlags.NumberLiteral | TypeFlags.BigIntLiteral,
                cancellation))
            return primitives;
        return await algebra.MapAsync(primitives, part =>
        {
            var flags = (part.Flags & TypeFlags.String) != 0 ? TypeFlags.String | TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping
                : TypeAlgebra.IsPatternLiteral(part)
                    && !predicates.Maybe(literals, TypeFlags.String | TypeFlags.TemplateLiteral | TypeFlags.StringMapping, cancellation)
                    ? TypeFlags.StringLiteral
                : (part.Flags & TypeFlags.Number) != 0 ? TypeFlags.Number | TypeFlags.NumberLiteral
                : (part.Flags & TypeFlags.BigInt) != 0 ? TypeFlags.BigInt | TypeFlags.BigIntLiteral : TypeFlags.None;
            return ValueTask.FromResult<Type?>(flags == 0 ? part : algebra.Filter(literals, t => (t.Flags & flags) != 0));
        }, cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
    }
}
