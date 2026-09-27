using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private readonly HashSet<Symbol> checkedModuleExports = [];
    internal int ModuleKind => (int?)program.Symbols.Program.Configuration.Options.Number("module") ?? program.Symbols.Program.Configuration.Options.String("module") switch
    {
        "commonjs" => 1,
        "amd" => 2,
        "umd" => 3,
        "system" => 4,
        "es6" or "es2015" => 5,
        "es2020" => 6,
        "es2022" => 7,
        "esnext" => 99,
        "node16" => 100,
        "node18" => 101,
        "node20" => 102,
        "nodenext" => 199,
        "preserve" => 200,
        _ => TargetYear == int.MaxValue ? 99 : TargetYear >= 2022 ? 7 : TargetYear >= 2020 ? 6 : TargetYear >= 2015 ? 5 : 1
    };

    private int EmitModuleKind(SyntaxNode node)
    {
        var file = SemanticSyntax.Source(node)!;
        var format = ModuleTargetMode(file);
        return format == ReferenceResolutionMode.Unspecified ? ModuleKind : format == ReferenceResolutionMode.Require ? 1 : 99;
    }

    private static bool AmbientModule(SyntaxNode? node) =>
        node is ModuleDeclarationNode { Name: StringLiteralNode } or ModuleDeclarationNode { Keyword: SyntaxKind.GlobalKeyword };

    private bool ModuleContext(SyntaxNode node, DiagnosticCode code)
    {
        if (node.Parent is SourceFileNode or ModuleBlockNode or ModuleDeclarationNode)
            return true;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            ErrorOnFirstToken(node, code);
        return false;
    }

    private bool ExternalModuleSyntax(SyntaxNode node, SyntaxNode? specifier)
    {
        if (specifier is null || specifier.Pos == specifier.End)
            return false;
        if (specifier is not StringLiteralNode literal)
        {
            Error(specifier, DiagnosticCode.StringLiteralExpected);
            return false;
        }
        bool ambient = node.Parent is ModuleBlockNode && AmbientModule(node.Parent.Parent);
        if (node.Parent is not SourceFileNode && !ambient)
        {
            Error(
                specifier,
                node is ExportDeclarationNode
                    ? DiagnosticCode.ExportDeclarationsAreNotPermittedInANamespace
                    : DiagnosticCode.ImportDeclarationsInANamespaceCannotReferenceAModule);
            return false;
        }
        if (ambient && RelativeModuleName(literal.Text)
            && !ModuleAugmentation(node.Parent!.Parent!))
        {
            Error(node, DiagnosticCode.ImportOrExportDeclarationInAnAmbientModuleDeclarationCannotReferenceModuleThroughRelativeModuleName);
            return false;
        }
        return ImportAttributeValues(node switch
        {
            ImportDeclarationNode import => import.Attributes,
            ExportDeclarationNode export => export.Attributes,
            _ => null
        });
    }

    private bool ModuleAugmentation(SyntaxNode node) => node is ModuleDeclarationNode && AmbientModule(node)
        && SemanticSyntax.Source(node)?.ModuleAugmentations.Contains(SemanticSyntax.Name(node)!) == true;

    private static bool RelativeModulePath(string name) => name is "." or ".."
        || name.StartsWith("./", StringComparison.Ordinal) || name.StartsWith("../", StringComparison.Ordinal)
        || name.StartsWith(".\\", StringComparison.Ordinal) || name.StartsWith("..\\", StringComparison.Ordinal);

    private static bool RelativeModuleName(string name) => RelativeModulePath(name) || CompilerPath.EncodedRootLength(name) > 0;

    private async ValueTask CheckMisplacedModuleNameAsync(SyntaxNode node, CancellationToken cancellation)
    {
        if (node is ImportDeclarationNode { ImportClause: null }
            or ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode }
            || DeclarationOrder.Ancestor(node.Parent, Binder.IsContainer) is not SourceFileNode)
            return;
        var specifier = node is ImportEqualsDeclarationNode ? AliasTargets.ModuleSpecifier(node) : AliasTargets.Specifier(node);
        if (specifier is not null)
            await program.ExternalModuleAsync(node, specifier, node switch
            {
                ImportDeclarationNode import => import.Attributes,
                ExportDeclarationNode export => export.Attributes,
                _ => null
            }, cancellation);
    }

    private void ExportedDeclaration(SyntaxNode node, bool value)
    {
        if (DeclarationModifiers(node))
            return;
        if (node is not IModifiedNode { Modifiers: { } modifiers })
            return;
        if (value && node.Parent is SourceFileNode && (node.Flags & NodeFlags.Ambient) == 0
            && program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true && EmitModuleKind(node) == 1
            && modifiers.FirstOrDefault(m => m.Kind == SyntaxKind.ExportKeyword) is { } export)
            Error(
                export,
                DiagnosticCode.ATopLevelExportModifierCannotBeUsedOnValueDeclarationsInACommonJSModuleWhenVerbatimModuleSyntaxIsEnabled);
        else if (node is VariableStatementNode { DeclarationList: { } declarations } && (declarations.Flags & NodeFlags.Using) != 0
            && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0
            && modifiers.FirstOrDefault(
                m => m.Kind is SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.DeclareKeyword) is { } modifier)
            Error(
                modifier,
                (declarations.Flags & NodeFlags.BlockScoped) == NodeFlags.AwaitUsing
                    ? DiagnosticCode.X0ModifierCannotAppearOnAnAwaitUsingDeclaration
                    : DiagnosticCode.X0ModifierCannotAppearOnAUsingDeclaration,
                TokenFacts.Text(modifier.Kind)!);
    }

    private async ValueTask CheckNamespaceSourceAsync(ModuleDeclarationNode node, CancellationToken cancellation)
    {
        CheckDeclarationName(node);
        DeclarationModifiers(node);
        if (node.Body is not null)
            await CheckSourceElementAsync(node.Body, cancellation).ConfigureAwait(false);
        bool global = node.Keyword == SyntaxKind.GlobalKeyword, ambient = (node.Flags & NodeFlags.Ambient) != 0;
        if (!global)
            RegisterUnused(node);
        if (global && !ambient)
            Error(
                node.Name!,
                DiagnosticCode.AugmentationsForTheGlobalScopeShouldHaveDeclareModifierUnlessTheyAppearInAlreadyAmbientContext);
        if (node.Attributes is not null)
            await CheckModuleAttributesAsync(node, cancellation).ConfigureAwait(false);
        if (!ModuleContext(
            node,
            AmbientModule(node)
                ? DiagnosticCode.AnAmbientModuleDeclarationIsOnlyAllowedAtTheTopLevelInAFile
                : DiagnosticCode.ANamespaceDeclarationIsOnlyAllowedAtTheTopLevelOfANamespaceOrModule))
            return;
        if (!ambient && node.Name is StringLiteralNode)
            Error(node.Name, DiagnosticCode.OnlyAmbientModulesCanUseQuotedNames);
        if (node.Name is IdentifierNode && node.Keyword == SyntaxKind.ModuleKeyword)
            Error(
                node.Name,
                DiagnosticCode.ANamespaceDeclarationShouldNotBeDeclaredUsingTheModuleKeywordPleaseUseTheNamespaceKeywordInstead);
        var symbol = program.Symbols.Declaration(node)!;
        await CheckMergedExportsAsync(node, cancellation).ConfigureAwait(false);
        int state = Binder.ModuleState(node);
        bool instantiated = state == 2
            || state == 1 && (IsolatedModules || program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true);
        if ((symbol.Flags & SymbolFlags.ValueModule) != 0 && !ambient && instantiated)
        {
            if (ErasableSyntaxOnly && (node.Flags & NodeFlags.JavaScriptFile) == 0)
                Error(node, DiagnosticCode.ThisSyntaxIsNotAllowedWhenErasableSyntaxOnlyIsEnabled);
            if (IsolatedModules && program.Symbols.Binding(node)?.IsModule != true)
                Error(
                    node.Name!,
                    DiagnosticCode.NamespacesAreNotAllowedInGlobalScriptFilesWhen0IsEnabledIfThisFileIsNotIntendedToBeAGlobalScriptSetModuleDetectionToForceOrAddAnEmptyExportStatement,
                    IsolatedModuleOptionName);
            var first = symbol.Declarations.FirstOrDefault(
                d => (d is ClassDeclarationNode || d is FunctionDeclarationNode { Body: not null }) && (d.Flags & NodeFlags.Ambient) == 0);
            if (first is not null)
            {
                if (SemanticSyntax.Source(node) != SemanticSyntax.Source(first))
                    Error(node.Name!, DiagnosticCode.ANamespaceDeclarationCannotBeInADifferentFileFromAClassOrFunctionWithWhichItIsMerged);
                else if (node.Pos < first.Pos)
                    Error(node.Name!, DiagnosticCode.ANamespaceDeclarationCannotBeLocatedPriorToAClassOrFunctionWithWhichItIsMerged);
            }
            ExportedDeclaration(node, true);
        }
        if (AmbientModule(node))
        {
            if (ModuleAugmentation(node))
            {
                if (node.Body is ModuleBlockNode block && (global || (symbol.Flags & SymbolFlags.Transient) != 0))
                    foreach (var statement in block.Statements!)
                    {
                        if (statement is ImportDeclarationNode
                            or ImportEqualsDeclarationNode { ModuleReference: ExternalModuleReferenceNode })
                        {
                            if (SemanticSyntax.Source(statement)?.ParseDiagnostics.Count == 0)
                                ErrorOnFirstToken(
                                    statement,
                                    DiagnosticCode.ImportsAreNotPermittedInModuleAugmentationsConsiderMovingThemToTheEnclosingExternalModule);
                        }
                        else if (statement is ExportDeclarationNode or ExportAssignmentNode)
                        {
                            if (SemanticSyntax.Source(statement)?.ParseDiagnostics.Count == 0)
                                ErrorOnFirstToken(
                                    statement,
                                    DiagnosticCode.ExportsAndExportAssignmentsAreNotPermittedInModuleAugmentations);
                        }
                    }
            }
            else if (node.Parent is SourceFileNode file && program.Symbols.Binding(file)?.IsModule != true)
            {
                if (global)
                    Error(
                        node.Name!,
                        DiagnosticCode.AugmentationsForTheGlobalScopeCanOnlyBeDirectlyNestedInExternalModulesOrAmbientModuleDeclarations);
                else if (node.Name is StringLiteralNode name && RelativeModuleName(name.Text))
                    Error(name, DiagnosticCode.AmbientModuleDeclarationCannotSpecifyRelativeModuleName);
            }
            else
                Error(
                    node.Name!,
                    global
                        ? DiagnosticCode.AugmentationsForTheGlobalScopeCanOnlyBeDirectlyNestedInExternalModulesOrAmbientModuleDeclarations
                        : DiagnosticCode.AmbientModulesCannotBeNestedInOtherModulesOrNamespaces);
        }
    }

    private async ValueTask CheckImportEqualsSourceAsync(ImportEqualsDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(
            node,
            (node.Flags & NodeFlags.JavaScriptFile) != 0
                ? DiagnosticCode.AnImportDeclarationCanOnlyBeUsedAtTheTopLevelOfAModule
                : DiagnosticCode.AnImportDeclarationCanOnlyBeUsedAtTheTopLevelOfANamespaceOrModule))
        {
            await CheckMisplacedModuleNameAsync(node, cancellation);
            return;
        }
        ExportedDeclaration(node, false);
        if (ErasableSyntaxOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, DiagnosticCode.ThisSyntaxIsNotAllowedWhenErasableSyntaxOnlyIsEnabled);
        if (node.ModuleReference is ExternalModuleReferenceNode external && !ExternalModuleSyntax(node, external.Expression))
            return;
        await CheckAliasSourceAsync(node, cancellation).ConfigureAwait(false);
        await MarkLinkedReferenceForEmitAsync(node, cancellation).ConfigureAwait(false);
        if (node.ModuleReference is not ExternalModuleReferenceNode)
        {
            var target = await program.Aliases.ResolveAsync(program.Symbols.Declaration(node)!, cancellation).ConfigureAwait(false);
            if (target != program.Symbols.UnknownSymbol)
            {
                var flags = await program.Aliases.FlagsAsync(target, cancellation: cancellation).ConfigureAwait(false);
                if ((flags & SymbolFlags.Type) != 0 && ReservedTypeName(node.Name!.Text))
                    Error(node.Name, DiagnosticCode.ImportNameCannotBe0, node.Name.Text);
                if ((flags & SymbolFlags.Value) != 0)
                {
                    var first = node.ModuleReference!;
                    while (first is QualifiedNameNode qualified)
                        first = qualified.Left!;
                    var resolved = await program.EntityNames.ResolveAsync(
                        first,
                        SymbolFlags.Value | SymbolFlags.Namespace,
                        cancellation: cancellation).ConfigureAwait(false);
                    if (resolved is not null && (resolved.Flags & SymbolFlags.Namespace) == 0)
                        Error(
                            first,
                            DiagnosticCode.Module0IsHiddenByALocalDeclarationWithTheSameName,
                            CheckerDiagnostic.DeclarationName(first));
                }
            }
            if (node.IsTypeOnly && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                Error(node, DiagnosticCode.AnImportAliasCannotUseImportType);
        }
        else if (ModuleKind is >= 5 and <= 99 && !node.IsTypeOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(
                node,
                DiagnosticCode.ImportAssignmentCannotBeUsedWhenTargetingECMAScriptModulesConsiderUsingImportAsteriskAsNsFromModImportAFromModImportDFromModOrAnotherModuleFormatInstead);
    }

    private async ValueTask CheckImportSourceAsync(ImportDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(
            node,
            (node.Flags & NodeFlags.JavaScriptFile) != 0
                ? DiagnosticCode.AnImportDeclarationCanOnlyBeUsedAtTheTopLevelOfAModule
                : DiagnosticCode.AnImportDeclarationCanOnlyBeUsedAtTheTopLevelOfANamespaceOrModule))
        {
            await CheckMisplacedModuleNameAsync(node, cancellation);
            return;
        }
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            ErrorOnFirstToken(node, DiagnosticCode.AnImportDeclarationCannotHaveModifiers);
        bool validModule = ExternalModuleSyntax(node, node.ModuleSpecifier);
        await CheckImportAttributesAsync(node, node.Attributes, cancellation).ConfigureAwait(false);
        if (!validModule)
            return;
        if (node.ImportClause is { } clause)
        {
            if (!ImportClauseGrammar(clause))
            {
                if (clause.Name is not null)
                    await CheckAliasSourceAsync(clause, cancellation).ConfigureAwait(false);
                if (clause.NamedBindings is NamespaceImportNode ns)
                {
                    await CheckAliasSourceAsync(ns, cancellation).ConfigureAwait(false);
                    if (EmitModuleKind(node) == 1)
                        await ExternalHelpersAsync(node, ["__importStar"], cancellation);
                }
                else if (clause.NamedBindings is NamedImportsNode imports
                    && await program.ExternalModuleAsync(
                        node,
                        node.ModuleSpecifier,
                        node.Attributes,
                        cancellation).ConfigureAwait(false) is { } module)
                {
                    if (DefaultOnlyModule(module, node.ModuleSpecifier!)
                        && imports.Elements?.Any(
                            e => (e as ImportSpecifierNode)?.PropertyName is not IdentifierNode { Text: "default" }) == true)
                        ListError(
                            imports,
                            imports.Elements!,
                            DiagnosticCode.NamedImportsFromAJSONFileIntoAnECMAScriptModuleAreNotAllowedWhenModuleIsSetTo0,
                            ModuleKind switch { 100 => "Node16", 101 => "Node18", 102 => "Node20", _ => "NodeNext" });
                    foreach (var binding in imports.Elements!)
                        await CheckAliasSourceAsync(binding, cancellation).ConfigureAwait(false);
                }
                if (clause.Name is not null && clause.NamedBindings is not NamespaceImportNode && EmitModuleKind(node) == 1)
                    await ExternalHelpersAsync(node, ["__importDefault"], cancellation);
            }
            if (!SemanticSyntax.TypeOnly(clause) && ModuleKind is >= 101 and <= 199
                && await ResolveImportModuleAsync(node, node.ModuleSpecifier,
                    node.Attributes is null ? null : await ImportAttributesExpressionAsync(node.Attributes, cancellation),
                    cancellation) is { } resolved
                && DefaultOnlyModule(resolved, node.ModuleSpecifier!)
                && node.Attributes?.Attributes?.OfType<ImportAttributeNode>().Any(a => ImportAttributeName(a.Name!) == "type"
                    && a.Value is StringLiteralNode { Text: "json" }) != true)
                Error(
                    node.ModuleSpecifier!,
                    DiagnosticCode.ImportingAJSONFileIntoAnECMAScriptModuleRequiresATypeColonJsonImportAttributeWhenModuleIsSetTo0,
                    ModuleKind switch { 101 => "Node18", 102 => "Node20", _ => "NodeNext" });
        }
        else if (program.Symbols.Program.Configuration.Options.Boolean("noUncheckedSideEffectImports") != false)
        {
            await program.ExternalModuleAsync(node, node.ModuleSpecifier, node.Attributes, cancellation).ConfigureAwait(false);
        }
    }

    private bool ImportClauseGrammar(ImportClauseNode clause)
    {
        if (SemanticSyntax.Source(clause)?.ParseDiagnostics.Count != 0)
            return false;
        DiagnosticCode code = DiagnosticCode.None;
        if (clause.PhaseModifier == SyntaxKind.TypeKeyword)
        {
            if ((clause.Flags & NodeFlags.JSDoc) == 0 && clause.Name is not null && clause.NamedBindings is not null)
                code = DiagnosticCode.ATypeOnlyImportCanSpecifyADefaultImportOrNamedBindingsButNotBoth;
            else if (clause.NamedBindings is NamedImportsNode imports)
                return TypeOnlyBindingsGrammar(
                    imports.Elements!,
                    DiagnosticCode.TheTypeModifierCannotBeUsedOnANamedImportWhenImportTypeIsUsedOnItsImportStatement);
        }
        else if (clause.PhaseModifier == SyntaxKind.DeferKeyword)
            code = clause.Name is not null ? DiagnosticCode.DefaultImportsAreNotAllowedInADeferredImport : clause.NamedBindings is NamedImportsNode ? DiagnosticCode.NamedImportsAreNotAllowedInADeferredImport
                : ModuleKind is not (99 or 200)
                    ? DiagnosticCode.DeferredImportsAreOnlySupportedWhenTheModuleFlagIsSetToEsnextOrPreserve
                    : DiagnosticCode.None;
        if (code == DiagnosticCode.None)
            return false;
        Error(clause, code);
        return true;
    }

    private bool TypeOnlyBindingsGrammar(NodeList bindings, DiagnosticCode code)
    {
        foreach (var binding in bindings)
            if (SemanticSyntax.TypeOnly(binding))
            {
                ErrorOnFirstToken(binding, code);
                return true;
            }
        return false;
    }

    private async ValueTask CheckAliasSourceAsync(SyntaxNode node, CancellationToken cancellation)
    {
        CheckDeclarationName(node);
        var symbol = program.Symbols.Declaration(node)!;
        var target = await program.Aliases.ResolveAsync(symbol, cancellation).ConfigureAwait(false);
        if (target == program.Symbols.UnknownSymbol)
            return;
        symbol = program.Symbols.Merger.GetMergedSymbol(symbol.ExportSymbol ?? symbol)!;
        var flags = await program.Aliases.FlagsAsync(target, cancellation: cancellation).ConfigureAwait(false);
        if ((node.Flags & NodeFlags.JavaScriptFile) != 0 && (flags & SymbolFlags.Value) == 0 && !AliasResolver.IsTypeOnly(node))
        {
            var name = node switch
            {
                ImportSpecifierNode imported => imported.PropertyName ?? imported.Name,
                ExportSpecifierNode export => export.PropertyName ?? export.Name,
                _ => SemanticSyntax.Name(node)
            } ?? node;
            if (node is ExportSpecifierNode)
            {
                var diagnostic = CheckerDiagnostic.Create(name, Messages.Types_cannot_appear_in_export_declarations_in_JavaScript_files);
                var source = SemanticSyntax.Source(node)!;
                if (program.Symbols.Declaration(source)?.Exports.GetValueOrDefault(SyntaxNameText.Get(name)) == target
                    && target.Declarations.FirstOrDefault(d => d.Kind == SyntaxKind.JSTypeAliasDeclaration) is { } declaration)
                    diagnostic = diagnostic with
                    {
                        RelatedInformation = [CheckerDiagnostic.Create(declaration,
                        Messages.X_0_is_automatically_exported_here, target.Name)]
                    };
                Error(name, diagnostic);
            }
            else
            {
                var declaration = DeclarationOrder.Ancestor(node,
                    n => n is ImportDeclarationNode or ImportEqualsDeclarationNode or VariableDeclarationNode);
                var specifier = declaration is VariableDeclarationNode { Initializer: CallExpressionNode { Arguments: { Count: > 0 } arguments } }
                    ? arguments[0] : declaration is null ? null : AliasTargets.Specifier(declaration);
                string text = name is IdentifierNode identifier ? identifier.Text : symbol.Name;
                string importText = "import(\"" + (AliasTargets.Text(specifier) ?? "...") + "\")"
                    + (node is ImportSpecifierNode ? "." + text : "");
                Error(name, DiagnosticCode.X0IsATypeAndCannotBeImportedInJavaScriptFilesUse1InAJSDocTypeAnnotation, text, importText);
            }
            return;
        }
        var excluded = (symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0 ? SymbolFlags.Value : 0;
        if ((symbol.Flags & SymbolFlags.Type) != 0)
            excluded |= SymbolFlags.Type;
        if ((symbol.Flags & SymbolFlags.Namespace) != 0)
            excluded |= SymbolFlags.Namespace;
        if ((flags & excluded) != 0)
            Error(
                node,
                node is ExportSpecifierNode
                    ? DiagnosticCode.ExportDeclarationConflictsWithExportedDeclarationOf0
                    : DiagnosticCode.ImportDeclarationConflictsWithLocalDeclarationOf0,
                TypeDisplay.SymbolName(symbol));
        else if (node is not ExportSpecifierNode && program.Symbols.Program.Configuration.Options.Boolean("isolatedModules") == true
            && !AliasResolver.IsTypeOnly(node) && (symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0)
            Error(
                node,
                DiagnosticCode.Import0ConflictsWithLocalValueSoMustBeDeclaredWithATypeOnlyImportWhenIsolatedModulesIsEnabled,
                TypeDisplay.SymbolName(symbol),
                IsolatedModuleOptionName);
        bool typeOnly = AliasResolver.IsTypeOnly(node);
        if (IsolatedModules && !typeOnly && (node.Flags & NodeFlags.Ambient) == 0)
        {
            var typeOnlyDeclaration = await program.Aliases.TypeOnlyAsync(symbol, cancellation: cancellation).ConfigureAwait(false);
            bool type = (flags & SymbolFlags.Value) == 0;
            bool verbatim = program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true;
            if (type || typeOnlyDeclaration is not null)
            {
                if (node is ImportClauseNode or ImportSpecifierNode or ImportEqualsDeclarationNode)
                {
                    if (verbatim)
                    {
                        string name = AliasTargets.Text(
                            (node as ImportSpecifierNode)?.PropertyName ?? SemanticSyntax.Name(node)) ?? symbol.Name;
                        DiagnosticCode code = node is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode }
                            ? DiagnosticCode.AnImportAliasCannotResolveToATypeOrTypeOnlyDeclarationWhenVerbatimModuleSyntaxIsEnabled : type
                                ? DiagnosticCode.X0IsATypeAndMustBeImportedUsingATypeOnlyImportWhenVerbatimModuleSyntaxIsEnabled
                                : DiagnosticCode.X0ResolvesToATypeOnlyDeclarationAndMustBeImportedUsingATypeOnlyImportWhenVerbatimModuleSyntaxIsEnabled;
                        TypeOnlyAliasError(node, code, type ? null : typeOnlyDeclaration, name, name);
                    }
                    if (type && node is ImportEqualsDeclarationNode && SemanticSyntax.HasModifier(node, SyntaxKind.ExportKeyword))
                        Error(node, DiagnosticCode.CannotUseExportImportOnATypeOrTypeOnlyNamespaceWhen0IsEnabled, IsolatedModuleOptionName);
                }
                else if (node is ExportSpecifierNode export && (verbatim
                    || SemanticSyntax.Source(typeOnlyDeclaration) != SemanticSyntax.Source(node)))
                {
                    string name = AliasTargets.Text(export.PropertyName ?? export.Name) ?? symbol.Name;
                    TypeOnlyAliasError(
                        node,
                        type
                            ? DiagnosticCode.ReExportingATypeWhen0IsEnabledRequiresUsingExportType
                            : DiagnosticCode.X0ResolvesToATypeOnlyDeclarationAndMustBeReExportedUsingATypeOnlyReExportWhen1IsEnabled,
                        type ? null : typeOnlyDeclaration,
                        name,
                        type ? [IsolatedModuleOptionName] : [name, IsolatedModuleOptionName]);
                }
            }
            if (verbatim
                && node is not ImportEqualsDeclarationNode
                && (node.Flags & NodeFlags.JavaScriptFile) == 0
                && EmitModuleKind(node) == 1)
                Error(node, VerbatimModuleCode(node));
            else if (ModuleKind == 200 && node is not (ImportEqualsDeclarationNode or VariableDeclarationNode or BindingElementNode)
                && EmitModuleKind(node) == 1)
                Error(node, DiagnosticCode.ECMAScriptModuleSyntaxIsNotAllowedInACommonJSModuleWhenModuleIsSetToPreserve);
            if (verbatim && (flags & SymbolFlags.ConstEnum) != 0 && target.ValueDeclaration is { } enumDeclaration
                && (enumDeclaration.Flags & NodeFlags.Ambient) != 0)
            {
                var redirect = program.Symbols.Program.ProjectReferences.Outputs.GetValueOrDefault(SemanticSyntax.Source(enumDeclaration)!.FileName);
                if (redirect is null || !(redirect.Project.Options.Boolean("preserveConstEnums") == true
                    || redirect.Project.Options.Boolean("isolatedModules") == true || redirect.Project.Options.Boolean("verbatimModuleSyntax") == true))
                    Error(node, DiagnosticCode.CannotAccessAmbientConstEnumsWhen0IsEnabled, IsolatedModuleOptionName);
            }
        }
        if (node is ImportSpecifierNode import)
        {
            CheckModuleExportName(import.PropertyName, true);
            if (AliasTargets.Text(import.PropertyName ?? import.Name) == "default" && EmitModuleKind(import) == 1)
                await ExternalHelpersAsync(import, ["__importDefault"], cancellation);
            var deprecated = await program.Aliases.WithDeprecationAsync(symbol, node, cancellation).ConfigureAwait(false);
            if (program.Deprecations.Symbol(deprecated))
                program.Suggestion(node, DiagnosticCode.X0IsDeprecated, deprecated.Name);
        }
    }

    private DiagnosticCode VerbatimModuleCode(SyntaxNode node) => SemanticSyntax.Source(node)!.FileName.EndsWith(
        ".cts",
        StringComparison.OrdinalIgnoreCase)
        || SemanticSyntax.Source(node)!.FileName.EndsWith(
            ".cjs",
            StringComparison.OrdinalIgnoreCase) ? DiagnosticCode.ECMAScriptImportsAndExportsCannotBeWrittenInACommonJSFileUnderVerbatimModuleSyntax : DiagnosticCode.ECMAScriptImportsAndExportsCannotBeWrittenInACommonJSFileUnderVerbatimModuleSyntaxAdjustTheTypeFieldInTheNearestPackageJsonToMakeThisFileAnECMAScriptModuleOrAdjustYourVerbatimModuleSyntaxModuleAndModuleResolutionSettingsInTypeScript;

    private string IsolatedModuleOptionName => program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true
        ? "verbatimModuleSyntax" : "isolatedModules";

    private void TypeOnlyAliasError(
        SyntaxNode node,
        DiagnosticCode code,
        SyntaxNode? typeOnlyDeclaration,
        string name,
        params string[] arguments)
    {
        var diagnostic = CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(code), arguments);
        if (typeOnlyDeclaration is not null)
            diagnostic = diagnostic with
            {
                RelatedInformation = [CheckerDiagnostic.Create(typeOnlyDeclaration,
                    typeOnlyDeclaration is ExportSpecifierNode or ExportDeclarationNode or NamespaceExportNode
                        ? Messages.X_0_was_exported_here : Messages.X_0_was_imported_here, name)]
            };
        Error(node, diagnostic);
    }

    private void CheckModuleExportName(SyntaxNode? name, bool allowString)
    {
        if (name is not StringLiteralNode || SemanticSyntax.Source(name)?.ParseDiagnostics.Count != 0)
            return;
        if (!allowString)
            Error(name, DiagnosticCode.IdentifierExpected);
        else if (ModuleKind is 5 or 6 && SemanticSyntax.Source(name)?.IsDeclarationFile != true)
            Error(name, DiagnosticCode.StringLiteralImportAndExportNamesAreNotSupportedWhenTheModuleFlagIsSetToEs2015OrEs2020);
    }

    private async ValueTask CheckExportSourceAsync(ExportDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(
            node,
            (node.Flags & NodeFlags.JavaScriptFile) != 0
                ? DiagnosticCode.AnExportDeclarationCanOnlyBeUsedAtTheTopLevelOfAModule
                : DiagnosticCode.AnExportDeclarationCanOnlyBeUsedAtTheTopLevelOfANamespaceOrModule))
        {
            await CheckMisplacedModuleNameAsync(node, cancellation);
            return;
        }
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            ErrorOnFirstToken(node, DiagnosticCode.AnExportDeclarationCannotHaveModifiers);
        await CheckImportAttributesAsync(node, node.Attributes, cancellation).ConfigureAwait(false);
        if (node.ModuleSpecifier is not null && !ExternalModuleSyntax(node, node.ModuleSpecifier))
            return;
        if (node.ExportClause is NamedExportsNode exports)
        {
            if (SemanticSyntax.TypeOnly(node) && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
                TypeOnlyBindingsGrammar(
                    exports.Elements!,
                    DiagnosticCode.TheTypeModifierCannotBeUsedOnANamedExportWhenExportTypeIsUsedOnItsExportStatement);
            foreach (ExportSpecifierNode binding in exports.Elements!)
            {
                await CheckAliasSourceAsync(binding, cancellation).ConfigureAwait(false);
                CheckModuleExportName(binding.PropertyName, node.ModuleSpecifier is not null);
                CheckModuleExportName(binding.Name, true);
                if (node.ModuleSpecifier is not null && AliasTargets.Text(binding.PropertyName ?? binding.Name) == "default"
                    && EmitModuleKind(node) == 1)
                    await ExternalHelpersAsync(binding, ["__importDefault"], cancellation);
                if (node.ModuleSpecifier is null && (binding.PropertyName ?? binding.Name) is IdentifierNode name)
                {
                    var symbol = program.Symbols.NameResolver(cancellation).Resolve(
                        name,
                        name.Text,
                        SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias,
                        isUse: true);
                    if (symbol == program.Symbols.UndefinedSymbol || symbol == program.Symbols.GlobalThisSymbol
                        || symbol?.Declarations.FirstOrDefault() is { } declaration
                            && SemanticSyntax.DeclarationContainer(declaration) is SourceFileNode file
                            && program.Symbols.Binding(file)?.IsModule != true)
                        Error(name, DiagnosticCode.CannotExport0OnlyLocalDeclarationsCanBeExportedFromAModule, name.Text);
                    else if (symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0)
                        await AliasReferences.MarkAsync(symbol, name, cancellation).ConfigureAwait(false);
                }
            }
            bool ambient = node.Parent is ModuleBlockNode
                && (AmbientModule(node.Parent.Parent) || (node.Flags & NodeFlags.Ambient) != 0 && node.ModuleSpecifier is null);
            if (node.Parent is not SourceFileNode && !ambient)
                Error(node, DiagnosticCode.ExportDeclarationsAreNotPermittedInANamespace);
        }
        else if (await program.ExternalModuleAsync(
            node,
            node.ModuleSpecifier,
            node.Attributes,
            cancellation).ConfigureAwait(false) is { } module)
        {
            if (module.Exports.ContainsKey("export="))
                Error(
                    node.ModuleSpecifier!,
                    DiagnosticCode.Module0UsesExportAndCannotBeUsedWithExportAsterisk,
                    await SymbolDisplayNameAsync(module, null, SymbolFlags.All, cancellation));
            else if (node.ExportClause is NamespaceExportNode ns)
            {
                await CheckAliasSourceAsync(ns, cancellation).ConfigureAwait(false);
                CheckModuleExportName(ns.Name, true);
            }
            if (EmitModuleKind(node) == 1)
                await ExternalHelpersAsync(node, node.ExportClause is null ? ["__exportStar"] : ["__importStar"], cancellation);
        }
    }

    private async ValueTask CheckExportAssignmentSourceAsync(ExportAssignmentNode node, CancellationToken cancellation)
    {
        var type = await CachedExpressionAsync(node.Expression!, 0, cancellation).ConfigureAwait(false);
        if (!ModuleContext(
            node,
            node.IsExportEquals
                ? DiagnosticCode.AnExportAssignmentMustBeAtTheTopLevelOfAFileOrModuleDeclaration
                : DiagnosticCode.ADefaultExportMustBeAtTheTopLevelOfAFileOrModuleDeclaration))
            return;
        if (node.Parent is ModuleBlockNode && !AmbientModule(node.Parent.Parent))
        {
            Error(
                node,
                node.IsExportEquals
                    ? DiagnosticCode.AnExportAssignmentCannotBeUsedInANamespace
                    : DiagnosticCode.ADefaultExportCanOnlyBeUsedInAnECMAScriptStyleModule);
            return;
        }
        if (node.IsExportEquals && ErasableSyntaxOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, DiagnosticCode.ThisSyntaxIsNotAllowedWhenErasableSyntaxOnlyIsEnabled);
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            ErrorOnFirstToken(node, DiagnosticCode.AnExportAssignmentCannotHaveModifiers);
        bool verbatim = program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true;
        bool illegalDefault = !node.IsExportEquals && (node.Flags & NodeFlags.Ambient) == 0 && verbatim && EmitModuleKind(node) == 1;
        if (node.Expression is IdentifierNode identifier)
        {
            var symbol = await program.EntityNames.ResolveAsync(
                identifier,
                SymbolFlags.All,
                true,
                true,
                cancellation: cancellation).ConfigureAwait(false);
            if (symbol is not null)
            {
                if ((symbol.Flags & SymbolFlags.ExportValue) != 0)
                    symbol = symbol.ExportSymbol ?? symbol;
                if ((symbol.Flags & SymbolFlags.Alias) != 0)
                    await AliasReferences.MarkAsync(symbol, identifier, cancellation).ConfigureAwait(false);
                var typeOnly = await program.Aliases.TypeOnlyAsync(symbol, SymbolFlags.Value, cancellation).ConfigureAwait(false);
                if (!illegalDefault && (node.Flags & NodeFlags.Ambient) == 0)
                {
                    var flags = await program.Aliases.FlagsAsync(symbol, cancellation: cancellation).ConfigureAwait(false);
                    if (verbatim)
                    {
                        if ((flags & SymbolFlags.Value) == 0)
                            Error(
                                identifier,
                                node.IsExportEquals
                                    ? DiagnosticCode.AnExportDeclarationMustReferenceAValueWhenVerbatimModuleSyntaxIsEnabledBut0OnlyRefersToAType
                                    : DiagnosticCode.AnExportDefaultMustReferenceAValueWhenVerbatimModuleSyntaxIsEnabledBut0OnlyRefersToAType,
                                identifier.Text);
                        else if (typeOnly is not null)
                            Error(
                                identifier,
                                node.IsExportEquals
                                    ? DiagnosticCode.AnExportDeclarationMustReferenceARealValueWhenVerbatimModuleSyntaxIsEnabledBut0ResolvesToATypeOnlyDeclaration
                                    : DiagnosticCode.AnExportDefaultMustReferenceARealValueWhenVerbatimModuleSyntaxIsEnabledBut0ResolvesToATypeOnlyDeclaration,
                                identifier.Text);
                    }
                    if (IsolatedModules && (symbol.Flags & SymbolFlags.Value) == 0)
                    {
                        var nonLocal = await program.Aliases.FlagsAsync(
                            symbol,
                            excludeLocal: true,
                            cancellation: cancellation).ConfigureAwait(false);
                        bool otherFile = typeOnly is not null && SemanticSyntax.Source(typeOnly) != SemanticSyntax.Source(node);
                        if ((symbol.Flags & SymbolFlags.Alias) != 0
                            && (nonLocal & SymbolFlags.Type) != 0
                            && (nonLocal & SymbolFlags.Value) == 0
                            && (typeOnly is null || otherFile))
                            Error(
                                identifier,
                                node.IsExportEquals
                                    ? DiagnosticCode.X0ResolvesToATypeAndMustBeMarkedTypeOnlyInThisFileBeforeReExportingWhen1IsEnabledConsiderUsingImportTypeWhere0IsImported
                                    : DiagnosticCode.X0ResolvesToATypeAndMustBeMarkedTypeOnlyInThisFileBeforeReExportingWhen1IsEnabledConsiderUsingExportType0AsDefault,
                                identifier.Text,
                                IsolatedModuleOptionName);
                        else if (otherFile)
                            Error(identifier, CheckerDiagnostic.Create(identifier,
                                DiagnosticLocalization.GetMessage(
                                    node.IsExportEquals
                                        ? DiagnosticCode.X0ResolvesToATypeOnlyDeclarationAndMustBeMarkedTypeOnlyInThisFileBeforeReExportingWhen1IsEnabledConsiderUsingImportTypeWhere0IsImported
                                        : DiagnosticCode.X0ResolvesToATypeOnlyDeclarationAndMustBeMarkedTypeOnlyInThisFileBeforeReExportingWhen1IsEnabledConsiderUsingExportType0AsDefault),
                                identifier.Text,
                                IsolatedModuleOptionName) with
                            {
                                RelatedInformation = [CheckerDiagnostic.Create(typeOnly,
                                typeOnly is ExportSpecifierNode or ExportDeclarationNode ? Messages.X_0_was_exported_here
                                    : Messages.X_0_was_imported_here, identifier.Text)]
                            });
                    }
                }
            }
        }
        if (illegalDefault)
            Error(node, VerbatimModuleCode(node));
        await CheckExternalExportsAsync(
            node.Parent is SourceFileNode ? node.Parent : node.Parent!.Parent!,
            cancellation).ConfigureAwait(false);
        if (node.Type is not null)
            await CheckLiteralAssignableAsync(
                type,
                await Nodes.FromNodeAsync(node.Type, cancellation).ConfigureAwait(false),
                node.Expression!,
                node.Expression!,
                cancellation).ConfigureAwait(false);
        if ((node.Flags & NodeFlags.Ambient) != 0 && !TypeScript.Compiler.Semantics.ConstantEvaluator.EntityName(node.Expression!))
            Error(node.Expression!, DiagnosticCode.TheExpressionOfAnExportAssignmentMustBeAnIdentifierOrQualifiedNameInAnAmbientContext);
        bool ambient = (node.Flags & NodeFlags.Ambient) != 0;
        var format = ModuleTargetMode(SemanticSyntax.Source(node)!);
        if (node.IsExportEquals
            && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0
            && ModuleKind >= 5
            && ModuleKind != 200
            && (ambient ? format == ReferenceResolutionMode.Import : format != ReferenceResolutionMode.Require))
            Error(
                node,
                DiagnosticCode.ExportAssignmentCannotBeUsedWhenTargetingECMAScriptModulesConsiderUsingExportDefaultOrAnotherModuleFormatInstead);
        else if (node.IsExportEquals && ModuleKind == 4 && (node.Flags & NodeFlags.Ambient) == 0
            && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, DiagnosticCode.ExportAssignmentIsNotSupportedWhenModuleFlagIsSystem);
    }

    private async ValueTask CheckExternalExportsAsync(SyntaxNode node, CancellationToken cancellation)
    {
        var symbol = program.Symbols.Declaration(node)!;
        if (checkedModuleExports.Contains(symbol))
            return;
        if (symbol.Exports.TryGetValue("export=", out var assignment))
        {
            bool value = false;
            foreach (var (name, exported) in symbol.Exports)
                if (name != "export="
                    && (await program.Aliases.FlagsAsync(
                        exported,
                        cancellation: cancellation).ConfigureAwait(false) & SymbolFlags.Value) != 0)
                {
                    value = true;
                    break;
                }
            if (!value && (assignment.Flags & (SymbolFlags.NamespaceModule | SymbolFlags.Alias))
                == (SymbolFlags.NamespaceModule | SymbolFlags.Alias))
            {
                var target = await program.Aliases.ResolveAsync(assignment, cancellation);
                if ((target.Flags & SymbolFlags.Namespace) != 0)
                    foreach (var exported in target.Exports.Values)
                        if (exported.Name != "export=" && (await program.Aliases.FlagsAsync(exported, cancellation: cancellation)
                            & (SymbolFlags.Type | SymbolFlags.Namespace)) != 0)
                        {
                            value = true;
                            break;
                        }
            }
            if (value && (AliasResolver.Declaration(assignment) ?? assignment.ValueDeclaration) is { } declaration
                && !(declaration.Parent is ModuleBlockNode { Parent: { } module } && ModuleAugmentation(module)))
                Error(declaration, DiagnosticCode.AnExportAssignmentCannotBeUsedInAModuleWithOtherExportedElements);
        }
        foreach (var (name, exported) in await program.ModuleExports.ResolveAsync(symbol, cancellation).ConfigureAwait(false))
        {
            if (name == Symbol.InternalPrefix + "export" || (exported.Flags & (SymbolFlags.Namespace | SymbolFlags.Enum)) != 0)
                continue;
            bool NotOverload(SyntaxNode n) =>
                n is not FunctionDeclarationNode and not MethodDeclarationNode || SemanticSyntax.Body(n) is not null;
            int count = exported.Declarations.Count(
                d => NotOverload(d)
                    && d is not GetAccessorDeclarationNode and not SetAccessorDeclarationNode and not InterfaceDeclarationNode);
            if ((exported.Flags & SymbolFlags.TypeAlias) != 0 && count <= 2)
                continue;
            if (count > 1 && !exported.Declarations.All(d => d is BinaryExpressionNode binary && ExportsPropertyAssignment(binary.Left!)))
                foreach (var declaration in exported.Declarations.Where(NotOverload))
                    Error(declaration, DiagnosticCode.CannotRedeclareExportedVariable0, TypeDisplay.SymbolName(exported));
        }
        cancellation.ThrowIfCancellationRequested();
        checkedModuleExports.Add(symbol);
    }
}
