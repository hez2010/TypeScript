using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class FlowTypes
{
    private readonly Dictionary<(Type Declared, Type Assigned), Type> reducedAssignments = [];

    private async ValueTask<FlowType?> AssignmentAsync(FlowState state, FlowNode flow, CancellationToken cancellation)
    {
        var node = flow.Node!;
        if (await host.MatchesAsync(state.Reference, node, cancellation).ConfigureAwait(false))
        {
            if (!await Reachability.ReachableAsync(flow, cancellation).ConfigureAwait(false))
                return new FlowType(context.UnreachableNeverType);
            if (ReferenceSyntax.AssignmentKind(node) == 2)
            {
                var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
                return Result(await widening.LiteralBaseAsync(antecedent.Type, cancellation).ConfigureAwait(false), antecedent.Incomplete);
            }
            if (state.Declared == context.AutoType || state.Declared == host.AutoArray)
            {
                if (node is VariableDeclarationNode { Initializer: ArrayLiteralExpressionNode { Elements.Count: 0 } }
                    || node is not BindingElementNode
                        && node.Parent is BinaryExpressionNode { Right: ArrayLiteralExpressionNode { Elements.Count: 0 } })
                    return new FlowType(Evolving(context.NeverType));
                var assigned = await widening.LiteralAsync(
                    await host.InitialOrAssignedAsync(node, state.Reference, cancellation).ConfigureAwait(false),
                    cancellation).ConfigureAwait(false);
                return new FlowType(
                    await relations.RelatedAsync(assigned, state.Declared, RelationKind.Assignable, cancellation).ConfigureAwait(false)
                    ? assigned : host.AnyArray);
            }
            var type = state.Declared;
            if (CompoundLike(node))
                type = await widening.LiteralBaseAsync(type, cancellation).ConfigureAwait(false);
            return new FlowType(type is UnionType ? await AssignmentReducedAsync(type,
                await host.InitialOrAssignedAsync(node, state.Reference, cancellation).ConfigureAwait(false),
                cancellation).ConfigureAwait(false) : type);
        }
        if (await host.ContainsAsync(state.Reference, node, false, cancellation).ConfigureAwait(false))
        {
            if (!await Reachability.ReachableAsync(flow, cancellation).ConfigureAwait(false))
                return new FlowType(context.UnreachableNeverType);
            if (node is VariableDeclarationNode { Initializer: FunctionExpressionNode or ArrowFunctionNode }
                && ((node.Flags & NodeFlags.JavaScriptFile) != 0 || host.ConstLike(node)))
                return await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
            return new FlowType(state.Declared);
        }
        if (node is VariableDeclarationNode
            && node.Parent?.Parent is ForInOrOfStatementNode { Kind: SyntaxKind.ForInStatement, Expression: var expression }
            && (await host.MatchesAsync(state.Reference, expression!, cancellation).ConfigureAwait(false)
                || await host.ContainsAsync(expression!, state.Reference, true, cancellation).ConfigureAwait(false)))
        {
            var antecedent = await AtAsync(state, flow.Antecedent!, cancellation).ConfigureAwait(false);
            var type = await FinalizeAsync(antecedent.Type, cancellation).ConfigureAwait(false);
            return new FlowType(await facts.GetAsync(type, TypeFacts.IsUndefinedOrNull, cancellation).ConfigureAwait(false) != 0
                ? await facts.NonNullableAsync(type, cancellation).ConfigureAwait(false) : type);
        }
        return null;
    }

    internal async ValueTask<Type> AssignmentReducedAsync(Type declared, Type assigned, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(declared);
        context.RequireOwned(assigned);
        if (declared == assigned || (assigned.Flags & TypeFlags.Never) != 0)
            return assigned;
        var key = (declared, assigned);
        if (reducedAssignments.TryGetValue(key, out var cached))
            return cached;
        var result = await algebra.FilterAsync(declared, async type =>
        {
            if (assigned is UnionType union && union.Types.Contains(type))
                return true;
            foreach (var part in assigned is UnionType source ? source.Types : (IReadOnlyList<Type>)[assigned])
                if (await relations.RelatedAsync(part, type, RelationKind.Assignable, cancellation).ConfigureAwait(false))
                    return true;
            return false;
        }, cancellation).ConfigureAwait(false);
        if ((assigned.Flags & TypeFlags.BooleanLiteral) != 0 && assigned.IsFreshLiteral)
            result = await algebra.MapAsync(
                result,
                t => ValueTask.FromResult<Type?>(t is LiteralType literal ? context.GetFreshLiteralType(literal) : t),
                cancellation: cancellation).ConfigureAwait(false) ?? context.NeverType;
        if (!await relations.RelatedAsync(assigned, result, RelationKind.Assignable, cancellation).ConfigureAwait(false))
            result = declared;
        cancellation.ThrowIfCancellationRequested();
        return reducedAssignments[key] = result;
    }

    internal static bool CompoundLike(SyntaxNode node)
    {
        if (ReferenceSyntax.AssignmentTarget(node) is not BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } assignment)
            return false;
        var right = assignment.Right;
        while (right is ParenthesizedExpressionNode parentheses)
            right = parentheses.Expression;
        return right is BinaryExpressionNode binary && binary.OperatorToken!.Kind is SyntaxKind.AsteriskAsteriskToken
            or SyntaxKind.AsteriskToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken or SyntaxKind.PlusToken or SyntaxKind.MinusToken
            or SyntaxKind.LessThanLessThanToken or SyntaxKind.GreaterThanGreaterThanToken or SyntaxKind.GreaterThanGreaterThanGreaterThanToken;
    }
}
