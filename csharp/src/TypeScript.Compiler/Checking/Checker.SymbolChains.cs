using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private enum SymbolTableKind
    {
        Locals,
        Exports,
        Members,
        Globals,
        ResolvedExports
    }

    private readonly record struct SymbolTableIdentity(SymbolTableKind Kind, object? Source);

    private readonly record struct ChainKey(Symbol Symbol, SyntaxNode? Scope, SymbolFlags Meaning, bool ExternalOnly);

    private readonly record struct ScopeTable(IReadOnlyDictionary<string, Symbol> Table, SymbolTableIdentity Identity,
            bool LocalNames, SyntaxNode? Scope);

    private sealed record ChainContext(Symbol Symbol, SyntaxNode? Enclosing, SymbolFlags Meaning, bool ExternalOnly,
            HashSet<(Symbol, SymbolTableIdentity)> ActiveTables);

    private readonly Dictionary<ChainKey, IReadOnlyList<Symbol>?> accessibleChains = [];
    private readonly Dictionary<SymbolTableIdentity, IReadOnlyList<Symbol>> tableAliases = [];
    private readonly Dictionary<SyntaxNode, IReadOnlyDictionary<string, Symbol>> classNameTables = [];
    private ChainChanges? chainChanges;
    internal Action<Symbol>? BeforeSymbolChainTable { get; set; }
    internal int AccessibleChainCacheCount => accessibleChains.Count;
    internal int SymbolTableAliasCacheCount => tableAliases.Count;

    private sealed class ChainChanges
    {
        internal Dictionary<ChainKey, (bool Exists, IReadOnlyList<Symbol>? Value)> Chains { get; } = [];
        internal HashSet<SymbolTableIdentity> Aliases { get; } = [];
        internal HashSet<SyntaxNode> ClassNames { get; } = [];
    }

    internal async ValueTask<IReadOnlyList<Symbol>?> GetAccessibleSymbolChainAsync(Symbol? symbol, SyntaxNode? enclosingDeclaration,
        SymbolFlags meaning, bool useOnlyExternalAliasing = false, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(enclosingDeclaration, cancellation).ConfigureAwait(false);
        if (symbol is null)
            return null;
        return await ChainOperationAsync(() => AccessibleChainAsync(
            new(symbol, enclosingDeclaration, meaning, useOnlyExternalAliasing, []), cancellation), cancellation);
    }

    private async ValueTask<T> ChainOperationAsync<T>(Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        var changes = new ChainChanges();
        chainChanges = changes;
        try
        {
            var result = await action();
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            foreach (var (key, previous) in changes.Chains)
                if (previous.Exists)
                    accessibleChains[key] = previous.Value;
                else
                    accessibleChains.Remove(key);
            foreach (var key in changes.Aliases)
                tableAliases.Remove(key);
            foreach (var key in changes.ClassNames)
                classNameTables.Remove(key);
            throw;
        }
        finally
        {
            chainChanges = null;
        }
    }

    private async ValueTask<IReadOnlyList<Symbol>?> AccessibleChainAsync(ChainContext lookup, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if (lookup.Symbol.Declarations.Count != 0 && lookup.Symbol.Declarations.All(d => d is
            PropertyDeclarationNode or MethodDeclarationNode or GetAccessorDeclarationNode or SetAccessorDeclarationNode))
            return null;
        SyntaxNode? firstScope = null;
        foreach (var scope in SymbolTablesInScope(lookup.Enclosing, cancellation))
        {
            firstScope = scope.Scope;
            break;
        }
        var key = new ChainKey(lookup.Symbol, firstScope, lookup.Meaning, lookup.ExternalOnly);
        if (accessibleChains.TryGetValue(key, out var cached))
            return cached;
        IReadOnlyList<Symbol>? result = null;
        foreach (var scope in SymbolTablesInScope(lookup.Enclosing, cancellation))
        {
            result = await ChainFromTableAsync(lookup, scope.Table, scope.Identity, false, scope.LocalNames, cancellation);
            if (result is not null)
                break;
        }
        cancellation.ThrowIfCancellationRequested();
        if (firstScope is not null && typeSyntaxScopes.ContainsKey(firstScope))
            return result;
        bool exists = accessibleChains.TryGetValue(key, out var previous);
        chainChanges?.Chains.TryAdd(key, (exists, previous));
        accessibleChains[key] = result;
        return result;
    }

    private async ValueTask<IReadOnlyList<Symbol>?> ChainFromTableAsync(ChainContext lookup, IReadOnlyDictionary<string, Symbol> table,
        SymbolTableIdentity identity, bool ignoreQualification, bool localNames, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var entry = (lookup.Symbol, identity);
        if (!lookup.ActiveTables.Add(entry))
            return null;
        try
        {
            BeforeSymbolChainTable?.Invoke(lookup.Symbol);
            var direct = table.GetValueOrDefault(lookup.Symbol.Name);
            if (direct is not null && await ChainSymbolAccessibleAsync(lookup, direct, null, ignoreQualification, cancellation))
                return Array.AsReadOnly(new[] { lookup.Symbol });
            IReadOnlyList<Symbol>? best = null;
            if (direct?.ExportSymbol is { } exported && await ChainSymbolAccessibleAsync(lookup,
                program.Symbols.Merger.GetMergedSymbol(exported)!, null, ignoreQualification, cancellation))
                best = Array.AsReadOnly(new[] { lookup.Symbol });
            foreach (var alias in SymbolTableAliases(table, identity))
            {
                cancellation.ThrowIfCancellationRequested();
                if (alias.Name is "default" or "export="
                    || alias.Declarations.FirstOrDefault() is NamespaceExportDeclarationNode
                        && SemanticSyntax.Source(lookup.Enclosing)?.ExternalModuleIndicator is not null
                    || lookup.ExternalOnly
                        && !alias.Declarations.Any(d => d is ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode })
                    || localNames
                        && alias.Declarations.Any(
                            d => d is NamespaceExportNode { Parent: ExportDeclarationNode { ModuleSpecifier: not null } })
                    || !ignoreQualification && alias.Declarations.Any(d => d is ExportSpecifierNode))
                    continue;
                var resolved = await program.Aliases.ResolveAsync(alias, cancellation);
                var candidate = await ChainThroughAliasAsync(lookup, alias, resolved, ignoreQualification, cancellation);
                if (candidate is not null && (best is null || CompareChains(candidate, best) < 0))
                    best = candidate;
            }
            if (best is not null)
                return best;
            return identity.Kind == SymbolTableKind.Globals
                ? await ChainThroughAliasAsync(lookup, program.Symbols.GlobalThisSymbol, program.Symbols.GlobalThisSymbol,
                    ignoreQualification, cancellation) : null;
        }
        finally
        {
            lookup.ActiveTables.Remove(entry);
        }
    }

    private IReadOnlyList<Symbol> SymbolTableAliases(IReadOnlyDictionary<string, Symbol> table, SymbolTableIdentity identity)
    {
        if (identity.Kind == SymbolTableKind.Members)
            return [];
        bool cache = identity.Kind is SymbolTableKind.Globals or SymbolTableKind.Exports or SymbolTableKind.ResolvedExports;
        if (cache && tableAliases.TryGetValue(identity, out var result))
            return result;
        result = table.Values.Where(s => (s.Flags & SymbolFlags.Alias) != 0).ToArray();
        if (cache)
        {
            chainChanges?.Aliases.Add(identity);
            tableAliases.Add(identity, result);
        }
        return result;
    }

    private int CompareChains(IReadOnlyList<Symbol> first, IReadOnlyList<Symbol> second)
    {
        int result = first.Count.CompareTo(second.Count);
        for (int i = 0; result == 0 && i < first.Count; i++)
            result = Algebra.Order.CompareSymbols(first[i], second[i]);
        return result;
    }

    private async ValueTask<IReadOnlyList<Symbol>?> ChainThroughAliasAsync(ChainContext lookup, Symbol alias, Symbol resolved,
        bool ignoreQualification, CancellationToken cancellation)
    {
        if (await ChainSymbolAccessibleAsync(lookup, alias, resolved, ignoreQualification, cancellation))
            return Array.AsReadOnly(new[] { alias });
        var exports = await ExportsAsync(resolved, cancellation);
        var chain = await ChainFromTableAsync(lookup, exports, new(SymbolTableKind.ResolvedExports, resolved), true, false, cancellation);
        if (chain is null || !await CanQualifyAsync(lookup, alias, QualifiedLeftMeaning(lookup.Meaning), cancellation))
            return null;
        var result = new Symbol[chain.Count + 1];
        result[0] = alias;
        for (int i = 0; i < chain.Count; i++)
            result[i + 1] = chain[i];
        return Array.AsReadOnly(result);
    }

    private async ValueTask<bool> ChainSymbolAccessibleAsync(ChainContext lookup, Symbol candidate, Symbol? resolved,
        bool ignoreQualification, CancellationToken cancellation)
    {
        var merger = program.Symbols.Merger;
        var target = merger.GetMergedSymbol(lookup.Symbol);
        if (lookup.Symbol != candidate && lookup.Symbol != resolved && target != merger.GetMergedSymbol(candidate)
            && target != merger.GetMergedSymbol(resolved))
            return false;
        if (candidate.Declarations.Any(d => d is ModuleDeclarationNode { Name: StringLiteralNode }
            || d is SourceFileNode && program.Symbols.Binding(d)?.IsModule == true))
            return false;
        return ignoreQualification || await CanQualifyAsync(lookup, merger.GetMergedSymbol(candidate)!, lookup.Meaning, cancellation);
    }

    private async ValueTask<bool> CanQualifyAsync(
        ChainContext lookup,
        Symbol candidate,
        SymbolFlags meaning,
        CancellationToken cancellation)
    {
        if (!await NeedsQualificationAsync(candidate, lookup.Enclosing, meaning, cancellation))
            return true;
        return candidate.Parent is { } parent && await AccessibleChainAsync(lookup with
        { Symbol = parent, Meaning = QualifiedLeftMeaning(meaning) }, cancellation) is not null;
    }

    private async ValueTask<bool> NeedsQualificationAsync(
        Symbol symbol,
        SyntaxNode? enclosing,
        SymbolFlags meaning,
        CancellationToken cancellation)
    {
        foreach (var scope in SymbolTablesInScope(enclosing, cancellation))
        {
            var found = program.Symbols.Merger.GetMergedSymbol(scope.Table.GetValueOrDefault(symbol.Name));
            if (found is null)
                continue;
            if (found == symbol)
                return false;
            bool alias = (found.Flags & SymbolFlags.Alias) != 0 && !found.Declarations.Any(d => d is ExportSpecifierNode);
            if (alias)
                found = await program.Aliases.ResolveAsync(found, cancellation);
            var flags = alias ? await program.Aliases.FlagsAsync(found, cancellation: cancellation) : found.Flags;
            if ((flags & meaning) != 0)
                return true;
        }
        return false;
    }

    private static SymbolFlags QualifiedLeftMeaning(SymbolFlags meaning) =>
        meaning == SymbolFlags.Value ? SymbolFlags.Value : SymbolFlags.Namespace;

    private IEnumerable<ScopeTable> SymbolTablesInScope(SyntaxNode? enclosing, CancellationToken cancellation)
    {
        for (var location = enclosing; location is not null; location = location.Parent)
        {
            cancellation.ThrowIfCancellationRequested();
            if (typeSyntaxScopes.TryGetValue(location, out var synthetic))
                yield return new(synthetic, new(SymbolTableKind.Locals, location), true, location);
            var binding = program.Symbols.Binding(location);
            if (!(location is SourceFileNode && binding?.IsModule != true) && binding?.Get(location) is { HasLocals: true } data)
                yield return new(data.Locals, new(SymbolTableKind.Locals, location), true, location);
            switch (location)
            {
                case SourceFileNode when binding?.IsModule != true:
                    break;
                case SourceFileNode or ModuleDeclarationNode:
                    var module = program.Symbols.Declaration(QuerySyntax.Reparsed(location))!;
                    yield return new(module.Exports, new(SymbolTableKind.Exports, module), true, location);
                    break;
                case ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode:
                    var symbol = program.Symbols.Declaration(location)!;
                    var members = symbol.Members.Where(p => (p.Value.Flags & (SymbolFlags.Type & ~SymbolFlags.Assignment)) != 0)
                        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                    if (members.Count != 0)
                        yield return new(members, new(SymbolTableKind.Members, symbol), false, location);
                    if (location is ClassExpressionNode { Name: IdentifierNode { Text.Length: > 0 } name })
                    {
                        if (!classNameTables.TryGetValue(location, out var table))
                        {
                            table = new Dictionary<string, Symbol>(StringComparer.Ordinal) { [name.Text] = symbol }.AsReadOnly();
                            classNameTables.Add(location, table);
                            chainChanges?.ClassNames.Add(location);
                        }
                        yield return new(table, new(SymbolTableKind.Locals, location), true, location);
                    }
                    break;
            }
        }
        yield return new(program.Symbols.Globals, new(SymbolTableKind.Globals, null), true, null);
    }
}
