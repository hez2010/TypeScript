using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal interface ILateMemberHost
{
    ValueTask<Type> ComputedNameAsync(ComputedPropertyNameNode node, CancellationToken cancellation);

    ValueTask<Type> ConstAssertionAsync(SyntaxNode expression, CancellationToken cancellation);

    ValueTask<bool> LateIndexTypeAsync(Type type, CancellationToken cancellation);

    ValueTask<IReadOnlyDictionary<string, Symbol>> ModuleExportsAsync(Symbol symbol, CancellationToken cancellation);

    void ExpressionError(SyntaxNode node, int code);
}

internal sealed class LateMembers(CheckerSymbols symbols, CheckerLinks links, ILateMemberHost host)
{
    private readonly Dictionary<Symbol, Symbol> late = [];
    private readonly Dictionary<(Symbol, bool), IReadOnlyDictionary<string, Symbol>> tables = [];
    private List<Action>? rollback;
    internal int CachedTableCount => tables.Count;

    internal ValueTask<Symbol> SymbolAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & SymbolFlags.ClassMember) == 0 || symbol.Name != Symbol.InternalPrefix + "computed")
            return ValueTask.FromResult(symbol);
        if (late.TryGetValue(symbol, out var cached))
            return ValueTask.FromResult(cached);
        return RunAsync(async () =>
        {
            foreach (var declaration in symbol.Declarations)
                if (await BindableAsync(declaration, cancellation).ConfigureAwait(false))
                {
                    var parent = symbols.Merger.GetMergedSymbol(symbol.Parent)!;
                    await TableAsync(parent, symbol.Declarations.Any(SemanticSyntax.IsStatic), cancellation).ConfigureAwait(false);
                    break;
                }
            if (late.TryGetValue(symbol, out cached))
                return cached;
            SetLate(symbol, symbol);
            return symbol;
        });
    }

    internal ValueTask<IReadOnlyDictionary<string, Symbol>> TableAsync(
        Symbol symbol,
        bool exports = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & SymbolFlags.LateBindingContainer) == 0)
            return exports && (symbol.Flags & SymbolFlags.Module) != 0 ? host.ModuleExportsAsync(symbol, cancellation)
                : ValueTask.FromResult(exports ? symbol.Exports : symbol.Members);
        if (tables.TryGetValue((symbol, exports), out var cached))
            return ValueTask.FromResult(cached);
        return RunAsync(async () =>
        {
            var early = exports && (symbol.Flags & SymbolFlags.Module) != 0
                ? await host.ModuleExportsAsync(symbol, cancellation).ConfigureAwait(false) : exports ? symbol.Exports : symbol.Members;
            tables[(symbol, exports)] = early;
            rollback!.Add(() => tables.Remove((symbol, exports)));
            var members = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            foreach (var declaration in symbol.Declarations)
                foreach (var member in Members(declaration))
                    if (SemanticSyntax.IsStatic(member) == exports)
                        await BindAsync(member).ConfigureAwait(false);
            if (exports && symbol.Exports.TryGetValue(Symbol.InternalPrefix + "assignment", out var assignments))
                foreach (var member in assignments.Declarations)
                    if (await BindableAsync(member, cancellation).ConfigureAwait(false))
                        await BindMemberAsync(symbol, early, members, member, cancellation).ConfigureAwait(false);
            if (members.Count == 0)
                return early;
            if (early.Count == 0)
                return tables[(symbol, exports)] = members.AsReadOnly();
            var combined = new Dictionary<string, Symbol>(StringComparer.Ordinal);
            await symbols.Merger.MergeTableAsync(combined, early, cancellation: cancellation).ConfigureAwait(false);
            await symbols.Merger.MergeTableAsync(combined, members, cancellation: cancellation).ConfigureAwait(false);
            return tables[(symbol, exports)] = combined.AsReadOnly();

            async ValueTask BindAsync(SyntaxNode member)
            {
                if (await BindableAsync(member, cancellation).ConfigureAwait(false))
                    await BindMemberAsync(symbol, early, members, member, cancellation).ConfigureAwait(false);
                else if (Name(member) is { } name && LateSyntax(name)
                    && await host.LateIndexTypeAsync(
                        await NameTypeAsync(name, cancellation).ConfigureAwait(false),
                        cancellation).ConfigureAwait(false))
                {
                    string indexName = Symbol.InternalPrefix + "index";
                    if (!members.TryGetValue(indexName, out var index))
                    {
                        if (early.TryGetValue(indexName, out var original))
                        {
                            index = new Symbol(original.Flags | SymbolFlags.Transient, original.Name)
                            {
                                CheckFlags = original.CheckFlags | CheckFlags.Late,
                                Parent = original.Parent,
                                ValueDeclaration = original.ValueDeclaration
                            };
                            index.DeclarationList.AddRange(original.Declarations);
                        }
                        else
                            index = new Symbol(SymbolFlags.Transient, indexName) { CheckFlags = CheckFlags.Late };
                        members[indexName] = index;
                    }
                    if (index.Declarations.Count == 0 || (Raw(member)!.Flags & SymbolFlags.ReplaceableByMethod) == 0)
                        index.DeclarationList.Add(member);
                }
            }
        });
    }

    internal async ValueTask<bool> BindableAsync(SyntaxNode declaration, CancellationToken cancellation = default) =>
        Name(declaration) is { } name && LateSyntax(name)
            && ((await NameTypeAsync(name, cancellation).ConfigureAwait(false)).Flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0;

    private async ValueTask<Symbol> BindMemberAsync(Symbol parent, IReadOnlyDictionary<string, Symbol> early,
        Dictionary<string, Symbol> members, SyntaxNode declaration, CancellationToken cancellation)
    {
        var data = links.SymbolNodes.Get(declaration);
        if (data.ResolvedSymbol is { } cached)
            return cached;
        var original = Raw(declaration)!;
        data.ResolvedSymbol = original;
        rollback!.Add(() => data.ResolvedSymbol = null);
        var nameNode = Name(declaration)!;
        var type = await NameTypeAsync(nameNode, cancellation).ConfigureAwait(false);
        if ((type.Flags & TypeFlags.StringOrNumberLiteralOrUnique) == 0)
            return original;
        string name = MappedMembers.PropertyName(type);
        if (!members.TryGetValue(name, out var symbol))
            members[name] = symbol = new Symbol(SymbolFlags.Transient, name) { CheckFlags = CheckFlags.Late };
        if ((symbol.Flags & SymbolMerger.ExcludedFlags(original.Flags)) != 0)
        {
            var declarations = early.TryGetValue(name, out var initial)
                ? initial.Declarations.Concat(symbol.Declarations)
                : symbol.Declarations;
            foreach (var previous in declarations)
                host.ExpressionError((previous as INamedNode)?.Name ?? previous, 2300);
            host.ExpressionError(nameNode, 2300);
            if ((symbol.Flags & SymbolFlags.Accessor) != 0
                && (symbol.Flags & SymbolFlags.Accessor) != (original.Flags & SymbolFlags.Accessor))
                symbol.Flags |= SymbolFlags.Accessor;
            symbol = new Symbol(SymbolFlags.Transient, name) { CheckFlags = CheckFlags.Late };
        }
        links.Values.Get(symbol).NameType = type;
        SetLate(original, symbol);
        if (symbol.Declarations.Count == 0 || (original.Flags & SymbolFlags.ReplaceableByMethod) == 0)
        {
            symbol.Flags |= original.Flags;
            symbol.DeclarationList.Add(declaration);
        }
        else if ((symbol.Flags & SymbolFlags.ReplaceableByMethod) != 0 && (original.Flags & SymbolFlags.Method) != 0)
        {
            symbol.DeclarationList.RemoveAll(d => (Raw(d)!.Flags & SymbolFlags.ReplaceableByMethod) != 0);
            symbol.DeclarationList.Add(declaration);
            var oldFlags = symbol.Flags;
            symbol.Flags = 0;
            foreach (var item in symbol.Declarations)
                symbol.Flags |= Raw(item)!.Flags;
            if ((oldFlags & SymbolFlags.Accessor) != 0)
                symbol.Flags |= SymbolFlags.Accessor;
        }
        if ((original.Flags & SymbolFlags.Value) != 0
            && (symbol.ValueDeclaration is not { } existing || existing is BinaryExpressionNode or CallExpressionNode
                && declaration is not BinaryExpressionNode and not CallExpressionNode || existing.Kind != declaration.Kind
                    && existing.Kind == SyntaxKind.ModuleDeclaration))
            symbol.ValueDeclaration = declaration;
        symbol.Parent ??= parent;
        return data.ResolvedSymbol = symbol;
    }

    private async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> action)
    {
        bool owner = rollback is null;
        rollback ??= [];
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch
        {
            if (owner)
                for (int i = rollback.Count - 1; i >= 0; i--)
                    rollback[i]();
            throw;
        }
        finally
        {
            if (owner)
                rollback = null;
        }
    }

    private void SetLate(Symbol original, Symbol value)
    {
        var old = late.GetValueOrDefault(original);
        rollback!.Add(() =>
        {
            if (old is null)
                late.Remove(original);
            else
                late[original] = old;
        });
        late[original] = value;
    }

    private Symbol? Raw(SyntaxNode node) => symbols.Binding(node)?.Get(node)?.Symbol;

    internal static SyntaxNode? Name(SyntaxNode node) => node is BinaryExpressionNode binary ? binary.Left : (node as INamedNode)?.Name;

    internal static bool LateSyntax(SyntaxNode name) => name switch
    {
        ComputedPropertyNameNode computed => ConstantEvaluator.EntityName(computed.Expression!),
        ElementAccessExpressionNode element => ConstantEvaluator.EntityName(element.ArgumentExpression!),
        _ => false
    };

    private ValueTask<Type> NameTypeAsync(SyntaxNode name, CancellationToken cancellation) => name is ElementAccessExpressionNode element
            ? host.ConstAssertionAsync(
                element.ArgumentExpression!,
                cancellation) : host.ComputedNameAsync((ComputedPropertyNameNode)name, cancellation);

    private static IEnumerable<SyntaxNode> Members(SyntaxNode declaration) => declaration switch
    {
        ClassDeclarationNode node => (IEnumerable<SyntaxNode>?)node.Members ?? [],
        ClassExpressionNode node => (IEnumerable<SyntaxNode>?)node.Members ?? [],
        InterfaceDeclarationNode node => (IEnumerable<SyntaxNode>?)node.Members ?? [],
        TypeLiteralNode node => (IEnumerable<SyntaxNode>?)node.Members ?? [],
        ObjectLiteralExpressionNode node => (IEnumerable<SyntaxNode>?)node.Properties ?? [],
        _ => []
    };
}
