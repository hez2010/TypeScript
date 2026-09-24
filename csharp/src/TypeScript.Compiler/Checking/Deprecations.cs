using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed class Deprecations(CheckerSymbols symbols)
{
    private readonly Dictionary<SyntaxNode, bool> declarations = [];

    internal bool Declaration(SyntaxNode declaration)
    {
        if (declarations.TryGetValue(declaration, out bool cached))
            return cached;
        var root = SemanticSyntax.RootDeclaration(declaration);
        var flags = root.Flags;
        if (root is VariableDeclarationNode)
            root = root.Parent!;
        if (root is VariableDeclarationListNode)
        {
            flags |= root.Flags;
            root = root.Parent!;
        }
        if (root is VariableStatementNode)
            flags |= root.Flags;
        bool deprecated = false;
        if ((flags & NodeFlags.PossiblyContainsDeprecatedTag) != 0)
            for (var node = declaration; node is not null; node = node.Parent)
                if ((node.Flags & NodeFlags.PossiblyContainsDeprecatedTag) != 0)
                {
                    deprecated = SemanticSyntax.Source(node)?.GetDocumentation(node)
                        .Any(comment => comment.Tags?.Any(tag => tag.Kind == SyntaxKind.JSDocDeprecatedTag) == true) == true;
                    break;
                }
        return declarations[declaration] = deprecated;
    }

    internal bool Symbol(Symbol symbol)
    {
        if (symbols.Parent(symbol) is { } parent && symbol.Declarations.Count > 1)
            return (parent.Flags & SymbolFlags.Interface) != 0
                ? symbol.Declarations.Any(Declaration)
                : symbol.Declarations.All(Declaration);
        return symbol.ValueDeclaration is { } declaration && Declaration(declaration)
            || symbol.Declarations.Count != 0 && symbol.Declarations.All(Declaration);
    }

    internal async ValueTask<bool> UncalledAsync(
        SyntaxNode node,
        Symbol symbol,
        FlowReferences references,
        CancellationToken cancellation = default)
    {
        if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method)) == 0)
            return true;
        var parent = DeclarationOrder.Ancestor(
            node.Parent,
            n => n is not (PropertyAccessExpressionNode or ElementAccessExpressionNode)) ?? node.Parent;
        if (parent is CallExpressionNode or NewExpressionNode or TaggedTemplateExpressionNode or DecoratorNode
            or JsxOpeningElementNode or JsxSelfClosingElementNode or JsxOpeningFragmentNode or BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.InstanceOfKeyword })
        {
            if (node is not IdentifierNode || parent is not (CallExpressionNode or NewExpressionNode))
                return false;
            var arguments = parent is CallExpressionNode call ? call.Arguments : ((NewExpressionNode)parent).Arguments;
            foreach (var argument in (IEnumerable<SyntaxNode>?)arguments ?? [])
                if (await references.MatchesAsync(node, argument, cancellation).ConfigureAwait(false)
                    || await references.ContainsAsync(node, argument, false, cancellation).ConfigureAwait(false)
                    || await references.ContainsAsync(argument, node, true, cancellation).ConfigureAwait(false))
                    return true;
            var expression = parent is CallExpressionNode invoked ? invoked.Expression : ((NewExpressionNode)parent).Expression;
            return expression is PropertyAccessExpressionNode access
                && (await references.MatchesAsync(node, access.Expression!, cancellation).ConfigureAwait(false)
                    || await references.ContainsAsync(node, access.Expression!, false, cancellation).ConfigureAwait(false));
        }
        return symbol.Declarations.All(d => d is not IFunctionSignature || Declaration(d));
    }
}
