using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

// Per-checker merging leaves bound symbols immutable. Only transient clones can
// be extended, so older programs and other checkers keep their declaration sets.
internal sealed class SymbolMerger(Symbol unknownSymbol, Symbol globalThisSymbol,
    Func<Symbol, Symbol> resolveSymbol, Action<Symbol, Symbol, bool> reportConflict)
{
    private readonly Dictionary<Symbol, Symbol> merged = new(ReferenceEqualityComparer.Instance);
    private MergeJournal? journal;

    internal Symbol? GetMergedSymbol(Symbol? symbol) => symbol is null ? null : merged.GetValueOrDefault(symbol, symbol);

    internal Symbol CloneSymbol(Symbol symbol)
    {
        var result = new Symbol(
            symbol.Flags | S.Transient,
            symbol.Name)
        { Parent = symbol.Parent, ValueDeclaration = symbol.ValueDeclaration };
        result.DeclarationList.AddRange(symbol.Declarations);
        foreach (var pair in symbol.Members)
            result.MemberTable.Add(pair.Key, pair.Value);
        foreach (var pair in symbol.Exports)
            result.ExportTable.Add(pair.Key, pair.Value);
        RecordMerge(symbol, result);
        return result;
    }

    internal async ValueTask MergeTableAsync(Dictionary<TextSlice, Symbol> target, IReadOnlyDictionary<TextSlice, Symbol> source,
        bool unidirectional = false, Symbol? mergedParent = null, CancellationToken cancellation = default)
    {
        bool ownsJournal = journal is null;
        journal ??= new();
        try
        {
            await MergeTableCore(target, source, unidirectional, mergedParent, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (ownsJournal)
                journal.Rollback(merged);
            throw;
        }
        finally
        {
            if (ownsJournal)
                journal = null;
        }
    }

    private async ValueTask MergeTableCore(Dictionary<TextSlice, Symbol> target, IReadOnlyDictionary<TextSlice, Symbol> source,
        bool unidirectional, Symbol? mergedParent, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        journal!.Capture(target);
        foreach (var pair in source)
        {
            cancellation.ThrowIfCancellationRequested();
            var previous = target.GetValueOrDefault(pair.Key);
            var result = previous is null ? GetMergedSymbol(pair.Value)!
                : await MergeCore(previous, pair.Value, unidirectional, cancellation).ConfigureAwait(false);
            if (mergedParent is not null && previous is not null && (result.Flags & S.Transient) != 0)
            {
                journal.Capture(result);
                result.Parent = mergedParent;
            }
            target[pair.Key] = result;
        }
    }

    internal async ValueTask<Symbol> MergeAsync(
        Symbol target,
        Symbol source,
        bool unidirectional = false,
        CancellationToken cancellation = default)
    {
        bool ownsJournal = journal is null;
        journal ??= new();
        try
        {
            return await MergeCore(target, source, unidirectional, cancellation).ConfigureAwait(false);
        }
        catch
        {
            if (ownsJournal)
                journal.Rollback(merged);
            throw;
        }
        finally
        {
            if (ownsJournal)
                journal = null;
        }
    }

    private async ValueTask<Symbol> MergeCore(Symbol target, Symbol source, bool unidirectional, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((target.Flags & ExcludedFlags(source.Flags)) == 0 || ((source.Flags | target.Flags) & S.Assignment) != 0)
        {
            if (source == target)
                return target;
            if ((target.Flags & S.Transient) == 0)
            {
                var resolved = resolveSymbol(target);
                if (resolved == unknownSymbol)
                    return source;
                if ((resolved.Flags & ExcludedFlags(source.Flags)) == 0 || ((source.Flags | resolved.Flags) & S.Assignment) != 0)
                    target = CloneSymbol(resolved);
                else
                {
                    reportConflict(target, source, false);
                    return source;
                }
            }
            journal!.Capture(target);
            if ((source.Flags & S.ValueModule) != 0
                && (target.Flags & (S.ValueModule | S.ConstEnumOnlyModule)) == (S.ValueModule | S.ConstEnumOnlyModule)
                && (source.Flags & S.ConstEnumOnlyModule) == 0)
                target.Flags &= ~S.ConstEnumOnlyModule;
            target.Flags |= (target.Flags & S.ConstEnumOnlyModule) == 0 ? source.Flags & ~S.ConstEnumOnlyModule : source.Flags;
            if (source.ValueDeclaration is { } value)
                SetValueDeclaration(target, value);
            target.DeclarationList.AddRange(source.Declarations);
            if (source.Members.Count != 0)
                await MergeTableCore(target.MemberTable, source.Members, unidirectional, null, cancellation).ConfigureAwait(false);
            if (source.Exports.Count != 0)
                await MergeTableCore(target.ExportTable, source.Exports, unidirectional, target, cancellation).ConfigureAwait(false);
            if (!unidirectional)
                RecordMerge(source, target);
        }
        else if ((target.Flags & S.NamespaceModule) != 0)
        {
            if (target != globalThisSymbol)
                reportConflict(target, source, true);
        }
        else
            reportConflict(target, source, false);
        return target;
    }

    private void RecordMerge(Symbol source, Symbol target)
    {
        journal?.CaptureMapping(source, merged.GetValueOrDefault(source));
        merged[source] = target;
    }

    // A canceled or failed operation restores owned merge state. Diagnostics and
    // alias resolution belong to the caller and retain their own request lifetime.
    private sealed class MergeJournal
    {
        private sealed record Snapshot(S Flags, Symbol? Parent, SyntaxNode? Value, SyntaxNode[] Declarations);

        private readonly Dictionary<Symbol, Snapshot> symbols = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Dictionary<TextSlice, Symbol>, KeyValuePair<TextSlice, Symbol>[]> tables = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Symbol, Symbol?> mappings = new(ReferenceEqualityComparer.Instance);

        internal void Capture(Symbol symbol)
        {
            if (!symbols.ContainsKey(symbol))
                symbols.Add(symbol, new(symbol.Flags, symbol.Parent, symbol.ValueDeclaration, symbol.Declarations.ToArray()));
        }

        internal void Capture(Dictionary<TextSlice, Symbol> table)
        {
            if (!tables.ContainsKey(table))
                tables.Add(table, table.ToArray());
        }

        internal void CaptureMapping(Symbol source, Symbol? previous) => mappings.TryAdd(source, previous);

        internal void Rollback(Dictionary<Symbol, Symbol> merged)
        {
            foreach (var (symbol, snapshot) in symbols)
            {
                symbol.Flags = snapshot.Flags;
                symbol.Parent = snapshot.Parent;
                symbol.ValueDeclaration = snapshot.Value;
                symbol.DeclarationList.Clear();
                symbol.DeclarationList.AddRange(snapshot.Declarations);
            }
            foreach (var (table, entries) in tables)
            {
                table.Clear();
                foreach (var pair in entries)
                    table.Add(pair.Key, pair.Value);
            }
            foreach (var (source, previous) in mappings)
                if (previous is null)
                    merged.Remove(source);
                else
                    merged[source] = previous;
        }
    }

    private static void SetValueDeclaration(Symbol symbol, SyntaxNode node)
    {
        static bool Assignment(SyntaxNode n) => n.Kind is K.BinaryExpression or K.PropertyAccessExpression or K.ElementAccessExpression
            or K.Identifier or K.CallExpression;
        if (symbol.ValueDeclaration is not { } existing || Assignment(existing) && !Assignment(node)
            || existing.Kind != node.Kind && existing.Kind is K.ModuleDeclaration or K.Identifier)
            symbol.ValueDeclaration = node;
    }

    internal static S ExcludedFlags(S flags)
    {
        S result = 0;
        if ((flags & S.BlockScopedVariable) != 0)
            result |= S.BlockScopedVariableExcludes;
        if ((flags & S.FunctionScopedVariable) != 0)
            result |= S.FunctionScopedVariableExcludes;
        if ((flags & S.Property) != 0)
            result |= S.PropertyExcludes;
        if ((flags & S.EnumMember) != 0)
            result |= S.EnumMemberExcludes;
        if ((flags & S.Function) != 0)
            result |= S.FunctionExcludes;
        if ((flags & S.Class) != 0)
            result |= S.ClassExcludes;
        if ((flags & S.Interface) != 0)
            result |= S.InterfaceExcludes;
        if ((flags & S.RegularEnum) != 0)
            result |= S.RegularEnumExcludes;
        if ((flags & S.ConstEnum) != 0)
            result |= S.ConstEnumExcludes;
        if ((flags & S.ValueModule) != 0)
            result |= S.ValueModuleExcludes;
        if ((flags & S.Method) != 0)
            result |= S.MethodExcludes;
        if ((flags & S.GetAccessor) != 0)
            result |= S.GetAccessorExcludes;
        if ((flags & S.SetAccessor) != 0)
            result |= S.SetAccessorExcludes;
        if ((flags & S.TypeParameter) != 0)
            result |= S.TypeParameterExcludes;
        if ((flags & S.TypeAlias) != 0)
            result |= S.TypeAliasExcludes;
        if ((flags & S.Alias) != 0)
            result |= S.AliasExcludes;
        if ((flags & S.ReplaceableByMethod) != 0)
            result &= ~S.Method;
        return result;
    }
}
