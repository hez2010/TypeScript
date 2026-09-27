using System.Runtime.CompilerServices;

namespace TypeScript.Compiler.Checking;

internal enum TypeMapperKind
{
    Unknown,
    Simple,
    Array,
    Merged
}

// Port of checker/mapper.go. Merged/composite mapper chains are input-shaped;
// evaluate them with an explicit continuation stack instead of the native stack.
internal abstract class TypeMapper
{
    internal virtual TypeMapperKind Kind => TypeMapperKind.Unknown;
    internal virtual bool MapsThisOnly => false;

    private protected abstract Type MapLeaf(Type type);

    internal (Type Source, Type Target) Single => this is Simple simple ? (simple.Source, simple.Target)
        : throw new InvalidOperationException("Mapper is not a single-parameter mapping");

    internal IReadOnlyList<Type> Sources => this is Direct direct ? direct.SourceTypes
        : throw new InvalidOperationException("Mapper has no direct sources");
    internal IReadOnlyList<Type> Targets => this is Direct direct ? direct.TargetTypes
        : throw new InvalidOperationException("Mapper has no direct targets");
    internal (TypeMapper First, TypeMapper Second) Parts => this switch
    {
        Merged merged => (merged.First, merged.Second),
        Composite composite => (composite.First, composite.Second),
        _ => throw new InvalidOperationException("Mapper has no composition")
    };

    internal Type Map(Type type, CancellationToken cancellation = default) => MapAsync(type, cancellation).GetAwaiter().GetResult();

    internal async ValueTask<Type> MapAsync(Type type, CancellationToken cancellation = default)
    {
        // A composite can re-enter through structural instantiation. Preserve the
        // checker's semantic limits instead of imposing a native-stack limit.
        cancellation.ThrowIfCancellationRequested();
        if (this is Simple or Direct or SingleTarget)
        {
            var mapped = MapLeaf(type);
            type.Context.RequireOwned(mapped);
            return mapped;
        }
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (this is not (Merged or Composite))
        {
            var mapped = this is AsyncFunctionMapper function
                ? await function.Callback(type, cancellation).ConfigureAwait(false)
                : MapLeaf(type);
            type.Context.RequireOwned(mapped);
            return mapped;
        }
        Type result = type;
        var pending = Interlocked.Exchange(ref type.Context.MappingScratch, null) ?? new Stack<(TypeMapper Mapper, Type? Original)>();
        try
        {
            pending.Push((this, null));
            while (pending.TryPop(out var frame))
            {
                cancellation.ThrowIfCancellationRequested();
                switch (frame.Mapper)
                {
                    case Merged merged:
                        pending.Push((merged.Second, null));
                        pending.Push((merged.First, null));
                        break;
                    case Composite composite when frame.Original is null:
                        pending.Push((composite, result));
                        pending.Push((composite.First, null));
                        break;
                    case Composite composite:
                        if (result != frame.Original)
                            result = await composite.Instantiate(result, composite.Second, cancellation).ConfigureAwait(false);
                        else
                            pending.Push((composite.Second, null));
                        break;
                    case AsyncFunctionMapper function:
                        result = await function.Callback(result, cancellation).ConfigureAwait(false);
                        break;
                    default:
                        result = frame.Mapper.MapLeaf(result);
                        break;
                }
                type.Context.RequireOwned(result);
            }
            return result;
        }
        finally
        {
            pending.Clear();
            Interlocked.CompareExchange(ref type.Context.MappingScratch, pending, null);
        }
    }

    internal Type MapType(Type type, CancellationToken cancellation = default)
        => Map(type is TypeParameter parameter ? parameter.NonDistributed : type, cancellation);

    internal ValueTask<Type> MapTypeAsync(Type type, CancellationToken cancellation = default)
        => MapAsync(type is TypeParameter parameter ? parameter.NonDistributed : type, cancellation);

    internal static TypeMapper Create(ReadOnlySpan<Type> sources, ReadOnlySpan<Type> targets)
    {
        Validate(sources, targets);
        return sources.Length == 1 ? new Simple(sources[0], targets[0]) : new Direct(sources.ToArray(), targets.ToArray());
    }

    internal static TypeMapper ToSingle(ReadOnlySpan<Type> sources, Type target)
    {
        target.Context.RequireOwned(sources);
        return new SingleTarget(sources.ToArray(), target);
    }

    internal static TypeMapper Deferred(ReadOnlySpan<Type> sources, ReadOnlySpan<Func<Type>> targets)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(sources.Length, targets.Length);
        if (sources.Length != 0)
            sources[0].Context.RequireOwned(sources);
        return new DeferredTargets(sources.ToArray(), targets.ToArray());
    }

    internal static TypeMapper Function(Func<Type, Type> map) => new FunctionMapper(map);

    internal static TypeMapper FunctionAsync(Func<Type, CancellationToken, ValueTask<Type>> map) => new AsyncFunctionMapper(map);

    internal static TypeMapper Merge(TypeMapper? first, TypeMapper second) => first is null ? second : new Merged(first, second);

    internal static TypeMapper Combine(TypeMapper? first, TypeMapper second, Func<Type, TypeMapper, Type> instantiate)
            => CombineAsync(first, second, (type, mapper, _) => ValueTask.FromResult(instantiate(type, mapper)));

    internal static TypeMapper CombineAsync(TypeMapper? first, TypeMapper second,
        Func<Type, TypeMapper, CancellationToken, ValueTask<Type>> instantiate)
        => first is null ? second : new Composite(first, second, instantiate);

    internal static TypeMapper Prepend(Type source, Type target, TypeMapper? mapper)
    {
        var mapping = Create([source is TypeParameter parameter ? parameter.NonDistributed : source], [target]);
        return mapper is null ? mapping : Merge(mapping, mapper);
    }

    internal static TypeMapper Append(TypeMapper? mapper, Type source, Type target)
        => Merge(mapper, Create([source is TypeParameter parameter ? parameter.NonDistributed : source], [target]));

    private static void Validate(ReadOnlySpan<Type> sources, ReadOnlySpan<Type> targets)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(sources.Length, targets.Length);
        if (sources.Length != 0)
        {
            sources[0].Context.RequireOwned(sources);
            sources[0].Context.RequireOwned(targets);
        }
    }

    private sealed class Simple(Type source, Type target) : TypeMapper
    {
        internal Type Source => source;
        internal Type Target => target;
        internal override TypeMapperKind Kind => TypeMapperKind.Simple;
        internal override bool MapsThisOnly => source is TypeParameter { IsThisType: true };
        private protected override Type MapLeaf(Type type) => type == source ? target : type;
    }

    private sealed class Direct(Type[] sources, Type[] targets) : TypeMapper
    {
        internal IReadOnlyList<Type> SourceTypes { get; } = Array.AsReadOnly(sources);
        internal IReadOnlyList<Type> TargetTypes { get; } = Array.AsReadOnly(targets);
        internal override TypeMapperKind Kind => TypeMapperKind.Array;
        internal override bool MapsThisOnly => sources is [TypeParameter { IsThisType: true }];

        private protected override Type MapLeaf(Type type)
        {
            int index = Array.IndexOf(sources, type);
            return index < 0 ? type : targets[index];
        }
    }

    private sealed class SingleTarget(Type[] sources, Type target) : TypeMapper
    {
        internal override bool MapsThisOnly => sources is [TypeParameter { IsThisType: true }];

        private protected override Type MapLeaf(Type type) => Array.IndexOf(sources, type) < 0 ? type : target;
    }

    private sealed class DeferredTargets(Type[] sources, Func<Type>[] targets) : TypeMapper
    {
        internal override bool MapsThisOnly => sources is [TypeParameter { IsThisType: true }];

        private protected override Type MapLeaf(Type type)
        {
            int index = Array.IndexOf(sources, type);
            return index < 0 ? type : targets[index]();
        }
    }

    private sealed class FunctionMapper(Func<Type, Type> map) : TypeMapper
    {
        private protected override Type MapLeaf(Type type) => map(type);
    }

    private sealed class AsyncFunctionMapper(Func<Type, CancellationToken, ValueTask<Type>> map) : TypeMapper
    {
        internal Func<Type, CancellationToken, ValueTask<Type>> Callback => map;

        private protected override Type MapLeaf(Type type) =>
            throw new InvalidOperationException("Asynchronous mapper requires a continuation");
    }

    private sealed class Merged(TypeMapper first, TypeMapper second) : TypeMapper
    {
        internal TypeMapper First => first;
        internal TypeMapper Second => second;
        internal override TypeMapperKind Kind => TypeMapperKind.Merged;

        private protected override Type MapLeaf(Type type) => throw new InvalidOperationException("Composite mapper requires continuation");
    }

    private sealed class Composite(TypeMapper first, TypeMapper second,
        Func<Type, TypeMapper, CancellationToken, ValueTask<Type>> instantiate) : TypeMapper
    {
        internal TypeMapper First => first;
        internal TypeMapper Second => second;
        internal Func<Type, TypeMapper, CancellationToken, ValueTask<Type>> Instantiate => instantiate;

        private protected override Type MapLeaf(Type type) => throw new InvalidOperationException("Composite mapper requires continuation");
    }
}
