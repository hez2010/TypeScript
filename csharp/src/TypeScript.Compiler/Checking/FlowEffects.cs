using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFlowEffectHost
{
    ValueTask<Type?> DottedTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> NonNullExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> OptionalCallTargetAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> HasInstanceMethodAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> CallSignaturesAsync(Type type, CancellationToken cancellation);

    ValueTask<Signature?> ResolvedCallAsync(SyntaxNode node, CancellationToken cancellation);
}

internal sealed class FlowEffects(TypeContext context, TypeViews views, Signatures signatures, IFlowEffectHost host)
{
    private readonly Dictionary<SyntaxNode, Signature?> cache = [];

    internal async ValueTask<Signature?> GetAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (cache.TryGetValue(node, out var cached))
            return cached;
        Type? functionType = null;
        if (node is BinaryExpressionNode binary)
            functionType = await host.HasInstanceMethodAsync(
                await host.NonNullExpressionAsync(binary.Right!, cancellation).ConfigureAwait(false), cancellation).ConfigureAwait(false);
        else if (node.Parent is ExpressionStatementNode)
            functionType = await host.DottedTypeAsync(((CallExpressionNode)node).Expression!, cancellation).ConfigureAwait(false);
        else if (node is CallExpressionNode { Expression: { Kind: not SyntaxKind.SuperKeyword } expression })
            functionType = (node.Flags & NodeFlags.OptionalChain) != 0
                ? await host.OptionalCallTargetAsync(expression, cancellation).ConfigureAwait(false)
                : await host.NonNullExpressionAsync(expression, cancellation).ConfigureAwait(false);
        var apparent = functionType is null
            ? context.UnknownType
            : await views.ApparentAsync(functionType, cancellation).ConfigureAwait(false);
        var calls = await host.CallSignaturesAsync(apparent, cancellation).ConfigureAwait(false);
        Signature? result = null;
        if (calls.Count == 1 && calls[0].TypeParameters.Count == 0)
            result = calls[0];
        else
            foreach (var signature in calls)
                if (await HasEffectAsync(signature, cancellation).ConfigureAwait(false))
                {
                    result = await host.ResolvedCallAsync(node, cancellation).ConfigureAwait(false);
                    break;
                }
        if (result is not null && !await HasEffectAsync(result, cancellation).ConfigureAwait(false))
            result = null;
        cancellation.ThrowIfCancellationRequested();
        cache[node] = result;
        return result;
    }

    private async ValueTask<bool> HasEffectAsync(Signature signature, CancellationToken cancellation)
        => await signatures.PredicateAsync(signature, cancellation).ConfigureAwait(false) is not null
            || signature.Declaration is { } declaration
                && (((await signatures.AnnotationAsync(
                    declaration,
                    cancellation).ConfigureAwait(false)) ?? context.UnknownType).Flags & TypeFlags.Never) != 0;
}
