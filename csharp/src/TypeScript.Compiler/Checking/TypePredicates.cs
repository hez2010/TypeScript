namespace TypeScript.Compiler.Checking;

internal sealed class TypePredicates(TypeContext context, TypeConstraints constraints, TypeRelations relations)
{
    private readonly (TypeFlags Flags, Type Type)[] primitives =
    [
        (TypeFlags.NumberLike, context.NumberType), (TypeFlags.BigIntLike, context.BigIntType),
        (TypeFlags.StringLike, context.StringType), (TypeFlags.BooleanLike, context.BooleanType),
        (TypeFlags.Void, context.VoidType), (TypeFlags.Never, context.NeverType),
        (TypeFlags.Null, context.NullType), (TypeFlags.Undefined, context.UndefinedType),
        (TypeFlags.ESSymbol, context.ESSymbolType), (TypeFlags.NonPrimitive, context.NonPrimitiveType)
    ];

    internal bool Maybe(Type type, TypeFlags flags, CancellationToken cancellation = default)
    {
        context.RequireOwned(type);
        cancellation.ThrowIfCancellationRequested();
        if ((type.Flags & flags) != 0)
            return true;
        if (type is not UnionOrIntersectionType composite)
            return false;
        // Union and intersection constituents are flattened when the composite is built, so a direct
        // scan decides the common case without a work list. Nested composites, which the walk below
        // would descend into, keep the explicit work list.
        var constituents = composite.Types;
        bool nested = false;
        for (int i = 0; i < constituents.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var part = constituents[i];
            if ((part.Flags & flags) != 0)
                return true;
            nested |= part is UnionOrIntersectionType;
        }
        return nested && Nested(composite, flags, cancellation);
    }

    private static bool Nested(UnionOrIntersectionType composite, TypeFlags flags, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        pending.Push(composite);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if ((current.Flags & flags) != 0)
                return true;
            if (current is UnionOrIntersectionType nested)
                foreach (var part in nested.Types)
                    pending.Push(part);
        }
        return false;
    }

    internal async ValueTask<bool> MaybeAsync(
        Type type,
        TypeFlags flags,
        bool baseConstraint = false,
        CancellationToken cancellation = default)
        =>
            Maybe(type, flags, cancellation)
                || baseConstraint
                    && Maybe(await constraints.BaseConstraintOrTypeAsync(type, cancellation).ConfigureAwait(false), flags, cancellation);

    internal async ValueTask<bool> AssignableAsync(
        Type type,
        TypeFlags flags,
        bool strict = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((type.Flags & flags) != 0)
            return true;
        if (strict && (type.Flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.Nullable)) != 0)
            return false;
        foreach (var primitive in primitives)
            if ((flags & primitive.Flags) != 0
                && await relations.RelatedAsync(type, primitive.Type, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                return true;
        return false;
    }
}
