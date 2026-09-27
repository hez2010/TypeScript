using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal enum SymbolAccessibility
{
    Accessible,
    NotAccessible,
    CannotBeNamed,
    NotResolved
}

internal sealed record SymbolAccessibilityResult(SymbolAccessibility Accessibility, IReadOnlyList<SyntaxNode>? AliasesToMakeVisible = null,
    TextSlice ErrorSymbolName = default, TextSlice ErrorModuleName = default, SyntaxNode? ErrorNode = null);

// Preserve the symbols that require diagnostic names. Formatting those names
// belongs to the node-builder service, separate from the accessibility decision.
internal sealed record SymbolAccessibilityDecision(
    SymbolAccessibility Accessibility,
    IReadOnlyList<SyntaxNode>? AliasesToMakeVisible = null,
    Symbol? ErrorSymbol = null,
    Symbol? ErrorModule = null,
    SyntaxNode? ErrorNode = null,
    SymbolFlags ErrorMeaning = SymbolFlags.None);

internal sealed partial class Checker
{
    internal ValueTask<SymbolAccessibilityResult> GetSymbolAccessibilityAsync(Symbol? symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, bool computeAliases = false, bool allowModules = true, CancellationToken cancellation = default) =>
        VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(async () =>
        {
            var result = await SymbolAccessibilityAsync(symbol, enclosing, meaning, computeAliases, allowModules, cancellation);
            TextSlice name = result.ErrorSymbol is null
                ? ""
                : await SymbolDisplayNameAsync(result.ErrorSymbol, enclosing, result.ErrorMeaning, cancellation);
            TextSlice module = result.ErrorModule is null ? "" : await SymbolDisplayNameAsync(result.ErrorModule,
                result.Accessibility == SymbolAccessibility.CannotBeNamed ? null : enclosing,
                result.Accessibility == SymbolAccessibility.CannotBeNamed ? SymbolFlags.All : SymbolFlags.Namespace, cancellation);
            return new SymbolAccessibilityResult(result.Accessibility, result.AliasesToMakeVisible, name, module, result.ErrorNode);
        }, cancellation), cancellation), cancellation);

    internal ValueTask<SymbolAccessibilityResult> GetEntityNameVisibilityAsync(
        SyntaxNode entityName,
        SyntaxNode enclosing,
        CancellationToken cancellation = default) => VisibilityQueryAsync(
            entityName,
            () => ChainOperationAsync(
            () => ContainerOperationAsync(
            () => EntityNameVisibilityAsync(entityName, enclosing, cancellation), cancellation), cancellation), cancellation);

    private async ValueTask<SymbolAccessibilityResult> EntityNameVisibilityAsync(SyntaxNode entityName, SyntaxNode enclosing,
        CancellationToken cancellation, bool computeAliases = true)
    {
        RequireNode(enclosing);
        if ((entityName.Flags & NodeFlags.Synthesized) != 0)
            return new(SymbolAccessibility.NotAccessible);
        var parent = entityName.Parent;
        var meaning = parent is TypeQueryNode or ComputedPropertyNameNode or BinaryExpressionNode
            || parent is ExpressionWithTypeArgumentsNode && !QuerySyntax.PartOfType(parent)
            || parent is TypePredicateNode predicate && predicate.ParameterName == entityName
                ? SymbolFlags.Value | SymbolFlags.ExportValue
                : entityName is QualifiedNameNode or PropertyAccessExpressionNode || parent is ImportEqualsDeclarationNode
                    || parent is QualifiedNameNode qualified && qualified.Left == entityName
                    || parent is PropertyAccessExpressionNode property && property.Expression == entityName
                    || parent is ElementAccessExpressionNode element && element.Expression == entityName
                        ? SymbolFlags.Namespace : SymbolFlags.Type;
        var first = entityName;
        while (first is QualifiedNameNode or PropertyAccessExpressionNode)
            first = first is QualifiedNameNode name ? name.Left! : ((PropertyAccessExpressionNode)first).Expression!;
        if (first is not IdentifierNode identifier)
            throw new ArgumentException("Expected an entity name", nameof(entityName));
        var symbol = program.Symbols.NameResolver(cancellation).Resolve(enclosing, identifier.Text, meaning);
        if (symbol is not null && (symbol.Flags & SymbolFlags.TypeParameter) != 0 && (meaning & SymbolFlags.Type) != 0)
            return new(SymbolAccessibility.Accessible);
        if (symbol is null && identifier.Text == "this")
        {
            var container = MissingNamePrefixes.ThisContainer(identifier, false, false);
            var owner = container is null ? null : program.Symbols.Declaration(container);
            if ((await SymbolAccessibilityAsync(
                owner,
                enclosing,
                meaning,
                false,
                true,
                cancellation)).Accessibility == SymbolAccessibility.Accessible)
                return new(SymbolAccessibility.Accessible);
        }
        if (symbol is null)
            return new(SymbolAccessibility.NotResolved, ErrorSymbolName: identifier.Text, ErrorNode: identifier);
        return VisibleDeclarations(symbol, computeAliases, cancellation) is { } aliases ? new(SymbolAccessibility.Accessible, aliases)
            : new(SymbolAccessibility.NotAccessible, ErrorSymbolName: identifier.Text, ErrorNode: identifier);
    }

    internal ValueTask<SymbolAccessibilityDecision> GetSymbolAccessibilityDecisionAsync(Symbol? symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, bool computeAliases = false, bool allowModules = true, CancellationToken cancellation = default) =>
        VisibilityQueryAsync(enclosing, () => ChainOperationAsync(() => ContainerOperationAsync(
            () => SymbolAccessibilityAsync(symbol, enclosing, meaning, computeAliases, allowModules, cancellation),
            cancellation),
            cancellation),
            cancellation);

    internal async ValueTask<bool> IsTypeSymbolAccessibleAsync(
        Symbol symbol,
        SyntaxNode enclosing,
        CancellationToken cancellation = default) =>
        (await GetSymbolAccessibilityDecisionAsync(
            symbol,
            enclosing,
            SymbolFlags.Type,
            cancellation: cancellation)).Accessibility == SymbolAccessibility.Accessible;

    internal async ValueTask<bool> IsValueSymbolAccessibleAsync(
        Symbol symbol,
        SyntaxNode enclosing,
        CancellationToken cancellation = default) =>
        (await GetSymbolAccessibilityDecisionAsync(
            symbol,
            enclosing,
            SymbolFlags.Value,
            cancellation: cancellation)).Accessibility == SymbolAccessibility.Accessible;

    internal async ValueTask<bool> IsSymbolAccessibleByFlagsAsync(Symbol symbol, SyntaxNode enclosing, SymbolFlags meaning,
        CancellationToken cancellation = default) =>
        (await GetSymbolAccessibilityDecisionAsync(
            symbol,
            enclosing,
            meaning,
            allowModules: false,
            cancellation: cancellation)).Accessibility == SymbolAccessibility.Accessible;

    private async ValueTask<SymbolAccessibilityDecision> SymbolAccessibilityAsync(Symbol? symbol, SyntaxNode? enclosing,
        SymbolFlags meaning, bool computeAliases, bool allowModules, CancellationToken cancellation)
    {
        if (symbol is null || enclosing is null)
            return new(SymbolAccessibility.Accessible);
        var result = await AnySymbolAccessibleAsync([symbol], enclosing, symbol, meaning, computeAliases, allowModules, cancellation);
        if (result is not null)
            return result;
        var external = symbol.Declarations.Select(ExternalModuleContainer).FirstOrDefault(s => s is not null);
        if (external is not null && external != ExternalModuleContainer(enclosing))
            return new(SymbolAccessibility.CannotBeNamed, ErrorSymbol: symbol, ErrorModule: external,
                ErrorNode: (enclosing.Flags & NodeFlags.JavaScriptFile) != 0 ? enclosing : null, ErrorMeaning: meaning);
        return new(SymbolAccessibility.NotAccessible, ErrorSymbol: symbol, ErrorMeaning: meaning);
    }

    private async ValueTask<SymbolAccessibilityDecision?> AnySymbolAccessibleAsync(IReadOnlyList<Symbol> symbols, SyntaxNode enclosing,
        Symbol initial, SymbolFlags meaning, bool computeAliases, bool allowModules, CancellationToken cancellation)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        Symbol? accessibleChain = null;
        bool moduleFallback = false;
        foreach (var symbol in symbols)
        {
            var chain = await AccessibleChainAsync(new(symbol, enclosing, meaning, false, []), cancellation);
            if (chain is not null)
            {
                accessibleChain = symbol;
                if (VisibleDeclarations(chain[0], computeAliases, cancellation) is { } aliases)
                    return new(SymbolAccessibility.Accessible, aliases);
            }
            if (allowModules && symbol.Declarations.Any(NonGlobalExternalModule))
            {
                if (!computeAliases)
                    return new(SymbolAccessibility.Accessible);
                moduleFallback = true;
                continue;
            }
            var containers = await ContainersOfSymbolAsync(symbol, enclosing, meaning, cancellation);
            var parent = await AnySymbolAccessibleAsync(containers, enclosing, initial,
                initial == symbol ? QualifiedLeftMeaning(meaning) : meaning, computeAliases, allowModules, cancellation);
            if (parent is not null)
                return parent;
        }
        return moduleFallback ? new(SymbolAccessibility.Accessible)
            : accessibleChain is null ? null
            : new(
                SymbolAccessibility.NotAccessible,
                ErrorSymbol: initial,
                ErrorModule: accessibleChain == initial ? null : accessibleChain, ErrorMeaning: meaning);
    }
}
