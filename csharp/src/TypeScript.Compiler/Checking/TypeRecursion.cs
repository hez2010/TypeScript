using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;

namespace TypeScript.Compiler.Checking;

// Reference identities distinguish syntax origins, symbols and concrete types.
// No process-local ID is substituted for object identity in recursion detection.
internal readonly record struct RecursionIdentity
{
    private readonly object value;

    internal RecursionIdentity(Type type) => value = type;

    internal RecursionIdentity(Symbol symbol) => value = symbol;

    internal RecursionIdentity(SyntaxNode node) => value = node;

    internal object Value => value;
}

internal sealed class TypeRecursion(Func<MappedType, CancellationToken, ValueTask<Type?>> getMappedModifiers)
{
    internal async ValueTask<Type> TargetAsync(Type type, CancellationToken cancellation = default)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (type is IndexedAccessType indexed)
            {
                type = indexed.ObjectType;
                continue;
            }
            if ((type.ObjectFlags & O.InstantiatedMapped) == O.InstantiatedMapped)
            {
                var target = await getMappedModifiers((MappedType)type, cancellation).ConfigureAwait(false);
                if (target is not null
                    && (target.Symbol is not null
                        || target is IntersectionType intersection && intersection.Types.Any(t => t.Symbol is not null)))
                {
                    type = target;
                    continue;
                }
            }
            return type;
        }
    }

    internal async ValueTask<RecursionIdentity> IdentityAsync(Type type, CancellationToken cancellation = default)
        => FromTarget(await TargetAsync(type, cancellation).ConfigureAwait(false));

    internal static RecursionIdentity FromTarget(Type type)
    {
        if ((type.Flags & F.Object) != 0 && (type.ObjectFlags & (O.ObjectLiteral | O.ArrayLiteral)) == 0)
        {
            if ((type.ObjectFlags & O.Reference) != 0 && type is TypeReference { Node: { } node })
                return new(node);
            if (type.Symbol is { } symbol && !((type.ObjectFlags & O.Anonymous) != 0 && (symbol.Flags & SymbolFlags.Class) != 0)
                && (type.ObjectFlags & O.FromTypeNode) == 0)
                return new(symbol);
            if (type is TypeReference { Target: TupleType target } && (type.ObjectFlags & O.FromTypeNode) == 0)
                return new(target);
        }
        if (type is TypeParameter { Symbol: { } parameter })
            return new(parameter);
        if (type is ConditionalType conditional)
            return new(conditional.Root.Node);
        return new(type);
    }

    internal async ValueTask<bool> MatchesAsync(Type type, RecursionIdentity identity, CancellationToken cancellation = default)
    {
        // The work list is only needed once an intersection constituent appears; the single-type
        // input that dominates this call used to allocate a Stack and its backing array for nothing.
        Type current = type;
        Stack<Type>? pending = null;
        while (true)
        {
            var target = await TargetAsync(current, cancellation).ConfigureAwait(false);
            if (target is IntersectionType intersection)
            {
                pending ??= new Stack<Type>();
                for (int i = 0; i < intersection.Types.Count; i++)
                    pending.Push(intersection.Types[i]);
            }
            else if (FromTarget(target) == identity)
                return true;
            if (pending is null || !pending.TryPop(out current!))
                return false;
        }
    }

    internal async ValueTask<bool> IsDeeplyNestedAsync(
        Type type,
        IReadOnlyList<Type> stack,
        int maximumDepth,
        CancellationToken cancellation = default)
    {
        if (stack.Count < maximumDepth)
            return false;
        // Same lazy work list: most inputs are not intersections, so the stack is only created when
        // one is actually seen.
        Type current = type;
        Stack<Type>? pending = null;
        while (true)
        {
            var target = await TargetAsync(current, cancellation).ConfigureAwait(false);
            if (target is IntersectionType intersection)
            {
                pending ??= new Stack<Type>();
                for (int i = 0; i < intersection.Types.Count; i++)
                    pending.Push(intersection.Types[i]);
            }
            else
            {
                var identity = FromTarget(target);
                int count = 0;
                uint lastId = 0;
                foreach (var entry in stack)
                    if (await MatchesAsync(entry, identity, cancellation).ConfigureAwait(false))
                    {
                        if (entry.Id >= lastId && ++count >= maximumDepth)
                            return true;
                        lastId = entry.Id;
                    }
            }
            if (pending is null || !pending.TryPop(out current!))
                return false;
        }
    }
}
