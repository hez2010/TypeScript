using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class FlowReachability(IFlowGraphHost host)
{
    private readonly Dictionary<FlowNode, bool> reachable = [];
    private readonly Dictionary<FlowNode, bool> postSuper = [];
    private FlowNode? lastFlow;
    private bool lastReachable;
    internal int ReachableCacheCount => reachable.Count;
    internal int PostSuperCacheCount => postSuper.Count;

    internal async ValueTask<bool> ReachableAsync(FlowNode flow, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = await ReachableWorkerAsync(flow, [], false, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        lastFlow = flow;
        lastReachable = result;
        return result;
    }

    private async ValueTask<bool> ReachableWorkerAsync(
        FlowNode flow,
        List<FlowNode> reductions,
        bool skipCache,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (flow == lastFlow)
                return lastReachable;
            var flags = flow.Flags;
            if ((flags & FlowFlags.Shared) != 0)
            {
                if (!skipCache && reductions.Count == 0)
                {
                    if (reachable.TryGetValue(flow, out bool cached))
                        return cached;
                    bool result = await ReachableWorkerAsync(flow, reductions, true, cancellation).ConfigureAwait(false);
                    return reachable[flow] = result;
                }
                skipCache = false;
            }
            if ((flags & (FlowFlags.Assignment | FlowFlags.Condition | FlowFlags.ArrayMutation)) != 0)
                flow = flow.Antecedent!;
            else if ((flags & FlowFlags.Call) != 0)
            {
                if (await host.EffectsAsync(flow.Node!, cancellation).ConfigureAwait(false) is { } signature)
                {
                    var predicate = await host.PredicateAsync(signature, cancellation).ConfigureAwait(false);
                    if (predicate is { Kind: TypePredicateKind.AssertsIdentifier, Type: null, ParameterIndex: >= 0 }
                        && flow.Node is CallExpressionNode { Arguments: var arguments } && predicate.ParameterIndex < arguments!.Count
                        && FalseExpression(arguments[predicate.ParameterIndex]))
                        return false;
                    if (((await host.ReturnTypeAsync(signature, cancellation).ConfigureAwait(false)).Flags & TypeFlags.Never) != 0)
                        return false;
                }
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.BranchLabel) != 0)
            {
                foreach (var antecedent in FlowTypes.Antecedents(flow, reductions))
                    if (await ReachableWorkerAsync(antecedent, reductions, false, cancellation).ConfigureAwait(false))
                        return true;
                return false;
            }
            else if ((flags & FlowFlags.LoopLabel) != 0)
            {
                if (flow.Antecedents.Count == 0)
                    return false;
                flow = flow.Antecedents[0];
            }
            else if ((flags & FlowFlags.SwitchClause) != 0)
            {
                if (flow.ClauseStart == flow.ClauseEnd && await host.ExhaustiveAsync(flow.Node!, cancellation).ConfigureAwait(false))
                    return false;
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.ReduceLabel) != 0)
            {
                lastFlow = null;
                reductions.Add(flow);
                try
                {
                    return await ReachableWorkerAsync(flow.Antecedent!, reductions, false, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    reductions.RemoveAt(reductions.Count - 1);
                }
            }
            else
                return (flags & FlowFlags.Unreachable) == 0;
        }
    }

    internal ValueTask<bool> PostSuperAsync(FlowNode flow, bool skipCache = false, CancellationToken cancellation = default)
        => PostSuperWorkerAsync(flow, [], skipCache, cancellation);

    private async ValueTask<bool> PostSuperWorkerAsync(
        FlowNode flow,
        List<FlowNode> reductions,
        bool skipCache,
        CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var flags = flow.Flags;
            if ((flags & FlowFlags.Shared) != 0)
            {
                if (!skipCache)
                {
                    if (postSuper.TryGetValue(flow, out bool cached))
                        return cached;
                    postSuper[flow] = await PostSuperWorkerAsync(flow, reductions, true, cancellation).ConfigureAwait(false);
                }
                skipCache = false;
            }
            if ((flags & (FlowFlags.Assignment | FlowFlags.Condition | FlowFlags.ArrayMutation | FlowFlags.SwitchClause)) != 0)
                flow = flow.Antecedent!;
            else if ((flags & FlowFlags.Call) != 0)
            {
                if (flow.Node is CallExpressionNode { Expression.Kind: SyntaxKind.SuperKeyword })
                    return true;
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.BranchLabel) != 0)
            {
                foreach (var antecedent in FlowTypes.Antecedents(flow, reductions))
                    if (!await PostSuperWorkerAsync(antecedent, reductions, false, cancellation).ConfigureAwait(false))
                        return false;
                return true;
            }
            else if ((flags & FlowFlags.LoopLabel) != 0)
                flow = flow.Antecedents[0];
            else if ((flags & FlowFlags.ReduceLabel) != 0)
            {
                reductions.Add(flow);
                try
                {
                    return await PostSuperWorkerAsync(flow.Antecedent!, reductions, false, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    reductions.RemoveAt(reductions.Count - 1);
                }
            }
            else
                return (flags & FlowFlags.Unreachable) != 0;
        }
    }

    internal static bool FalseExpression(SyntaxNode expression)
    {
        var pending = new Stack<(SyntaxNode Right, bool Both)>();
        while (true)
        {
            while (expression is ParenthesizedExpressionNode parentheses)
                expression = parentheses.Expression!;
            if (expression is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.AmpersandAmpersandToken or SyntaxKind.BarBarToken } binary)
            {
                pending.Push((binary.Right!, binary.OperatorToken.Kind == SyntaxKind.BarBarToken));
                expression = binary.Left!;
                continue;
            }
            bool result = expression.Kind == SyntaxKind.FalseKeyword;
            while (pending.TryPop(out var next))
            {
                if (next.Both ? !result : result)
                    continue;
                expression = next.Right;
                goto Evaluate;
            }
            return result;
        Evaluate:
            ;
        }
    }
}
