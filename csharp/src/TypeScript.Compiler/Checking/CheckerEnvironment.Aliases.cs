using TypeScript.Compiler.Text;
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

    public void CircularAlias(Symbol symbol, SyntaxNode declaration)
        => AddDiagnostic(declaration, Messages.Circular_definition_of_import_alias_0,
            [declaration is ExportAssignmentNode { Expression: IdentifierNode exported } ? exported.Text : symbol.Name]);

    public bool IsDeprecated(Symbol symbol) => Deprecations.Symbol(symbol);

    public void DeprecatedAlias(SyntaxNode location, Symbol symbol) =>
        Suggestion(location, DiagnosticCode.X0IsDeprecated, symbol.Name);

    public DiagnosticMessage CannotFindName(IdentifierNode name) => ReferenceSymbols.MissingName(name);

    public ValueTask<IReadOnlyDictionary<TextSlice, Symbol>> ExportsAsync(Symbol symbol, CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            return checker.ExportsAsync(symbol, cancellation);
        if ((symbol.Flags & S.LateBindingContainer) != 0)
            throw new InvalidOperationException("Checker requires late-bound exported members");
        return (symbol.Flags & S.Module) != 0 ? ModuleExports.ResolveAsync(symbol, cancellation) : ValueTask.FromResult(symbol.Exports);
    }

    public ValueTask<Symbol> CommonJsNamespaceAsync(Symbol symbol, CancellationToken cancellation)
        => symbol.ValueDeclaration is VariableDeclarationNode { Initializer: CallExpressionNode } declaration
            && (declaration.Flags & NodeFlags.JavaScriptFile) != 0
            ? throw new InvalidOperationException("Checker requires CommonJS namespace normalization") : ValueTask.FromResult(symbol);

    public async ValueTask MissingQualifiedAsync(
        SyntaxNode name,
        SyntaxNode right,
        Symbol parent,
        S meaning,
        CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            await checker.MissingQualifiedAsync(name, right, parent, meaning, cancellation);
        else
            Error(right, Messages.Namespace_0_has_no_exported_member_1, parent.Name, CheckerDiagnostic.DeclarationName(right));
    }

    public async ValueTask<Symbol?> ExternalModuleAsync(
        SyntaxNode location,
        SyntaxNode? specifier,
        ImportAttributesNode? attributes,
        CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            return await checker.ResolveImportModuleAsync(location, specifier,
                attributes is null ? null : await checker.ImportAttributesExpressionAsync(attributes, cancellation), cancellation);
        if (attributes is not null)
            throw new InvalidOperationException("Checker requires import attribute evaluation");
        TextSlice? name = AliasTargets.Text(specifier) ?? (specifier as NoSubstitutionTemplateLiteralNode)?.Text;
        if (name is null)
            return null;
        var file = Symbols.Binding(location)!.SourceFile;
        var reference = Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(r => r.Node == specifier);
        var result = reference?.Resolution.IsResolved == true
            ? Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol
            : null;
        result ??= Symbols.PatternAugmentations.GetValueOrDefault(name.Value) ?? Symbols.Globals.GetValueOrDefault(TextSlice.Concat("\"", name.Value, "\""));
        if (result is null && reference?.Resolution.IsResolved == true)
            AliasDiagnostic(DiagnosticCode.File0IsNotAModule, specifier!);
        else if (result is null)
            AliasDiagnostic(DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations, specifier!);
        return Symbols.Merger.GetMergedSymbol(result);
    }

    public async ValueTask<Symbol?> AdjustEsModuleAsync(
        Symbol module,
        Symbol target,
        SyntaxNode declaration,
        SyntaxNode specifier,
        CancellationToken cancellation)
    {
        if (SemanticChecker is { } checker)
            return await checker.AdjustModuleAsync(module, target, declaration, specifier, cancellation).ConfigureAwait(false);
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
            // Scope-only hosts can resolve declared, non-callable ES namespaces.
            // The production checker supplies synthetic and callable module types.
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
        if (SemanticChecker is { } checker)
            return await checker.ModuleDefaultAsync(module, declaration, dontResolveAlias, cancellation).ConfigureAwait(false);
        if (module.Exports.ContainsKey("export=") || Symbols.Program.Configuration.Options.Boolean("allowSyntheticDefaultImports") == true)
            throw new InvalidOperationException("Checker requires synthetic default interop");
        var result = await Aliases.SymbolAsync(
            module.Exports.GetValueOrDefault("default"),
            dontResolveAlias,
            cancellation).ConfigureAwait(false);
        if (result is null)
            AliasDiagnostic(
                declaration is ImportClauseNode ? DiagnosticCode.Module0HasNoDefaultExport : DiagnosticCode.Module0HasNoExportedMember1,
                declaration);
        return result;
    }

    public async ValueTask<Symbol?> ExternalMemberAsync(
        SyntaxNode importOrExport,
        SyntaxNode specifier,
        bool dontResolveAlias,
        CancellationToken cancellation)
    {
        var moduleSpecifier = importOrExport is VariableDeclarationNode { Initializer: CallExpressionNode call }
            && SemanticSyntax.RequireCall(call) ? call.Arguments![0] : AliasTargets.Specifier(importOrExport);
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
        if (SemanticChecker is null && module.Exports.ContainsKey("export="))
            throw new InvalidOperationException("Checker requires export-assignment member types");
        var nameNode = specifier switch
        {
            ImportSpecifierNode import => import.PropertyName ?? import.Name,
            ExportSpecifierNode export => export.PropertyName ?? export.Name,
            BindingElementNode binding => binding.PropertyName ?? binding.Name,
            PropertyAccessExpressionNode property => property.Name,
            _ => throw new InvalidOperationException("Expected an external module member name")
        };
        var name = AliasTargets.Text(nameNode);
        if (name is null)
            return null;
        if (SemanticChecker is { } checker)
            return await checker.ModuleMemberAsync(
                module,
                target,
                specifier,
                nameNode!,
                dontResolveAlias,
                cancellation).ConfigureAwait(false);
        var result = Symbols.Merger.GetMergedSymbol(
            await ModuleExports.ExportAsync(target, (name).Value, specifier, dontResolveAlias, cancellation).ConfigureAwait(false));
        if (result is null)
            await MissingModuleMemberAsync(module, target, specifier, nameNode!, cancellation).ConfigureAwait(false);
        return result;
    }

    internal async ValueTask MissingModuleMemberAsync(
        Symbol module,
        Symbol target,
        SyntaxNode specifier,
        SyntaxNode nameNode,
        CancellationToken cancellation)
    {
        if (Symbols.Program.Configuration.Options.Boolean("noCheck") == true)
            return;
        TextSlice name = AliasTargets.Text(nameNode) ?? SyntaxNameText.Get(nameNode);
        var suggestion = nameNode is IdentifierNode ? await new SymbolSuggestions(
            Aliases,
            new(Symbols.Program.SourceFiles.Select(f => f.Syntax)))
            .FindAsync(
                name,
                (await ModuleExports.ResolveAsync(target, cancellation).ConfigureAwait(false)).Values,
                S.ModuleMember,
                cancellation).ConfigureAwait(false) : null;
        TextSlice moduleName = SemanticChecker is { } checker
            ? await checker.FullyQualifiedNameAsync(module, specifier, cancellation) : module.Name;
        TextSlice declarationName = CheckerDiagnostic.DeclarationName(nameNode);
        DiagnosticCode code = suggestion is not null
            ? DiagnosticCode.X0HasNoExportedMemberNamed1DidYouMean2
            : module.Exports.ContainsKey("default")
                ? DiagnosticCode.Module0HasNoExportedMember1DidYouMeanToUseImport1From0Instead
                : DiagnosticCode.Module0HasNoExportedMember1;
        TextSlice[] arguments = suggestion is null ? [moduleName, declarationName] : [moduleName, declarationName, suggestion.Name];
        var related = new List<Diagnostic>();
        if (suggestion?.ValueDeclaration is { } suggestedDeclaration)
            related.Add(CheckerDiagnostic.Create(suggestedDeclaration, Messages.X_0_is_declared_here, suggestion.Name));
        if (code == DiagnosticCode.Module0HasNoExportedMember1 && module.ValueDeclaration is { } declaration
            && Symbols.Binding(declaration)?.Get(declaration)?.Locals.GetValueOrDefault(name) is { } local)
        {
            if (module.Exports.TryGetValue("export=", out var assignment))
            {
                if (await SameReferenceAsync(assignment, local))
                {
                    code = SemanticChecker?.ModuleKind >= 5
                        ? DiagnosticCode.X0CanOnlyBeImportedByUsingADefaultImport
                        : (nameNode.Flags & NodeFlags.JavaScriptFile) != 0
                            ? DiagnosticCode.X0CanOnlyBeImportedByUsingARequireCallOrByUsingADefaultImport
                            : DiagnosticCode.X0CanOnlyBeImportedByUsingImport1Require2OrADefaultImport;
                    arguments = code == DiagnosticCode.X0CanOnlyBeImportedByUsingImport1Require2OrADefaultImport
                        ? [declarationName, declarationName, moduleName]
                        : [declarationName];
                }
            }
            else
            {
                code = DiagnosticCode.Module0Declares1LocallyButItIsNotExported;
                foreach (var exported in module.Exports.Values)
                    if (await SameReferenceAsync(exported, local))
                    {
                        code = DiagnosticCode.Module0Declares1LocallyButItIsExportedAs2;
                        arguments = [moduleName, declarationName, exported.Name];
                        break;
                    }
                foreach (var localDeclaration in local.Declarations)
                    related.Add(CheckerDiagnostic.Create(localDeclaration,
                        related.Count == 0 ? Messages.X_0_is_declared_here : Messages.X_and_here, declarationName));
            }
        }
        if (reported.Add((nameNode, code, "")))
        {
            Diagnostics.Add(code);
            DiagnosticFiles.Add((nameNode, CheckerDiagnostic.Create(nameNode, DiagnosticLocalization.GetMessage(code), arguments) with
            { RelatedInformation = related }));
        }

        async ValueTask<bool> SameReferenceAsync(Symbol left, Symbol right) =>
            Symbols.Merger.GetMergedSymbol(await Aliases.SymbolAsync(Symbols.Merger.GetMergedSymbol(left), cancellation: cancellation))
                == Symbols.Merger.GetMergedSymbol(
                    await Aliases.SymbolAsync(Symbols.Merger.GetMergedSymbol(right), cancellation: cancellation));
    }

    public async ValueTask<Symbol?> AliasExpressionAsync(SyntaxNode expression, CancellationToken cancellation)
    {
        var checker = SemanticChecker ?? throw new InvalidOperationException("Checker requires expression checking for alias targets");
        await checker.CachedExpressionAsync(expression, 0, cancellation).ConfigureAwait(false);
        return expression is ClassExpressionNode ? Symbols.Declaration(expression) : links.SymbolNodes.TryGet(expression)?.ResolvedSymbol;
    }

    public void TypeOnlyImportAlias(ImportEqualsDeclarationNode declaration, SyntaxNode typeOnlyDeclaration, bool exported)
    {
        DiagnosticCode code = exported
            ? DiagnosticCode.AnImportAliasCannotReferenceADeclarationThatWasExportedUsingExportType
            : DiagnosticCode.AnImportAliasCannotReferenceADeclarationThatWasImportedUsingImportType;
        var node = declaration.ModuleReference!;
        var name = SemanticSyntax.Name(typeOnlyDeclaration) ?? declaration.Name!;
        Diagnostics.Add(code);
        DiagnosticFiles.Add((node, CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code)) with
        {
            RelatedInformation = [CheckerDiagnostic.Create(typeOnlyDeclaration,
            exported ? Messages.X_0_was_exported_here : Messages.X_0_was_imported_here, SyntaxNameText.Get(name))]
        }));
    }

    public bool UsesRequireModuleExports => Symbols.Program.Configuration.Options.String("module") is "node20" or "nodenext";

    public ValueTask<Symbol?> ExportOfModuleAsync(Symbol module, TextSlice name, SyntaxNode declaration, CancellationToken cancellation)
        => ModuleExports.ExportAsync(module, name, declaration, true, cancellation);

    public ValueTask<Symbol?> ExportStarModuleAsync(ExportDeclarationNode declaration, CancellationToken cancellation)
        => ExternalModuleAsync(declaration, declaration.ModuleSpecifier, declaration.Attributes, cancellation);

    public void AmbiguousExport(ExportDeclarationNode declaration, TextSlice earlierSpecifierText, TextSlice name)
        => Error(
            declaration,
            Messages.Module_0_has_already_exported_a_member_named_1_Consider_explicitly_re_exporting_to_resolve_the_ambiguity,
            earlierSpecifierText, name);

    internal void AliasDiagnostic(DiagnosticCode code, SyntaxNode node)
    {
        if (reported.Add((node, code, "")))
            AddDiagnostic(node, code);
    }
}
