using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;

namespace TypeScript.Compiler.Checking;

internal sealed class SymbolSuggestions(AliasResolver aliases, TypeOrder order)
{
    internal ValueTask<Symbol?> FindAsync(
        string name,
        IEnumerable<Symbol> symbols,
        SymbolFlags meaning,
        CancellationToken cancellation = default)
        => SpellingSuggestions.FindAsync(name, symbols, async symbol =>
        {
            var declarationName = SemanticSyntax.Name(symbol.ValueDeclaration);
            string candidate = declarationName is PrivateIdentifierNode privateName ? privateName.Text : symbol.Name;
            if (candidate.Length == 0 || candidate[0] == '"' || candidate.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal))
                return null;
            if ((symbol.Flags & meaning) != 0)
                return candidate;
            if ((symbol.Flags & SymbolFlags.Alias) != 0
                && await aliases.TryResolveAsync(symbol, cancellation).ConfigureAwait(false) is { } target
                && (target.Flags & meaning) != 0)
                return candidate;
            return null;
        }, order.CompareSymbols, cancellation: cancellation);
}
