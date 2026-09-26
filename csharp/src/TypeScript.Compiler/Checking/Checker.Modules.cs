using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
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

    private bool ModuleContext(SyntaxNode node, int code)
    {
        if (node.Parent is SourceFileNode or ModuleBlockNode or ModuleDeclarationNode)
            return true;
        if (SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0)
            Error(node, code);
        return false;
    }

    private bool ExternalModuleSyntax(SyntaxNode node, SyntaxNode? specifier)
    {
        if (specifier is null || specifier.Pos == specifier.End)
            return false;
        if (specifier is not StringLiteralNode literal)
        {
            Error(specifier, 1141);
            return false;
        }
        bool ambient = node.Parent is ModuleBlockNode && AmbientModule(node.Parent.Parent);
        if (node.Parent is not SourceFileNode && !ambient)
        {
            Error(specifier, node is ExportDeclarationNode ? 1194 : 1147);
            return false;
        }
        if (ambient && (literal.Text.StartsWith(".", StringComparison.Ordinal) || literal.Text.StartsWith('/'))
            && !ModuleAugmentation(node.Parent!.Parent!))
        {
            Error(node, 2439);
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

    private void ExportedDeclaration(SyntaxNode node, bool value)
    {
        if (DeclarationModifiers(node))
            return;
        if (node is not IModifiedNode { Modifiers: { } modifiers })
            return;
        if (value && node.Parent is SourceFileNode && (node.Flags & NodeFlags.Ambient) == 0
            && program.Symbols.Program.Configuration.Options.Boolean("verbatimModuleSyntax") == true && EmitModuleKind(node) == 1
            && modifiers.FirstOrDefault(m => m.Kind == SyntaxKind.ExportKeyword) is { } export)
            Error(export, 1287);
        else if (node is VariableStatementNode { DeclarationList: { } declarations } && (declarations.Flags & NodeFlags.Using) != 0
            && SemanticSyntax.Source(node)?.ParseDiagnostics.Count == 0
            && modifiers.FirstOrDefault(
                m => m.Kind is SyntaxKind.ExportKeyword or SyntaxKind.DefaultKeyword or SyntaxKind.DeclareKeyword) is { } modifier)
            Error(modifier, (declarations.Flags & NodeFlags.BlockScoped) == NodeFlags.AwaitUsing ? 1495 : 1491);
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
            Error(node.Name!, 2670);
        if (node.Attributes is not null)
            await CheckModuleAttributesAsync(node, cancellation).ConfigureAwait(false);
        if (!ModuleContext(node, AmbientModule(node) ? 1234 : 1235))
            return;
        if (!ambient && node.Name is StringLiteralNode)
            Error(node.Name, 1035);
        if (node.Name is IdentifierNode && node.Keyword == SyntaxKind.ModuleKeyword)
            Error(node.Name, 1540);
        var symbol = program.Symbols.Declaration(node)!;
        await CheckMergedExportsAsync(node, cancellation).ConfigureAwait(false);
        int state = Binder.ModuleState(node);
        bool instantiated = state == 2
            || state == 1 && (IsolatedModules || program.Symbols.Program.Configuration.Options.Boolean("preserveConstEnums") == true);
        if ((symbol.Flags & SymbolFlags.ValueModule) != 0 && !ambient && instantiated)
        {
            if (ErasableSyntaxOnly && (node.Flags & NodeFlags.JavaScriptFile) == 0)
                Error(node, 1294);
            if (IsolatedModules && program.Symbols.Binding(node)?.IsModule != true)
                Error(node.Name!, 1280);
            var first = symbol.Declarations.FirstOrDefault(
                d => (d is ClassDeclarationNode || d is FunctionDeclarationNode { Body: not null }) && (d.Flags & NodeFlags.Ambient) == 0);
            if (first is not null)
            {
                if (SemanticSyntax.Source(node) != SemanticSyntax.Source(first))
                    Error(node.Name!, 2433);
                else if (node.Pos < first.Pos)
                    Error(node.Name!, 2434);
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
                        if (statement is ImportDeclarationNode or ImportEqualsDeclarationNode)
                            Error(statement, 2667);
                        else if (statement is ExportDeclarationNode or ExportAssignmentNode)
                            Error(statement, 2666);
                    }
            }
            else if (node.Parent is SourceFileNode file && program.Symbols.Binding(file)?.IsModule != true)
            {
                if (global)
                    Error(node.Name!, 2669);
                else if (node.Name is StringLiteralNode name && (name.Text.StartsWith('.') || name.Text.StartsWith('/')))
                    Error(name, 2436);
            }
            else
                Error(node.Name!, global ? 2669 : 2435);
        }
    }

    private async ValueTask CheckImportEqualsSourceAsync(ImportEqualsDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(node, (node.Flags & NodeFlags.JavaScriptFile) != 0 ? 1473 : 1232))
            return;
        ExportedDeclaration(node, false);
        if (ErasableSyntaxOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, 1294);
        if (node.ModuleReference is ExternalModuleReferenceNode external && !ExternalModuleSyntax(node, external.Expression))
            return;
        await CheckAliasSourceAsync(node, cancellation).ConfigureAwait(false);
        if (node.ModuleReference is not ExternalModuleReferenceNode)
        {
            var target = await program.Aliases.ResolveAsync(program.Symbols.Declaration(node)!, cancellation).ConfigureAwait(false);
            if (target != program.Symbols.UnknownSymbol)
            {
                var flags = await program.Aliases.FlagsAsync(target, cancellation: cancellation).ConfigureAwait(false);
                if ((flags & SymbolFlags.Type) != 0 && ReservedTypeName(node.Name!.Text))
                    Error(node.Name, 2438);
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
                        Error(first, 2437);
                }
            }
            if (node.IsTypeOnly)
                Error(node, 1392);
        }
        else if (ModuleKind is >= 5 and <= 99 && !node.IsTypeOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, 1202);
    }

    private async ValueTask CheckImportSourceAsync(ImportDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(node, (node.Flags & NodeFlags.JavaScriptFile) != 0 ? 1473 : 1232))
            return;
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            Error(node, 1191);
        bool validModule = ExternalModuleSyntax(node, node.ModuleSpecifier);
        await CheckImportAttributesAsync(node, node.Attributes, cancellation).ConfigureAwait(false);
        if (!validModule)
            return;
        if (node.ImportClause is { } clause)
        {
            if (SemanticSyntax.TypeOnly(clause) && clause.Name is not null && clause.NamedBindings is not null)
                Error(clause, 1363);
            else
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
                        Error(imports, 1544);
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
                Error(node.ModuleSpecifier!, 1543);
        }
        else if (program.Symbols.Program.Configuration.Options.Boolean("noUncheckedSideEffectImports") != false)
        {
            await program.ExternalModuleAsync(node, node.ModuleSpecifier, node.Attributes, cancellation).ConfigureAwait(false);
        }
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
                Error(name, 18042, text, importText);
            }
            return;
        }
        var excluded = (symbol.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue)) != 0 ? SymbolFlags.Value : 0;
        if ((symbol.Flags & SymbolFlags.Type) != 0)
            excluded |= SymbolFlags.Type;
        if ((symbol.Flags & SymbolFlags.Namespace) != 0)
            excluded |= SymbolFlags.Namespace;
        if ((flags & excluded) != 0)
            Error(node, node is ExportSpecifierNode ? 2484 : 2440, TypeDisplay.SymbolName(symbol));
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
                        Error(
                            node,
                            node is ImportEqualsDeclarationNode { ModuleReference: not ExternalModuleReferenceNode }
                                ? 1485
                                : type ? 1484 : 1485);
                    if (type && node is ImportEqualsDeclarationNode && SemanticSyntax.HasModifier(node, SyntaxKind.ExportKeyword))
                        Error(node, 1269);
                }
                else if (node is ExportSpecifierNode)
                    Error(node, type ? 1205 : 1448);
            }
            if (verbatim
                && node is not ImportEqualsDeclarationNode
                && (node.Flags & NodeFlags.JavaScriptFile) == 0
                && EmitModuleKind(node) == 1)
                Error(node, VerbatimModuleCode(node));
            else if (ModuleKind == 200 && node is not (ImportEqualsDeclarationNode or VariableDeclarationNode or BindingElementNode)
                && EmitModuleKind(node) == 1)
                Error(node, 1293);
        }
        if (node is ImportSpecifierNode import)
        {
            CheckModuleExportName(import.PropertyName, true);
            if (AliasTargets.Text(import.PropertyName ?? import.Name) == "default" && EmitModuleKind(import) == 1)
                await ExternalHelpersAsync(import, ["__importDefault"], cancellation);
            var deprecated = await program.Aliases.WithDeprecationAsync(symbol, node, cancellation).ConfigureAwait(false);
            if (program.Deprecations.Symbol(deprecated))
                program.Suggestion(node, 6385, deprecated.Name);
        }
    }

    private int VerbatimModuleCode(SyntaxNode node) => SemanticSyntax.Source(node)!.FileName.EndsWith(
        ".cts",
        StringComparison.OrdinalIgnoreCase)
        || SemanticSyntax.Source(node)!.FileName.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) ? 1286 : 1295;

    private void CheckModuleExportName(SyntaxNode? name, bool allowString)
    {
        if (name is not StringLiteralNode || SemanticSyntax.Source(name)?.ParseDiagnostics.Count != 0)
            return;
        if (!allowString)
            Error(name, 1003);
        else if (ModuleKind is 5 or 6 && SemanticSyntax.Source(name)?.IsDeclarationFile != true)
            Error(name, 18057);
    }

    private async ValueTask CheckExportSourceAsync(ExportDeclarationNode node, CancellationToken cancellation)
    {
        if (!ModuleContext(node, (node.Flags & NodeFlags.JavaScriptFile) != 0 ? 1474 : 1233))
            return;
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            Error(node, 1193);
        await CheckImportAttributesAsync(node, node.Attributes, cancellation).ConfigureAwait(false);
        if (node.ModuleSpecifier is not null && !ExternalModuleSyntax(node, node.ModuleSpecifier))
            return;
        if (node.ExportClause is NamedExportsNode exports)
        {
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
                        Error(name, 2661, name.Text);
                    else if (symbol is not null && (symbol.Flags & SymbolFlags.Alias) != 0)
                        await AliasReferences.MarkAsync(symbol, name, cancellation).ConfigureAwait(false);
                }
            }
            bool ambient = node.Parent is ModuleBlockNode
                && (AmbientModule(node.Parent.Parent) || (node.Flags & NodeFlags.Ambient) != 0 && node.ModuleSpecifier is null);
            if (node.Parent is not SourceFileNode && !ambient)
                Error(node, 1194);
        }
        else if (await program.ExternalModuleAsync(
            node,
            node.ModuleSpecifier,
            node.Attributes,
            cancellation).ConfigureAwait(false) is { } module)
        {
            if (module.Exports.ContainsKey("export="))
                Error(node.ModuleSpecifier!, 2498);
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
        if (!ModuleContext(node, node.IsExportEquals ? 1063 : 1258))
            return;
        if (node.Parent is ModuleBlockNode && !AmbientModule(node.Parent.Parent))
        {
            Error(node, node.IsExportEquals ? 1063 : 1319);
            return;
        }
        if (node.IsExportEquals && ErasableSyntaxOnly && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, 1294);
        if (!DeclarationModifiers(node) && node.Modifiers is { Count: > 0 })
            Error(node, 1120);
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
                            Error(identifier, node.IsExportEquals ? 1282 : 1284);
                        else if (typeOnly is not null)
                            Error(identifier, node.IsExportEquals ? 1283 : 1285);
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
                            Error(identifier, node.IsExportEquals ? 1291 : 1292);
                        else if (otherFile)
                            Error(identifier, node.IsExportEquals ? 1289 : 1290);
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
            Error(node.Expression!, 2714);
        bool ambient = (node.Flags & NodeFlags.Ambient) != 0;
        var format = ModuleTargetMode(SemanticSyntax.Source(node)!);
        if (node.IsExportEquals
            && ModuleKind >= 5
            && ModuleKind != 200
            && (ambient ? format == ReferenceResolutionMode.Import : format != ReferenceResolutionMode.Require))
            Error(node, 1203);
        else if (node.IsExportEquals && ModuleKind == 4 && (node.Flags & NodeFlags.Ambient) == 0)
            Error(node, 1218);
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
            if (value && (AliasResolver.Declaration(assignment) ?? assignment.ValueDeclaration) is { } declaration)
                Error(declaration, 2309);
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
                    Error(declaration, 2323, TypeDisplay.SymbolName(exported));
        }
        cancellation.ThrowIfCancellationRequested();
        checkedModuleExports.Add(symbol);
    }
}
