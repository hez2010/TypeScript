using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IAssignmentCheckHost
{
    bool ExportsPropertyAssignment(SyntaxNode left);

    ValueTask<Type> PropertyWriteAsync(PropertyAccessExpressionNode left, CancellationToken cancellation);

    ValueTask<Type?> AssignedPropertyTypeAsync(PropertyAccessExpressionNode left, CancellationToken cancellation);

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask CheckAssignableAsync(
        Type source,
        Type target,
        SyntaxNode errorNode,
        SyntaxNode expression,
        bool exactOptionalMismatch,
        CancellationToken cancellation);

    void AssignmentError(SyntaxNode node, int code);
}

internal sealed class AssignmentChecks(TypeContext context, CheckerLinks links, TypePredicates predicates, IAssignmentCheckHost host)
{
    internal async ValueTask OperatorAsync(
        SyntaxNode left,
        SyntaxKind op,
        SyntaxNode right,
        Type leftType,
        Type rightType,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!BinaryExpressions.Assignment(op))
            return;
        if (host.ExportsPropertyAssignment(left) && links.SymbolNodes.Get(left).ResolvedSymbol is { Declarations.Count: > 1 }
            && (rightType.Flags & TypeFlags.Undefined) != 0)
            return;
        if (op is >= SyntaxKind.FirstCompoundAssignment and <= SyntaxKind.LastCompoundAssignment
            && left is PropertyAccessExpressionNode property)
            leftType = await host.PropertyWriteAsync(property, cancellation).ConfigureAwait(false);
        if (!Reference(left, 2364, 2779))
            return;
        bool mismatch = false;
        if (context.ExactOptionalPropertyTypes
            && left is PropertyAccessExpressionNode access
            && predicates.Maybe(rightType, TypeFlags.Undefined, cancellation))
        {
            var target = await host.AssignedPropertyTypeAsync(access, cancellation).ConfigureAwait(false);
            mismatch = target == context.MissingType || target is UnionType union && union.Types.Contains(context.MissingType);
        }
        await host.CheckAssignableAsync(rightType, leftType, left, right, mismatch, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> ReferenceAsync(
        SyntaxNode target,
        Type source,
        CheckMode mode = 0,
        CancellationToken cancellation = default)
    {
        var type = await host.CheckExpressionAsync(target, mode, cancellation).ConfigureAwait(false);
        bool rest = target.Parent is SpreadAssignmentNode;
        if (Reference(target, rest ? 2701 : 2364, rest ? 2778 : 2779))
            await host.CheckAssignableAsync(source, type, target, target, false, cancellation).ConfigureAwait(false);
        return source;
    }

    internal bool Reference(SyntaxNode expression, int invalidReference, int invalidOptional)
    {
        var node = expression;
        while (true)
        {
            var inner = node switch
            {
                ParenthesizedExpressionNode p => p.Expression,
                AsExpressionNode a => a.Expression,
                TypeAssertionNode a => a.Expression,
                NonNullExpressionNode n => n.Expression,
                SatisfiesExpressionNode s => s.Expression,
                _ => null
            };
            if (inner is null)
                break;
            node = inner;
        }
        if (node is not (IdentifierNode or PropertyAccessExpressionNode or ElementAccessExpressionNode))
        {
            host.AssignmentError(expression, invalidReference);
            return false;
        }
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            host.AssignmentError(expression, invalidOptional);
            return false;
        }
        return true;
    }
}
