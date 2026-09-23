using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class RelationSupport(
    TypeContext context,
    CheckerLinks links,
    TypeAlgebra algebra,
    TypeConstraints constraints,
    TypeNormalization normalization,
    TypeViews views,
    TypeProperties properties,
    DeclaredTypes declared,
    BaseTypes bases,
    IObjectRelationHost host)
{
    internal async ValueTask<Type?> EffectiveConstraintAsync(
        IReadOnlyList<Type> types,
        bool targetUnion,
        CancellationToken cancellation = default)
    {
        var result = new List<Type>();
        bool disjoint = false;
        foreach (var type in types)
        {
            cancellation.ThrowIfCancellationRequested();
            context.RequireOwned(type);
            if ((type.Flags & TypeFlags.Instantiable) != 0)
            {
                var constraint = await constraints.ConstraintAsync(type, cancellation).ConfigureAwait(false);
                while (constraint is not null
                    && (constraint.Flags & (TypeFlags.TypeParameter | TypeFlags.Index | TypeFlags.Conditional)) != 0)
                    constraint = await constraints.ConstraintAsync(constraint, cancellation).ConfigureAwait(false);
                if (constraint is not null)
                {
                    result.Add(constraint);
                    if (targetUnion)
                        result.Add(type);
                }
            }
            else if ((type.Flags & TypeFlags.DisjointDomains) != 0
                || await views.EmptyAnonymousAsync(type, cancellation).ConfigureAwait(false))
                disjoint = true;
        }
        if (result.Count == 0 || !targetUnion && !disjoint)
            return null;
        if (disjoint)
            foreach (var type in types)
                if ((type.Flags & TypeFlags.DisjointDomains) != 0
                    || await views.EmptyAnonymousAsync(type, cancellation).ConfigureAwait(false))
                    result.Add(type);
        return await normalization.GetAsync(
            await algebra.IntersectionAsync(
                result,
                IntersectionFlags.NoConstraintReduction,
                cancellation: cancellation).ConfigureAwait(false),
            cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Ternary?> TypeParameterAsync(
        RelationOperation operation,
        Type source,
        Type target,
        IntersectionState intersection,
        CancellationToken cancellation = default)
    {
        if (source is TypeParameter)
        {
            if (target is TypeParameter && operation.Kind == RelationKind.Comparable)
            {
                var constraint = await constraints.ConstraintAsync(source, cancellation).ConfigureAwait(false);
                if (constraint is not null
                    && (constraint is UnionType union ? union.Types.Any(t => t is TypeParameter) : constraint is TypeParameter))
                    return await operation.CompareAsync(
                        constraint,
                        target,
                        RecursionFlags.Source,
                        cancellation: cancellation).ConfigureAwait(false);
                return Ternary.False;
            }
            var bound = await constraints.ConstraintAsync(source, cancellation).ConfigureAwait(false) ?? context.UnknownType;
            var result = await operation.CompareAsync(
                bound,
                target,
                RecursionFlags.Source,
                intersection,
                cancellation).ConfigureAwait(false);
            if (result != Ternary.False)
                return result;
            return await operation.CompareAsync(
                await bases.WithThisAsync(bound, source, cancellation: cancellation).ConfigureAwait(false),
                target,
                RecursionFlags.Source,
                intersection,
                cancellation).ConfigureAwait(false);
        }
        return target is TypeParameter && (source.Flags & TypeFlags.Instantiable) == 0 && source is not MappedType ? Ternary.False : null;
    }

    internal async ValueTask<bool> ValidOverrideAsync(Symbol source, Symbol target, CancellationToken cancellation = default)
    {
        foreach (var property in await UnderlyingAsync(target, cancellation).ConfigureAwait(false))
        {
            if ((host.AccessFlags(property, false) & CheckFlags.ContainsProtected) == 0)
                continue;
            var baseType = await DeclaringClassAsync(property, cancellation).ConfigureAwait(false);
            if (baseType is null)
                throw new InvalidOperationException("Protected property has no declaring class");
            bool found = false;
            foreach (var candidate in await UnderlyingAsync(source, cancellation).ConfigureAwait(false))
            {
                var derived = await DeclaringClassAsync(candidate, cancellation).ConfigureAwait(false);
                if (derived is not null && await bases.HasBaseAsync(derived, baseType, cancellation).ConfigureAwait(false))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    private async ValueTask<Type?> DeclaringClassAsync(Symbol symbol, CancellationToken cancellation)
        =>
            symbol.Parent is { } parent && (parent.Flags & SymbolFlags.Class) != 0
                ? await declared.GetAsync(parent, cancellation).ConfigureAwait(false)
                : null;

    private async ValueTask<IReadOnlyList<Symbol>> UnderlyingAsync(Symbol symbol, CancellationToken cancellation)
    {
        var result = new List<Symbol>();
        var pending = new Stack<Symbol>();
        pending.Push(symbol);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((current.CheckFlags & CheckFlags.Synthetic) == 0)
            {
                result.Add(current);
                continue;
            }
            var containing = links.Values.Get(current).ContainingType as UnionOrIntersectionType ?? throw new InvalidOperationException("Synthetic property has no containing type");
            var children = new List<Symbol>();
            foreach (var type in containing.Types)
                if (await properties.PropertyAsync(type, current.Name, cancellation: cancellation).ConfigureAwait(false) is { } property)
                    children.Add(property);
            for (int i = children.Count - 1; i >= 0; i--)
                pending.Push(children[i]);
        }
        return result;
    }
}
