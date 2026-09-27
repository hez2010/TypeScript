using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ICallResolutionHost
{
    bool NoImplicitAny { get; }
    bool InferencePartiallyBlocked { get; }
    int? ApparentArgumentCount { get; }

    bool ContextSensitive(SyntaxNode node);

    Type GlobalFunction { get; }

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<IReadOnlyList<Signature>> SignaturesAsync(Type type, bool construct, CancellationToken cancellation);

    ValueTask<bool> ConstructorAccessibleAsync(SyntaxNode node, IReadOnlyList<Signature> signatures, CancellationToken cancellation);

    ValueTask<Signature> SpecialCallAsync(SyntaxNode node, List<Signature>? candidates, CheckMode mode, CancellationToken cancellation);

    ValueTask InvocationErrorAsync(SyntaxNode node, Type type, bool construct, CancellationToken cancellation);

    ValueTask<bool> ArgumentRelatedAsync(Type source, Type target, RelationKind relation, SyntaxNode? errorNode, SyntaxNode expression,
            int code, CancellationToken cancellation);

    ValueTask<Type?> NumberIndexAsync(Type type, CancellationToken cancellation);

    ValueTask MissingAwaitInfoAsync(SyntaxNode node, Type source, Type target, RelationKind relation, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, int code);

    void DeferExpression(SyntaxNode node);

    ValueTask ReportCallErrorsAsync(CallResolution.State state, IReadOnlyList<Signature> original, CancellationToken cancellation);

    ValueTask<bool> JsxApplicableAsync(
        SyntaxNode node,
        Signature signature,
        RelationKind relation,
        CheckMode mode,
        bool report,
        int headCode,
        CancellationToken cancellation);
}

internal sealed partial class CallResolution(TypeContext context, CheckerLinks links, TypeResolutionStack resolutions,
    TypeAlgebra algebra, TypeViews views, ExpressionChecks checks, OptionalExpressions optional,
    ExpressionContexts contexts, ObjectLiterals objects, TypeRelations relations, Signatures signatures,
    SignatureParameters parameters, SignatureInstantiation instantiation, TypeInference inference, TypeConstraints constraints,
    SymbolTypes values, TypeWidening widening, TupleTypes tuples,
    CallSignatures rules, CallArguments arguments, CallInference callInference, FlowTypes flows, ICallResolutionHost host)
{
    internal sealed class State(
        SyntaxNode node,
        List<Signature> candidates,
        IReadOnlyList<SyntaxNode> arguments,
        IReadOnlyList<SyntaxNode> typeArguments)
    {
        internal SyntaxNode Node { get; } = node;
        internal List<Signature> Candidates { get; } = candidates;
        internal IReadOnlyList<SyntaxNode> Arguments { get; } = arguments;
        internal IReadOnlyList<SyntaxNode> TypeArguments { get; } = typeArguments;
        internal List<Signature> ArgumentErrors { get; } = [];
        internal Signature? ArgumentArityError { get; set; }
        internal Signature? TypeArgumentError { get; set; }
        internal bool Single { get; set; }
        internal bool Recursive { get; set; }
        internal bool TrailingComma { get; set; }
        internal CheckMode ArgumentMode { get; set; }
    }

    private readonly List<SyntaxNode> stack = [];

    private sealed class Frame(SyntaxNode node)
    {
        internal SyntaxNode Node { get; } = node;
        internal Signature? Published { get; set; }
    }

    private readonly List<Frame> frames = [];
    internal int ActiveCount => stack.Count;
    internal int ResolutionDepth => frames.Count;

    internal async ValueTask<Signature> GetAsync(
        SyntaxNode node,
        List<Signature>? candidates = null,
        CheckMode mode = 0,
        CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var data = links.Signatures.Get(node);
        var cached = data.ResolvedSignature;
        if (cached is not null && cached != rules.Resolving && candidates is null)
            return cached;
        int previousStart = resolutions.ResolutionStart;
        if (cached is null)
            resolutions.ResolutionStart = resolutions.Count;
        data.ResolvedSignature = rules.Resolving;
        var frame = new Frame(node);
        frames.Add(frame);
        Signature result;
        try
        {
            result = await ResolveAsync(node, candidates, mode, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (data.ResolvedSignature == rules.Resolving || data.ResolvedSignature == frame.Published)
                data.ResolvedSignature = cached;
            throw;
        }
        finally
        {
            resolutions.ResolutionStart = previousStart;
            frames.RemoveAt(frames.Count - 1);
        }
        if (result != rules.Resolving)
        {
            if (data.ResolvedSignature != rules.Resolving)
                result = data.ResolvedSignature!;
            data.ResolvedSignature = flows.ActiveLoopCount == 0 ? result : cached;
        }
        return result;
    }

    private async ValueTask<Signature> ResolveAsync(
        SyntaxNode node,
        List<Signature>? candidates,
        CheckMode mode,
        CancellationToken cancellation)
    {
        if (node is not CallExpressionNode and not NewExpressionNode
            || node is CallExpressionNode && CallArguments.Target(node)?.Kind is SyntaxKind.SuperKeyword or SyntaxKind.ImportKeyword)
            return await host.SpecialCallAsync(node, candidates, mode, cancellation).ConfigureAwait(false);
        bool construct = node is NewExpressionNode;
        var target = CallArguments.Target(node)!;
        var type = await host.CheckExpressionAsync(target, 0, cancellation).ConfigureAwait(false);
        SignatureFlags chain = 0;
        if (!construct && OptionalExpressions.Chain(node))
        {
            var nonOptional = await optional.ReceiverAsync(type, target, cancellation).ConfigureAwait(false);
            if (nonOptional != type)
                chain = !OptionalExpressions.Chain(node.Parent) || OptionalExpressions.Root(node.Parent)
                    || OptionalExpressions.Receiver(node.Parent!) != node ? SignatureFlags.IsOuterCallChain : SignatureFlags.IsInnerCallChain;
            type = nonOptional;
        }
        type = construct ? await checks.NonNullAsync(type, target, cancellation).ConfigureAwait(false)
            : await checks.InvocationAsync(type, target, cancellation).ConfigureAwait(false);
        if (type == context.SilentNeverType)
            return rules.SilentNever;
        var apparent = await views.ApparentAsync(type, cancellation).ConfigureAwait(false);
        if (Error(apparent))
            return await UntypedAsync(node, true, cancellation).ConfigureAwait(false);
        var calls = await host.SignaturesAsync(apparent, false, cancellation).ConfigureAwait(false);
        var constructors = await host.SignaturesAsync(apparent, true, cancellation).ConfigureAwait(false);
        if (construct)
        {
            if ((apparent.Flags & TypeFlags.Any) != 0)
            {
                if (CallArguments.TypeNodes(node) is { Count: > 0 })
                    host.ExpressionError(node, 2347);
                return await UntypedAsync(node, false, cancellation).ConfigureAwait(false);
            }
            if (constructors.Count != 0)
            {
                if (!await host.ConstructorAccessibleAsync(node, constructors, cancellation).ConfigureAwait(false))
                    return await UntypedAsync(node, true, cancellation).ConfigureAwait(false);
                if (constructors.Any(s => s.Composite is { IsUnion: true } union
                        ? union.Signatures.Any(part => (part.Flags & SignatureFlags.Abstract) != 0)
                        : s.Composite is null && (s.Flags & SignatureFlags.Abstract) != 0)
                    || apparent.Symbol?.Declarations.Any(
                        d => SemanticSyntax.ClassLike(d) && SemanticSyntax.HasModifier(d, SyntaxKind.AbstractKeyword)) == true)
                {
                    host.ExpressionError(node, 2511);
                    return await UntypedAsync(node, true, cancellation).ConfigureAwait(false);
                }
                return await OverloadAsync(node, constructors, candidates, mode, 0, cancellation).ConfigureAwait(false);
            }
            if (calls.Count != 0)
            {
                var signature = await OverloadAsync(node, calls, candidates, mode, 0, cancellation).ConfigureAwait(false);
                if (!host.NoImplicitAny)
                {
                    if (signature.Declaration is not null
                        && await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false) != context.VoidType)
                        host.ExpressionError(node, 2350);
                    if (await parameters.ThisAsync(signature, cancellation).ConfigureAwait(false) == context.VoidType)
                        host.ExpressionError(node, 2679);
                }
                return signature;
            }
        }
        else
        {
            bool untyped = (type.Flags & TypeFlags.Any) != 0
                || (apparent.Flags & TypeFlags.Any) != 0 && (type.Flags & TypeFlags.TypeParameter) != 0
                || calls.Count == 0 && constructors.Count == 0 && apparent is not UnionType
                    && ((await views.ReducedAsync(apparent, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) == 0
                    && await relations.RelatedAsync(type, host.GlobalFunction, RelationKind.Assignable, cancellation).ConfigureAwait(false);
            if (untyped)
            {
                if (!Error(type) && CallArguments.TypeNodes(node) is not null)
                    host.ExpressionError(node, 2347);
                return await UntypedAsync(node, false, cancellation).ConfigureAwait(false);
            }
            if (calls.Count != 0)
            {
                if ((mode & CheckMode.SkipGenericFunctions) != 0 && CallArguments.TypeNodes(node) is null or { Count: 0 })
                    foreach (var signature in calls)
                        if (signature.TypeParameters.Count != 0
                            && (await host.SignaturesAsync(
                                await signatures.ReturnAsync(signature, cancellation).ConfigureAwait(false),
                                false,
                                cancellation).ConfigureAwait(false)).Count != 0)
                        {
                            if ((mode & CheckMode.Inferential) != 0)
                                contexts.InferenceFor(node)!.Flags |= InferenceFlags.SkippedGenericFunction;
                            return rules.Resolving;
                        }
                return await OverloadAsync(node, calls, candidates, mode, chain, cancellation).ConfigureAwait(false);
            }
            if (constructors.Count != 0)
            {
                host.ExpressionError(node, 2348);
                return await UntypedAsync(node, true, cancellation).ConfigureAwait(false);
            }
        }
        await host.InvocationErrorAsync(target, apparent, construct, cancellation).ConfigureAwait(false);
        return await UntypedAsync(node, true, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Signature> UntypedAsync(SyntaxNode node, bool error, CancellationToken cancellation = default)
    {
        if (CallArguments.TypeNodes(node) is { } types)
            foreach (var type in types)
                await host.CheckedFunctionTypeAsync(type, cancellation).ConfigureAwait(false);
        if (node is TaggedTemplateExpressionNode tagged)
            await host.CheckExpressionAsync(tagged.Template!, 0, cancellation).ConfigureAwait(false);
        else if (node is JsxOpeningElementNode opening)
            await host.CheckExpressionAsync(opening.Attributes!, 0, cancellation).ConfigureAwait(false);
        else if (node is JsxSelfClosingElementNode self)
            await host.CheckExpressionAsync(self.Attributes!, 0, cancellation).ConfigureAwait(false);
        else
            foreach (var argument in CallArguments.List(node) ?? (IEnumerable<SyntaxNode>)[])
                await host.CheckExpressionAsync(argument, 0, cancellation).ConfigureAwait(false);
        return error ? rules.Unknown : rules.Any;
    }

    internal async ValueTask<Signature> OverloadAsync(SyntaxNode node, IReadOnlyList<Signature> original, List<Signature>? candidatesOut,
        CheckMode mode, SignatureFlags chain = 0, CancellationToken cancellation = default)
    {
        var typeNodes = (IReadOnlyList<SyntaxNode>?)CallArguments.TypeNodes(node) ?? [];
        foreach (var typeNode in typeNodes)
            await host.CheckedFunctionTypeAsync(typeNode, cancellation).ConfigureAwait(false);
        var candidates = rules.Reorder(original, chain);
        if (candidatesOut is not null)
        {
            candidatesOut.Clear();
            candidatesOut.AddRange(candidates);
            candidates = candidatesOut;
        }
        if (candidates.Count == 0)
            return rules.Unknown;
        var state = new State(node, candidates, await arguments.EffectiveAsync(node, cancellation).ConfigureAwait(false), typeNodes)
        {
            Single = candidates.Count == 1 && candidates[0].TypeParameters.Count == 0,
            TrailingComma = (mode & CheckMode.IsForSignatureHelp) != 0 && node is CallExpressionNode { Arguments.HasTrailingComma: true },
            Recursive = stack.Contains(node)
        };
        if (!state.Single && state.Arguments.Any(host.ContextSensitive))
            state.ArgumentMode = CheckMode.SkipContextSensitive;
        Signature? result = null;
        stack.Add(node);
        try
        {
            if (candidates.Count > 1)
                result = await ChooseAsync(state, RelationKind.Subtype, cancellation).ConfigureAwait(false);
            result ??= await ChooseAsync(state, RelationKind.Assignable, cancellation).ConfigureAwait(false);
        }
        finally
        {
            stack.RemoveAt(stack.Count - 1);
        }
        if (result is not null)
            return result;
        result = await FailureAsync(state, candidatesOut is not null, mode, cancellation).ConfigureAwait(false);
        links.Signatures.Get(node).ResolvedSignature = result;
        for (int i = frames.Count - 1; i >= 0; i--)
            if (frames[i].Node == node)
            {
                frames[i].Published = result;
                break;
            }
        if (candidatesOut is null && !host.InferencePartiallyBlocked)
            await host.ReportCallErrorsAsync(state, original, cancellation).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask<bool> ImplementationApplicableAsync(State state, Signature implementation, CancellationToken cancellation)
    {
        var local = new State(state.Node, [implementation], state.Arguments, state.TypeArguments)
        {
            Single = implementation.TypeParameters.Count == 0,
            Recursive = state.Recursive,
            TrailingComma = state.TrailingComma,
            ArgumentMode = state.ArgumentMode
        };
        return await ChooseAsync(local, RelationKind.Assignable, cancellation).ConfigureAwait(false) is not null;
    }

    private async ValueTask<Signature?> ChooseAsync(State state, RelationKind relation, CancellationToken cancellation)
    {
        state.ArgumentErrors.Clear();
        state.ArgumentArityError = state.TypeArgumentError = null;
        if (state.Single)
        {
            var candidate = state.Candidates[0];
            if (state.TypeArguments.Count != 0
                || !await rules.ArityAsync(state.Node, state.Arguments, candidate, state.TrailingComma, cancellation).ConfigureAwait(false))
                return null;
            if (!await ApplicableAsync(
                state.Node,
                state.Arguments,
                candidate,
                relation,
                0,
                cancellation: cancellation).ConfigureAwait(false))
            {
                state.ArgumentErrors.Add(candidate);
                return null;
            }
            return candidate;
        }
        for (int i = 0; i < state.Candidates.Count; i++)
        {
            var candidate = state.Candidates[i];
            if (!CallSignatures.TypeArity(candidate, state.TypeArguments)
                || !await rules.ArityAsync(state.Node, state.Arguments, candidate, state.TrailingComma, cancellation).ConfigureAwait(false))
                continue;
            InferenceContext? inferred = null;
            Signature checkedCandidate = candidate;
            if (candidate.TypeParameters.Count != 0)
            {
                IReadOnlyList<Type>? types;
                if (state.TypeArguments.Count != 0)
                {
                    types = await rules.TypeArgumentsAsync(
                        candidate,
                        state.TypeArguments,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (types is null)
                    {
                        state.TypeArgumentError = candidate;
                        continue;
                    }
                }
                else
                {
                    inferred = inference.Create(candidate.TypeParameters, candidate,
                        (state.Recursive && state.Candidates.Count == 1 ? InferenceFlags.NoConstraintChecks : 0)
                            | ((state.Node.Flags & NodeFlags.JavaScriptFile) != 0 ? InferenceFlags.AnyDefault : 0));
                    types = await callInference.InferAsync(
                        state.Node,
                        candidate,
                        state.Arguments,
                        state.ArgumentMode | CheckMode.SkipGenericFunctions,
                        inferred,
                        cancellation).ConfigureAwait(false);
                    if ((inferred.Flags & InferenceFlags.SkippedGenericFunction) != 0)
                        state.ArgumentMode |= CheckMode.SkipGenericFunctions;
                }
                checkedCandidate = await InstantiateAsync(candidate, types, inferred, cancellation).ConfigureAwait(false);
                if (await rules.NonArrayRestAsync(candidate, cancellation).ConfigureAwait(false) is not null
                    && !await rules.ArityAsync(
                        state.Node,
                        state.Arguments,
                        checkedCandidate,
                        state.TrailingComma,
                        cancellation).ConfigureAwait(false))
                {
                    state.ArgumentArityError = checkedCandidate;
                    continue;
                }
            }
            if (!await ApplicableAsync(
                state.Node,
                state.Arguments,
                checkedCandidate,
                relation,
                state.ArgumentMode,
                cancellation: cancellation).ConfigureAwait(false))
            {
                state.ArgumentErrors.Add(checkedCandidate);
                continue;
            }
            if (state.ArgumentMode != 0)
            {
                state.ArgumentMode = 0;
                if (inferred is not null)
                {
                    checkedCandidate = await InstantiateAsync(
                        candidate,
                        await callInference.InferAsync(
                            state.Node,
                            candidate,
                            state.Arguments,
                            0,
                            inferred,
                            cancellation).ConfigureAwait(false),
                        inferred,
                        cancellation).ConfigureAwait(false);
                    if (await rules.NonArrayRestAsync(candidate, cancellation).ConfigureAwait(false) is not null
                        && !await rules.ArityAsync(
                            state.Node,
                            state.Arguments,
                            checkedCandidate,
                            state.TrailingComma,
                            cancellation).ConfigureAwait(false))
                    {
                        state.ArgumentArityError = checkedCandidate;
                        continue;
                    }
                }
                if (!await ApplicableAsync(
                    state.Node,
                    state.Arguments,
                    checkedCandidate,
                    relation,
                    0,
                    cancellation: cancellation).ConfigureAwait(false))
                {
                    state.ArgumentErrors.Add(checkedCandidate);
                    continue;
                }
            }
            state.Candidates[i] = checkedCandidate;
            return checkedCandidate;
        }
        return null;
    }

    private ValueTask<Signature> InstantiateAsync(
        Signature signature,
        IReadOnlyList<Type> types,
        InferenceContext? inferred,
        CancellationToken cancellation) =>
        instantiation.GetAsync(signature, types, ((signature.Declaration?.Flags ?? 0) & NodeFlags.JavaScriptFile) != 0,
            inferred?.InferredTypeParameters?.Cast<TypeParameter>().ToArray(), cancellation);

    internal async ValueTask<bool> ApplicableAsync(SyntaxNode node, IReadOnlyList<SyntaxNode> args, Signature signature,
        RelationKind relation, CheckMode mode, bool report = false, int headCode = 2345, CancellationToken cancellation = default)
    {
        if (node is JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode)
            return await host.JsxApplicableAsync(node, signature, relation, mode, report, headCode, cancellation).ConfigureAwait(false);
        var receiver = await parameters.ThisAsync(signature, cancellation).ConfigureAwait(false);
        var target = CallArguments.Target(node);
        bool superProperty = target is PropertyAccessExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword }
            or ElementAccessExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword };
        if (receiver is not null && receiver != context.VoidType && node is not NewExpressionNode && !superProperty)
        {
            var receiverNode = arguments.ThisNode(node);
            if (!await host.ArgumentRelatedAsync(
                await arguments.ThisTypeAsync(receiverNode, cancellation).ConfigureAwait(false),
                receiver,
                relation,
                report ? receiverNode ?? node : null, receiverNode ?? node, 2684, cancellation).ConfigureAwait(false))
                return false;
        }
        var rest = await rules.NonArrayRestAsync(signature, cancellation).ConfigureAwait(false);
        int count = rest is null
            ? args.Count
            : Math.Min(await parameters.CountAsync(signature, cancellation).ConfigureAwait(false) - 1, args.Count);
        for (int i = 0; i < count; i++)
        {
            if (args[i].Kind == SyntaxKind.OmittedExpression)
                continue;
            var type = await parameters.AtAsync(signature, i, cancellation).ConfigureAwait(false);
            var arg = await contexts.CheckWithAsync(args[i], type, mode: mode, cancellation: cancellation).ConfigureAwait(false);
            if ((mode & CheckMode.SkipContextSensitive) != 0)
                arg = await objects.RegularAsync(arg, cancellation).ConfigureAwait(false);
            if (!await host.ArgumentRelatedAsync(
                arg,
                type,
                relation,
                report ? EffectiveNode(args[i]) : null,
                EffectiveNode(args[i]),
                headCode,
                cancellation).ConfigureAwait(false))
            {
                if (report)
                    await host.MissingAwaitInfoAsync(args[i], arg, type, relation, cancellation).ConfigureAwait(false);
                return false;
            }
        }
        if (rest is null)
            return true;
        var spread = await arguments.SpreadAsync(args, count, rest, mode: mode, cancellation: cancellation).ConfigureAwait(false);
        var errorNode = (args.Count - count) switch
        {
            0 => node,
            1 => EffectiveNode(args[count]),
            _ => CallArguments.Synthetic(node, spread)
        };
        bool related = await host.ArgumentRelatedAsync(
            spread,
            rest,
            relation,
            report ? errorNode : null,
            errorNode,
            headCode,
            cancellation).ConfigureAwait(false);
        if (!related && report)
            await host.MissingAwaitInfoAsync(errorNode, spread, rest, relation, cancellation).ConfigureAwait(false);
        return related;
    }

    internal static SyntaxNode EffectiveNode(SyntaxNode node)
    {
        while (true)
            switch (node)
            {
                case SyntheticExpressionNode:
                    node = node.Parent!;
                    break;
                case ParenthesizedExpressionNode parentheses:
                    node = parentheses.Expression!;
                    break;
                case SatisfiesExpressionNode satisfies:
                    node = satisfies.Expression!;
                    break;
                default:
                    return node;
            }
    }

    private bool Error(Type type) => type == context.ErrorType || (type.Flags & TypeFlags.Any) != 0 && type.Alias is not null;
}
