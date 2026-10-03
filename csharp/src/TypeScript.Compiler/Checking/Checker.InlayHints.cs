using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal ValueTask<(SyntaxNode Node, IReadOnlyDictionary<SyntaxNode, Symbol> Symbols)> GetInlayHintTypeSyntaxAsync(
        Type? type, TypePredicate? predicate, CancellationToken cancellation)
        => VisibilityQueryAsync(null, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var state = new TypeSyntaxContext(null, true, flags: NodeBuilderFlags.IgnoreErrors
                | NodeBuilderFlags.AllowUniqueESSymbolType | NodeBuilderFlags.UseAliasDefinedOutsideCurrentScope) { DisplaySymbols = [] };
            var node = predicate is null ? await TypeSyntaxAsync(type!, state, cancellation)
                : await PredicateTypeSyntaxAsync(predicate, state, cancellation);
            return (node, (IReadOnlyDictionary<SyntaxNode, Symbol>)state.DisplaySymbols);
        }, cancellation), cancellation), cancellation);
}
