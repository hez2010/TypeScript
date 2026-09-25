using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class OptionalExpressions(TypeContext context, TypeAlgebra algebra, TypeFactQueries facts)
{
    internal Type RemoveMarker(Type type) => context.StrictNullChecks ? Remove(type, context.OptionalType) : type;

    internal ValueTask<Type> ReceiverAsync(Type type, SyntaxNode expression, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        return Root(expression.Parent) && Receiver(expression.Parent!) == expression ? facts.NonNullableAsync(type, cancellation)
            : ValueTask.FromResult(Chain(expression) && context.StrictNullChecks ? Remove(type, context.OptionalType) : type);
    }

    internal ValueTask<Type> PropagateAsync(Type type, SyntaxNode node, bool optional, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        context.RequireOwned(type);
        if (!optional || !context.StrictNullChecks)
            return ValueTask.FromResult(type);
        bool outermost = !Chain(node.Parent) || Root(node.Parent) || Receiver(node.Parent!) != node;
        return algebra.UnionAsync([type, outermost ? context.UndefinedType : context.OptionalType], cancellation: cancellation);
    }

    private Type Remove(Type type, Type target)
    {
        if (type is not UnionType union)
            return type == target ? context.NeverType : type;
        if (union.Origin is UnionType origin && origin.Types.Contains(target))
            return algebra.Filter(type, t => t != target);
        if (!union.Types.Contains(target))
            return type;
        return context.GetUnionFromSortedTypes(union.Types.Where(t => t != target).ToArray(),
            union.ObjectFlags & (ObjectFlags.PrimitiveUnion | ObjectFlags.ContainsIntersections));
    }

    internal static bool Chain(SyntaxNode? node) => node is PropertyAccessExpressionNode or ElementAccessExpressionNode
        or CallExpressionNode or NonNullExpressionNode
        && (node.Flags & NodeFlags.OptionalChain) != 0;

    internal static bool Root(SyntaxNode? node) => Chain(node) && node switch
    {
        PropertyAccessExpressionNode property => property.QuestionDotToken is not null,
        ElementAccessExpressionNode element => element.QuestionDotToken is not null,
        CallExpressionNode call => call.QuestionDotToken is not null,
        _ => false
    };

    internal static SyntaxNode? Receiver(SyntaxNode node) => FlowReferences.Receiver(node);
}
