using System.Globalization;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed class GenericExpressions(TypeContext context, StructuredMembers members, Signatures signatures,
    SignatureInstantiation signatureInstantiation, ExpressionContexts contexts, TypeFactQueries facts, TypeInference inference)
{
    internal async ValueTask<Signature?> SingleAsync(Type type, bool construct = false, bool allowMembers = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is not ObjectType obj)
            return null;
        var resolved = await members.ResolveAsync(obj, cancellation).ConfigureAwait(false);
        if (!allowMembers && ((resolved.Properties?.Count ?? 0) != 0 || resolved.IndexInfos.Count != 0))
            return null;
        var candidates = construct ? resolved.ConstructSignatures : resolved.CallSignatures;
        var opposite = construct ? resolved.CallSignatures : resolved.ConstructSignatures;
        return candidates.Count == 1 && opposite.Count == 0 ? candidates[0] : null;
    }

    internal async ValueTask<Type> FinishAsync(SyntaxNode node, Type type, CheckMode mode, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if ((mode & (CheckMode.Inferential | CheckMode.SkipGenericFunctions)) == 0)
            return type;
        var call = await SingleAsync(type, false, true, cancellation).ConfigureAwait(false);
        var construct = await SingleAsync(type, true, true, cancellation).ConfigureAwait(false);
        var signature = call ?? construct;
        if (signature is null || signature.TypeParameters.Count == 0)
            return type;
        var contextualType = await contexts.ApparentAsync(node, ContextFlags.NoConstraints, cancellation).ConfigureAwait(false);
        if (contextualType is null)
            return type;
        var contextual = await SingleAsync(
            await facts.NonNullableAsync(contextualType, cancellation).ConfigureAwait(false),
            call is null,
            false,
            cancellation).ConfigureAwait(false);
        if (contextual is null || contextual.TypeParameters.Count != 0)
            return type;
        var outer = contexts.InferenceFor(node);
        if ((mode & CheckMode.SkipGenericFunctions) != 0)
        {
            if ((mode & CheckMode.Inferential) != 0)
                outer!.Flags |= InferenceFlags.SkippedGenericFunction;
            return context.AnyFunctionType;
        }
        if (outer is null)
            throw new InvalidOperationException("Inferential generic expression has no inference context");
        return await outer.RunAsync(async () =>
        {
            Signature? returned = null;
            if (outer.Signature is { } outerSignature)
            {
                var returnType = await signatures.ReturnAsync(outerSignature, cancellation).ConfigureAwait(false);
                returned = await SingleAsync(returnType, cancellation: cancellation).ConfigureAwait(false)
                    ?? await SingleAsync(returnType, true, cancellation: cancellation).ConfigureAwait(false);
            }
            if (returned is { TypeParameters.Count: 0 } && !outer.Inferences.All(i => i.HasCandidates))
            {
                var unique = Unique(outer, signature.TypeParameters);
                var instantiated = await signatureInstantiation.WithoutFillingAsync(signature, unique, cancellation).ConfigureAwait(false);
                var collected = inference.Create(outer.Inferences.Select(i => i.Parameter).ToArray());
                await inference.ApplyParametersAsync(instantiated, contextual,
                    (source, target) => inference.InferAsync(collected, source, target, contravariant: true, cancellation: cancellation),
                    cancellation).ConfigureAwait(false);
                if (collected.Inferences.Any(i => i.HasCandidates))
                {
                    await inference.ApplyReturnsAsync(instantiated, contextual,
                        (source, target) => inference.InferAsync(collected, source, target, cancellation: cancellation),
                        cancellation).ConfigureAwait(false);
                    if (!outer.Inferences.Where((info, i) => info.HasCandidates && collected.Inferences[i].HasCandidates).Any())
                    {
                        for (int i = 0; i < outer.Inferences.Length; i++)
                            if (!outer.Inferences[i].HasCandidates && collected.Inferences[i].HasCandidates)
                                outer.Inferences[i] = collected.Inferences[i];
                        outer.InferredTypeParameters = [.. outer.InferredTypeParameters ?? [], .. unique];
                        return (Type)signatureInstantiation.FromSignature(instantiated);
                    }
                }
            }
            return signatureInstantiation.FromSignature(
                await inference.ContextualSignatureAsync(signature, contextual, outer, cancellation: cancellation).ConfigureAwait(false));
        }, cancellation).ConfigureAwait(false);
    }

    private IReadOnlyList<Type> Unique(InferenceContext outer, IReadOnlyList<TypeParameter> parameters)
    {
        var used = new HashSet<string>((outer.InferredTypeParameters ?? []).Select(t => t.Symbol!.Name), StringComparer.Ordinal);
        var result = new List<Type>();
        var oldTypes = new List<Type>();
        var newTypes = new List<TypeParameter>();
        foreach (var parameter in parameters)
        {
            string name = parameter.Symbol!.Name;
            if (used.Add(name))
                result.Add(parameter);
            else
            {
                string stem = name;
                while (stem.Length > 1 && stem[^1] is >= '0' and <= '9')
                    stem = stem[..^1];
                int index = 1;
                while (!used.Add(name = stem + index.ToString(CultureInfo.InvariantCulture)))
                    index++;
                var replacement = context.NewTypeParameter(new Symbol(SymbolFlags.TypeParameter | SymbolFlags.Transient, name));
                replacement.Target = parameter;
                oldTypes.Add(parameter);
                newTypes.Add(replacement);
                result.Add(replacement);
            }
        }
        if (newTypes.Count != 0)
        {
            var mapper = TypeMapper.Create(oldTypes.ToArray(), newTypes.Cast<Type>().ToArray());
            foreach (var parameter in newTypes)
                parameter.Mapper = mapper;
        }
        return result;
    }
}
