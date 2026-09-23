namespace TypeScript.Compiler.Checking;

internal sealed class RelationKey : IEquatable<RelationKey>
{
    internal readonly record struct Part(byte Kind, Type? Type = null, int Ordinal = 0);

    private readonly Part[] parts;
    private readonly int hash;
    internal IntersectionState Intersection { get; }
    internal IReadOnlyList<Part> Parts => Array.AsReadOnly(parts);

    internal RelationKey(IEnumerable<Part> parts, IntersectionState intersection)
    {
        this.parts = parts.ToArray();
        Intersection = intersection;
        var builder = new HashCode();
        builder.Add(intersection);
        foreach (var part in this.parts)
            builder.Add(part);
        hash = builder.ToHashCode();
    }

    public bool Equals(RelationKey? other) =>
        other is not null && hash == other.hash && Intersection == other.Intersection && parts.AsSpan().SequenceEqual(other.parts);

    public override bool Equals(object? obj) => obj is RelationKey other && Equals(other);

    public override int GetHashCode() => hash;
}

// Generic keys rename unconstrained parameters by first occurrence across both
// references. Concrete types and constrained parameters retain their identities.
internal sealed class RelationKeys(TypeContext context, TypeReferences references, TypeConstraints constraints)
{
    internal async ValueTask<(RelationKey Key, bool Constrained)> CreateAsync(Type source, Type target,
        IntersectionState intersection = 0, bool identity = false, bool ignoreConstraints = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(source);
        context.RequireOwned(target);
        if (identity && source.Id > target.Id)
            (source, target) = (target, source);
        var parts = new List<RelationKey.Part>();
        bool constrained = false;
        if (await GenericAsync(source, cancellation).ConfigureAwait(false)
            && await GenericAsync(target, cancellation).ConfigureAwait(false))
        {
            parts.Add(new((byte)'g'));
            var parameters = new Dictionary<Type, int>();
            await WriteAsync((TypeReference)source, 0).ConfigureAwait(false);
            parts.Add(new((byte)','));
            await WriteAsync((TypeReference)target, 0).ConfigureAwait(false);
            async ValueTask WriteAsync(TypeReference type, int depth)
            {
                parts.Add(new((byte)'t', type.ReferencedType));
                foreach (var argument in type.ResolvedTypeArguments!)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (argument is TypeParameter parameter)
                    {
                        if (ignoreConstraints
                            || await constraints.ParameterConstraintAsync(parameter, cancellation).ConfigureAwait(false) is null)
                        {
                            if (!parameters.TryGetValue(parameter, out int ordinal))
                                parameters.Add(parameter, ordinal = parameters.Count);
                            parts.Add(new((byte)'=', Ordinal: ordinal));
                            continue;
                        }
                        constrained = true;
                    }
                    else if (depth < 4 && await GenericAsync(argument, cancellation).ConfigureAwait(false))
                    {
                        parts.Add(new((byte)'<'));
                        await WriteAsync((TypeReference)argument, depth + 1).ConfigureAwait(false);
                        parts.Add(new((byte)'>'));
                        continue;
                    }
                    parts.Add(new((byte)'-', argument));
                }
            }
        }
        else
        {
            parts.Add(new((byte)'s'));
            parts.Add(new((byte)'t', source));
            parts.Add(new((byte)'t', target));
        }
        return (new(parts, intersection), constrained);
    }

    private async ValueTask<bool> GenericAsync(Type type, CancellationToken cancellation)
    {
        if (type is not TypeReference { Node: null } reference || (type.ObjectFlags & ObjectFlags.Reference) == 0)
            return false;
        var pending = new Stack<Type>();
        var arguments = await references.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
        for (int i = arguments.Count - 1; i >= 0; i--)
            pending.Push(arguments[i]);
        while (pending.TryPop(out var current))
        {
            cancellation.ThrowIfCancellationRequested();
            if (current is TypeParameter)
                return true;
            if (current is TypeReference { Node: null } nested && (current.ObjectFlags & ObjectFlags.Reference) != 0)
            {
                var next = await references.TypeArgumentsAsync(nested, cancellation).ConfigureAwait(false);
                for (int i = next.Count - 1; i >= 0; i--)
                    pending.Push(next[i]);
            }
        }
        return false;
    }
}
