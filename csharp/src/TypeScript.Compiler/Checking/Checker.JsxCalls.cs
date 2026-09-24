using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    public async ValueTask<IReadOnlyList<Type>> InferJsxAsync(
        SyntaxNode node,
        Signature signature,
        CheckMode mode,
        InferenceContext inference,
        CancellationToken cancellation)
    {
        var props = await JsxPropsAsync(signature, node, cancellation);
        var attributes = await Contexts.CheckWithAsync(JsxAttributes(node)!, props, inference, mode, cancellation);
        await Inference.InferAsync(inference, attributes, props, cancellation: cancellation);
        return await Inference.GetAllAsync(inference, cancellation);
    }

    public async ValueTask<bool> JsxApplicableAsync(
        SyntaxNode node,
        Signature signature,
        RelationKind relation,
        CheckMode mode,
        bool report,
        int headCode,
        CancellationToken cancellation)
    {
        var props = await JsxPropsAsync(signature, node, cancellation);
        var attributes = node is JsxOpeningFragmentNode ? await JsxAttributesTypeAsync(node, 0, cancellation)
            : await Contexts.CheckWithAsync(JsxAttributes(node)!, props, mode: mode, cancellation: cancellation);
        if ((mode & CheckMode.SkipContextSensitive) != 0)
            attributes = await ObjectLiterals.RegularAsync(attributes, cancellation);
        if (!await JsxFactoryArityAsync(node, report, cancellation))
            return false;
        int? previous = relationDiagnosticHead;
        if (report && headCode == 2769)
            relationDiagnosticHead = headCode;
        try
        {
            return await RelationDiagnostics.CheckAsync(
                attributes,
                props,
                relation,
                report ? JsxTag(node) ?? node : null,
                JsxAttributes(node),
                report && headCode == 2769 ? headCode : null, cancellation);
        }
        finally
        {
            relationDiagnosticHead = previous;
        }
    }

    private async ValueTask<bool> JsxFactoryArityAsync(SyntaxNode node, bool report, CancellationToken cancellation)
    {
        if (node is JsxOpeningFragmentNode || IntrinsicJsx(JsxTag(node)) || await JsxImplicitModuleAsync(node, cancellation) is not null)
            return true;
        var tag = await Expressions.CheckAsync(JsxTag(node)!, cancellation: cancellation);
        var calls = await SignaturesAsync(tag, false, cancellation);
        if (calls.Count == 0)
            return true;
        var factory = await program.EntityNames.ResolveAsync(
            JsxFactoryEntity(JsxFactoryName(node), node),
            SymbolFlags.Value,
            true,
            location: node,
            cancellation: cancellation);
        if (factory is null)
            return true;
        int maximum = -1;
        foreach (var signature in await SignaturesAsync(await Values.GetAsync(factory, cancellation), false, cancellation))
            foreach (var accepted in await SignaturesAsync(await Parameters.AtAsync(signature, 0, cancellation), false, cancellation))
            {
                if (await Parameters.HasRestAsync(accepted, cancellation))
                    return true;
                maximum = Math.Max(maximum, await Parameters.CountAsync(accepted, cancellation));
            }
        if (maximum < 0)
            return true;
        foreach (var signature in calls)
            if (await Parameters.MinimumAsync(signature, cancellation: cancellation) <= maximum)
                return true;
        if (report)
            Error(JsxTag(node)!, 6229);
        return false;
    }

    private async ValueTask<Signature> JsxIntrinsicSignatureAsync(SyntaxNode node, Type props, CancellationToken cancellation)
    {
        var parameter = new Symbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.Transient, "props");
        links.Values.Get(parameter).ResolvedType = props;
        return context.NewSignature(0, null, [], null, [parameter], await JsxTypeAsync("Element", node, cancellation), null, 1);
    }

    private async ValueTask<IReadOnlyList<Signature>> JsxSignaturesAsync(Type type, SyntaxNode node, CancellationToken cancellation)
    {
        if ((type.Flags & TypeFlags.String) != 0)
            return [CallSignatures.Any];
        if (type is LiteralType { Flags: var flags, Value: string name } && (flags & TypeFlags.StringLiteral) != 0)
        {
            var intrinsic = await JsxTypeAsync("IntrinsicElements", node, cancellation);
            Type? props = intrinsic == context.ErrorType
                ? context.AnyType
                : await FlowPropertyTypeAsync(intrinsic, name, true, cancellation);
            if (props is null)
            {
                Error(node, 2339);
                return [];
            }
            return [await JsxIntrinsicSignatureAsync(node, props, cancellation)];
        }
        var apparent = await Views.ApparentAsync(type, cancellation);
        var signatures = await SignaturesAsync(apparent, true, cancellation);
        if (signatures.Count == 0)
            signatures = await SignaturesAsync(apparent, false, cancellation);
        if (signatures.Count == 0 && apparent is UnionType union)
        {
            var lists = new List<IReadOnlyList<Signature>>();
            foreach (var part in union.Types)
                lists.Add(await JsxSignaturesAsync(part, node, cancellation));
            signatures = await SignatureComposition.UnionAsync(lists, cancellation);
        }
        return signatures;
    }

    private async ValueTask<Signature> ResolveJsxAsync(
        SyntaxNode node,
        List<Signature>? candidates,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (node is not JsxOpeningFragmentNode && IntrinsicJsx(JsxTag(node)))
        {
            var props = await JsxIntrinsicTypeAsync(node, cancellation);
            var signature = await JsxIntrinsicSignatureAsync(node, props, cancellation);
            var attributes = await Contexts.CheckWithAsync(
                JsxAttributes(node)!,
                await JsxPropsAsync(signature, node, cancellation),
                cancellation: cancellation);
            await RelationDiagnostics.CheckAsync(
                attributes,
                props,
                RelationKind.Assignable,
                JsxTag(node)!,
                JsxAttributes(node),
                cancellation: cancellation);
            if (CallArguments.TypeNodes(node) is { Count: > 0 } typeArguments)
            {
                foreach (var argument in typeArguments)
                    await CheckedFunctionTypeAsync(argument, cancellation);
                Error(node, 2558);
            }
            return signature;
        }
        var type = node is JsxOpeningFragmentNode
            ? await JsxFragmentTypeAsync(node, cancellation)
            : await Expressions.CheckAsync(JsxTag(node)!, cancellation: cancellation);
        var apparent = await Views.ApparentAsync(type, cancellation);
        if (apparent == context.ErrorType)
            return await CallResolution.UntypedAsync(node, true, cancellation);
        var signatures = await JsxSignaturesAsync(type, node, cancellation);
        if ((type.Flags & TypeFlags.Any) != 0 || (apparent.Flags & TypeFlags.Any) != 0 && type is TypeParameter
            || signatures.Count == 0
                && apparent is not UnionType
                && ((await Views.ReducedAsync(apparent, cancellation)).Flags & TypeFlags.Never) == 0
                && await AssignableAsync(type, GlobalFunction, cancellation))
            return await CallResolution.UntypedAsync(node, false, cancellation);
        if (signatures.Count == 0)
        {
            Error(JsxTag(node) ?? node, 2604);
            return await CallResolution.UntypedAsync(node, true, cancellation);
        }
        return await CallResolution.OverloadAsync(node, signatures, candidates, mode, cancellation: cancellation);
    }
}
