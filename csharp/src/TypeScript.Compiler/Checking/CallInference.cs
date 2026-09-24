using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class CallInference(TypeInference inference, TypeConstraints constraints,
    TypeInstantiation instantiation, Signatures signatures, SignatureInstantiation signatureInstantiation,
    SignatureParameters parameters, TypeVariables variables, ExpressionContexts expressions, GenericExpressions generic,
    CallSignatures rules, CallArguments arguments)
{
    internal ValueTask<IReadOnlyList<Type>> InferAsync(SyntaxNode node, Signature signature, IReadOnlyList<SyntaxNode> args,
        CheckMode mode, InferenceContext target, CancellationToken cancellation = default) => target.RunAsync(async () =>
    {
        if (node is JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode)
            return await arguments.InferJsxAsync(node, signature, mode, target, cancellation).ConfigureAwait(false);
        if (node is not DecoratorNode and not BinaryExpressionNode)
        {
            bool skipPatterns = true;
            foreach (var parameter in signature.TypeParameters)
                if (await constraints.DefaultAsync(parameter, cancellation).ConfigureAwait(false) is null)
                {
                    skipPatterns = false;
                    break;
                }
            var contextual = await expressions.GetAsync(
                node,
                skipPatterns ? ContextFlags.SkipBindingPatterns : 0,
                cancellation).ConfigureAwait(false);
            if (contextual is not null)
            {
                var returnType = await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false);
                if (await variables.CouldContainAsync(returnType, cancellation).ConfigureAwait(false))
                {
                    var outer = expressions.InferenceFor(node);
                    bool fromPattern = !skipPatterns
                        && await expressions.GetAsync(
                            node,
                            ContextFlags.SkipBindingPatterns,
                            cancellation).ConfigureAwait(false) != contextual;
                    if (!fromPattern)
                    {
                        var mapper = outer is null ? null : inference.Clone(outer, InferenceFlags.NoDefault).Mapper;
                        var instantiated = (await instantiation.InstantiateAsync(
                            contextual,
                            mapper,
                            cancellation: cancellation).ConfigureAwait(false))!;
                        var contextualSignature = await generic.SingleAsync(instantiated, cancellation: cancellation).ConfigureAwait(false);
                        var source = contextualSignature is { TypeParameters.Count: > 0 }
                            ? signatureInstantiation.FromSignature(await signatureInstantiation.WithoutFillingAsync(contextualSignature,
                                contextualSignature.TypeParameters, cancellation).ConfigureAwait(false)) : instantiated;
                        await inference.InferAsync(
                            target,
                            source,
                            returnType,
                            InferencePriority.ReturnType,
                            cancellation: cancellation).ConfigureAwait(false);
                    }
                    var returnContext = inference.Create(signature.TypeParameters, signature, target.Flags);
                    TypeMapper? returnMapper = null;
                    if (outer is not null)
                    {
                        if (outer.OuterReturnMapper is null)
                        {
                            var mapper = inference.Clone(outer).Mapper;
                            outer.OuterReturnMapper = outer.ReturnMapper is { } first ? TypeMapper.Merge(first, mapper) : mapper;
                        }
                        returnMapper = outer.OuterReturnMapper;
                    }
                    await inference.InferAsync(
                        returnContext,
                        (await instantiation.InstantiateAsync(contextual, returnMapper, cancellation: cancellation).ConfigureAwait(false))!,
                        returnType, cancellation: cancellation).ConfigureAwait(false);
                    target.ReturnMapper = inference.CloneInferred(returnContext)?.Mapper;
                }
            }
        }
        var rest = await rules.NonArrayRestAsync(signature, cancellation).ConfigureAwait(false);
        int count = rest is null
            ? args.Count
            : Math.Min(await parameters.CountAsync(signature, cancellation).ConfigureAwait(false) - 1, args.Count);
        if (rest is TypeParameter)
        {
            var info = target.Inferences.FirstOrDefault(i => i.Parameter == rest);
            if (info is not null && !args.Skip(count).Any(CallArguments.Spread))
                info.ImpliedArity = args.Count - count;
        }
        var receiver = await parameters.ThisAsync(signature, cancellation).ConfigureAwait(false);
        if (receiver is not null && await variables.CouldContainAsync(receiver, cancellation).ConfigureAwait(false))
            await inference.InferAsync(
                target,
                await arguments.ThisTypeAsync(arguments.ThisNode(node), cancellation).ConfigureAwait(false),
                receiver,
                cancellation: cancellation).ConfigureAwait(false);
        for (int i = 0; i < count; i++)
        {
            if (args[i].Kind == SyntaxKind.OmittedExpression)
                continue;
            var parameter = await parameters.AtAsync(signature, i, cancellation).ConfigureAwait(false);
            if (await variables.CouldContainAsync(parameter, cancellation).ConfigureAwait(false))
            {
                var type = await expressions.CheckWithAsync(args[i], parameter, target, mode, cancellation).ConfigureAwait(false);
                await inference.InferAsync(target, type, parameter, cancellation: cancellation).ConfigureAwait(false);
            }
        }
        if (rest is not null && await variables.CouldContainAsync(rest, cancellation).ConfigureAwait(false))
            await inference.InferAsync(
                target,
                await arguments.SpreadAsync(args, count, rest, target, mode, cancellation).ConfigureAwait(false),
                rest,
                cancellation: cancellation).ConfigureAwait(false);
        return await inference.GetAllAsync(target, cancellation).ConfigureAwait(false);
    }, cancellation);
}
