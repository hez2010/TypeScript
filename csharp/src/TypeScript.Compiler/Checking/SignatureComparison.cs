using System.Runtime.CompilerServices;

namespace TypeScript.Compiler.Checking;

internal interface ISignatureComparisonHost
{
    ValueTask<Ternary> CompareTypesAsync(Type source, Type target, bool subtype, CancellationToken cancellation);
}

internal sealed class SignatureComparison(TypeContext context, SignatureParameters parameters, Signatures signatures,
    TypeConstraints constraints, TypeInstantiation instantiation, ISignatureComparisonHost host)
{
    internal async ValueTask<Ternary> CompareAsync(Signature source, Signature target, bool partial = false,
        bool ignoreThis = false, bool ignoreReturn = false, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        RequireOwned(source);
        RequireOwned(target);
        cancellation.ThrowIfCancellationRequested();
        if (source == target)
            return Ternary.True;
        if (!await MatchingAsync(source, target, partial, cancellation).ConfigureAwait(false)
            || source.TypeParameters.Count != target.TypeParameters.Count)
            return Ternary.False;
        if (target.TypeParameters.Count != 0)
        {
            var mapper = TypeMapper.Create(source.TypeParameters.ToArray(), target.TypeParameters.ToArray());
            for (int i = 0; i < target.TypeParameters.Count; i++)
            {
                var s = source.TypeParameters[i];
                var t = target.TypeParameters[i];
                if (s == t)
                    continue;
                var sourceConstraint = await InstantiateAsync(
                    await ConstraintAsync(s, cancellation).ConfigureAwait(false),
                    mapper,
                    cancellation).ConfigureAwait(false);
                if (await host.CompareTypesAsync(
                    sourceConstraint,
                    await ConstraintAsync(t, cancellation).ConfigureAwait(false),
                    partial,
                    cancellation).ConfigureAwait(false) == Ternary.False)
                    return Ternary.False;
                var sourceDefault = await InstantiateAsync(
                    await constraints.DefaultAsync(s, cancellation).ConfigureAwait(false) ?? context.UnknownType,
                    mapper,
                    cancellation).ConfigureAwait(false);
                if (await host.CompareTypesAsync(
                    sourceDefault,
                    await constraints.DefaultAsync(t, cancellation).ConfigureAwait(false) ?? context.UnknownType,
                    partial,
                    cancellation).ConfigureAwait(false) == Ternary.False)
                    return Ternary.False;
            }
            source = await instantiation.SignatureAsync(source, mapper, true, cancellation).ConfigureAwait(false);
        }
        Ternary result = Ternary.True;
        if (!ignoreThis && await parameters.ThisAsync(source, cancellation).ConfigureAwait(false) is { } sourceThis)
        {
            if (await parameters.ThisAsync(target, cancellation).ConfigureAwait(false) is { } targetThis)
            {
                var related = await host.CompareTypesAsync(sourceThis, targetThis, partial, cancellation).ConfigureAwait(false);
                if (related == Ternary.False)
                    return Ternary.False;
                result &= related;
            }
        }
        int count = await parameters.CountAsync(target, cancellation).ConfigureAwait(false);
        for (int i = 0; i < count; i++)
        {
            var s = await parameters.AtAsync(source, i, cancellation).ConfigureAwait(false);
            var t = await parameters.AtAsync(target, i, cancellation).ConfigureAwait(false);
            var related = await host.CompareTypesAsync(t, s, partial, cancellation).ConfigureAwait(false);
            if (related == Ternary.False)
                return Ternary.False;
            result &= related;
        }
        if (!ignoreReturn)
        {
            var sourcePredicate = await signatures.PredicateAsync(source, cancellation).ConfigureAwait(false);
            var targetPredicate = await signatures.PredicateAsync(target, cancellation).ConfigureAwait(false);
            if (sourcePredicate is not null || targetPredicate is not null)
                result &= await PredicateAsync(sourcePredicate, targetPredicate, partial, cancellation).ConfigureAwait(false);
            else
                result &= await host.CompareTypesAsync(await signatures.ReturnAsync(source, cancellation).ConfigureAwait(false),
                await signatures.ReturnAsync(target, cancellation).ConfigureAwait(false), partial, cancellation).ConfigureAwait(false);
        }
        return result;
    }

    internal async ValueTask<bool> MatchingAsync(Signature source, Signature target, bool partial, CancellationToken cancellation = default)
    {
        int sourceCount = await parameters.CountAsync(source, cancellation).ConfigureAwait(false);
        int targetCount = await parameters.CountAsync(target, cancellation).ConfigureAwait(false);
        int sourceMinimum = await parameters.MinimumAsync(source, cancellation: cancellation).ConfigureAwait(false);
        int targetMinimum = await parameters.MinimumAsync(target, cancellation: cancellation).ConfigureAwait(false);
        bool sourceRest = await parameters.HasRestAsync(source, cancellation).ConfigureAwait(false);
        bool targetRest = await parameters.HasRestAsync(target, cancellation).ConfigureAwait(false);
        return sourceCount == targetCount && sourceMinimum == targetMinimum && sourceRest == targetRest
            || partial && sourceMinimum <= targetMinimum;
    }

    internal async ValueTask<Signature?> FindAsync(IReadOnlyList<Signature> signatures, Signature signature, bool partial = false,
        bool ignoreThis = false, bool ignoreReturn = false, CancellationToken cancellation = default)
    {
        foreach (var current in signatures)
            if (await CompareAsync(
                current,
                signature,
                partial,
                ignoreThis,
                ignoreReturn,
                cancellation).ConfigureAwait(false) != Ternary.False)
                return current;
        return null;
    }

    internal async ValueTask<IReadOnlyList<Signature>?> FindAsync(IReadOnlyList<IReadOnlyList<Signature>> lists, Signature signature,
        int listIndex, CancellationToken cancellation = default)
    {
        if (signature.TypeParameters.Count != 0)
        {
            if (listIndex > 0)
                return null;
            for (int i = 1; i < lists.Count; i++)
                if (await FindAsync(lists[i], signature, cancellation: cancellation).ConfigureAwait(false) is null)
                    return null;
            return [signature];
        }
        var result = new List<Signature>();
        for (int i = 0; i < lists.Count; i++)
        {
            var match = i == listIndex ? signature : await FindAsync(
                lists[i],
                signature,
                ignoreReturn: true,
                cancellation: cancellation).ConfigureAwait(false)
                ?? await FindAsync(
                    lists[i],
                    signature,
                    partial: true,
                    ignoreReturn: true,
                    cancellation: cancellation).ConfigureAwait(false);
            if (match is null)
                return null;
            if (!result.Contains(match))
                result.Add(match);
        }
        return result.AsReadOnly();
    }

    internal async ValueTask<bool> TypeParametersAsync(IReadOnlyList<TypeParameter> source, IReadOnlyList<TypeParameter> target,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        foreach (var parameter in source)
            context.RequireOwned(parameter);
        foreach (var parameter in target)
            context.RequireOwned(parameter);
        if (source.Count != target.Count)
            return false;
        var mapper = TypeMapper.Create(target.ToArray(), source.ToArray());
        for (int i = 0; i < source.Count; i++)
        {
            if (source[i] == target[i])
                continue;
            var sourceConstraint = await ConstraintAsync(source[i], cancellation).ConfigureAwait(false);
            var targetConstraint = await InstantiateAsync(
                await ConstraintAsync(target[i], cancellation).ConfigureAwait(false),
                mapper,
                cancellation).ConfigureAwait(false);
            if (await host.CompareTypesAsync(
                sourceConstraint,
                targetConstraint,
                false,
                cancellation).ConfigureAwait(false) == Ternary.False)
                return false;
        }
        return true;
    }

    internal async ValueTask<Ternary> PredicateAsync(TypePredicate? source, TypePredicate? target, bool subtype = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (source?.Type is { } sourceType)
            context.RequireOwned(sourceType);
        if (target?.Type is { } targetType)
            context.RequireOwned(targetType);
        if (source is null || target is null || source.Kind != target.Kind || source.ParameterIndex != target.ParameterIndex)
            return Ternary.False;
        if (source.Type == target.Type)
            return Ternary.True;
        return source.Type is not null && target.Type is not null
            ? await host.CompareTypesAsync(source.Type, target.Type, subtype, cancellation).ConfigureAwait(false) : Ternary.False;
    }

    private async ValueTask<Type> ConstraintAsync(TypeParameter parameter, CancellationToken cancellation)
        => await constraints.ParameterConstraintWorkerAsync(parameter, cancellation).ConfigureAwait(false) ?? context.UnknownType;

    private async ValueTask<Type> InstantiateAsync(Type type, TypeMapper mapper, CancellationToken cancellation)
            => await instantiation.InstantiateAsync(type, mapper, cancellation: cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Signature comparison instantiation returned no type");

    private void RequireOwned(Signature signature)
    {
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
    }
}
