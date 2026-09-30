using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFlowGraphHost
{
    FlowNode? FlowOf(SyntaxNode node);

    ValueTask<Signature?> EffectsAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<TypePredicate?> PredicateAsync(Signature signature, CancellationToken cancellation);

    ValueTask<Type> ReturnTypeAsync(Signature signature, CancellationToken cancellation);

    ValueTask<bool> ExhaustiveAsync(SyntaxNode statement, CancellationToken cancellation);
}

internal interface IFlowTypeHost : IFlowGraphHost
{
    Type AutoArray { get; }
    Type AnyArray { get; }

    ValueTask<bool> MatchesAsync(SyntaxNode source, SyntaxNode target, CancellationToken cancellation);

    ValueTask<bool> ContainsAsync(SyntaxNode source, SyntaxNode target, bool optionalChain, CancellationToken cancellation);

    ValueTask<Utf8String?> ReferenceKeyAsync(FlowState state, CancellationToken cancellation);

    ValueTask<Type> InitialOrAssignedAsync(SyntaxNode node, SyntaxNode reference, CancellationToken cancellation);

    ValueTask<Type> NarrowAsync(FlowState state, Type type, SyntaxNode expression, bool assumeTrue, CancellationToken cancellation);

    ValueTask<Type> NarrowPredicateAsync(
        FlowState state,
        Type type,
        TypePredicate predicate,
        SyntaxNode call,
        CancellationToken cancellation);

    ValueTask<Type> NarrowAssertionAsync(FlowState state, Type type, SyntaxNode expression, CancellationToken cancellation);

    ValueTask<Type> NarrowSwitchAsync(FlowState state, Type type, FlowNode clause, CancellationToken cancellation);

    ValueTask<Type> ContextFreeExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> ExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> RegularObjectLiteralAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> EnumBaseAsync(Type type, CancellationToken cancellation);

    ValueTask<Type> ArrayAsync(Type element, CancellationToken cancellation);

    bool ConstLike(SyntaxNode declaration);

    void FlowControlError(SyntaxNode reference);
}

internal readonly record struct FlowType(Type Type, bool Incomplete = false);

internal sealed class FlowState
{
    internal SyntaxNode Reference { get; set; } = null!;
    internal Type Declared { get; set; } = null!;
    internal Type Initial { get; set; } = null!;
    internal SyntaxNode? Container { get; set; }
    internal Utf8String? Key { get; set; }
    internal bool KeyComputed { get; set; }
    internal int Depth { get; set; }
    internal int SharedStart { get; set; }
    internal List<FlowNode> Reductions { get; } = [];
}

internal sealed partial class FlowTypes(TypeContext context, TypeAlgebra algebra, TypeWidening widening,
    TypeFactQueries facts, TypeRelations relations, TypePredicates predicates, IFlowTypeHost host)
{
    private readonly List<(FlowNode Node, FlowType Type)> shared = [];
    private readonly Stack<FlowState> freeStates = [];
    private readonly Dictionary<(FlowNode Node, Utf8String Key), Type> loops = [];
    private List<((FlowNode Node, Utf8String Key) Key, List<Type> Types)> loopStack = [];
    internal Dictionary<SyntaxNode, Type>? ExpressionCache { get; private set; }
    internal FlowReachability Reachability { get; } = new(host);
    internal bool AnalysisDisabled { get; set; }
    internal long InvocationCount { get; private set; }
    internal int LoopCacheCount => loops.Count;
    internal int ActiveLoopCount => loopStack.Count;
    internal int SharedCount => shared.Count;

    internal async ValueTask<Type> GetAsync(SyntaxNode reference, Type declared, Type? initial = null,
        SyntaxNode? container = null, FlowNode? flow = null, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(declared);
        if (initial is not null)
            context.RequireOwned(initial);
        if (AnalysisDisabled)
            return context.ErrorType;
        flow ??= host.FlowOf(reference);
        if (flow is null)
            return declared;
        var state = freeStates.TryPop(out var available) ? available : new FlowState();
        state.Reference = reference;
        state.Declared = declared;
        state.Initial = initial ?? declared;
        state.Container = container;
        state.SharedStart = shared.Count;
        InvocationCount++;
        Type evolved;
        try
        {
            evolved = (await AtAsync(state, flow, cancellation).ConfigureAwait(false)).Type;
        }
        finally
        {
            shared.RemoveRange(state.SharedStart, shared.Count - state.SharedStart);
            state.Reference = null!;
            state.Declared = state.Initial = null!;
            state.Container = null;
            state.Key = null;
            state.KeyComputed = false;
            state.Depth = state.SharedStart = 0;
            state.Reductions.Clear();
            freeStates.Push(state);
        }
        var result = evolved is EvolvingArrayType && await EvolvingTargetAsync(reference, cancellation).ConfigureAwait(false)
            ? host.AutoArray : await FinalizeAsync(evolved, cancellation).ConfigureAwait(false);
        return result == context.UnreachableNeverType || reference.Parent is NonNullExpressionNode
            && (result.Flags & TypeFlags.Never) == 0
            && ((await facts.FilterAsync(
                result,
                TypeFacts.NEUndefinedOrNull,
                cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) != 0
            ? declared : result;
    }

    internal ValueTask<Type> StableAsync(Func<ValueTask<Type>> check, CancellationToken cancellation = default) =>
        StableAsync(check, static callback => callback(), cancellation);

    internal async ValueTask<Type> StableAsync<TState>(TState state, Func<TState, ValueTask<Type>> check,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var savedStack = loopStack;
        var savedCache = ExpressionCache;
        loopStack = [];
        ExpressionCache = null;
        try
        {
            return await check(state).ConfigureAwait(false);
        }
        finally
        {
            loopStack = savedStack;
            ExpressionCache = savedCache;
        }
    }

    internal async ValueTask<Type> ExpressionAsync(SyntaxNode node, Func<ValueTask<Type>> check, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (ExpressionCache?.TryGetValue(node, out var cached) == true)
            return cached;
        long before = InvocationCount;
        var result = await check().ConfigureAwait(false);
        if (InvocationCount != before)
            (ExpressionCache ??= [])[node] = result;
        return result;
    }

    private FlowType Result(Type type, bool incomplete) =>
        new(incomplete && (type.Flags & TypeFlags.Never) != 0 ? context.SilentNeverType : type, incomplete);

    private async ValueTask<FlowType> AtAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (state.Depth == 2000)
        {
            AnalysisDisabled = true;
            host.FlowControlError(state.Reference);
            return new(context.ErrorType);
        }
        state.Depth++;
        try
        {
            FlowNode? sharedNode = null;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var flags = flow.Flags;
                if ((flags & FlowFlags.Shared) != 0)
                {
                    for (int i = state.SharedStart; i < shared.Count; i++)
                        if (shared[i].Node == flow)
                            return shared[i].Type;
                    sharedNode = flow;
                }
                FlowType result;
                if ((flags & (FlowFlags.Assignment | FlowFlags.Call | FlowFlags.ArrayMutation)) != 0)
                {
                    var candidate = (flags & FlowFlags.Assignment) != 0 ? await AssignmentAsync(
                        state,
                        flow,
                        cancellation).ConfigureAwait(false)
                        : (flags & FlowFlags.Call) != 0 ? await CallAsync(state, flow, cancellation).ConfigureAwait(false)
                        : await MutationAsync(state, flow, cancellation).ConfigureAwait(false);
                    if (candidate is null)
                    {
                        flow = flow.Antecedent!;
                        continue;
                    }
                    result = candidate.Value;
                }
                else if ((flags & FlowFlags.Condition) != 0)
                    result = await ConditionAsync(state, flow, cancellation).ConfigureAwait(false);
                else if ((flags & FlowFlags.SwitchClause) != 0)
                {
                    var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
                    result = Result(
                        await host.NarrowSwitchAsync(state, antecedent.Type, flow, cancellation).ConfigureAwait(false),
                        antecedent.Incomplete);
                }
                else if ((flags & FlowFlags.BranchLabel) != 0)
                {
                    var antecedents = Antecedents(flow, state.Reductions);
                    if (antecedents.Count == 1)
                    {
                        flow = antecedents[0];
                        continue;
                    }
                    result = await BranchAsync(state, antecedents, cancellation).ConfigureAwait(false);
                }
                else if ((flags & FlowFlags.LoopLabel) != 0)
                {
                    if (flow.Antecedents.Count == 1)
                    {
                        flow = flow.Antecedents[0];
                        continue;
                    }
                    result = await LoopAsync(state, flow, cancellation).ConfigureAwait(false);
                }
                else if ((flags & FlowFlags.ReduceLabel) != 0)
                {
                    state.Reductions.Add(flow);
                    try
                    {
                        result = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        state.Reductions.RemoveAt(state.Reductions.Count - 1);
                    }
                }
                else if ((flags & FlowFlags.Start) != 0)
                {
                    var container = flow.Node;
                    if (container is not null
                        && container != state.Container
                        && state.Reference is not (PropertyAccessExpressionNode or ElementAccessExpressionNode)
                        && !(state.Reference.Kind == SyntaxKind.ThisKeyword && container is not ArrowFunctionNode))
                    {
                        flow = host.FlowOf(container) ?? throw new InvalidOperationException("Flow container has no outer flow");
                        continue;
                    }
                    result = new(state.Initial);
                }
                else
                    result = new(ConvertAuto(state.Declared));
                if (sharedNode is not null)
                    shared.Add((sharedNode, result));
                return result;
            }
        }
        finally
        {
            state.Depth--;
        }
    }

    internal Type ConvertAuto(Type type) => type == context.AutoType ? context.AnyType : type == host.AutoArray ? host.AnyArray : type;

    internal static IReadOnlyList<FlowNode> Antecedents(FlowNode flow, IReadOnlyList<FlowNode> reductions)
    {
        for (int i = reductions.Count - 1; i >= 0; i--)
            if (reductions[i].ReducedTarget == flow)
                return reductions[i].ReducedAntecedents;
        return flow.Antecedents;
    }

    private async ValueTask<FlowType> ConditionAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
        if ((antecedent.Type.Flags & TypeFlags.Never) != 0)
            return antecedent;
        var finalized = await FinalizeAsync(antecedent.Type, cancellation).ConfigureAwait(false);
        var narrowed = await host.NarrowAsync(
            state,
            finalized,
            flow.Node!,
            (flow.Flags & FlowFlags.TrueCondition) != 0,
            cancellation).ConfigureAwait(false);
        return narrowed == finalized ? antecedent : Result(narrowed, antecedent.Incomplete);
    }

    private async ValueTask<FlowType?> CallAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        var signature = await host.EffectsAsync(flow.Node!, cancellation).ConfigureAwait(false);
        if (signature is null)
            return null;
        var predicate = await host.PredicateAsync(signature, cancellation).ConfigureAwait(false);
        if (predicate?.Kind is TypePredicateKind.AssertsThis or TypePredicateKind.AssertsIdentifier)
        {
            var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
            var type = await FinalizeAsync(antecedent.Type, cancellation).ConfigureAwait(false);
            var narrowed = predicate.Type is not null
                ? await host.NarrowPredicateAsync(state, type, predicate, flow.Node!, cancellation).ConfigureAwait(false)
                : predicate.Kind == TypePredicateKind.AssertsIdentifier && flow.Node is CallExpressionNode { Arguments: var arguments }
                    && predicate.ParameterIndex >= 0 && predicate.ParameterIndex < arguments!.Count
                    ? await host.NarrowAssertionAsync(
                        state,
                        type,
                        arguments[predicate.ParameterIndex],
                        cancellation).ConfigureAwait(false) : type;
            return narrowed == type ? antecedent : Result(narrowed, antecedent.Incomplete);
        }
        return ((await host.ReturnTypeAsync(signature, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) != 0
            ? new FlowType(context.UnreachableNeverType) : null;
    }
}
