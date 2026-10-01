using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Emission;

/// <summary>Rewrites typed children without changing nodes retained by a program.</summary>
public abstract partial class SyntaxRewriter(EmitContext context, CancellationToken cancellation = default)
{
    protected EmitContext Context { get; } = context;
    protected CancellationToken Cancellation { get; } = cancellation;

    public async ValueTask<SyntaxNode?> VisitAsync(SyntaxNode? node)
    {
        var result = await VisitRawAsync(node).ConfigureAwait(false);
        if (result is SyntaxListNode list)
        {
            if (list.Children.Length != 1 || list.Children[0] is SyntaxListNode)
                throw new InvalidOperationException("A single-node replacement must contain exactly one node");
            return list.Children[0];
        }
        return result;
    }

    private async ValueTask<SyntaxNode?> VisitRawAsync(SyntaxNode? node)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (node is null)
            return null;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        var depth = Context.EnvironmentDepth;
        try { return await VisitNodeAsync(node).ConfigureAwait(false); }
        catch { Context.RestoreEnvironmentDepth(depth); throw; }
    }

    protected virtual ValueTask<SyntaxNode?> VisitNodeAsync(SyntaxNode node) => VisitEachChildAsync(node);

    protected virtual async ValueTask<NodeList?> VisitListAsync(NodeList? nodes)
    {
        if (nodes is null)
            return null;
        List<SyntaxNode>? result = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var visited = await VisitRawAsync(nodes[i]).ConfigureAwait(false);
            if (result is null && visited != nodes[i])
            {
                result = new(nodes.Count);
                for (int j = 0; j < i; j++)
                    result.Add(nodes[j]);
            }
            if (visited is SyntaxListNode list && result is not null)
                result.AddRange(list.Children);
            else if (visited is not null)
                result?.Add(visited);
        }
        return result is null ? nodes : new(result.ToArray(), nodes.Pos, nodes.End, nodes.IsMissing, nodes.HasTrailingComma);
    }

    protected async ValueTask<SyntaxNode[]> VisitArrayAsync(SyntaxNode[] nodes)
    {
        var list = new NodeList(nodes);
        var result = await VisitListAsync(list).ConfigureAwait(false);
        return ReferenceEquals(result, list) ? nodes : result?.ToArray() ?? [];
    }

    protected virtual async ValueTask<NodeList?> VisitTopLevelStatementsAsync(NodeList? nodes)
    {
        Context.StartVariableEnvironment();
        var updated = await VisitListAsync(nodes).ConfigureAwait(false);
        return Context.MergeEnvironment(updated, Context.EndVariableEnvironment());
    }

    protected virtual async ValueTask<NodeList?> VisitParametersAsync(NodeList? nodes)
    {
        Context.BeginParameters();
        return Context.EndParameters(await VisitListAsync(nodes).ConfigureAwait(false));
    }

    protected virtual async ValueTask<SyntaxNode?> VisitFunctionBodyAsync(SyntaxNode? node)
    {
        var updated = await VisitAsync(node).ConfigureAwait(false);
        var declarations = Context.EndVariableEnvironment();
        if (declarations.Count == 0)
            return updated;
        if (updated is null)
            return Context.Factory.NewBlock(new(declarations.ToArray()), true);
        if (updated is not BlockNode)
            Context.AddFlags(updated, EmitFlags.NoComments);
        var block = Context.ConvertToFunctionBlock(updated);
        var result = Context.Clone(block);
        result.Statements = Context.MergeEnvironment(block.Statements, declarations);
        return result;
    }

    protected virtual async ValueTask<SyntaxNode?> VisitEmbeddedStatementAsync(SyntaxNode? node)
    {
        if (node is null)
            return null;
        var updated = await VisitRawAsync(node).ConfigureAwait(false);
        if (updated is SyntaxListNode list)
            updated = list.Children.Length == 1 ? list.Children[0] : Context.Factory.NewBlock(new(list.Children), true);
        if (updated is null or NotEmittedStatementNode)
        {
            updated = EmitContext.CopyRange(Context.Factory.NewEmptyStatement(), node);
            Context.SetOriginal(updated, node);
            Context.SetCommentRange(updated, new(node.Pos, node.End));
        }
        return updated;
    }

    protected virtual async ValueTask<SyntaxNode?> VisitIterationBodyAsync(SyntaxNode? node)
    {
        if (node is null)
            return null;
        Context.StartLexicalEnvironment();
        var updated = (await VisitEmbeddedStatementAsync(node).ConfigureAwait(false))!;
        var declarations = Context.EndLexicalEnvironment();
        if (declarations.Count == 0)
            return updated;
        if (updated is BlockNode block)
        {
            var result = Context.Clone(block);
            result.Statements = new([.. declarations, .. block.Statements ?? new([])], block.Statements?.Pos ?? -1, block.Statements?.End ?? -1);
            return result;
        }
        return Context.Factory.NewBlock(new([.. declarations, updated]), true);
    }
}
