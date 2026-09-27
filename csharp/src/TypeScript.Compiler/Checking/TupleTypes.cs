using TypeScript.Compiler.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;
using F = TypeScript.Compiler.Checking.TypeFlags;
using O = TypeScript.Compiler.Checking.ObjectFlags;
using E = TypeScript.Compiler.Checking.ElementFlags;

namespace TypeScript.Compiler.Checking;

internal interface ITupleTypeHost
{
    Type ArrayTarget(bool isReadonly);

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<bool> IsGenericMappedAsync(Type type, CancellationToken cancellation);

    ValueTask<Type?> ArrayLikeElementAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> IndexedAccessAsync(Type objectType, Type indexType, CancellationToken cancellation);

    void TupleTooLarge();

    void CrossProductTooLarge(long size);
}

internal sealed class TupleTypes(TypeContext context, TypeAlgebra algebra, CheckerLinks links, ITupleTypeHost host)
{
    private readonly Dictionary<TupleKey, TupleType> targets = [];

    internal async ValueTask<Type> CreateAsync(IReadOnlyList<Type> elements, IReadOnlyList<TupleElementInfo> infos, bool isReadonly = false,
        CancellationToken cancellation = default)
    {
        if (elements.Count != infos.Count)
            throw new ArgumentException("Tuple element/type arity mismatch");
        foreach (var element in elements)
            context.RequireOwned(element);
        var target = await TargetAsync(infos, isReadonly, cancellation).ConfigureAwait(false);
        if (target == context.EmptyGenericType)
            return context.EmptyObjectType;
        return elements.Count == 0
            ? target
            : await NormalizeReferenceAsync((InterfaceType)target, elements, cancellation: cancellation).ConfigureAwait(false);
    }

    internal ValueTask<Type> ArrayAsync(Type element, bool isReadonly = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(element);
        var target = host.ArrayTarget(isReadonly);
        return ValueTask.FromResult<Type>(
            target == context.EmptyGenericType ? context.EmptyObjectType : context.CreateTypeReference((InterfaceType)target, [element]));
    }

    internal async ValueTask<Type> TargetAsync(
        IReadOnlyList<TupleElementInfo> infos,
        bool isReadonly,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (infos.Count == 1 && (infos[0].Flags & E.Rest) != 0)
            return host.ArrayTarget(isReadonly);
        var key = new TupleKey(infos, isReadonly);
        if (targets.TryGetValue(key, out var cached))
            return cached;
        int arity = infos.Count, minimum = infos.Count(i => (i.Flags & (E.Required | E.Variadic)) != 0);
        var parameters = new TypeParameter[arity];
        var members = new Dictionary<TextSlice, Symbol>();
        E combined = 0;
        for (int i = 0; i < arity; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            parameters[i] = context.NewTypeParameter();
            var flags = infos[i].Flags;
            combined |= flags;
            if ((combined & E.Variable) == 0)
            {
                var property = new Symbol(
                    SymbolFlags.Property | SymbolFlags.Transient | ((flags & E.Optional) != 0 ? SymbolFlags.Optional : 0),
                    TextSlice.Format(i))
                { CheckFlags = isReadonly ? CheckFlags.Readonly : 0 };
                links.Values.Get(property).ResolvedType = parameters[i];
                members.Add(property.Name, property);
            }
        }
        int fixedLength = members.Count;
        var length = new Symbol(
            SymbolFlags.Property | SymbolFlags.Transient,
            "length")
        { CheckFlags = isReadonly ? CheckFlags.Readonly : 0 };
        Type lengthType = context.NumberType;
        if ((combined & E.Variable) == 0)
        {
            var lengths = new Type[arity - minimum + 1];
            for (int i = minimum; i <= arity; i++)
                lengths[i - minimum] = context.GetNumberLiteralType(i);
            lengthType = await algebra.UnionAsync(lengths, cancellation: cancellation).ConfigureAwait(false);
        }
        links.Values.Get(length).ResolvedType = lengthType;
        members.Add(length.Name, length);
        var tuple = (TupleType)context.NewObjectType(O.Tuple | O.Reference);
        var thisType = context.NewTypeParameter();
        thisType.IsThisType = true;
        thisType.Constraint = tuple;
        tuple.ThisType = thisType;
        tuple.AllTypeParameters = Array.AsReadOnly<Type>([.. parameters, thisType]);
        tuple.Instantiations = new() { [new TypeCacheKey(parameters)] = tuple };
        tuple.Target = tuple;
        tuple.ResolvedTypeArguments = Array.AsReadOnly<Type>(parameters);
        tuple.DeclaredMembersResolved = true;
        tuple.DeclaredMembers = members.AsReadOnly();
        tuple.ElementInfos = Array.AsReadOnly(infos.ToArray());
        tuple.MinLength = minimum;
        tuple.FixedLength = fixedLength;
        tuple.CombinedFlags = combined;
        tuple.IsReadonly = isReadonly;
        targets[key] = tuple;
        return tuple;
    }

    internal async ValueTask<Type> NormalizeReferenceAsync(InterfaceType target, IReadOnlyList<Type> elements, O objectFlags = 0,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(target);
        foreach (var type in elements)
            context.RequireOwned(type);
        if (target is not TupleType tuple || (tuple.CombinedFlags & E.NonRequired) == 0)
            return context.CreateTypeReference(target, elements.ToArray(), objectFlags);
        if (elements.Count < tuple.ElementInfos.Count)
            throw new ArgumentException("Tuple reference has too few type arguments");
        if ((tuple.CombinedFlags & E.Variadic) != 0)
            for (int i = 0; i < elements.Count; i++)
                if (i < tuple.ElementInfos.Count
                    && (tuple.ElementInfos[i].Flags & E.Variadic) != 0
                    && (elements[i].Flags & (F.Never | F.Union)) != 0)
                {
                    var product = elements.Select(
                        (t, index) => index < tuple.ElementInfos.Count && (tuple.ElementInfos[index].Flags & E.Variadic) != 0
                            ? t
                            : context.UnknownType).ToArray();
                    long size = TypeAlgebra.CrossProductSize(product);
                    if (size >= 100_000)
                        host.CrossProductTooLarge(size);
                    else
                    {
                        int index = i;
                        return await algebra.MapAsync(elements[i], async type =>
                        {
                            var replaced = elements.ToArray();
                            replaced[index] = type;
                            return await NormalizeReferenceAsync(target, replaced, objectFlags, cancellation).ConfigureAwait(false);
                        },
                            cancellation: cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("Tuple distribution removed all types");
                    }
                }
        var types = new List<Type>();
        var infos = new List<TupleElementInfo>();
        int lastRequired = -1, firstRest = -1, lastOptionalOrRest = -1;
        async ValueTask Add(Type type, TupleElementInfo info)
        {
            if ((info.Flags & E.Required) != 0)
                lastRequired = types.Count;
            if ((info.Flags & E.Rest) != 0 && firstRest < 0)
                firstRest = types.Count;
            if ((info.Flags & (E.Optional | E.Rest)) != 0)
                lastOptionalOrRest = types.Count;
            if (context.StrictNullChecks && (info.Flags & E.Optional) != 0)
            {
                var missing = context.UndefinedOrMissingType;
                if (type != missing && !(type is UnionType union && union.Types[0] == missing))
                    type = await algebra.UnionAsync([type, missing], cancellation: cancellation).ConfigureAwait(false);
            }
            types.Add(type);
            infos.Add(info);
        }
        for (int i = 0; i < tuple.ElementInfos.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var type = elements[i];
            var info = tuple.ElementInfos[i];
            if ((info.Flags & E.Variadic) == 0)
                await Add(type, info).ConfigureAwait(false);
            else if ((type.Flags & F.Any) != 0)
                await Add(type, info with { Flags = E.Rest }).ConfigureAwait(false);
            else if ((type.Flags & F.InstantiableNonPrimitive) != 0
                || await host.IsGenericMappedAsync(type, cancellation).ConfigureAwait(false))
                await Add(type, info).ConfigureAwait(false);
            else if (type is TypeReference { Target: TupleType spread } reference)
            {
                var spreadTypes = await ElementsAsync(reference, cancellation).ConfigureAwait(false);
                if ((long)spreadTypes.Count + types.Count >= 10_000)
                {
                    host.TupleTooLarge();
                    return context.ErrorType;
                }
                for (int j = 0; j < spreadTypes.Count; j++)
                    await Add(spreadTypes[j], spread.ElementInfos[j]).ConfigureAwait(false);
            }
            else
                await Add(await host.ArrayLikeElementAsync(type, cancellation).ConfigureAwait(false) ?? context.ErrorType,
                info with { Flags = E.Rest }).ConfigureAwait(false);
        }
        for (int i = 0; i < lastRequired; i++)
            if ((infos[i].Flags & E.Optional) != 0)
                infos[i] = infos[i] with { Flags = E.Required };
        if (firstRest >= 0 && firstRest < lastOptionalOrRest)
        {
            var rest = new List<Type>();
            for (int i = firstRest; i <= lastOptionalOrRest; i++)
                rest.Add((infos[i].Flags & E.Variadic) != 0
                    ? await host.IndexedAccessAsync(types[i], context.NumberType, cancellation).ConfigureAwait(false) : types[i]);
            types[firstRest] = await algebra.UnionAsync(rest, cancellation: cancellation).ConfigureAwait(false);
            types.RemoveRange(firstRest + 1, lastOptionalOrRest - firstRest);
            infos.RemoveRange(firstRest + 1, lastOptionalOrRest - firstRest);
        }
        if (elements.Count > tuple.ElementInfos.Count)
            types.Add(elements[tuple.ElementInfos.Count]);
        var normalizedTarget = await TargetAsync(infos, tuple.IsReadonly, cancellation).ConfigureAwait(false);
        if (normalizedTarget == context.EmptyGenericType)
            return context.EmptyObjectType;
        return types.Count == 0
            ? normalizedTarget
            : context.CreateTypeReference((InterfaceType)normalizedTarget, types.ToArray(), objectFlags);
    }

    internal async ValueTask<IReadOnlyList<Type>> ElementsAsync(TypeReference reference, CancellationToken cancellation = default)
    {
        var arguments = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
        var target = (InterfaceType)reference.ReferencedType;
        int arity = Math.Max(0, target.AllTypeParameters.Count - 1);
        return arguments.Count == arity ? arguments : Array.AsReadOnly(arguments.Take(arity).ToArray());
    }

    internal async ValueTask<Type?> SliceElementAsync(TypeReference reference, int start, int endSkip = 0, bool writing = false,
        bool noReductions = false, CancellationToken cancellation = default)
    {
        var tuple = (TupleType)reference.ReferencedType;
        int length = tuple.AllTypeParameters.Count - 1 - endSkip;
        if (start >= length)
            return null;
        var arguments = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
        var elements = new List<Type>();
        for (int i = start; i < length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var type = arguments[i];
            if ((tuple.ElementInfos[i].Flags & E.Variadic) != 0)
                type = await host.IndexedAccessAsync(type, context.NumberType, cancellation).ConfigureAwait(false);
            elements.Add(type);
        }
        return writing ? await algebra.IntersectionAsync(elements, cancellation: cancellation).ConfigureAwait(false)
            : await algebra.UnionAsync(
                elements,
                noReductions ? UnionReduction.None : UnionReduction.Literal,
                cancellation: cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> SliceAsync(TypeReference reference, int start, int endSkip = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(reference);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        var tuple = (TupleType)reference.ReferencedType;
        int end = tuple.ElementInfos.Count - Math.Max(endSkip, 0);
        if (start > tuple.FixedLength)
        {
            var rest = await SliceElementAsync(reference, tuple.FixedLength, cancellation: cancellation).ConfigureAwait(false);
            return rest is null ? await CreateAsync([], [], cancellation: cancellation).ConfigureAwait(false)
                : await ArrayAsync(rest, cancellation: cancellation).ConfigureAwait(false);
        }
        if (start >= end)
            return await CreateAsync([], [], cancellation: cancellation).ConfigureAwait(false);
        var arguments = await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false);
        return await CreateAsync(
            arguments.Skip(start).Take(end - start).ToArray(),
            tuple.ElementInfos.Skip(start).Take(end - start).ToArray(),
            cancellation: cancellation).ConfigureAwait(false);
    }

    private sealed class TupleKey : IEquatable<TupleKey>
    {
        private readonly TupleElementInfo[] infos;
        private readonly bool isReadonly;
        private readonly int hash;

        internal TupleKey(IReadOnlyList<TupleElementInfo> infos, bool isReadonly)
        {
            this.infos = infos.Select(
                i => i with
                {
                    Flags = (i.Flags & E.Required) != 0
                    ? E.Required
                    : (i.Flags & E.Optional) != 0 ? E.Optional : (i.Flags & E.Rest) != 0 ? E.Rest : E.Variadic
                }).ToArray();
            this.isReadonly = isReadonly;
            var builder = new HashCode();
            builder.Add(isReadonly);
            foreach (var info in this.infos)
            {
                builder.Add(info.Flags);
                builder.Add(info.LabeledDeclaration);
            }
            hash = builder.ToHashCode();
        }

        public bool Equals(TupleKey? other) =>
            other is not null && hash == other.hash && isReadonly == other.isReadonly && infos.AsSpan().SequenceEqual(other.infos);

        public override bool Equals(object? obj) => obj is TupleKey other && Equals(other);

        public override int GetHashCode() => hash;
    }
}
