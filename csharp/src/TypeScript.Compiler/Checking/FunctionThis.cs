using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IFunctionThisHost
{
    bool NoImplicitThis { get; }
    Type GlobalThisMarker { get; }

    ValueTask<IReadOnlyList<Type>> TypeArgumentsAsync(TypeReference type, CancellationToken cancellation);

    ValueTask<Type> CachedExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    bool ExportsReceiver(SyntaxNode node);
}

internal sealed class FunctionThis(CheckerSymbols symbols, FunctionContexts functions, ExpressionContexts expressions, TypeAlgebra algebra,
    SymbolTypes values, TypeInstantiation instantiation, TypeFactQueries facts, TypeWidening widening, IFunctionThisHost host)
{
    internal async ValueTask<Type?> GetAsync(SyntaxNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node is ArrowFunctionNode)
            return null;
        if (FunctionSyntax.Contextual(node) && FunctionSyntax.Sensitive(node, symbols)
            && await functions.GetAsync(node, cancellation).ConfigureAwait(false) is { ThisParameter: { } receiver })
            return await values.GetAsync(receiver, cancellation).ConfigureAwait(false);
        bool js = (node.Flags & NodeFlags.JavaScriptFile) != 0;
        if (!host.NoImplicitThis && !js)
            return null;
        ObjectLiteralExpressionNode? literal = node switch
        {
            MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode => node.Parent as ObjectLiteralExpressionNode,
            FunctionExpressionNode { Parent: PropertyAssignmentNode property } => property.Parent as ObjectLiteralExpressionNode,
            _ => null
        };
        if (literal is not null)
        {
            var contextual = await expressions.ApparentAsync(literal, cancellation: cancellation).ConfigureAwait(false);
            var currentLiteral = literal;
            var current = contextual;
            while (current is not null)
            {
                var type = await algebra.MapAsync(current, async part =>
                {
                    foreach (var candidate in part is IntersectionType intersection ? intersection.Types : [part])
                        if (candidate is TypeReference reference && reference.Target == host.GlobalThisMarker)
                            return (await host.TypeArgumentsAsync(reference, cancellation).ConfigureAwait(false))[0];
                    return null;
                }, cancellation: cancellation).ConfigureAwait(false);
                if (type is not null)
                    return await instantiation.InstantiateAsync(
                        type,
                        expressions.InferenceFor(literal)?.Mapper,
                        cancellation: cancellation).ConfigureAwait(false);
                if (currentLiteral.Parent is not PropertyAssignmentNode { Parent: ObjectLiteralExpressionNode parent })
                    break;
                currentLiteral = parent;
                current = await expressions.ApparentAsync(parent, cancellation: cancellation).ConfigureAwait(false);
            }
            var inferred = contextual is not null ? await facts.NonNullableAsync(contextual, cancellation).ConfigureAwait(false)
                : await host.CachedExpressionAsync(literal, 0, cancellation).ConfigureAwait(false);
            return await widening.GetAsync(inferred, cancellation).ConfigureAwait(false);
        }
        var owner = node.Parent;
        while (owner is ParenthesizedExpressionNode)
            owner = owner.Parent;
        if (owner is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } assignment)
        {
            var expression = assignment.Left switch
            {
                PropertyAccessExpressionNode property => property.Expression,
                ElementAccessExpressionNode element => element.Expression,
                _ => null
            };
            if (expression is not null)
                return js && host.ExportsReceiver(expression) ? null
                    : await widening.GetAsync(
                        await host.CachedExpressionAsync(expression, 0, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false);
        }
        return null;
    }
}
