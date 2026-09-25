using System.Globalization;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private sealed class TypeSyntaxNames
    {
        internal Dictionary<TypeParameter, string> Names { get; } = [];
        internal HashSet<string> Used { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, int> Suffixes { get; } = new(StringComparer.Ordinal);
        private Scope? scope;

        internal IDisposable EnterScope() => new Scope(this);

        internal void Add(TypeParameter parameter, string name, string original, int suffix)
        {
            scope?.Parameters.Add(parameter);
            scope?.Names.Add(name);
            if (scope is not null && !scope.Suffixes.ContainsKey(original))
                scope.Suffixes.Add(original, Suffixes.TryGetValue(original, out int previous) ? previous : null);
            Names.Add(parameter, name);
            Used.Add(name);
            Suffixes[original] = suffix;
        }

        private sealed class Scope : IDisposable
        {
            private readonly TypeSyntaxNames owner;
            private readonly Scope? previous;
            internal List<TypeParameter> Parameters { get; } = [];
            internal List<string> Names { get; } = [];
            internal Dictionary<string, int?> Suffixes { get; } = new(StringComparer.Ordinal);

            internal Scope(TypeSyntaxNames owner)
            {
                this.owner = owner;
                previous = owner.scope;
                owner.scope = this;
            }

            public void Dispose()
            {
                foreach (var parameter in Parameters)
                    owner.Names.Remove(parameter);
                foreach (string name in Names)
                    owner.Used.Remove(name);
                foreach (var (name, value) in Suffixes)
                    if (value is { } suffix)
                        owner.Suffixes[name] = suffix;
                    else
                        owner.Suffixes.Remove(name);
                owner.scope = previous;
            }
        }
    }

    private readonly HashSet<SyntaxNode> generatedParameterScopes = [];
    private readonly HashSet<SyntaxNode> valueParameterScopes = [];

    private GeneratedParameterScope? EnterValueParameterScope(SyntaxNode? declaration, IReadOnlyList<Symbol> parameters,
        IReadOnlyList<Symbol>? originals, TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (declaration is null || parameters.Count == 0 || state.Symbols.Enclosing is not { } previous)
            return null;
        var existing = valueParameterScopes.Contains(previous) ? previous
            : previous.Parent is { } parent && valueParameterScopes.Contains(parent) ? parent : null;
        var node = existing ?? state.Factory.NewBlock(new([]), false);
        var table = existing is null ? new Dictionary<string, Symbol>(StringComparer.Ordinal)
            : (Dictionary<string, Symbol>)typeSyntaxScopes[node];
        if (existing is null)
        {
            node.Flags |= TypeScript.Compiler.Syntax.NodeFlags.Synthesized;
            node.Parent = previous;
            typeSyntaxScopes.Add(node, table);
            valueParameterScopes.Add(node);
            state.Symbols.Enclosing = node;
        }
        var scope = new GeneratedParameterScope(this, state, previous, node, table, existing is null);
        try
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var parameter = parameters[i];
                var original = originals is not null && i < originals.Count ? originals[i] : null;
                if (originals is not null && parameter != original)
                {
                    if (original is not null)
                        scope.Add(original.Name, original);
                }
                else if (parameter.Declarations.OfType<ParameterDeclarationNode>().FirstOrDefault()?.Name is BindingPatternNode pattern)
                {
                    foreach (var element in pattern.DescendantsAndSelf().OfType<BindingElementNode>())
                        if (element.Name is IdentifierNode && program.Symbols.Declaration(element) is { } symbol)
                            scope.Add(symbol.Name, symbol);
                }
                else
                    scope.Add(parameter.Name, parameter);
            }
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private string TypeSyntaxParameterName(TypeParameter parameter, TypeSyntaxContext state, CancellationToken cancellation)
    {
        var names = state.ParameterNames;
        if (names?.Names.TryGetValue(parameter, out string? cached) == true)
            return cached;
        string original = parameter.Symbol is { } symbol ? DisplayNameAsWritten(symbol, state.Symbols, true, cancellation)
            : "(Missing type parameter)";
        if (names is null)
            return original;
        int suffix = names.Suffixes.GetValueOrDefault(original);
        string name = original;
        while (names.Used.Contains(name) || ShadowsParameter(name))
        {
            cancellation.ThrowIfCancellationRequested();
            name = original + "_" + (++suffix).ToString(CultureInfo.InvariantCulture);
        }
        names.Add(parameter, name, original, suffix);
        return name;

        bool ShadowsParameter(string text)
        {
            Symbol? found = null;
            for (var node = state.Symbols.Enclosing; node is not null; node = node.Parent)
                if (typeSyntaxScopes.TryGetValue(node, out var locals) && locals.TryGetValue(text, out var local)
                    && (local.Flags & SymbolFlags.Type) != 0)
                {
                    found = local;
                    break;
                }
            found ??= program.Symbols.NameResolver(cancellation).Resolve(state.Symbols.Enclosing, text, SymbolFlags.Type);
            return found is not null && (found.Flags & SymbolFlags.TypeParameter) != 0 && found != parameter.Symbol;
        }
    }

    private GeneratedParameterScope? EnterGeneratedParameterScope(SyntaxNode? declaration, IReadOnlyList<TypeParameter> parameters,
        TypeSyntaxContext state, CancellationToken cancellation)
    {
        if (state.ParameterNames is null || parameters.Count == 0 || declaration is null || state.Symbols.Enclosing is not { } previous)
            return null;
        var existing = generatedParameterScopes.Contains(previous) ? previous
            : previous.Parent is { } parent && generatedParameterScopes.Contains(parent) ? parent : null;
        var node = existing ?? state.Factory.NewBlock(new([]), false);
        var table = existing is null ? new Dictionary<string, Symbol>(StringComparer.Ordinal)
            : (Dictionary<string, Symbol>)typeSyntaxScopes[node];
        if (existing is null)
        {
            node.Flags |= TypeScript.Compiler.Syntax.NodeFlags.Synthesized;
            node.Parent = previous;
            typeSyntaxScopes.Add(node, table);
            generatedParameterScopes.Add(node);
            state.Symbols.Enclosing = node;
        }
        var scope = new GeneratedParameterScope(this, state, previous, node, table, existing is null);
        try
        {
            foreach (var parameter in parameters)
                if (parameter.Symbol is { } symbol)
                    scope.Add(TypeSyntaxParameterName(parameter, state, cancellation), symbol);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private sealed class GeneratedParameterScope(Checker checker, TypeSyntaxContext state, SyntaxNode previous, SyntaxNode node,
        Dictionary<string, Symbol> table, bool owned) : IDisposable
    {
        private readonly Dictionary<string, Symbol?> changes = new(StringComparer.Ordinal);

        internal void Add(string name, Symbol symbol)
        {
            changes.TryAdd(name, table.GetValueOrDefault(name));
            table[name] = symbol;
        }

        public void Dispose()
        {
            state.Symbols.Enclosing = previous;
            if (owned)
            {
                checker.typeSyntaxScopes.Remove(node);
                checker.generatedParameterScopes.Remove(node);
                checker.valueParameterScopes.Remove(node);
            }
            else
                foreach (var (name, symbol) in changes)
                    if (symbol is null)
                        table.Remove(name);
                    else
                        table[name] = symbol;
        }
    }

    private async ValueTask<SyntaxNode> ReuseGeneratedAnnotationSyntaxAsync(SyntaxNode node,
        IReadOnlyDictionary<SyntaxNode, TypeParameter> parameters, TypeSyntaxContext state, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (parameters.TryGetValue(node, out var parameter))
        {
            var identifier = state.Factory.NewIdentifier(TypeSyntaxParameterName(parameter, state, cancellation));
            state.NoAsciiEscape.Add(identifier);
            return identifier;
        }
        if (node is TypeQueryNode query)
            return await ReuseTypeQuerySyntaxAsync(query, state, cancellation, countLength: false)
                ?? await TypeSyntaxAsync((await Instantiation.Engine.InstantiateAsync(await Nodes.FromNodeAsync(node, cancellation),
                    state.Mapper, cancellation: cancellation))!, state, cancellation);
        var copies = new Dictionary<SyntaxNode, SyntaxNode>();
        if (node is ConditionalTypeNode conditional)
        {
            copies[conditional.CheckType!] = await Visit(conditional.CheckType!);
            var inferred = program.Symbols.Binding(node)?.Get(node)?.Locals.Values
                .Where(s => (s.Flags & SymbolFlags.TypeParameter) != 0).Select(program.Scopes.Parameter).ToArray() ?? [];
            using (state.ParameterNames?.EnterScope())
            using (EnterGeneratedParameterScope(node, inferred, state, cancellation))
            {
                copies[conditional.ExtendsType!] = await Visit(conditional.ExtendsType!);
                copies[conditional.TrueType!] = await Visit(conditional.TrueType!);
            }
            copies[conditional.FalseType!] = await Visit(conditional.FalseType!);
        }
        else
        {
            bool introducesScope = Signatures.FunctionLike(node) || node is MappedTypeNode;
            using var names = introducesScope ? state.ParameterNames?.EnterScope() : null;
            var signature = Signatures.FunctionLike(node) ? await Signatures.FromDeclarationAsync(node, cancellation) : null;
            using var values = signature is null ? null : EnterValueParameterScope(node, signature.Parameters, null, state, cancellation);
            IReadOnlyList<TypeParameter> declaredParameters = node is MappedTypeNode mapped
                ? [program.Scopes.Parameter(program.Symbols.Declaration(mapped.TypeParameter!)!)]
                : signature?.TypeParameters ?? [];
            using var scope = introducesScope ? EnterGeneratedParameterScope(node, declaredParameters, state, cancellation) : null;
            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.GetChild(i);
                copies[child] = await Visit(child);
            }
        }
        var clone = node.ShallowClone();
        clone.Parent = null;
        clone.RewriteChildren(copies);
        if (node.Pos < 0 || SemanticSyntax.Source(node) != SemanticSyntax.Source(state.Symbols.Enclosing))
            clone.Pos = clone.End = -1;
        if (clone is StringLiteralNode literal)
            literal.TokenFlags |= state.Symbols.StringLiteralFlags;
        state.NoAsciiEscape.Add(clone);
        state.SingleLine.Add(clone);
        return clone;

        ValueTask<SyntaxNode> Visit(SyntaxNode child) => ReuseGeneratedAnnotationSyntaxAsync(child, parameters, state, cancellation);
    }
}
