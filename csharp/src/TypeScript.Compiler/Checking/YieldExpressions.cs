using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IYieldExpressionHost
{
    bool NoImplicitAny { get; }

    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    ValueTask CheckLiteralAssignableAsync(Type source, Type target, SyntaxNode node, SyntaxNode expression, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, DiagnosticCode code);

    ValueTask AsyncYieldHelpersAsync(SyntaxNode node, CancellationToken cancellation);
}

internal sealed class YieldExpressions(TypeContext context, TypeAlgebra algebra, GeneratorTypes generators, IteratorProtocols protocols,
    ExpressionContexts expressions, Signatures signatures, IYieldExpressionHost host)
{
    internal async ValueTask<Type> CheckAsync(YieldExpressionNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((node.Flags & NodeFlags.YieldContext) == 0 && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            host.ExpressionError(node, DiagnosticCode.AYieldExpressionIsOnlyAllowedInAGeneratorBody);
        if (ThisExpressions.ParameterInitializer(node))
            host.ExpressionError(node, DiagnosticCode.XYieldExpressionsCannotBeUsedInAParameterInitializer);
        var operand = node.Expression is { } expression
            ? await host.CheckExpressionAsync(expression, 0, cancellation).ConfigureAwait(false) : context.UndefinedWideningType;
        var function = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature);
        if (function is null || !SemanticSyntax.Generator(function))
            return context.AnyType;
        bool async = SemanticSyntax.HasModifier(function, SyntaxKind.AsyncKeyword);
        if (async && node.AsteriskToken is not null)
            await host.AsyncYieldHelpersAsync(node, cancellation).ConfigureAwait(false);
        var annotation = await signatures.AnnotationAsync(function, cancellation).ConfigureAwait(false);
        if (annotation is UnionType)
            annotation = await algebra.FilterAsync(
                annotation,
                part => generators.AssignableReturnAsync(part, async, cancellation),
                cancellation).ConfigureAwait(false);
        var iteration = annotation is not null
            ? await protocols.GeneratorAsync(annotation, async, cancellation).ConfigureAwait(false)
            : default;
        var yielded = await generators.YieldedAsync(
            node,
            operand,
            iteration.Next ?? context.AnyType,
            async,
            cancellation).ConfigureAwait(false);
        if (annotation is not null && yielded is not null)
            await host.CheckLiteralAssignableAsync(
                yielded,
                iteration.Yield ?? context.AnyType,
                node.Expression ?? node,
                node.Expression ?? node,
                cancellation).ConfigureAwait(false);
        if (node.AsteriskToken is not null)
            return (await protocols.IterableAsync(
                operand,
                async ? IterationUse.AsyncYieldStar : IterationUse.YieldStar,
                node.Expression,
                cancellation).ConfigureAwait(false)).Return ?? context.AnyType;
        if (annotation is not null)
            return (await protocols.GeneratorAsync(annotation, async, cancellation).ConfigureAwait(false)).Next ?? context.AnyType;
        var next = await generators.ContextualAsync(function, IterationTypeKind.Next, cancellation).ConfigureAwait(false);
        if (next is not null)
            return next;
        if (host.NoImplicitAny && !Unused(node))
        {
            var contextual = await expressions.GetAsync(node, cancellation: cancellation).ConfigureAwait(false);
            if (contextual is null || (contextual.Flags & TypeFlags.Any) != 0)
                host.ExpressionError(
                    node,
                    DiagnosticCode.XYieldExpressionImplicitlyResultsInAnAnyTypeBecauseItsContainingGeneratorLacksAReturnTypeAnnotation);
        }
        return context.AnyType;
    }

    internal static bool Unused(SyntaxNode node)
    {
        while (true)
        {
            if (node.Parent is ParenthesizedExpressionNode parentheses)
            {
                node = parentheses;
                continue;
            }
            if (node.Parent is ExpressionStatementNode or VoidExpressionNode
                || node.Parent is ForStatementNode loop && (loop.Initializer == node || loop.Incrementor == node))
                return true;
            if (node.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.CommaToken } binary)
            {
                if (binary.Left == node)
                    return true;
                node = binary;
                continue;
            }
            return false;
        }
    }
}
