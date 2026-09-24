using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface IAwaitExpressionHost
{
    ValueTask<Type> CheckExpressionAsync(SyntaxNode node, CheckMode mode, CancellationToken cancellation);

    void TopLevelAwait(SyntaxNode node);

    void ExpressionError(SyntaxNode node, int code);

    void ExpressionSuggestion(SyntaxNode node, int code);
}

internal sealed class AwaitExpressions(TypeContext context, AwaitedTypes awaited, IAwaitExpressionHost host)
{
    internal async ValueTask<Type> CheckAsync(AwaitExpressionNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var container = DeclarationOrder.Ancestor(node.Parent, n => n is IFunctionSignature or ClassStaticBlockDeclarationNode);
        if (container is ClassStaticBlockDeclarationNode)
            host.ExpressionError(node, 18037);
        else if ((node.Flags & NodeFlags.AwaitContext) == 0 && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
        {
            if (MissingNamePrefixes.ThisContainer(node, true, false) is SourceFileNode)
                host.TopLevelAwait(node);
            else
                host.ExpressionError(node, 1308);
        }
        if (ThisExpressions.ParameterInitializer(node))
            host.ExpressionError(node, 2524);
        var operand = await host.CheckExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        var result = await awaited.GetAsync(operand, true, node, 1320, cancellation).ConfigureAwait(false) ?? context.ErrorType;
        if (result == operand && result != context.ErrorType && !((result.Flags & TypeFlags.Any) != 0 && result.Alias is not null)
            && (operand.Flags & TypeFlags.AnyOrUnknown) == 0)
            host.ExpressionSuggestion(node, 80007);
        return result;
    }
}
