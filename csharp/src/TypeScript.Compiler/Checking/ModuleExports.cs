using TypeScript.Compiler.Text;
using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface IModuleExportHost
{
    ValueTask<Symbol?> ExportStarModuleAsync(ExportDeclarationNode declaration, CancellationToken cancellation);

    void AmbiguousExport(ExportDeclarationNode declaration, TextSlice earlierSpecifierText, TextSlice name);
}

internal sealed class ModuleExports(CheckerLinks links, AliasResolver aliases, AliasTargets targets, IModuleExportHost host)
{
    private sealed class Collision(TextSlice specifier)
    {
        internal TextSlice Specifier { get; } = specifier;
        internal List<ExportDeclarationNode> Duplicates { get; } = [];
    }

    internal async ValueTask<IReadOnlyDictionary<TextSlice, Symbol>> ResolveAsync(Symbol module, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        var data = links.Modules.Get(module);
        if (data.ResolvedExports is { } cached)
            return cached;
        var visited = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        var nonTypeOnlyNames = new HashSet<TextSlice>();
        var typeOnly = new Dictionary<TextSlice, SyntaxNode>(TextSliceComparer.Ordinal);
        Symbol? original = null;
        if (await aliases.SymbolAsync(
            module.Exports.GetValueOrDefault("export="),
            cancellation: cancellation).ConfigureAwait(false) is not null)
            original = module;
        var resolved = await targets.ExternalModuleAsync(module, false, cancellation).ConfigureAwait(false);
        var exports = await VisitAsync(resolved, null, false).ConfigureAwait(false) ?? new(TextSliceComparer.Ordinal);
        if (original is { Exports.Count: > 1 })
            foreach (var symbol in original.Exports.Values)
            {
                if (symbol.Name.Span is "export=" || symbol.Name == Symbol.InternalPrefix + "export")
                    continue;
                var flags = await aliases.FlagsAsync(symbol, cancellation: cancellation).ConfigureAwait(false);
                if ((flags & (S.Type | S.Namespace)) != 0 && (flags & S.Value) == 0)
                    exports.TryAdd(symbol.Name, symbol);
            }
        foreach (TextSlice name in nonTypeOnlyNames)
            typeOnly.Remove(name);
        cancellation.ThrowIfCancellationRequested();
        data.TypeOnlyExportStars = typeOnly.AsReadOnly();
        return data.ResolvedExports = exports.AsReadOnly();

        async ValueTask<Dictionary<TextSlice, Symbol>?> VisitAsync(Symbol? symbol, ExportDeclarationNode? exportStar, bool isTypeOnly)
        {
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
                ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            if (!isTypeOnly && symbol is not null)
                foreach (TextSlice name in symbol.Exports.Keys)
                    nonTypeOnlyNames.Add(name);
            if (symbol is null || !visited.Add(symbol))
                return null;
            var result = new Dictionary<TextSlice, Symbol>(symbol.Exports, TextSliceComparer.Ordinal);
            if (symbol.Exports.TryGetValue(Symbol.InternalPrefix + "export", out var stars))
            {
                var nested = new Dictionary<TextSlice, Symbol>();
                var collisions = new Dictionary<TextSlice, Collision>(TextSliceComparer.Ordinal);
                foreach (var declaration in stars.Declarations.Cast<ExportDeclarationNode>())
                {
                    var imported = await host.ExportStarModuleAsync(declaration, cancellation).ConfigureAwait(false);
                    var members = await VisitAsync(imported, declaration, isTypeOnly || declaration.IsTypeOnly).ConfigureAwait(false);
                    await ExtendAsync(nested, members, collisions, declaration).ConfigureAwait(false);
                }
                foreach (var (name, collision) in collisions)
                    if (name != "export=" && !result.ContainsKey(name))
                        foreach (var declaration in collision.Duplicates)
                            host.AmbiguousExport(declaration, collision.Specifier, name);
                await ExtendAsync(result, nested, null, null).ConfigureAwait(false);
            }
            if (exportStar is { IsTypeOnly: true })
                foreach (TextSlice name in result.Keys)
                    typeOnly[name] = exportStar;
            return result;
        }

        async ValueTask ExtendAsync(Dictionary<TextSlice, Symbol> target, Dictionary<TextSlice, Symbol>? source,
            Dictionary<TextSlice, Collision>? collisions, ExportDeclarationNode? declaration)
        {
            if (source is null)
                return;
            foreach (var (name, sourceSymbol) in source)
            {
                cancellation.ThrowIfCancellationRequested();
                if (name == "default")
                    continue;
                if (!target.TryGetValue(name, out var existing))
                {
                    target.Add(name, sourceSymbol);
                    if (collisions is not null && declaration is not null)
                        collisions[name] = new(SpecifierText(declaration.ModuleSpecifier!));
                }
                else if (collisions is not null && declaration is not null
                    && await aliases.SymbolAsync(existing, cancellation: cancellation).ConfigureAwait(false)
                        != await aliases.SymbolAsync(sourceSymbol, cancellation: cancellation).ConfigureAwait(false))
                    collisions[name].Duplicates.Add(declaration);
            }
        }
    }

    internal SyntaxNode? TypeOnlyStar(Symbol module, TextSlice name) => links.Modules.Get(module).TypeOnlyExportStars?.GetValueOrDefault(name);

    internal async ValueTask<Symbol?> ExportAsync(Symbol module, TextSlice name, SyntaxNode? declaration,
        bool dontResolveAlias = false, CancellationToken cancellation = default)
    {
        if ((module.Flags & S.Module) == 0)
            return null;
        TextSlice key = name.Span.StartsWith(Symbol.InternalPrefix, StringComparison.Ordinal) ? TextSlice.Concat(Symbol.InternalPrefix, name) : name;
        var exports = await ResolveAsync(module, cancellation).ConfigureAwait(false);
        var result = await aliases.SymbolAsync(exports.GetValueOrDefault(key), dontResolveAlias, cancellation).ConfigureAwait(false);
        aliases.MarkTypeOnly(declaration, TypeOnlyStar(module, key));
        return result;
    }

    private static TextSlice SpecifierText(SyntaxNode node)
    {
        var file = SemanticSyntax.Source(node) ?? throw new InvalidOperationException("Export specifier has no source file");
        // Skip trivia while retaining the original quote style and escape text.
        int start = file.Source.ToUtf16Position(node.Pos);
        var scanner = new Scanner(file.Source);
        scanner.Rewind(new(start, start, start, SyntaxKind.Unknown, "", 0, 0, 0, 0));
        scanner.Scan();
        var text = file.Source.Text[scanner.TokenStart..file.Source.ToUtf16Position(node.End)];
        if ((node.Flags & NodeFlags.ReparserTransformedLiteral) != 0 && node is StringLiteralNode literal)
            return (literal.TokenFlags & TokenFlags.SingleQuote) != 0 ? TextSlice.Concat("'", text, "'") : TextSlice.Concat("\"", text, "\"");
        return text;
    }
}
