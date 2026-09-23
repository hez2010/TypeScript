using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class TypeInference
{
    internal async ValueTask<Signature> ContextualSignatureAsync(Signature signature, Signature contextual, InferenceContext? outer = null,
        Func<Type, Type, CancellationToken, ValueTask<Ternary>>? compare = null, CancellationToken cancellation = default)
    {
        var inference = Create(
            await signatureInstantiation.ParametersAsync(signature, cancellation).ConfigureAwait(false),
            signature,
            compare: compare);
        var rest = await parameters.EffectiveRestAsync(contextual, cancellation).ConfigureAwait(false);
        var mapper = outer is null ? null : rest is TypeParameter ? outer.NonFixingMapper : outer.Mapper;
        var source = mapper is null
            ? contextual
            : await instantiation.SignatureAsync(contextual, mapper, false, cancellation).ConfigureAwait(false);
        await ApplyParametersAsync(
            source,
            signature,
            (s, t) => InferAsync(inference, s, t, cancellation: cancellation),
            cancellation).ConfigureAwait(false);
        if (outer is null)
            await ApplyReturnsAsync(
                contextual,
                signature,
                (s, t) => InferAsync(inference, s, t, InferencePriority.ReturnType, cancellation: cancellation),
                cancellation).ConfigureAwait(false);
        return await signatureInstantiation.GetAsync(signature, await GetAllAsync(inference, cancellation).ConfigureAwait(false),
            ((contextual.Declaration?.Flags ?? 0) & NodeFlags.JavaScriptFile) != 0, cancellation: cancellation).ConfigureAwait(false);
    }
}
