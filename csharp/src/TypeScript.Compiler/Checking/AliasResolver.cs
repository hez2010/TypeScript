using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal interface IAliasResolverHost
{
    ValueTask<Symbol?> TargetAsync(SyntaxNode declaration, CancellationToken cancellation);

    void CircularAlias(Symbol symbol, SyntaxNode declaration);

    bool IsDeprecated(Symbol symbol);

    void DeprecatedAlias(SyntaxNode location, Symbol symbol);
}

// Alias state is checker-owned. Pure aliases are followed transitively, while
// merged aliases retain their local meanings until a caller requests more flags.
internal sealed class AliasResolver(CheckerSymbols symbols, CheckerLinks links, TypeResolutionStack resolutions, IAliasResolverHost host)
{
    internal static bool NonLocal(Symbol? symbol, S excludes = S.Value | S.Type | S.Namespace)
        => symbol is not null && ((symbol.Flags & (S.Alias | excludes)) == S.Alias
            || (symbol.Flags & (S.Alias | S.Assignment)) == (S.Alias | S.Assignment));

    internal static SyntaxNode? Declaration(Symbol symbol) => symbol.Declarations.LastOrDefault(ReferenceResolver.IsAliasDeclaration);

    internal ValueTask<Symbol?> SymbolAsync(Symbol? symbol, bool dontResolveAlias = false, CancellationToken cancellation = default)
        => !dontResolveAlias && NonLocal(symbol) ? ResolvedSymbolAsync(symbol!, cancellation) : ValueTask.FromResult(symbol);

    private async ValueTask<Symbol?> ResolvedSymbolAsync(Symbol symbol, CancellationToken cancellation)
        => await ResolveAsync(symbol, cancellation).ConfigureAwait(false);

    internal async ValueTask<Symbol> ResolveAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack()
            ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & S.Alias) == 0)
            throw new ArgumentException("Symbol has no alias meaning", nameof(symbol));
        var data = links.Aliases.Get(symbol);
        if (data.AliasTarget is { } cached)
            return cached;
        if (!resolutions.Push(symbol, TypeSystemPropertyName.AliasTarget))
            return symbols.UnknownSymbol;
        bool active = true;
        var previousTypeOnly = data.TypeOnlyDeclaration;
        Symbol? assigned = null;
        try
        {
            var declaration = Declaration(symbol) ?? throw new InvalidOperationException("Alias symbol has no alias declaration");
            var target = await host.TargetAsync(declaration, cancellation).ConfigureAwait(false);
            if (NonLocal(target))
                target = await IndirectionAsync(symbol, target!, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            data.AliasTarget = assigned = target ?? symbols.UnknownSymbol;
            bool resolved = resolutions.Pop();
            active = false;
            if (!resolved)
            {
                host.CircularAlias(symbol, declaration);
                data.AliasTarget = assigned = symbols.UnknownSymbol;
            }
            return data.AliasTarget;
        }
        catch
        {
            if (assigned is not null && data.AliasTarget == assigned)
                data.AliasTarget = null;
            data.TypeOnlyDeclaration = previousTypeOnly;
            throw;
        }
        finally
        {
            if (active)
                resolutions.Pop();
        }
    }

    internal async ValueTask<Symbol> IndirectionAsync(Symbol source, Symbol target, CancellationToken cancellation = default)
    {
        var result = symbols.Merger.GetMergedSymbol(await ResolveAsync(target, cancellation).ConfigureAwait(false))!;
        if (links.Aliases.Get(target).TypeOnlyDeclaration is { } typeOnly)
            links.Aliases.Get(source).TypeOnlyDeclaration ??= typeOnly;
        return result;
    }

    internal async ValueTask<Symbol?> TryResolveAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = links.Aliases.Get(symbol);
        return data.AliasTarget is not null || resolutions.FindCycleStart(symbol, TypeSystemPropertyName.AliasTarget) < 0
            ? await ResolveAsync(symbol, cancellation).ConfigureAwait(false) : null;
    }

    internal async ValueTask<Symbol?> ImmediateAsync(Symbol symbol, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & S.Alias) == 0)
            throw new ArgumentException("Symbol has no alias meaning", nameof(symbol));
        var data = links.Aliases.Get(symbol);
        if (data.ImmediateTarget is not null)
            return data.ImmediateTarget;
        var declaration = Declaration(symbol) ?? throw new InvalidOperationException("Alias symbol has no alias declaration");
        var previousTypeOnly = data.TypeOnlyDeclaration;
        try
        {
            var target = await host.TargetAsync(declaration, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return data.ImmediateTarget = target;
        }
        catch
        {
            data.TypeOnlyDeclaration = previousTypeOnly;
            throw;
        }
    }

    internal async ValueTask<SyntaxNode?> TypeOnlyAsync(Symbol symbol, S? meaning = null, CancellationToken cancellation = default)
    {
        while ((symbol.Flags & S.Alias) != 0 && (meaning is null || (symbol.Flags & meaning.Value) == 0))
        {
            cancellation.ThrowIfCancellationRequested();
            var target = await ResolveAsync(symbol, cancellation).ConfigureAwait(false);
            if (links.Aliases.Get(symbol).TypeOnlyDeclaration is { } declaration)
                return declaration;
            if (meaning is null)
                return null;
            symbol = target;
        }
        return null;
    }

    internal async ValueTask<S> FlagsAsync(Symbol symbol, bool excludeTypeOnly = false, bool excludeLocal = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        HashSet<Symbol>? seen = null;
        S flags = excludeLocal ? 0 : symbol.Flags;
        while ((symbol.Flags & S.Alias) != 0)
        {
            if (excludeTypeOnly && await TypeOnlyAsync(symbol, cancellation: cancellation).ConfigureAwait(false) is not null)
                break;
            var target = symbols.ExportedValue(await ResolveAsync(symbol, cancellation).ConfigureAwait(false))!;
            if (target == symbols.UnknownSymbol)
                return S.All;
            if ((target.Flags & S.Alias) != 0)
            {
                if (target == symbol || seen?.Contains(target) == true)
                    break;
                seen ??= new(ReferenceEqualityComparer.Instance) { symbol };
                seen.Add(target);
            }
            flags |= target.Flags;
            symbol = target;
        }
        return flags;
    }

    internal bool MarkTypeOnly(SyntaxNode? declaration, SyntaxNode? exportStar = null)
    {
        if (declaration is null || symbols.Declaration(declaration) is not { } source)
            return false;
        var data = links.Aliases.Get(source);
        if (data.TypeOnlyDeclaration is null && IsTypeOnly(declaration))
        {
            data.TypeOnlyDeclaration = declaration;
            return true;
        }
        if (data.TypeOnlyDeclaration is null && exportStar is not null)
        {
            data.TypeOnlyDeclaration = exportStar;
            return true;
        }
        return data.TypeOnlyDeclaration is not null;
    }

    internal async ValueTask<Symbol> WithDeprecationAsync(Symbol symbol, SyntaxNode location, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if ((symbol.Flags & S.Alias) == 0 || host.IsDeprecated(symbol) || Declaration(symbol) is null)
            return symbol;
        var result = await ResolveAsync(symbol, cancellation).ConfigureAwait(false);
        if (result == symbols.UnknownSymbol)
            return result;
        while ((symbol.Flags & S.Alias) != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var target = await ImmediateAsync(symbol, cancellation).ConfigureAwait(false);
            if (target is null || target == result || target.Declarations.Length == 0)
                break;
            if (host.IsDeprecated(target))
            {
                host.DeprecatedAlias(location, target);
                break;
            }
            if (symbol == result)
                break;
            symbol = target;
        }
        return result;
    }

    internal static bool IsTypeOnly(SyntaxNode declaration) => declaration switch
    {
        ImportClauseNode clause => SemanticSyntax.TypeOnly(clause),
        ImportEqualsDeclarationNode import => import.IsTypeOnly,
        NamespaceImportNode => declaration.Parent is ImportClauseNode clause && SemanticSyntax.TypeOnly(clause),
        ImportSpecifierNode import => import.IsTypeOnly
            || declaration.Parent?.Parent is ImportClauseNode clause && SemanticSyntax.TypeOnly(clause),
        ExportSpecifierNode export => export.IsTypeOnly || declaration.Parent?.Parent is ExportDeclarationNode { IsTypeOnly: true },
        NamespaceExportNode => declaration.Parent is ExportDeclarationNode { IsTypeOnly: true },
        ExportDeclarationNode { IsTypeOnly: true, ModuleSpecifier: not null, ExportClause: null } => true,
        _ => false
    };
}
