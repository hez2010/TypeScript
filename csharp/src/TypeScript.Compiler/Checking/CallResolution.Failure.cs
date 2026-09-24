using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class CallResolution
{
    private async ValueTask<Signature> FailureAsync(State state, bool candidatesRequested, CheckMode mode, CancellationToken cancellation)
    {
        host.DeferExpression(state.Node);
        if (candidatesRequested || state.Candidates.Count == 1 || state.Candidates.Any(s => s.TypeParameters.Count != 0))
        {
            int best = -1, maximum = -1;
            for (int i = 0; i < state.Candidates.Count; i++)
            {
                int count = await parameters.CountAsync(state.Candidates[i], cancellation).ConfigureAwait(false);
                if (await parameters.HasRestAsync(state.Candidates[i], cancellation).ConfigureAwait(false)
                    || count >= state.Arguments.Count)
                {
                    best = i;
                    break;
                }
                if (count > maximum)
                {
                    best = i;
                    maximum = count;
                }
            }
            var candidate = state.Candidates[best];
            if (candidate.TypeParameters.Count == 0)
                return candidate;
            IReadOnlyList<Type> types;
            if (state.TypeArguments.Count != 0)
            {
                var result = new List<Type>();
                foreach (var node in state.TypeArguments.Take(candidate.TypeParameters.Count))
                    result.Add(await host.CheckedFunctionTypeAsync(node, cancellation).ConfigureAwait(false));
                while (result.Count < candidate.TypeParameters.Count)
                    result.Add(await constraints.DefaultAsync(candidate.TypeParameters[result.Count], cancellation).ConfigureAwait(false)
                        ?? await constraints.ConstraintAsync(
                            candidate.TypeParameters[result.Count],
                            cancellation).ConfigureAwait(false) ?? context.UnknownType);
                types = result;
            }
            else
            {
                var inferred = inference.Create(candidate.TypeParameters, candidate,
                    (state.Node.Flags & NodeFlags.JavaScriptFile) != 0 ? InferenceFlags.AnyDefault : 0);
                types = await callInference.InferAsync(state.Node, candidate, state.Arguments,
                    mode | CheckMode.SkipContextSensitive | CheckMode.SkipGenericFunctions, inferred, cancellation).ConfigureAwait(false);
            }
            return state.Candidates[best] = await instantiation.WithoutFillingAsync(candidate, types, cancellation).ConfigureAwait(false);
        }
        var candidates = state.Candidates;
        Symbol? receiver = null;
        var receivers = candidates.Where(s => s.ThisParameter is not null).Select(s => s.ThisParameter!).ToArray();
        if (receivers.Length != 0)
        {
            var types = new List<Type>();
            foreach (var symbol in receivers)
                types.Add(await parameters.ParameterAsync(symbol, cancellation).ConfigureAwait(false));
            receiver = widening.WithType(
                receivers[0],
                await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false));
        }
        int minimum = candidates.Min(s => s.Parameters.Count - (s.HasRestParameter ? 1 : 0));
        int maximumCount = candidates.Max(s => s.Parameters.Count - (s.HasRestParameter ? 1 : 0));
        var combined = new List<Symbol>();
        for (int i = 0; i < maximumCount; i++)
        {
            var sources = candidates.Select(s => s.HasRestParameter ? i < s.Parameters.Count - 1 ? s.Parameters[i] : s.Parameters[^1]
                : i < s.Parameters.Count ? s.Parameters[i] : null).Where(p => p is not null).ToArray();
            var types = new List<Type>();
            foreach (var signature in candidates)
                if (await parameters.TryAtAsync(signature, i, cancellation).ConfigureAwait(false) is { } type)
                    types.Add(type);
            combined.Add(
                widening.WithType(
                    sources[0]!,
                    await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false)));
        }
        var restSymbols = candidates.Where(s => s.HasRestParameter).Select(s => s.Parameters[^1]).ToArray();
        var flags = SignatureFlags.IsSignatureCandidateForOverloadFailure;
        if (restSymbols.Length != 0)
        {
            var types = new List<Type>();
            foreach (var symbol in restSymbols)
            {
                Type? rest = await values.GetAsync(symbol, cancellation).ConfigureAwait(false);
                if (rest is TypeReference { Target: TupleType tuple } reference)
                    rest = await tuples.SliceElementAsync(reference, tuple.FixedLength, cancellation: cancellation).ConfigureAwait(false);
                if (rest is not null && await host.NumberIndexAsync(rest, cancellation).ConfigureAwait(false) is { } element)
                    types.Add(element);
            }
            var type = await tuples.ArrayAsync(
                await algebra.UnionAsync(types, UnionReduction.Subtype, cancellation: cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false);
            combined.Add(widening.WithType(restSymbols[0], type));
            flags |= SignatureFlags.HasRestParameter;
        }
        if (candidates.Any(s => (s.Flags & SignatureFlags.HasLiteralTypes) != 0))
            flags |= SignatureFlags.HasLiteralTypes;
        var returns = new List<Type>();
        foreach (var signature in candidates)
            returns.Add(await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false));
        return context.NewSignature(flags, candidates[0].Declaration, [], receiver, combined.ToArray(),
            await algebra.IntersectionAsync(returns, cancellation: cancellation).ConfigureAwait(false), null, minimum);
    }
}
