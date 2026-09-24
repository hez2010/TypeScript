using TypeScript.Compiler.Ast;

namespace TypeScript.Compiler.Checking;

internal interface IReferenceTypeNarrowingHost
{
    ValueTask<Type> ExpressionAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type?> ContextualReferenceAsync(SyntaxNode node, bool skipBindingPatterns, CancellationToken cancellation);
}

internal sealed class ReferenceTypeNarrowing(
    TypeContext context,
    TypeAlgebra algebra,
    TypeConstraints constraints,
    TypePredicates predicates,
    MappedTypes mapped,
    IReferenceTypeNarrowingHost host)
{
    internal async ValueTask<Type> GetAsync(Type type, SyntaxNode node, CheckMode mode, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (type is SubstitutionType substitution && (substitution.Constraint.Flags & TypeFlags.Unknown) != 0)
            type = substitution.BaseType;
        if ((mode & CheckMode.Inferential) == 0 && await GenericConstraintAsync(type, true, cancellation).ConfigureAwait(false)
            && (await ConstraintPositionAsync(type, node, cancellation).ConfigureAwait(false)
                || await ConcreteContextAsync(node, mode, cancellation).ConfigureAwait(false)))
            return await algebra.MapAsync(
                type,
                async part => await constraints.BaseConstraintOrTypeAsync(part, cancellation).ConfigureAwait(false),
                cancellation: cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException("Constraint mapping removed a type");
        return type;
    }

    private async ValueTask<bool> ConstraintPositionAsync(Type type, SyntaxNode node, CancellationToken cancellation)
    {
        var parent = node.Parent;
        return parent is PropertyAccessExpressionNode or QualifiedNameNode
            || parent is CallExpressionNode call && call.Expression == node
            || parent is NewExpressionNode construct && construct.Expression == node
            || parent is ElementAccessExpressionNode index && index.Expression == node
                && !(await GenericConstraintAsync(type, false, cancellation).ConfigureAwait(false)
                    && ((await mapped.GenericFlagsAsync(
                        await host.ExpressionAsync(index.ArgumentExpression!, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false)) & ObjectFlags.IsGenericIndexType) != 0);
    }

    private async ValueTask<bool> ConcreteContextAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation)
    {
        if (node is not (IdentifierNode or PropertyAccessExpressionNode or ElementAccessExpressionNode)
            || node.Parent is JsxOpeningElementNode opening && opening.TagName == node
            || node.Parent is JsxSelfClosingElementNode selfClosing && selfClosing.TagName == node)
            return false;
        return await host.ContextualReferenceAsync(
            node,
            (mode & CheckMode.RestBindingElement) != 0,
            cancellation).ConfigureAwait(false) is { } type
            && await mapped.GenericFlagsAsync(type, cancellation).ConfigureAwait(false) == 0;
    }

    private async ValueTask<bool> GenericConstraintAsync(Type type, bool unionConstraint, CancellationToken cancellation)
    {
        var pending = new Stack<Type>();
        var parts = type is UnionType union ? union.Types : (IReadOnlyList<Type>)[type];
        for (int i = parts.Count - 1; i >= 0; i--)
            pending.Push(parts[i]);
        while (pending.TryPop(out var part))
        {
            cancellation.ThrowIfCancellationRequested();
            if (part is IntersectionType intersection)
            {
                for (int i = intersection.Types.Count - 1; i >= 0; i--)
                    pending.Push(intersection.Types[i]);
            }
            else if ((part.Flags & TypeFlags.Instantiable) != 0)
            {
                var constraint = await constraints.BaseConstraintOrTypeAsync(part, cancellation).ConfigureAwait(false);
                if (unionConstraint ? (constraint.Flags & (TypeFlags.Nullable | TypeFlags.Union)) != 0
                    : !predicates.Maybe(constraint, TypeFlags.Nullable, cancellation))
                    return true;
            }
        }
        return false;
    }
}
