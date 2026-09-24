using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ITypeAssertionHost
{
    bool ErasableSyntaxOnly { get; }

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask<Type> CheckedFunctionTypeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<Type> TypeFromNodeAsync(SyntaxNode node, CancellationToken cancellation);

    ValueTask<bool> ConstArgumentAsync(SyntaxNode node, CancellationToken cancellation);

    void DeferExpression(SyntaxNode node);

    void ExpressionError(SyntaxNode node, int code);
}

internal sealed class TypeAssertions(TypeContext context, TypeAlgebra algebra, TypeWidening widening, ObjectLiterals objects,
    TypeRelations relations, RelationDiagnostics diagnostics, ITypeAssertionHost host)
{
    private readonly Dictionary<SyntaxNode, Type> operands = [];
    internal int OperandCount => operands.Count;
    internal IReadOnlyCollection<SyntaxNode> CheckedNodes => operands.Keys;

    internal async ValueTask<Type> CheckAsync(SyntaxNode node, CheckMode mode = 0, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var (expression, annotation) = Parts(node);
        if (node is TypeAssertionNode && SemanticSyntax.Source(node) is { ParseDiagnostics.Count: 0 } source)
        {
            if (source.FileName.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)
                || source.FileName.EndsWith(".cts", StringComparison.OrdinalIgnoreCase))
                host.ExpressionError(node, 7059);
        }
        if (node is TypeAssertionNode && host.ErasableSyntaxOnly && (node.Flags & NodeFlags.JavaScriptFile) == 0)
            host.ExpressionError(node, 1294);
        var type = await host.CheckExpressionAsync(expression, mode, cancellation).ConfigureAwait(false);
        if (SemanticSyntax.ConstAssertion(node))
        {
            if (!await host.ConstArgumentAsync(expression, cancellation).ConfigureAwait(false))
                host.ExpressionError(expression, 1355);
            return await algebra.RegularTypeAsync(type, cancellation).ConfigureAwait(false);
        }
        await host.CheckedFunctionTypeAsync(annotation, cancellation).ConfigureAwait(false);
        var target = await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        operands[node] = type;
        host.DeferExpression(node);
        return target;
    }

    internal async ValueTask DeferredAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        var annotation = Parts(node).Annotation;
        var type = await objects.RegularAsync(
            await widening.LiteralBaseAsync(operands[node], cancellation).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);
        var target = await host.TypeFromNodeAsync(annotation, cancellation).ConfigureAwait(false);
        if (target == context.ErrorType || (target.Flags & TypeFlags.Any) != 0 && target.Alias is not null)
            return;
        var widened = await widening.GetAsync(type, cancellation).ConfigureAwait(false);
        if (!await relations.RelatedAsync(target, widened, RelationKind.Comparable, cancellation).ConfigureAwait(false))
            await diagnostics.CheckAsync(
                type,
                target,
                RelationKind.Comparable,
                (annotation.Flags & NodeFlags.Reparsed) != 0 ? annotation : node,
                null, 2352, cancellation).ConfigureAwait(false);
    }

    internal async ValueTask<Type> SatisfiesAsync(SatisfiesExpressionNode node, CancellationToken cancellation = default)
    {
        await host.CheckedFunctionTypeAsync(node.Type!, cancellation).ConfigureAwait(false);
        var type = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        var target = await host.TypeFromNodeAsync(node.Type!, cancellation).ConfigureAwait(false);
        if (target == context.ErrorType || (target.Flags & TypeFlags.Any) != 0 && target.Alias is not null)
            return target;
        await diagnostics.CheckAsync(
            type,
            target,
            RelationKind.Assignable,
            node,
            node.Expression,
            1360,
            cancellation).ConfigureAwait(false);
        return type;
    }

    private static (SyntaxNode Expression, SyntaxNode Annotation) Parts(SyntaxNode node) => node switch
    {
        AsExpressionNode assertion => (assertion.Expression!, assertion.Type!),
        TypeAssertionNode assertion => (assertion.Expression!, assertion.Type!),
        _ => throw new ArgumentException("Expected a type assertion", nameof(node))
    };
}
