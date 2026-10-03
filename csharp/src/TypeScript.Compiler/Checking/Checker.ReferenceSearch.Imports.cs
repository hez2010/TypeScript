using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;
using R = TypeScript.Compiler.LanguageServices.ReferenceNavigation;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    private sealed partial class ReferenceSearch
    {
        private enum ExportKind { Named, Default, ExportEquals, Umd, Module }
        private sealed record ExportInfo(Symbol Module, ExportKind Kind);
        private sealed record ImportSite(SourceFileNode File, SyntaxNode Declaration, SyntaxNode Literal, Symbol? Module, bool Implicit);
        private sealed record ImportSearch(SyntaxNode Location, Symbol Symbol);
        private sealed record ImportResults(IReadOnlyList<ImportSearch> Searches, IReadOnlyList<SyntaxNode> Singles, IReadOnlyList<SourceFileNode> Indirect);
        private IReadOnlyList<ImportSite>? importSites;
        private Dictionary<Symbol, List<SyntaxNode>>? directImports;

        private async ValueTask<IReadOnlyList<ImportSite>> ImportsAsync()
        {
            if (importSites is not null) return importSites;
            List<ImportSite> sites = [];
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var implicitImports = Program.GetFile(file.FileName)?.Resolutions.Where(r => r.Node is null && !r.TypeReference)
                    .OrderBy(r => r.Specifier == "tslib"u8).ToArray() ?? [];
                if (file.ExternalModuleIndicator is not null || file.Imports.Count + implicitImports.Length != 0)
                {
                    foreach (var literal in file.Imports)
                        if (ImportDeclaration(literal) is { } declaration) sites.Add(new(file, declaration, literal, await SymbolAsync(literal), false));
                    foreach (var import in implicitImports)
                    {
                        var literal = new StringLiteralNode { Text = import.Specifier, Parent = file, Pos = -1, End = -1 };
                        var module = await checker.ResolveImportModuleAsync(file, literal, null, cancellation, implicitImport: true);
                        sites.Add(new(file, literal, literal, module, true));
                    }
                }
                else
                    foreach (var declaration in PossibleImports(file))
                        if (R.ModuleSpecifier(declaration) is StringLiteralNode literal) sites.Add(new(file, declaration, literal, await SymbolAsync(literal), false));
            }
            importSites = sites;
            return sites;
        }
        private static SyntaxNode? ImportDeclaration(SyntaxNode literal) => literal.Parent switch
        {
            ImportDeclarationNode or ExportDeclarationNode or CallExpressionNode or JSDocImportTagNode => literal.Parent,
            ExternalModuleReferenceNode reference => reference.Parent,
            LiteralTypeNode { Parent: ImportTypeNode import } => import, _ => null,
        };
        private IEnumerable<SyntaxNode> PossibleImports(SyntaxNode source)
        {
            Stack<IEnumerator<SyntaxNode>> pending = new();
            pending.Push(Statements(source).GetEnumerator());
            try
            {
                while (pending.TryPeek(out var cursor))
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!cursor.MoveNext()) { pending.Pop().Dispose(); continue; }
                    var node = cursor.Current;
                    yield return node;
                    if (node is ModuleDeclarationNode { Name: StringLiteralNode }) pending.Push(Statements(node).GetEnumerator());
                }
            }
            finally { foreach (var cursor in pending) cursor.Dispose(); }
        }
        private static IEnumerable<SyntaxNode> Statements(SyntaxNode node) => node switch
        {
            SourceFileNode file => file.Statements ?? new([]), ModuleDeclarationNode { Body: ModuleBlockNode block } => block.Statements ?? new([]), _ => [],
        };
        private static SyntaxNode SourceLike(SyntaxNode node) => node is CallExpressionNode or JSDocImportTagNode
            ? SemanticSyntax.Source(node)! : node.Parent is SourceFileNode source ? source : node.Parent?.Parent ?? SemanticSyntax.Source(node)!;
        private async ValueTask<Dictionary<Symbol, List<SyntaxNode>>> DirectImportsAsync()
        {
            if (directImports is not null) return directImports;
            Dictionary<Symbol, List<SyntaxNode>> map = [];
            foreach (var site in await ImportsAsync())
                if (site.Module is { } module)
                {
                    if (!map.TryGetValue(module, out var list)) map.Add(module, list = []);
                    list.Add(site.Declaration);
                }
            return directImports = map;
        }
        private async ValueTask<(IReadOnlyList<SyntaxNode> Direct, IReadOnlyList<SourceFileNode> Indirect)> ImportersAsync(ExportInfo info)
        {
            var map = await DirectImportsAsync();
            List<SyntaxNode> direct = [], indirect = [];
            HashSet<SyntaxNode> seenDirect = [], seenIndirect = [];
            bool global = GlobalExports(info.Module.ValueDeclaration);
            async ValueTask AddIndirectAsync(SyntaxNode source, bool transitive)
            {
                await StackAsync();
                if (global || !seenIndirect.Add(source)) return;
                indirect.Add(source);
                if (!transitive || Merged(source.BindingSymbol) is not { } module) return;
                foreach (var imported in map.GetValueOrDefault(module) ?? [])
                    if (imported is not ImportTypeNode) await AddIndirectAsync(SourceLike(imported), true);
            }
            async ValueTask NamespaceImportAsync(SyntaxNode declaration, SyntaxNode? name, bool reexport, bool added)
            {
                if (info.Kind == ExportKind.ExportEquals) { if (!added) direct.Add(declaration); }
                else if (!global)
                {
                    var source = SourceLike(declaration);
                    await AddIndirectAsync(source, reexport || name is not null && await NamespaceReexportsAsync(source, name));
                }
            }
            async ValueTask VisitAsync(Symbol module)
            {
                await StackAsync();
                foreach (var declaration in map.GetValueOrDefault(module) ?? [])
                {
                    if (!seenDirect.Add(declaration)) continue;
                    switch (declaration)
                    {
                        case CallExpressionNode call:
                            if (call.Expression?.Kind == K.ImportKeyword)
                            {
                                var top = DeclarationOrder.Ancestor(call, n => n is ModuleDeclarationNode { Name: StringLiteralNode }) ?? SemanticSyntax.Source(call)!;
                                await AddIndirectAsync(top, Exported(call, true));
                            }
                            else if (!global && info.Kind == ExportKind.ExportEquals && call.Parent is VariableDeclarationNode { Name: IdentifierNode name }) direct.Add(name);
                            break;
                        case ImportEqualsDeclarationNode import:
                            await NamespaceImportAsync(import, import.Name, SemanticSyntax.HasModifier(import, K.ExportKeyword), false); break;
                        case ImportDeclarationNode or JSDocImportTagNode:
                            direct.Add(declaration);
                            var clause = Clause(declaration);
                            if (clause?.NamedBindings is NamespaceImportNode ns)
                                await NamespaceImportAsync(declaration, ns.Name, false, true);
                            else if (!global && clause?.Name is not null) await AddIndirectAsync(SourceLike(declaration), false);
                            break;
                        case ExportDeclarationNode export:
                            if (export.ExportClause is null)
                            {
                                if (Merged(SourceLike(export).BindingSymbol) is { } reexporting) await VisitAsync(reexporting);
                            }
                            else if (export.ExportClause is NamespaceExportNode) await AddIndirectAsync(SourceLike(export), true);
                            else direct.Add(export);
                            break;
                        case ImportTypeNode import:
                            if (!global && import.IsTypeOf && import.Qualifier is null && Exported(import, false))
                                await AddIndirectAsync(SemanticSyntax.Source(import)!, true);
                            direct.Add(import); break;
                    }
                }
            }
            await VisitAsync(info.Module);
            if (global) return (direct, files);
            foreach (var declaration in info.Module.Declarations)
                if (checker.ModuleAugmentation(declaration) && SemanticSyntax.Source(declaration) is { } file && allowedFiles.Contains(file)) await AddIndirectAsync(declaration, false);
            return (direct, indirect.Select(n => SemanticSyntax.Source(n)!).ToArray());
        }
        private static bool Exported(SyntaxNode? node, bool stopAtAmbient)
        {
            for (; node is not null && !(stopAtAmbient && node is ModuleDeclarationNode { Name: StringLiteralNode }); node = node.Parent)
                if (SemanticSyntax.HasModifier(node, K.ExportKeyword)) return true;
            return false;
        }
        private static ImportClauseNode? Clause(SyntaxNode node) => node switch
        { ImportDeclarationNode import => import.ImportClause as ImportClauseNode, JSDocImportTagNode import => import.ImportClause, _ => null };
        private async ValueTask<bool> NamespaceReexportsAsync(SyntaxNode source, SyntaxNode name)
        {
            var symbol = await SymbolAsync(name);
            foreach (var statement in PossibleImports(source))
                if (statement is ExportDeclarationNode { ModuleSpecifier: null, ExportClause: NamedExportsNode named })
                    foreach (var element in named.Elements ?? new([]))
                        if (await checker.ExportSpecifierLocalTargetSymbolAsync(element, cancellation) == symbol) return true;
            return false;
        }
        private async ValueTask<ImportResults> ImportSearchesAsync(Symbol symbol, ExportInfo info)
        {
            var (direct, indirect) = await ImportersAsync(info);
            List<ImportSearch> searches = [];
            List<SyntaxNode> singles = [];
            bool rename = options.Use == ReferenceUse.Rename;
            bool Matches(Utf8String name) => name == symbol.Name || info.Kind != ExportKind.Named && name == "default"u8;
            async ValueTask AddAsync(SyntaxNode node)
            { if (await SymbolAsync(node) is { } imported) searches.Add(new(node, imported)); }
            async ValueTask NamespaceAsync(SyntaxNode? name)
            { if (name is not null && info.Kind == ExportKind.ExportEquals && (!rename || Matches(R.Text(name)))) await AddAsync(name); }
            async ValueTask NamedAsync(NodeList? elements)
            {
                foreach (var element in elements ?? new([]))
                {
                    var name = element.DeclarationName!;
                    var property = element switch { ImportSpecifierNode n => n.PropertyName, ExportSpecifierNode n => n.PropertyName, _ => null };
                    if (!Matches(R.Text(property ?? name))) continue;
                    if (property is not null)
                    {
                        singles.Add(property);
                        if (!rename || R.Text(name) == symbol.Name) await AddAsync(name);
                    }
                    else await AddAsync(name);
                }
            }
            foreach (var declaration in direct)
            {
                if (declaration is ImportEqualsDeclarationNode import)
                { if (import.ModuleReference is ExternalModuleReferenceNode { Expression: StringLiteralNode }) await NamespaceAsync(import.Name); continue; }
                if (declaration is IdentifierNode) { await NamespaceAsync(declaration); continue; }
                if (declaration is ImportTypeNode type)
                {
                    if (type.Qualifier is { } qualifier)
                    {
                        while (qualifier is QualifiedNameNode qualified) qualifier = qualified.Left!;
                        if (R.Text(qualifier) == SymbolText(symbol)) singles.Add(qualifier);
                    }
                    else if (info.Kind == ExportKind.ExportEquals && type.Argument is LiteralTypeNode { Literal: { } literal }) singles.Add(literal);
                    continue;
                }
                var specifier = declaration is JSDocImportTagNode doc ? doc.ModuleSpecifier : R.ModuleSpecifier(declaration);
                if (specifier is not StringLiteralNode) continue;
                if (declaration is ExportDeclarationNode export)
                { if (export.ExportClause is NamedExportsNode named) await NamedAsync(named.Elements); continue; }
                if (Clause(declaration) is not { } clause) continue;
                if (clause.NamedBindings is NamespaceImportNode ns) await NamespaceAsync(ns.Name);
                else if (clause.NamedBindings is NamedImportsNode names && info.Kind is ExportKind.Named or ExportKind.Default) await NamedAsync(names.Elements);
                if (clause.Name is { } defaultName && info.Kind is ExportKind.Default or ExportKind.ExportEquals && (!rename || R.Text(defaultName) == NameNoDefault(symbol))) await AddAsync(defaultName);
            }
            return new(searches, singles, indirect);
        }
        private async ValueTask ImportsOfExportAsync(SyntaxNode location, Symbol symbol, ExportInfo info)
        {
            var imports = await ImportSearchesAsync(symbol, info);
            foreach (var reference in imports.Singles)
            {
                if ((await R.MeaningAsync(reference, cancellation) & meaning) == 0) continue;
                if (options.Use == ReferenceUse.Rename && (reference is not IdentifierNode && reference.Parent is not (ImportSpecifierNode or ExportSpecifierNode)
                    || reference.Parent is ImportSpecifierNode or ExportSpecifierNode && R.Text(reference) == "default"u8)) continue;
                Add(symbol, reference);
            }
            foreach (var import in imports.Searches)
            {
                var file = SemanticSyntax.Source(import.Location)!;
                await InContainerAsync(file, file, await CreateAsync(import.Location, import.Symbol, ImportDirection.Export), true);
            }
            if (imports.Indirect.Count == 0) return;
            var search = info.Kind == ExportKind.Named ? await CreateAsync(location, symbol, ImportDirection.Export)
                : info.Kind == ExportKind.Default && options.Use != ReferenceUse.Rename ? await CreateAsync(location, symbol, ImportDirection.Export, "default"u8) : null;
            if (search is not null) foreach (var file in imports.Indirect) await ForNameAsync(file, search);
        }
        private ExportInfo? Export(Symbol symbol, ExportKind kind) => Merged(symbol.Parent) is { } module && ExternalModule(module) ? new(module, kind) : null;
        private async ValueTask ImportOrExportAsync(SyntaxNode node, Symbol symbol, Search search)
        {
            var export = await ExportAtAsync(node, symbol, search.Direction == ImportDirection.Export);
            if (export is { Info: { } info }) { await ImportsOfExportAsync(node, export.Value.Symbol, info); return; }
            if (export is { Info: null })
            { if (!options.RenameWithAliases) await ImportedSymbolAsync(export.Value.Symbol); return; }
            if (search.Direction == ImportDirection.Export || options.RenameWithAliases || !NodeImport(node)) return;
            Symbol? imported = (symbol.Flags & SymbolFlags.Alias) != 0 ? await checker.program.Aliases.ImmediateAsync(symbol, cancellation)
                : symbol.Declarations.FirstOrDefault(R.ObjectBinding) is { } binding ? await BindingPropertyAsync(binding) : null;
            if (imported is null) return;
            imported = await SkipExportAsync(imported);
            if (imported?.Name == "export="u8)
                imported = (imported.Flags & SymbolFlags.Alias) != 0 ? await checker.program.Aliases.ImmediateAsync(imported, cancellation)
                    : imported.ValueDeclaration switch { ExportAssignmentNode ex => ex.Expression?.BindingSymbol,
                        BinaryExpressionNode binary => binary.Right?.BindingSymbol, SourceFileNode source => source.BindingSymbol, _ => null };
            if (imported is null) return;
            var name = NameNoDefault(imported);
            if (name.IsEmpty || name == "default"u8 || name == symbol.Name) await ImportedSymbolAsync(imported);
        }
        private async ValueTask<(Symbol Symbol, ExportInfo? Info)?> ExportAtAsync(SyntaxNode node, Symbol symbol, bool fromExport)
        {
            await StackAsync();
            var parent = node.Parent;
            var grandparent = parent?.Parent;
            (Symbol, ExportInfo?)? WithInfo(Symbol current, ExportKind kind) => Export(current, kind) is { } info ? (current, info) : null;
            (Symbol, ExportInfo?)? Assignment(ExportAssignmentNode assignment) => assignment.BindingSymbol?.Parent is { } module
                ? (symbol, new(module, assignment.IsExportEquals ? ExportKind.ExportEquals : ExportKind.Default)) : null;
            (Symbol, ExportInfo?)? Property(SyntaxNode assignment, bool lhs)
            {
                var kind = SyntaxLanguageService.AssignmentKind(assignment);
                if (kind is not (SyntaxLanguageService.AssignmentDeclaration.ExportsProperty or SyntaxLanguageService.AssignmentDeclaration.ModuleExports)) return null;
                var current = lhs ? assignment.BindingSymbol : symbol;
                return current is null ? null : WithInfo(current, kind == SyntaxLanguageService.AssignmentDeclaration.ModuleExports ? ExportKind.ExportEquals : ExportKind.Named);
            }
            static ExportKind Kind(SyntaxNode declaration) => SemanticSyntax.HasModifier(declaration, K.DefaultKeyword) ? ExportKind.Default : ExportKind.Named;
            if (symbol.ExportSymbol is { } exported)
            {
                if (parent is PropertyAccessExpressionNode) return grandparent is BinaryExpressionNode && symbol.Declarations.Contains(parent) ? Property(grandparent, false) : null;
                return WithInfo(exported, Kind(parent!));
            }
            var exportNode = ExportNode(parent, node);
            bool implicitExport = exportNode?.Parent is SourceFileNode && checker.program.Symbols.Binding(exportNode.Parent)?.IsModule == true
                && (exportNode.Kind == K.JSTypeAliasDeclaration || exportNode is ModuleDeclarationNode && (exportNode.Flags & NodeFlags.Reparsed) != 0);
            if (exportNode is not null && (SemanticSyntax.HasModifier(exportNode, K.ExportKeyword) || implicitExport))
            {
                if (exportNode is ImportEqualsDeclarationNode import && import.ModuleReference == node)
                    return !fromExport && import.Name is { } name && await SymbolAsync(name) is { } imported ? (imported, null) : null;
                return WithInfo(symbol, Kind(exportNode));
            }
            if (parent is NamespaceExportNode) return WithInfo(symbol, ExportKind.Named);
            if (parent is ExportAssignmentNode direct) return Assignment(direct);
            if (grandparent is ExportAssignmentNode outer) return Assignment(outer);
            if (parent is BinaryExpressionNode) return Property(parent, true);
            if (grandparent is BinaryExpressionNode) return Property(grandparent, true);
            return parent is JSDocTypedefTagNode or JSDocCallbackTagNode ? WithInfo(symbol, ExportKind.Named) : null;
        }
        private static SyntaxNode? ExportNode(SyntaxNode? parent, SyntaxNode node)
        {
            var declaration = parent is VariableDeclarationNode ? parent : parent is BindingElementNode ? SemanticSyntax.RootDeclaration(parent) : null;
            return declaration is null ? parent : parent?.DeclarationName == node && declaration.Parent is not CatchClauseNode
                && declaration.Parent?.Parent is VariableStatementNode statement ? statement : null;
        }
        private static bool NodeImport(SyntaxNode node) => node.Parent switch
        {
            ImportEqualsDeclarationNode import => import.Name == node && import.ModuleReference is ExternalModuleReferenceNode { Expression: StringLiteralNode },
            ImportSpecifierNode import => import.PropertyName is null,
            ImportClauseNode or NamespaceImportNode => true,
            BindingElementNode binding => (node.Flags & NodeFlags.JavaScriptFile) != 0 && RequireVariable(binding.Parent?.Parent), _ => false,
        };
        private async ValueTask<Symbol?> SkipExportAsync(Symbol symbol)
        {
            foreach (var declaration in symbol.Declarations)
            {
                if (declaration is ExportSpecifierNode { PropertyName: null, Parent.Parent: ExportDeclarationNode { ModuleSpecifier: null } } export)
                    return await checker.ExportSpecifierLocalTargetSymbolAsync(export, cancellation) ?? symbol;
                if (declaration is PropertyAccessExpressionNode { Expression: PropertyAccessExpressionNode { Expression: IdentifierNode module, Name: IdentifierNode exports }, Name: not PrivateIdentifierNode }
                    && module.Text == "module"u8 && exports.Text == "exports"u8) return await SymbolAsync(declaration);
                if (declaration is ShorthandPropertyAssignmentNode shorthand && declaration.Parent?.Parent is BinaryExpressionNode binary
                    && SyntaxLanguageService.AssignmentKind(binary) == SyntaxLanguageService.AssignmentDeclaration.ModuleExports)
                    return await checker.ExportSpecifierLocalTargetSymbolAsync(shorthand.Name!, cancellation);
            }
            return symbol;
        }
        private static Utf8String NameNoDefault(Symbol symbol) => symbol.Name != "default"u8 ? symbol.Name
            : symbol.Declarations.Select(d => d.DeclarationName).OfType<IdentifierNode>().FirstOrDefault()?.Text ?? default;
        private async ValueTask<IReadOnlyList<ReferenceGroup>?> SourceModuleAsync(Symbol symbol)
        {
            if ((symbol.Flags & SymbolFlags.Module) == 0 || symbol.Declarations.OfType<SourceFileNode>().FirstOrDefault() is not { } source) return null;
            var exported = symbol.Exports.GetValueOrDefault("export="u8);
            var references = await ModuleAsync(symbol, exported is not null);
            if (exported is null || (exported.Flags & SymbolFlags.Alias) == 0 || !allowedFiles.Contains(source)) return references;
            var target = await checker.program.Aliases.ResolveAsync(exported, cancellation);
            var other = new ReferenceSearch(checker, files, options, cancellation);
            await other.FindSymbolAsync(target, null);
            return Merge(references, other.result);
        }
        private async ValueTask<IReadOnlyList<ReferenceGroup>> ModuleAsync(Symbol symbol, bool excludeExportEqualsType)
        {
            List<ReferenceEntry> entries = [];
            var imports = await ImportsAsync();
            foreach (var file in files)
            {
                if (symbol.ValueDeclaration is SourceFileNode target)
                    foreach (var (references, kind) in new[] { (file.ReferencedFiles, FileIncludeKind.PathReference), (file.TypeReferenceDirectives, FileIncludeKind.TypeReference) })
                        foreach (var reference in references)
                            if (Program.IncludeReasons.TryGetValue(target.FileName, out var reasons)
                                && reasons.Any(reason => reason.ContainingFile == file.FileName && reason.Kind == kind && reason.Position == reference.Pos))
                                entries.Add(new(ReferenceEntryKind.Range, null, null, file, reference.Pos, reference.End));
                foreach (var import in imports)
                {
                    if (import.File != file || import.Module != symbol) continue;
                    if (import.Implicit)
                    {
                        var node = R.Text(import.Literal) != "tslib"u8 ? Descendants(file).FirstOrDefault(n => n is JsxElementNode or JsxSelfClosingElementNode or JsxFragmentNode) : null;
                        entries.Add(ReferenceEntry.FromNode(node ?? file.Statements?.FirstOrDefault() ?? file));
                    }
                    else if (!(excludeExportEqualsType && import.Literal.Parent is LiteralTypeNode { Parent: ImportTypeNode { Qualifier: null } }))
                        entries.Add(ReferenceEntry.FromNode(import.Literal));
                }
            }
            foreach (var declaration in symbol.Declarations)
                if (declaration is ModuleDeclarationNode module && SemanticSyntax.Source(module) is { } file && allowedFiles.Contains(file)) entries.Add(ReferenceEntry.FromNode(module.Name!));
            foreach (var declaration in symbol.Exports.GetValueOrDefault("export="u8)?.Declarations ?? [])
            {
                if (SemanticSyntax.Source(declaration) is not { } file || !allowedFiles.Contains(file)) continue;
                var node = declaration is BinaryExpressionNode { Left: PropertyAccessExpressionNode access } ? access.Expression
                    : declaration is ExportAssignmentNode ? await SyntaxNavigation.FindChildOfKindAsync(declaration, K.ExportKeyword, file, cancellation)
                    : declaration.DeclarationName ?? declaration;
                if (node is not null) entries.Add(ReferenceEntry.FromNode(node));
            }
            return entries.Count == 0 ? [] : One(ReferenceDefinitionKind.Symbol, null, symbol, entries);
        }
    }
}
