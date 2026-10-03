using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal enum ExportSyntax { None, Modifier, Named, DefaultModifier, DefaultDeclaration, Equals, Umd, Star, CommonJSModuleExports, CommonJSExportsProperty }
internal readonly record struct ExportId(Utf8String Module, Utf8String Name);
internal sealed record AutoImportExport(ExportId Id, Utf8String FileName, ExportSyntax Syntax, SymbolFlags Flags, Utf8String Name,
    ExportId Target, bool TypeOnly, Symbol? Symbol, Utf8String Path, Utf8String PackageName = default,
    ScriptElementKind Kind = ScriptElementKind.Unknown, bool Deprecated = false)
{
    internal bool Renameable => Id.Name == "default"u8 || Id.Name == "export="u8;
    internal bool UnresolvedAlias => Flags == SymbolFlags.Alias;
}

internal sealed partial class AutoImportView(CompilerProgram program, Checker checker, DocumentProjection projection,
    UserPreferences preferences, CancellationToken cancellation, Utf8String extractionPackage = default, AutoImportCache? cache = null)
{
    private SourceFileNode File => projection.File;
    private CompilerOptions Options => program.Configuration.Options;
    private IFileSystem FileSystem => cache?.FileSystem ?? program.FileSystem;
    private Dictionary<Utf8String, List<ExistingImport>>? existingImports;
    private bool? useRequire;
    private sealed record ExistingImport(SyntaxNode Node, Utf8String Specifier, int Index);

    internal ImportAdder CreateAdder() => new(projection, Options, preferences, ResolveSymbolImportAsync, cancellation);

    private async ValueTask<ImportFix?> ResolveSymbolImportAsync(Symbol symbol, bool typeOnlyUse, CancellationToken requestCancellation)
    {
        requestCancellation.ThrowIfCancellationRequested();
        var resolved = (await checker.GetAutoImportTargetAsync(symbol, cancellation)).Symbol;
        var exported = await ExportForSymbolAsync(resolved);
        if (exported is null) return null;
        List<ImportFix> fixes = [];
        foreach (var candidate in (await SearchAsync(default)).Where(candidate => candidate.Id == exported.Id))
            fixes.AddRange(await FixesAsync(candidate, false, typeOnlyUse, null));
        return fixes.OrderBy(fix => fix, Comparer<ImportFix>.Create(CompareRanking)).FirstOrDefault();
    }

    internal async ValueTask<AutoImportExport?> ExportForSymbolAsync(Symbol symbol)
    {
        if (symbol.Parent is { } parent && ImportTypeRewriter.ExternalModule(parent))
            return Module(parent, merge: false) is { } module
                ? await CreateExportAsync(symbol, module.Id, module.File, SemanticSyntax.Source(symbol.Declarations.FirstOrDefault())?.FileName ?? module.File) : null;
        var file = SemanticSyntax.Source(symbol.Declarations.FirstOrDefault());
        if (file is null || checker.Symbols.Declaration(file) is not { } owner) return null;
        foreach (var name in new Utf8String[] { "default"u8, "export="u8, symbol.Name })
            if (await checker.GetMemberInModuleExportsAsync(owner, name, cancellation) is { } candidate
                && (await checker.GetAutoImportTargetAsync(candidate, cancellation)).Symbol == symbol)
                return await CreateExportAsync(candidate, Canonical(file.FileName), file.FileName, file.FileName);
        return null;
    }

    internal async ValueTask<AutoImportExport?> CreateExportAsync(Symbol symbol, Utf8String moduleId, Utf8String moduleFile,
        Utf8String sourcePath, ExportSyntax? exportSyntax = null)
    {
        if ((symbol.Flags & SymbolFlags.Prototype) != 0) return null;
        var syntax = exportSyntax ?? Syntax(symbol);
        Symbol? target = null;
        SymbolFlags flags = symbol.CombinedFlags;
        bool typeOnly = false;
        ExportId targetId = default;
        ScriptElementKind elementKind = ScriptElementKind.Unknown;
        bool deprecated = false;
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            var info = await checker.GetAutoImportTargetAsync(symbol, cancellation);
            if (!checker.IsUnknownSymbol(info.Symbol))
            {
                target = info.Symbol; flags = info.Flags; typeOnly = info.TypeOnly is not null;
                var targetModule = Module(target.Parent);
                var declaration = target.Declarations.FirstOrDefault() ?? symbol.Declarations.FirstOrDefault();
                targetId = new(targetModule?.Id ?? Canonical(SemanticSyntax.Source(declaration)?.FileName ?? sourcePath), target.Name);
                elementKind = await SymbolClassification.KindAsync(checker, target, declaration, cancellation);
                deprecated = await checker.IsDeprecatedCompletionAsync(target, cancellation);
            }
        }
        else
        {
            elementKind = await SymbolClassification.KindAsync(null, symbol, symbol.Declarations.FirstOrDefault(), cancellation);
            deprecated = await checker.IsDeprecatedCompletionAsync(symbol, cancellation);
        }
        Utf8String exportName = syntax == ExportSyntax.Umd ? "export="u8 : symbol.Name;
        Utf8String name = syntax == ExportSyntax.Umd ? symbol.Name : exportName;
        if (symbol.Name == "default"u8 || symbol.Name == "export="u8)
        {
            name = ImportTypeRewriter.DefaultExportName(LocalDefault(symbol));
            if (Unusable(name)) name = targetId.Name;
            if (Unusable(name) && target is not null) name = ImportTypeRewriter.DefaultExportName(LocalDefault(target));
            if (Unusable(name)) name = ImportTypeRewriter.ModuleIdentifier(SemanticSyntax.Source(target?.Declarations.FirstOrDefault())?.FileName
                ?? (!moduleFile.IsEmpty ? moduleFile : moduleId));
        }
        if (Unusable(name)) return null;
        if (!extractionPackage.IsEmpty && !moduleFile.IsEmpty) moduleFile = PackageRealPath(moduleFile);
        return new(new(moduleId, exportName), moduleFile, syntax, flags, name, targetId, typeOnly, target ?? symbol, Canonical(sourcePath), extractionPackage, elementKind, deprecated);

        Symbol LocalDefault(Symbol candidate) => candidate.Name == "default"u8
            ? candidate.Declarations.Select(node => checker.Symbols.Binding(node)?.Get(node)?.LocalSymbol).FirstOrDefault(local => local is not null) ?? candidate : candidate;
    }

    private (Utf8String Id, Utf8String File)? Module(Symbol? symbol, bool merge = true)
    {
        if (symbol is null) return null;
        if (merge) symbol = checker.Symbols.Merger.GetMergedSymbol(symbol)!;
        if (!ImportTypeRewriter.ExternalModule(symbol)) return null;
        foreach (var declaration in symbol.Declarations)
        {
            if (declaration is SourceFileNode file) return (Canonical(file.FileName), file.FileName);
            if (declaration is ModuleDeclarationNode { Name: StringLiteralNode name }
                && !SemanticSyntax.Source(declaration)!.ModuleAugmentations.Contains(name)) return (name.Text, default);
        }
        return null;
    }

    private static bool Unusable(Utf8String name) => name.IsEmpty || name == "_default"u8 || name == "default"u8 || name == "export="u8 || name == Symbol.InternalExport;
    private StringComparison PathComparison => program.UseCaseSensitiveFileNames ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    private Utf8String Canonical(Utf8String path)
    {
        if (!extractionPackage.IsEmpty && CompilerPath.IsAbsolute(path)) path = PackageRealPath(path);
        return program.UseCaseSensitiveFileNames ? path : path.ToLowerInvariant();
    }

    private static ExportSyntax Syntax(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations)
        {
            if (declaration is ExportSpecifierNode) return ExportSyntax.Named;
            if (declaration is ExportAssignmentNode assignment) return assignment.IsExportEquals ? ExportSyntax.Equals : ExportSyntax.DefaultDeclaration;
            if (declaration is NamespaceExportDeclarationNode) return ExportSyntax.Umd;
            if (declaration is BinaryExpressionNode { Left: PropertyAccessExpressionNode access })
            {
                if (access.Expression is IdentifierNode { Text: var receiver } && receiver == "module"u8 && access.Name is IdentifierNode { Text: var name } && name == "exports"u8)
                    return ExportSyntax.CommonJSModuleExports;
                return ExportSyntax.CommonJSExportsProperty;
            }
            return (MemberModifiers.Flags(SemanticSyntax.RootDeclaration(declaration)) & ModifierFlags.Default) != 0 ? ExportSyntax.DefaultModifier : ExportSyntax.Modifier;
        }
        return ExportSyntax.None;
    }

    internal async ValueTask<IReadOnlyList<ImportFix>> FixesAsync(AutoImportExport export, bool jsx, bool typeOnlyUse, DocumentPosition? position)
    {
        var existing = await ExistingAsync();
        var imports = existing.GetValueOrDefault(export.Id.Module) ?? [];
        var kind = Kind(export);
        var typeOnly = !typeOnlyUse ? AddAsTypeOnly.NotAllowed
            : Options.VerbatimModuleSyntax == true && (export.TypeOnly || (export.Flags & SymbolFlags.Value) == 0)
                || export.TypeOnly && (export.Flags & SymbolFlags.Value) != 0 ? AddAsTypeOnly.Required : AddAsTypeOnly.Allowed;
        List<ImportFix> fixes = [];
        if (position is not null && kind == ImportKind.Named)
            foreach (var import in imports)
                if (NamespaceName(import.Node) is { IsEmpty: false } prefix && !import.Specifier.IsEmpty)
                {
                    fixes.Add(new(new(AutoImportFixKind.UseNamespace, export.Name, ImportKind.Namespace, false, AddAsTypeOnly.Allowed,
                        import.Specifier, import.Index, position, prefix))); break;
                }
        if (!(File.ScriptKind is ScriptKind.JS or ScriptKind.JSX && (export.Flags & SymbolFlags.Value) == 0 && imports.Any(import => import.Node is not JSDocImportTagNode))
            && kind is ImportKind.Default or ImportKind.Named)
        {
            ImportFix? best = null;
            foreach (var import in imports)
            {
                bool isTypeOnly = false;
                if (import.Node is VariableDeclarationNode { Name: BindingPatternNode { Kind: K.ObjectBindingPattern } }) { }
                else
                {
                    var clause = import.Node switch { ImportDeclarationNode node => node.ImportClause, JSDocImportTagNode node => node.ImportClause, _ => null };
                    if (clause is null) continue;
                    isTypeOnly = clause.PhaseModifier == K.TypeKeyword;
                    if (isTypeOnly && !(kind == ImportKind.Named && clause.NamedBindings is not null)
                        || kind == ImportKind.Default && (clause.Name is not null || typeOnly == AddAsTypeOnly.Required && clause.NamedBindings is not null)
                        || kind == ImportKind.Named && clause.NamedBindings is NamespaceImportNode) continue;
                }
                var fix = new ImportFix(new(AutoImportFixKind.AddToExisting, export.Name, kind, false, typeOnly, import.Specifier, import.Index));
                if (typeOnly != AddAsTypeOnly.NotAllowed && isTypeOnly || typeOnly == AddAsTypeOnly.NotAllowed && !isTypeOnly)
                { fixes.Add(fix); return fixes; }
                best ??= fix;
            }
            if (best is not null) { fixes.Add(best); return fixes; }
        }
        var (specifier, specifierKind) = ModuleSpecifier(export);
        if (specifier.IsEmpty) return fixes;
        bool reexport = export.Target.Module != export.Id.Module;
        if ((export.Flags & SymbolFlags.Value) == 0 && !export.UnresolvedAlias && File.ScriptKind is ScriptKind.JS or ScriptKind.JSX && position is not null)
            return [new(new(AutoImportFixKind.JsdocTypeImport, export.Name, 0, false, 0, specifier, UsagePosition: position), specifierKind, reexport, export.FileName)];
        var name = export.Name;
        if (jsx && !GoUnicode.IsUpper(name[0]))
        {
            if (!export.Renameable) return [];
            name = new Text.Utf8StringBuilder().AppendCodePoint(GoUnicode.Upper(name[0])).Append(name[1..]).ToUtf8String();
        }
        fixes.Add(new(new(AutoImportFixKind.AddNew, name, kind, ShouldUseRequire(), typeOnly, specifier), specifierKind, reexport, export.FileName));
        return fixes;
    }

    internal ImportKind Kind(AutoImportExport export, bool forceImport = false)
    {
        if (Options.VerbatimModuleSyntax == true && program.EmitModuleFormat(File) == ModuleKind.CommonJS) return ImportKind.CommonJS;
        if (export.Syntax is ExportSyntax.DefaultModifier or ExportSyntax.DefaultDeclaration || export.Syntax == ExportSyntax.Named && export.Id.Name == "default"u8)
            return ImportKind.Default;
        if (export.Syntax is ExportSyntax.Equals or ExportSyntax.CommonJSModuleExports or ExportSyntax.Umd && export.Id.Name == "export="u8)
        {
            if ((File.Statements ?? new([])).OfType<ImportEqualsDeclarationNode>().Any(import => import.ModuleReference is { } reference && reference.Pos != reference.End)) return ImportKind.CommonJS;
            return File.ExternalModuleIndicator is not null || forceImport || File.ScriptKind is not (ScriptKind.JS or ScriptKind.JSX) ? ImportKind.Default : ImportKind.CommonJS;
        }
        return ImportKind.Named;
    }

    private async ValueTask<Dictionary<Utf8String, List<ExistingImport>>> ExistingAsync()
    {
        if (existingImports is not null) return existingImports;
        Dictionary<Utf8String, List<ExistingImport>> result = [];
        for (int i = 0; i < File.Imports.Count; i++)
        {
            var specifier = File.Imports[i];
            var import = ImportEditor.ImportFromSpecifier(specifier);
            if (import is CallExpressionNode { Parent: VariableDeclarationNode declaration }) import = declaration;
            if (import is not (ImportDeclarationNode or ImportEqualsDeclarationNode or JSDocImportTagNode or VariableDeclarationNode)) continue;
            if (Module(await checker.GetSymbolAtLocationAsync(specifier, cancellation)) is not { } module) continue;
            if (!result.TryGetValue(module.Id, out var list)) result[module.Id] = list = [];
            list.Add(new(import, ModuleSpecifierPaths.ImportText(specifier), i));
        }
        return existingImports = result;
    }

    private static Utf8String NamespaceName(SyntaxNode node) => node switch
    {
        VariableDeclarationNode { Name: IdentifierNode name } => name.Text,
        ImportEqualsDeclarationNode { Name: { } name } => name.Text,
        ImportDeclarationNode { ImportClause.NamedBindings: NamespaceImportNode { Name: { } name } } => name.Text,
        JSDocImportTagNode { ImportClause.NamedBindings: NamespaceImportNode { Name: { } name } } => name.Text,
        _ => default,
    };
}
