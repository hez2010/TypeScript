using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using S = TypeScript.Compiler.Binding.SymbolFlags;

namespace TypeScript.Compiler.Checking;

internal sealed partial class CheckerEnvironment
{
    public ValueTask<Symbol?> TargetAsync(SyntaxNode declaration, CancellationToken cancellation)
        => AliasTargets.TargetAsync(declaration, cancellation);

    public void CircularAlias(Symbol symbol, SyntaxNode declaration) => Diagnostics.Add(2303);

    public bool IsDeprecated(Symbol symbol) => Deprecations.Symbol(symbol);

    public void DeprecatedAlias(SyntaxNode location, Symbol symbol) =>
        Suggestion(location, 6385, symbol.Name);

    public DiagnosticMessage CannotFindName(IdentifierNode name) => Messages.Cannot_find_name_0;

    public ValueTask<IReadOnlyDictionary<string, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation)
    {
        if ((symbol.Flags & S.LateBindingContainer) != 0)
            throw new InvalidOperationException("Checker requires late-bound exported members");
        if ((symbol.Flags & (S.Module | S.Enum)) == 0 && symbol != Symbols.UnknownSymbol)
            throw new InvalidOperationException("Checker requires non-module exported members");
        return (symbol.Flags & S.Module) != 0 ? ModuleExports.ResolveAsync(symbol, cancellation) : ValueTask.FromResult(symbol.Exports);
    }

    public ValueTask<Symbol> CommonJsNamespaceAsync(Symbol symbol, CancellationToken cancellation)
        => symbol.ValueDeclaration is VariableDeclarationNode { Initializer: CallExpressionNode } declaration
            && (declaration.Flags & NodeFlags.JavaScriptFile) != 0
            ? throw new InvalidOperationException("Checker requires CommonJS namespace normalization") : ValueTask.FromResult(symbol);

    public ValueTask MissingQualifiedAsync(SyntaxNode name, SyntaxNode right, Symbol parent, S meaning, CancellationToken cancellation)
    {
        Diagnostics.Add(2694);
        return ValueTask.CompletedTask;
    }

    public ValueTask<Symbol?> ExternalModuleAsync(
        SyntaxNode location,
        SyntaxNode? specifier,
        ImportAttributesNode? attributes,
        CancellationToken cancellation)
    {
        if (attributes is not null)
            throw new InvalidOperationException("Checker requires import attribute evaluation");
        string? name = AliasTargets.Text(specifier) ?? (specifier as NoSubstitutionTemplateLiteralNode)?.Text;
        if (name is null)
            return ValueTask.FromResult<Symbol?>(null);
        var file = Symbols.Binding(location)!.SourceFile;
        var reference = Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(r => r.Node == specifier);
        var result = reference?.Resolution.IsResolved == true
            ? Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol
            : null;
        result ??= Symbols.PatternAugmentations.GetValueOrDefault(name) ?? Symbols.Globals.GetValueOrDefault('"' + name + '"');
        if (result is null && reference?.Resolution.IsResolved == true)
            AliasDiagnostic(2306, specifier!);
        else if (result is null)
            AliasDiagnostic(2307, specifier!);
        return ValueTask.FromResult(Symbols.Merger.GetMergedSymbol(result));
    }

    public async ValueTask<Symbol?> AdjustEsModuleAsync(
        Symbol module,
        Symbol target,
        SyntaxNode declaration,
        SyntaxNode specifier,
        CancellationToken cancellation)
    {
        if ((target.Flags & S.Module) == 0 || Symbols.Program.Configuration.Options.Boolean("esModuleInterop") == true
            || module.Declarations.OfType<SourceFileNode>().Any(f => f.ScriptKind is ScriptKind.JS or ScriptKind.JSX))
            throw new InvalidOperationException("Checker requires ES module wrapper/type evaluation");
        if (specifier.Parent is ImportDeclarationNode { ImportClause.NamedBindings: NamespaceImportNode }
            && target.Exports.TryGetValue("default", out var defaultExport)
            && ((defaultExport.Flags & S.Value) != 0 || (defaultExport.Flags & S.Alias) != 0
                && (await Aliases.FlagsAsync(
                    defaultExport,
                    excludeTypeOnly: true,
                    cancellation: cancellation).ConfigureAwait(false) & S.Value) != 0))
        {
            // This fixture has a declared, non-callable ES namespace. Synthetic
            // defaults and callable/export-assignment module types are excluded.
            var type = context.NewObjectType(ObjectFlags.Anonymous | ObjectFlags.MembersResolved, target);
            type.Members = target.Exports;
            return await ModuleTypes.CloneAsync(target, type, specifier.Parent, cancellation).ConfigureAwait(false);
        }
        return target;
    }

    public async ValueTask<Symbol?> DefaultExportAsync(
        Symbol module,
        SyntaxNode declaration,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        if (module.Exports.ContainsKey("export=") || Symbols.Program.Configuration.Options.Boolean("allowSyntheticDefaultImports") == true)
            throw new InvalidOperationException("Checker requires synthetic default interop");
        var result = await Aliases.SymbolAsync(
            module.Exports.GetValueOrDefault("default"),
            dontResolveAlias,
            cancellation).ConfigureAwait(false);
        if (result is null)
            AliasDiagnostic(declaration is ImportClauseNode ? 1192 : 2305, declaration);
        return result;
    }

    public async ValueTask<Symbol?> ExternalMemberAsync(
        SyntaxNode importOrExport,
        SyntaxNode specifier,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        var moduleSpecifier = AliasTargets.Specifier(importOrExport);
        var module = await ExternalModuleAsync(
            importOrExport,
            moduleSpecifier,
            AliasTargets.Attributes(importOrExport),
            cancellation).ConfigureAwait(false);
        if (module is null)
            return null;
        var target = await AliasTargets.EsModuleAsync(module, specifier, moduleSpecifier!, cancellation).ConfigureAwait(false);
        if (target is null)
            return null;
        if (module.Exports.ContainsKey("export="))
            throw new InvalidOperationException("Checker requires export-assignment member types");
        var nameNode = specifier switch
        {
            ImportSpecifierNode import => import.PropertyName ?? import.Name,
            ExportSpecifierNode export => export.PropertyName ?? export.Name,
            _ => throw new InvalidOperationException("Checker requires CommonJS destructuring")
        };
        var name = AliasTargets.Text(nameNode);
        if (name is null)
            return null;
        var result = Symbols.Merger.GetMergedSymbol(
            await ModuleExports.ExportAsync(target, name, specifier, dontResolveAlias, cancellation).ConfigureAwait(false));
        if (result is null)
            AliasDiagnostic(2305, specifier);
        return result;
    }

    public ValueTask<Symbol?> AliasExpressionAsync(SyntaxNode expression, CancellationToken cancellation)
        => throw new InvalidOperationException("Checker requires expression checking for alias targets");

    public void TypeOnlyImportAlias(ImportEqualsDeclarationNode declaration, SyntaxNode typeOnlyDeclaration, bool exported)
        => Diagnostics.Add(exported ? 1379 : 1380);

    public bool UsesRequireModuleExports => Symbols.Program.Configuration.Options.String("module") is "node20" or "nodenext";

    public ValueTask<Symbol?> ExportOfModuleAsync(Symbol module, string name, SyntaxNode declaration, CancellationToken cancellation)
        => ModuleExports.ExportAsync(module, name, declaration, true, cancellation);

    public ValueTask<Symbol?> ExportStarModuleAsync(ExportDeclarationNode declaration, CancellationToken cancellation)
        => ExternalModuleAsync(declaration, declaration.ModuleSpecifier, declaration.Attributes, cancellation);

    public void AmbiguousExport(ExportDeclarationNode declaration, string earlierSpecifierText, string name)
        => Error(
            declaration,
            Messages.Module_0_has_already_exported_a_member_named_1_Consider_explicitly_re_exporting_to_resolve_the_ambiguity,
            earlierSpecifierText, name);

    private void AliasDiagnostic(int code, SyntaxNode node)
    {
        if (reported.Add((node, code, "")))
            Diagnostics.Add(code);
    }
}
