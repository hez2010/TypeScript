using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private sealed class QualifiedTypeParameterNames
    {
        private readonly HashSet<Symbol> symbols = [];
        private List<Symbol>? added;

        internal bool Add(Symbol symbol)
        {
            if (!symbols.Add(symbol))
                return false;
            added?.Add(symbol);
            return true;
        }

        internal IDisposable EnterScope() => new Scope(this);

        private sealed class Scope : IDisposable
        {
            private readonly QualifiedTypeParameterNames owner;
            private readonly List<Symbol>? previous;
            private readonly List<Symbol> current = [];

            internal Scope(QualifiedTypeParameterNames owner)
            {
                this.owner = owner;
                previous = owner.added;
                owner.added = current;
            }

            public void Dispose()
            {
                foreach (var symbol in current)
                    owner.symbols.Remove(symbol);
                owner.added = previous;
            }
        }
    }

    private async ValueTask<NodeList?> QualifiedTypeArgumentsAsync(IReadOnlyList<Symbol> chain, int index,
        SymbolDisplayContext symbols, CancellationToken cancellation)
    {
        if (symbols.Types is not { } state || !state.QualifiedNames.Add(chain[index])
            || (state.Flags & NodeBuilderFlags.WriteTypeParametersInQualifiedName) == 0 || index >= chain.Count - 1)
            return null;
        var nodes = await SymbolDisplayArgumentsAsync(chain[index], chain[index + 1], state, cancellation);
        return nodes.Count == 0 ? null : new(nodes.ToArray());
    }
}
