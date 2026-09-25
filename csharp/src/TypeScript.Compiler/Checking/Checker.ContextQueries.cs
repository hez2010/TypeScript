using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    public bool InferencePartiallyBlocked { get; private set; }
    public int? ApparentArgumentCount { get; private set; }

    internal async ValueTask<Type?> GetContextualTypeAsync(
        SyntaxNode node,
        ContextFlags flags = 0,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return (flags & ContextFlags.IgnoreNodeInferences) == 0 ? await Contexts.GetAsync(node, flags, cancellation)
            : await WithoutSourceInferenceAsync(node, () => Contexts.GetAsync(node, flags, cancellation), cancellation);
    }

    internal async ValueTask<Type?> GetContextualTypeForArgumentAtIndexAsync(
        SyntaxNode node,
        int index,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await ContextualArgumentAtIndexAsync(node, index, cancellation);
    }

    internal async ValueTask<Type?> GetContextualTypeForObjectLiteralElementAsync(SyntaxNode node, ContextFlags flags = 0,
        CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await ObjectElementContextAsync(node, flags, cancellation);
    }

    internal async ValueTask<Type?> GetContextualTypeForJsxAttributeAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        if (node is not JsxAttributeNode attribute)
            return await Contexts.GetAsync(node.Parent!, cancellation: cancellation);
        var type = await Contexts.ApparentAsync(attribute.Parent!, cancellation: cancellation);
        return type is null || (type.Flags & TypeFlags.Any) != 0 ? null
            : await ContextualPropertyAsync(type, JsxName(attribute.Name!), cancellation);
    }

    internal async ValueTask<Type?> GetContextualTypeForArrayLiteralAtPositionAsync(Type? contextual, ArrayLiteralExpressionNode array,
        int position, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(array, cancellation).ConfigureAwait(false);
        if (contextual is null)
            return null;
        context.RequireOwned(contextual);
        int firstSpread = -1, lastSpread = -1, index = 0;
        for (int i = 0; i < array.Elements!.Count; i++)
        {
            var element = array.Elements[i];
            if (element.Pos < position)
                index++;
            if (element is SpreadElementNode)
            {
                if (firstSpread == -1)
                    firstSpread = i;
                lastSpread = i;
            }
        }
        return await Contexts.ElementAsync(contextual, index, -1, firstSpread, lastSpread, cancellation);
    }

    internal async ValueTask<Signature> GetResolvedSignatureAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await CallResolution.GetAsync(node, cancellation: cancellation);
    }

    internal async ValueTask<(Signature Signature, IReadOnlyList<Signature> Candidates)> GetResolvedSignatureForSignatureHelpAsync(
        SyntaxNode node, int argumentCount, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(node, cancellation).ConfigureAwait(false);
        return await WithoutSignatureCachingAsync(node,
            () => SignatureCandidatesAsync(node, CheckMode.IsForSignatureHelp, argumentCount, cancellation), cancellation);
    }

    internal async ValueTask<IReadOnlyList<Signature>> GetCandidateSignaturesForStringLiteralCompletionsAsync(SyntaxNode call,
        SyntaxNode editingArgument, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(call, cancellation).ConfigureAwait(false);
        RequireNode(editingArgument);
        var blocked = await WithoutSourceInferenceAsync(editingArgument,
            () => SignatureCandidatesAsync(call, 0, 0, cancellation), cancellation);
        var ordinary = await WithoutSignatureCachingAsync(editingArgument,
            () => SignatureCandidatesAsync(call, 0, 0, cancellation), cancellation);
        var result = new List<Signature>(blocked.Candidates);
        var seen = new HashSet<Signature>(result);
        foreach (var candidate in ordinary.Candidates)
            if (!seen.Contains(candidate))
                result.Add(candidate);
        return result.AsReadOnly();
    }

    internal async ValueTask<Type> GetReturnTypeOfSignatureAsync(Signature signature, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(null, cancellation).ConfigureAwait(false);
        if (signature.Context != context)
            throw new ArgumentException("Signature belongs to another checker", nameof(signature));
        return await Signatures.ReturnAsync(signature, cancellation);
    }

    private async ValueTask<(Signature Signature, IReadOnlyList<Signature> Candidates)> SignatureCandidatesAsync(SyntaxNode node,
        CheckMode mode, int argumentCount, CancellationToken cancellation)
    {
        var previous = ApparentArgumentCount;
        ApparentArgumentCount = argumentCount;
        try
        {
            var candidates = new List<Signature>();
            var signature = await CallResolution.GetAsync(node, candidates, mode, cancellation);
            return (signature, candidates.AsReadOnly());
        }
        finally
        {
            ApparentArgumentCount = previous;
        }
    }

    private async ValueTask<T> WithoutSourceInferenceAsync<T>(SyntaxNode node, Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        var added = new List<SyntaxNode>();
        var call = DeclarationOrder.Ancestor(node, QueryCallLike);
        if (call is not null)
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                if (SkippedInferenceNodes.Add(current))
                    added.Add(current);
                if (current.Parent == call || current.Parent is null)
                    break;
            }
        bool previous = InferencePartiallyBlocked;
        InferencePartiallyBlocked = true;
        try
        {
            return await WithoutSignatureCachingAsync(node, action, cancellation);
        }
        finally
        {
            InferencePartiallyBlocked = previous;
            foreach (var entry in added)
                SkippedInferenceNodes.Remove(entry);
        }
    }

    private async ValueTask<T> WithoutSignatureCachingAsync<T>(SyntaxNode node, Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        var signatures = new List<(SignatureLinks Links, Signature? Value)>();
        var values = new List<(ValueSymbolLinks Links, Type? Value)>();
        try
        {
            for (SyntaxNode? current = node; current is not null; current = current.Parent)
            {
                cancellation.ThrowIfCancellationRequested();
                if (QueryCallLike(current) || current is FunctionExpressionNode or ArrowFunctionNode)
                {
                    var data = links.Signatures.Get(current);
                    signatures.Add((data, data.ResolvedSignature));
                    data.ResolvedSignature = null;
                    if (current is FunctionExpressionNode or ArrowFunctionNode)
                    {
                        var symbolData = links.Values.Get(program.Symbols.Declaration(current)!);
                        values.Add((symbolData, symbolData.ResolvedType));
                        symbolData.ResolvedType = null;
                    }
                }
            }
            return await action();
        }
        finally
        {
            foreach (var (data, value) in signatures)
                data.ResolvedSignature = value;
            foreach (var (data, value) in values)
                data.ResolvedType = value;
        }
    }

    internal static bool QueryCallLike(SyntaxNode node) => node is CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode
        or DecoratorNode or JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode
        || node is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.InstanceOfKeyword };
}
