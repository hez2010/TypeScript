using TypeScript.Compiler.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal interface ISignatureCompositionHost
{
    Type ArrayTarget(bool isReadonly);

    ValueTask<Symbol?> ResolveSymbolAsync(Symbol symbol, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);
}

internal sealed class SignatureComposition(TypeContext context, CheckerLinks links, TypeAlgebra algebra, SymbolTypes values,
    TypeProperties properties, TupleTypes tuples, TypeInstantiation instantiation, SignatureParameters parameters,
    SignatureComparison comparison, ISignatureCompositionHost host)
{
    internal async ValueTask<IReadOnlyList<Signature>> UnionAsync(
        IReadOnlyList<IReadOnlyList<Signature>> lists,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        var allocated = new List<Symbol>();
        try
        {
            return await UnionCoreAsync(lists, allocated, cancellation).ConfigureAwait(false);
        }
        catch
        {
            Release(allocated);
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<Signature>> UnionCoreAsync(IReadOnlyList<IReadOnlyList<Signature>> lists, List<Symbol> allocated,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (lists.Count == 0)
            throw new ArgumentException("Union has no signature lists", nameof(lists));
        foreach (var list in lists)
            foreach (var signature in list)
                if (signature.Context != context)
                    throw new ArgumentException("Signature belongs to another checker", nameof(lists));
        var result = new List<Signature>();
        int overloadedIndex = 0, overloadedCount = 0;
        for (int i = 0; i < lists.Count; i++)
        {
            if (lists[i].Count == 0)
                return [];
            if (lists[i].Count > 1)
            {
                overloadedIndex = i;
                overloadedCount++;
            }
            foreach (var signature in lists[i])
            {
                if (await comparison.FindAsync(
                    result,
                    signature,
                    ignoreReturn: true,
                    cancellation: cancellation).ConfigureAwait(false) is not null)
                    continue;
                var matching = await comparison.FindAsync(lists, signature, i, cancellation).ConfigureAwait(false);
                if (matching is null)
                    continue;
                var combined = signature;
                if (matching.Count > 1)
                {
                    var receiver = signature.ThisParameter;
                    var first = matching.Select(s => s.ThisParameter).FirstOrDefault(p => p is not null);
                    if (first is not null)
                    {
                        var types = new List<Type>();
                        foreach (var item in matching)
                            if (item.ThisParameter is { } p)
                                types.Add(await values.GetAsync(p, cancellation).ConfigureAwait(false));
                        receiver = properties.Clone(
                            first,
                            await algebra.IntersectionAsync(types, cancellation: cancellation).ConfigureAwait(false));
                        allocated.Add(receiver);
                    }
                    combined = context.CloneSignature(signature);
                    combined.Composite = new(true, matching);
                    combined.Target = null;
                    combined.Mapper = null;
                    combined.ThisParameter = receiver;
                }
                result.Add(combined);
            }
        }
        if (result.Count == 0 && overloadedCount <= 1)
        {
            var master = lists[overloadedIndex];
            result.AddRange(master);
            foreach (var list in lists)
            {
                if (ReferenceEquals(list, master))
                    continue;
                var signature = list[0];
                bool compatible = true;
                if (signature.TypeParameters.Count != 0)
                    foreach (var candidate in result)
                        if (candidate.TypeParameters.Count != 0
                            && !await comparison.TypeParametersAsync(
                                signature.TypeParameters,
                                candidate.TypeParameters,
                                cancellation).ConfigureAwait(false))
                        {
                            compatible = false;
                            break;
                        }
                if (!compatible)
                {
                    result.Clear();
                    Release(allocated);
                    allocated.Clear();
                    break;
                }
                for (int i = 0; i < result.Count; i++)
                    result[i] = await CombineCoreAsync(result[i], signature, true, allocated, cancellation).ConfigureAwait(false);
            }
        }
        cancellation.ThrowIfCancellationRequested();
        return result.AsReadOnly();
    }

    internal ValueTask<Signature> CombineAsync(Signature left, Signature right, bool isUnion, CancellationToken cancellation = default)
        => CombineCoreAsync(left, right, isUnion, null, cancellation);

    private async ValueTask<Signature> CombineCoreAsync(Signature left, Signature right, bool isUnion, List<Symbol>? parentAllocations,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        if (left.Context != context || right.Context != context)
            throw new ArgumentException("Signature belongs to another checker");
        cancellation.ThrowIfCancellationRequested();
        var typeParameters = left.TypeParameters.Count != 0 ? left.TypeParameters : right.TypeParameters;
        var mapper = left.TypeParameters.Count != 0 && right.TypeParameters.Count != 0
            ? TypeMapper.Create(right.TypeParameters.ToArray(), left.TypeParameters.ToArray()) : null;
        var flags = (left.Flags | right.Flags) & (SignatureFlags.PropagatingFlags & ~SignatureFlags.HasRestParameter);
        var allocated = new List<Symbol>();
        try
        {
            var combined = await ParametersAsync(left, right, mapper, isUnion, allocated, cancellation).ConfigureAwait(false);
            if (combined.Count > 0 && (combined[^1].CheckFlags & CheckFlags.RestParameter) != 0)
                flags |= SignatureFlags.HasRestParameter;
            var receiver = left.ThisParameter ?? right.ThisParameter;
            if (left.ThisParameter is { } l && right.ThisParameter is { } r)
            {
                var leftType = await values.GetAsync(l, cancellation).ConfigureAwait(false);
                var rightType = await InstantiateAsync(
                    await values.GetAsync(r, cancellation).ConfigureAwait(false),
                    mapper,
                    cancellation).ConfigureAwait(false);
                var type = isUnion ? await algebra.IntersectionAsync(
                    [leftType, rightType],
                    cancellation: cancellation).ConfigureAwait(false)
                    : await algebra.UnionAsync([leftType, rightType], cancellation: cancellation).ConfigureAwait(false);
                receiver = properties.Clone(l, type);
                allocated.Add(receiver);
            }
            cancellation.ThrowIfCancellationRequested();
            var result = context.NewSignature(flags, left.Declaration, typeParameters.ToArray(), receiver, combined.ToArray(), null, null,
                Math.Max(left.MinArgumentCount, right.MinArgumentCount));
            var parts = left.Composite is { IsUnion: true } composite ? composite.Signatures : [left];
            result.Composite = new(isUnion, Array.AsReadOnly<Signature>([.. parts, right]));
            if (mapper is not null)
                result.Mapper = left.Composite?.IsUnion == isUnion && left.Mapper is not null
                    ? instantiation.Combine(left.Mapper, mapper)
                    : mapper;
            else if (left.Composite?.IsUnion == isUnion)
                result.Mapper = left.Mapper;
            parentAllocations?.AddRange(allocated);
            return result;
        }
        catch
        {
            Release(allocated);
            throw;
        }
    }

    private async ValueTask<IReadOnlyList<Symbol>> ParametersAsync(Signature left, Signature right, TypeMapper? mapper, bool isUnion,
        List<Symbol> allocated, CancellationToken cancellation)
    {
        int leftCount = await parameters.CountAsync(left, cancellation).ConfigureAwait(false);
        int rightCount = await parameters.CountAsync(right, cancellation).ConfigureAwait(false);
        var longest = leftCount >= rightCount ? left : right;
        var shorter = longest == left ? right : left;
        int count = Math.Max(leftCount, rightCount);
        bool hasRest = await parameters.HasRestAsync(left, cancellation).ConfigureAwait(false)
            || await parameters.HasRestAsync(right, cancellation).ConfigureAwait(false);
        bool extraRest = hasRest && !await parameters.HasRestAsync(longest, cancellation).ConfigureAwait(false);
        var result = new Symbol[count + (extraRest ? 1 : 0)];
        for (int i = 0; i < count; i++)
        {
            var longestType = await parameters.TryAtAsync(
                longest,
                i,
                cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("Signature parameter is absent");
            if (longest == right)
                longestType = await InstantiateAsync(longestType, mapper, cancellation).ConfigureAwait(false);
            var shorterType = await parameters.TryAtAsync(shorter, i, cancellation).ConfigureAwait(false) ?? context.UnknownType;
            if (shorter == right)
                shorterType = await InstantiateAsync(shorterType, mapper, cancellation).ConfigureAwait(false);
            var type = isUnion ? await algebra.IntersectionAsync(
                [longestType, shorterType],
                cancellation: cancellation).ConfigureAwait(false)
                : await algebra.UnionAsync([longestType, shorterType], cancellation: cancellation).ConfigureAwait(false);
            bool rest = hasRest && !extraRest && i == count - 1;
            bool optional = i >= await parameters.MinimumAsync(longest, cancellation: cancellation).ConfigureAwait(false)
                && i >= await parameters.MinimumAsync(shorter, cancellation: cancellation).ConfigureAwait(false);
            TextSlice leftName = i < leftCount ? await parameters.NameAsync(left, i, cancellation).ConfigureAwait(false) : "";
            TextSlice rightName = i < rightCount ? await parameters.NameAsync(right, i, cancellation).ConfigureAwait(false) : "";
            TextSlice name = leftName == rightName || rightName.Length == 0 ? leftName : leftName.Length == 0 ? rightName : "";
            if (name.Length == 0)
                name = TextSlice.Concat("arg", TextSlice.Format(i));
            var symbol = new Symbol(
                SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient | (optional && !rest ? SymbolFlags.Optional : 0),
                name)
            { CheckFlags = rest ? CheckFlags.RestParameter : optional ? CheckFlags.OptionalParameter : 0 };
            allocated.Add(symbol);
            links.Values.Get(symbol).ResolvedType = rest
                ? await tuples.ArrayAsync(type, cancellation: cancellation).ConfigureAwait(false)
                : type;
            result[i] = symbol;
        }
        if (extraRest)
        {
            var symbol = new Symbol(
                SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient,
                "args")
            { CheckFlags = CheckFlags.RestParameter };
            allocated.Add(symbol);
            var type = await tuples.ArrayAsync(
                await parameters.AtAsync(shorter, count, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false);
            links.Values.Get(symbol).ResolvedType = type;
            if (shorter == right)
                links.Values.Get(symbol).ResolvedType = await InstantiateAsync(type, mapper, cancellation).ConfigureAwait(false);
            result[count] = symbol;
        }
        return Array.AsReadOnly(result);
    }

    internal async ValueTask<IReadOnlyList<Signature>> ArrayMembersAsync(UnionType type, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        var array = host.ArrayTarget(false);
        var readOnlyArray = host.ArrayTarget(true);
        if (array.Symbol is null || readOnlyArray.Symbol is null)
            return [];
        TextSlice? name = null;
        for (int i = 0; i < type.Types.Count; i++)
        {
            var part = type.Types[i];
            if ((part.ObjectFlags & ObjectFlags.Instantiated) == 0 || part.Symbol?.Parent is not { } parent
                || !await SameSymbolAsync(parent, array.Symbol, cancellation).ConfigureAwait(false)
                    && !await SameSymbolAsync(parent, readOnlyArray.Symbol, cancellation).ConfigureAwait(false))
                return [];
            if (i == 0)
                name = part.Symbol.Name;
            else if (name != part.Symbol.Name)
                return [];
        }
        var argument = await algebra.MapAsync(type, async part =>
        {
            var target = await SameSymbolAsync(part.Symbol!.Parent!, readOnlyArray.Symbol, cancellation).ConfigureAwait(false)
                ? readOnlyArray
                : array;
            var parameter = ((InterfaceType)target).AllTypeParameters[0];
            return await ((ObjectType)part).Mapper!.MapAsync(parameter, cancellation).ConfigureAwait(false);
        }, cancellation: cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("Array member arguments are absent");
        bool isReadonly = false;
        foreach (var part in type.Types)
            if (await SameSymbolAsync(part.Symbol!.Parent!, readOnlyArray.Symbol, cancellation).ConfigureAwait(false))
            {
                isReadonly = true;
                break;
            }
        var arrayType = await tuples.ArrayAsync(argument, isReadonly, cancellation).ConfigureAwait(false);
        var property = await properties.PropertyAsync(arrayType, name!.Value, cancellation: cancellation).ConfigureAwait(false);
        if (property is null)
            throw new InvalidOperationException("Array member is absent");
        return await host.SignaturesAsync(
            await values.GetAsync(property, cancellation).ConfigureAwait(false),
            false,
            cancellation).ConfigureAwait(false);
    }

    private async ValueTask<bool> SameSymbolAsync(Symbol left, Symbol right, CancellationToken cancellation)
        =>
            await host.ResolveSymbolAsync(
                left,
                cancellation).ConfigureAwait(false) == await host.ResolveSymbolAsync(right, cancellation).ConfigureAwait(false);

    private async ValueTask<Type> InstantiateAsync(Type type, TypeMapper? mapper, CancellationToken cancellation)
            => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Signature composition instantiation returned no type");

    private void Release(IReadOnlyList<Symbol> symbols)
    {
        foreach (var symbol in symbols)
            links.Values.Remove(symbol);
    }
}
