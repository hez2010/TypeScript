using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly Dictionary<(Symbol, SourceFileNode), IReadOnlyList<Symbol>> containingModulesByFile = [];
    private readonly Dictionary<Symbol, IReadOnlyList<Symbol>> containingModules = [];
    private ContainerChanges? containerChanges;
    internal int SymbolContainerCacheCount => containingModules.Count + containingModulesByFile.Count;
    internal Action<Symbol>? BeforeSymbolContainer { get; set; }

    private sealed class ContainerChanges
    {
        internal HashSet<(Symbol, SourceFileNode)> Files { get; } = [];
        internal HashSet<Symbol> Symbols { get; } = [];
    }

    private async ValueTask<T> ContainerOperationAsync<T>(Func<ValueTask<T>> action, CancellationToken cancellation)
    {
        var changes = new ContainerChanges();
        containerChanges = changes;
        try
        {
            var result = await action();
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            foreach (var key in changes.Files)
                containingModulesByFile.Remove(key);
            foreach (var symbol in changes.Symbols)
                containingModules.Remove(symbol);
            throw;
        }
        finally
        {
            containerChanges = null;
        }
    }

    internal async ValueTask<IReadOnlyList<Symbol>> GetContainersOfSymbolAsync(Symbol symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(enclosing, cancellation).ConfigureAwait(false);
        return await ChainOperationAsync(() => ContainerOperationAsync(
            () => ContainersOfSymbolAsync(symbol, enclosing, meaning, cancellation), cancellation), cancellation);
    }

    private async ValueTask<IReadOnlyList<Symbol>> ContainersOfSymbolAsync(Symbol symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        BeforeSymbolContainer?.Invoke(symbol);
        var parent = program.Symbols.Parent(symbol);
        if (parent is not null && (symbol.Flags & SymbolFlags.TypeParameter) == 0)
            return await AlternativeContainersAsync(parent, symbol, enclosing, meaning, cancellation);
        var candidates = new List<Symbol>();
        foreach (var declaration in symbol.Declarations)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!AmbientModule(declaration) && declaration.Parent is { } owner)
            {
                if (NonGlobalExternalModule(owner))
                {
                    Add(program.Symbols.Declaration(owner));
                    continue;
                }
                if (owner is ModuleBlockNode && owner.Parent is { } module
                    && await program.AliasTargets.ExternalModuleAsync(program.Symbols.Declaration(module), false, cancellation) == symbol)
                {
                    Add(program.Symbols.Declaration(module));
                    continue;
                }
            }
            if (declaration is ClassExpressionNode
                && declaration.Parent is BinaryExpressionNode { OperatorToken.Kind: SyntaxKind.EqualsToken } assignment
                && assignment.Left is PropertyAccessExpressionNode or ElementAccessExpressionNode
                && FlowReferences.Receiver(assignment.Left) is { } receiver && EntityExpression(receiver))
            {
                if (ModuleExportsAccess(assignment.Left) || receiver is IdentifierNode { Text: "exports" })
                    Add(program.Symbols.Declaration(SemanticSyntax.Source(declaration)!));
                else
                {
                    await CachedExpressionAsync(receiver, 0, cancellation);
                    Add(links.SymbolNodes.Get(receiver).ResolvedSymbol);
                }
            }
        }
        var best = new List<Symbol>();
        var alternatives = new List<Symbol>();
        foreach (var candidate in candidates)
        {
            if (await AliasInContainerAsync(candidate, symbol, cancellation) is null)
                continue;
            var containers = await AlternativeContainersAsync(candidate, symbol, enclosing, meaning, cancellation);
            if (containers.Count == 0)
                continue;
            best.Add(containers[0]);
            for (int i = 1; i < containers.Count; i++)
                alternatives.Add(containers[i]);
        }
        best.AddRange(alternatives);
        return best.AsReadOnly();
        void Add(Symbol? candidate)
        {
            if (candidate is not null && !candidates.Contains(candidate))
                candidates.Add(candidate);
        }
    }

    private async ValueTask<IReadOnlyList<Symbol>> AlternativeContainersAsync(Symbol container, Symbol symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, CancellationToken cancellation)
    {
        var additional = new List<Symbol>();
        foreach (var declaration in container.Declarations)
            if (await ExportEqualsContainerAsync(declaration, container, cancellation) is { } file)
                additional.Add(file);
        var reexports = enclosing is null ? [] : await AlternativeModulesAsync(symbol, enclosing, cancellation);
        var literalContainer = VariableContainer(container, meaning);
        var leftMeaning = QualifiedLeftMeaning(meaning);
        if (enclosing is not null && (container.Flags & leftMeaning) != 0
            && await AccessibleChainAsync(new(container, enclosing, SymbolFlags.Namespace, false, []), cancellation) is not null)
        {
            var result = new List<Symbol> { container };
            result.AddRange(additional);
            result.AddRange(reexports);
            if (literalContainer is not null)
                result.Add(literalContainer);
            return result.AsReadOnly();
        }
        var variables = new List<Symbol>();
        if (meaning == SymbolFlags.Value && (container.Flags & leftMeaning) == 0 && (container.Flags & SymbolFlags.Type) != 0
            && await Declared.GetAsync(container, cancellation) is ObjectType declared)
        {
            foreach (var scope in SymbolTablesInScope(enclosing, cancellation))
            {
                foreach (var candidate in scope.Table.Values)
                    if ((candidate.Flags & leftMeaning) != 0 && await Values.GetAsync(candidate, cancellation) == declared)
                        variables.Add(candidate);
                if (variables.Count != 0)
                    break;
            }
            variables.Sort(Algebra.Order.CompareSymbols);
        }
        variables.AddRange(additional);
        variables.Add(container);
        if (literalContainer is not null)
            variables.Add(literalContainer);
        variables.AddRange(reexports);
        return variables.AsReadOnly();
    }

    private async ValueTask<IReadOnlyList<Symbol>> AlternativeModulesAsync(
        Symbol symbol,
        SyntaxNode enclosing,
        CancellationToken cancellation)
    {
        var file = SemanticSyntax.Source(enclosing)!;
        var key = (symbol, file);
        if (containingModulesByFile.TryGetValue(key, out var existing))
            return existing;
        var result = new List<Symbol>();
        foreach (var import in file.Imports)
        {
            if (import.Pos < 0 || import.End < 0)
                continue;
            var module = await ResolveImportModuleAsync(enclosing, import, await ModuleSpecifierAttributesAsync(import, cancellation),
                cancellation, ignoreErrors: true, resolutionMode: ContainerResolutionMode(enclosing));
            if (module is not null && await AliasInContainerAsync(module, symbol, cancellation) is not null)
                result.Add(module);
        }
        if (result.Count != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            containerChanges?.Files.Add(key);
            return containingModulesByFile[key] = result.AsReadOnly();
        }
        if (containingModules.TryGetValue(symbol, out existing))
            return existing;
        foreach (var source in program.Symbols.Program.SourceFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            if (source.Syntax.ExternalModuleIndicator is null)
                continue;
            var module = program.Symbols.Declaration(source.Syntax)!;
            if (await AliasInContainerAsync(module, symbol, cancellation) is not null)
                result.Add(module);
        }
        cancellation.ThrowIfCancellationRequested();
        containerChanges?.Symbols.Add(symbol);
        return containingModules[symbol] = result.AsReadOnly();
    }

    private Symbol? VariableContainer(Symbol symbol, SymbolFlags meaning)
    {
        if ((meaning & SymbolFlags.Value) == 0
            || symbol.Declarations.FirstOrDefault() is not { Parent: VariableDeclarationNode declaration } first)
            return null;
        return first is ObjectLiteralExpressionNode && declaration.Initializer == first
            || first is TypeLiteralNode && declaration.Type == first
            ? program.Symbols.Declaration(declaration) : null;
    }

    private ReferenceResolutionMode ContainerResolutionMode(SyntaxNode location)
    {
        SyntaxNode? specifier = location switch
        {
            StringLiteralNode or NoSubstitutionTemplateLiteralNode => location,
            ModuleDeclarationNode module => module.Name,
            ImportTypeNode { Argument: LiteralTypeNode { Literal: StringLiteralNode literal } } => literal,
            _ when location.Parent is ModuleDeclarationNode module && module.Name == location => location,
            _ => null
        };
        if (location is VariableDeclarationNode { Initializer: { } initializer })
        {
            while (initializer is PropertyAccessExpressionNode or ElementAccessExpressionNode)
                initializer = FlowReferences.Receiver(initializer)!;
            if (initializer is CallExpressionNode call && SemanticSyntax.RequireCall(call))
                specifier = call.Arguments![0];
        }
        if (specifier is null)
        {
            var owner = DeclarationOrder.Ancestor(location, n => n is CallExpressionNode call && IsImportCall(call));
            owner ??= DeclarationOrder.Ancestor(location, n => n is ImportDeclarationNode);
            owner ??= DeclarationOrder.Ancestor(location, n => n is ExportDeclarationNode);
            owner ??= DeclarationOrder.Ancestor(location, n => n is ImportEqualsDeclarationNode);
            specifier = owner switch
            {
                CallExpressionNode { Arguments.Count: > 0 } call => call.Arguments[0],
                ImportDeclarationNode import => import.ModuleSpecifier,
                ExportDeclarationNode export => export.ModuleSpecifier,
                ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode module } => module.Expression,
                _ => null
            };
        }
        return program.Symbols.Program.ResolutionModeForUsage(SemanticSyntax.Source(location)!,
            specifier is StringLiteralNode or NoSubstitutionTemplateLiteralNode ? specifier : null);
    }

    private bool NonGlobalExternalModule(SyntaxNode declaration) => declaration is ModuleDeclarationNode { Name: StringLiteralNode }
        || declaration is SourceFileNode && program.Symbols.Binding(declaration)?.IsModule == true;

    private Symbol? ExternalModuleContainer(SyntaxNode declaration)
    {
        var container = DeclarationOrder.Ancestor(declaration, n => AmbientModule(n)
            || n is SourceFileNode && program.Symbols.Binding(n)?.IsModule == true);
        return container is null ? null : program.Symbols.Declaration(container);
    }

    private async ValueTask<Symbol?> ExportEqualsContainerAsync(SyntaxNode declaration, Symbol container, CancellationToken cancellation)
    {
        var file = ExternalModuleContainer(declaration);
        return file?.Exports.GetValueOrDefault("export=") is { } exported
            && await SameSymbolReferenceAsync(exported, container, cancellation)
            ? file : null;
    }

    private async ValueTask<bool> SameSymbolReferenceAsync(Symbol first, Symbol second, CancellationToken cancellation)
    {
        var merged = program.Symbols.Merger;
        return merged.GetMergedSymbol(await program.Aliases.SymbolAsync(merged.GetMergedSymbol(first), cancellation: cancellation))
            == merged.GetMergedSymbol(await program.Aliases.SymbolAsync(merged.GetMergedSymbol(second), cancellation: cancellation));
    }

    private async ValueTask<Symbol?> AliasInContainerAsync(Symbol container, Symbol symbol, CancellationToken cancellation)
    {
        if (container == program.Symbols.Parent(symbol))
            return symbol;
        if (container.Exports.GetValueOrDefault("export=") is { } exported
            && await SameSymbolReferenceAsync(exported, symbol, cancellation))
            return container;
        var exports = await ExportsAsync(container, cancellation);
        if (exports.GetValueOrDefault(symbol.Name) is { } quick && await SameSymbolReferenceAsync(quick, symbol, cancellation))
            return quick;
        Symbol? best = null;
        foreach (var candidate in exports.Values)
            if (await SameSymbolReferenceAsync(candidate, symbol, cancellation)
                && (best is null || Algebra.Order.CompareSymbols(candidate, best) < 0))
                best = candidate;
        return best;
    }
}
