namespace TypeScript.Compiler.Checking;

internal readonly struct RelationKey : IEquatable<RelationKey>
{
    internal readonly record struct Part(byte Kind, Type? Type = null, int Ordinal = 0);

    private readonly Part[]? parts;
    private readonly Type? source, target;
    private readonly int hash;
    internal IntersectionState Intersection { get; }
    internal IReadOnlyList<Part> Parts => Array.AsReadOnly(parts ?? (source is null ? [] :
        [new((byte)'s'), new((byte)'t', source), new((byte)'t', target)]));

    internal RelationKey(Type source, Type target, IntersectionState intersection)
    {
        this.source = source;
        this.target = target;
        Intersection = intersection;
        var builder = new HashCode();
        builder.Add(intersection);
        builder.Add(new Part((byte)'s'));
        builder.Add(new Part((byte)'t', source));
        builder.Add(new Part((byte)'t', target));
        hash = builder.ToHashCode();
    }

    internal RelationKey(IEnumerable<Part> parts, IntersectionState intersection)
    {
        this.parts = parts.ToArray();
        Intersection = intersection;
        var builder = new HashCode();
        builder.Add(intersection);
        foreach (var part in this.parts)
            builder.Add(part);
        hash = builder.ToHashCode();
        if (this.parts is [{ Kind: (byte)'s', Type: null, Ordinal: 0 },
            { Kind: (byte)'t', Type: not null, Ordinal: 0 }, { Kind: (byte)'t', Type: not null, Ordinal: 0 }])
        {
            source = this.parts[1].Type;
            target = this.parts[2].Type;
            this.parts = null;
        }
    }

    public bool Equals(RelationKey other) => hash == other.hash && Intersection == other.Intersection
        && source == other.source && target == other.target && parts.AsSpan().SequenceEqual(other.parts);
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
        if (!await GenericAsync(source, cancellation).ConfigureAwait(false)
            || !await GenericAsync(target, cancellation).ConfigureAwait(false))
            return (new(source, target, intersection), false);
        return await CreateGenericAsync((TypeReference)source, (TypeReference)target, intersection, ignoreConstraints,
            cancellation).ConfigureAwait(false);
    }

    // Keep the recursive writer's closure and collections off the ordinary two-type path.
    private async ValueTask<(RelationKey Key, bool Constrained)> CreateGenericAsync(TypeReference source, TypeReference target,
        IntersectionState intersection, bool ignoreConstraints, CancellationToken cancellation)
    {
        var parts = new List<RelationKey.Part>();
        bool constrained = false;
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
