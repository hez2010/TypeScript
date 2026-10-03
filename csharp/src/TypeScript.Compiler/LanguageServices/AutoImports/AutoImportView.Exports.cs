using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Programs;
using TypeScript.Compiler.Semantics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class AutoImportView
{
    private AutoImportData? exportData;

    internal async ValueTask<IEnumerable<AutoImportExport>> SearchAsync(Utf8String query, bool? caseSensitive = null)
    {
        exportData ??= cache is null ? await BuildIndexAsync() : await cache.GetIndexAsync(Canonical(CompilerPath.DirectoryName(File.FileName)), preferences, BuildIndexAsync, cancellation);
        var found = caseSensitive is { } exact ? exportData.Index.Find(query, exact) : exportData.Index.Search(query);
        return found.Where(export => !export.PackageName.IsEmpty || export.Id.Module != Canonical(File.FileName));
    }

    internal async ValueTask<IReadOnlyList<(ImportFix Fix, AutoImportExport Export)>> CompletionsAsync(Utf8String prefix,
        DocumentPosition position, bool jsx, bool typeOnly)
    {
        Dictionary<(ExportId Target, Utf8String Name, Utf8String Package), List<AutoImportExport>> groups = [];
        foreach (var export in await SearchAsync(prefix))
        {
            cancellation.ThrowIfCancellationRequested();
            var name = export.Name;
            if (ExcludedFile(export.Path) || !Identifier(name)
                || jsx && !GoUnicode.IsUpper(name[0]) && !export.Renameable) continue;
            Utf8String package = CompilerPath.IsAbsolute(export.Id.Module) ? export.PackageName : export.Id.Module;
            if ((export.PackageName == "@types/node"u8 || export.Path.Contains("/node_modules/@types/node/"u8))
                && NodeCoreModules.Contains(package) && !package.StartsWith("node:"u8)) package = "node:"u8 + package;
            var key = (export.Target == default ? export.Id : export.Target, name, package);
            if (!groups.TryGetValue(key, out var entries)) groups[key] = entries = [];
            int existing = entries.FindIndex(entry => entry.Id == export.Id);
            if (existing < 0) entries.Add(export);
            else entries[existing] = export with { Flags = export.Flags | entries[existing].Flags,
                TypeOnly = export.TypeOnly || entries[existing].TypeOnly, Syntax = (ExportSyntax)Math.Min((int)export.Syntax, (int)entries[existing].Syntax),
                Kind = (ScriptElementKind)Math.Min((int)export.Kind, (int)entries[existing].Kind), Deprecated = export.Deprecated || entries[existing].Deprecated };
        }
        List<(ImportFix Fix, AutoImportExport Export)> fixes = [];
        foreach (var group in groups.Values)
        {
            List<(ImportFix Fix, AutoImportExport Export)> candidates = [];
            foreach (var export in group)
                foreach (var fix in await FixesAsync(export, jsx, typeOnly, position))
                {
                    int compare = candidates.Count == 0 ? -1 : CompareRanking(fix, candidates[0].Fix);
                    if (compare > 0) continue;
                    if (compare < 0) candidates.Clear();
                    candidates.Add((fix, export));
                }
            fixes.AddRange(candidates);
        }
        return fixes.OrderBy(entry => entry.Fix, Comparer<ImportFix>.Create(CompareSorting)).ToArray();
    }

    private async ValueTask<AutoImportData> BuildIndexAsync()
    {
        var index = new AutoImportIndex();
        var packageRoots = await AddPackagesAsync(index);
        var supplemental = program.SourceFiles.SelectMany(file => file.SupplementalSourceFiles).ToHashSet();
        foreach (var entry in program.SourceFiles)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = entry.Syntax;
            if (entry.Library || supplemental.Contains(file.FileName) || ExcludedFile(Canonical(file.FileName))) continue;
            if (!program.GlobalTypingsCache.IsEmpty && CompilerPath.Contains(program.GlobalTypingsCache, file.FileName, program.UseCaseSensitiveFileNames)) continue;
            if (entry.Mapping is null && (file.FileName.Contains("/node_modules/"u8)
                || !CompilerPath.Contains(program.CurrentDirectory, file.FileName, program.UseCaseSensitiveFileNames)
                    && packageRoots.Any(root => CompilerPath.Contains(root, file.FileName, program.UseCaseSensitiveFileNames)))) continue;
            await ExtractFileAsync(entry, index);
        }
        return new(index, packageEntrypoints.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
    }

    private async ValueTask ExtractFileAsync(ProgramFile entry, AutoImportIndex index)
    {
        var file = entry.Syntax;
        if (entry.Binding.Symbol is { } module)
        {
            await ExtractModuleAsync(module, Canonical(file.FileName), file.FileName, file, index);
            foreach (var name in file.ModuleAugmentations.OfType<StringLiteralNode>())
            {
                if (checker.Symbols.Declaration(name.Parent!) is not { } augmentation) continue;
                Utf8String moduleName = name.Text, moduleFile = default;
                if (ImportSorter.Relative(moduleName))
                {
                    var resolved = entry.Resolutions.FirstOrDefault(reference => reference.Specifier == moduleName)?.Resolution;
                    moduleFile = resolved?.IsResolved == true ? resolved.FileName : CompilerPath.Resolve(CompilerPath.DirectoryName(file.FileName), moduleName);
                    moduleName = Canonical(moduleFile);
                }
                await ExtractModuleAsync(augmentation, moduleName, moduleFile, file, index);
            }
        }
        else if (file.AmbientModuleNames.Count != 0)
            foreach (var declaration in (file.Statements ?? new([])).OfType<ModuleDeclarationNode>())
                if (declaration.Name is StringLiteralNode name && !name.Text.Contains("*"u8) && checker.Symbols.Declaration(declaration) is { } ambient)
                    await ExtractModuleAsync(ambient, name.Text, default, file, index);
    }

    private async ValueTask ExtractModuleAsync(Symbol module, Utf8String moduleId, Utf8String moduleFile, SourceFileNode file, AutoImportIndex index)
    {
        foreach (var (name, symbol) in module.Exports)
        {
            cancellation.ThrowIfCancellationRequested();
            if (name == Symbol.InternalExport)
            {
                foreach (var exported in await checker.GetExportsOfModuleAsync(module, cancellation))
                {
                    if (module.Exports.Any(pair => pair.Key != Symbol.InternalExport && pair.Value == exported)) continue;
                    if (await CreateExportAsync(exported, moduleId, moduleFile, file.FileName, ExportSyntax.Star) is not { } entry) continue;
                    if (Module(exported.Parent) is { } target) entry = entry with { Target = new(target.Id, exported.Name) };
                    index.Add(entry with { Symbol = null });
                }
                continue;
            }
            if (await CreateExportAsync(symbol, moduleId, moduleFile, file.FileName) is not { } export) continue;
            index.Add(export with { Symbol = null });
            if (export.Syntax == ExportSyntax.Equals && (symbol.Flags & SymbolFlags.Alias) != 0 && export.Symbol is not null && (export.Symbol.Flags & SymbolFlags.Namespace) != 0)
                foreach (var (inner, nested) in export.Symbol.Exports)
                    if (inner != Symbol.InternalExport && await CreateExportAsync(nested, moduleId, moduleFile, file.FileName, export.Syntax) is { } child) index.Add(child with { Symbol = null });
            if (export.Syntax == ExportSyntax.CommonJSModuleExports && (symbol.Flags & SymbolFlags.Alias) == 0
                && symbol.Declarations.FirstOrDefault() is BinaryExpressionNode { Right: ObjectLiteralExpressionNode literal }
                && checker.Symbols.Declaration(literal) is { } objectSymbol)
                foreach (var property in literal.Properties ?? new([]))
                    if (property is ShorthandPropertyAssignmentNode || property is PropertyAssignmentNode { Name: IdentifierNode })
                        if (property.DeclarationName is IdentifierNode propertyName && objectSymbol.Members.TryGetValue(propertyName.Text, out var member)
                            && await CreateExportAsync(member, moduleId, moduleFile, file.FileName, export.Syntax) is { } child) index.Add(child with { Symbol = null });
        }
    }

    private static bool Identifier(Utf8String name)
    {
        if (name.IsEmpty) return false;
        for (int i = 0; i < name.Length;)
        {
            int point = Wtf8.Decode(name.Span[i..], out int width);
            if (!(i == 0 ? TokenFacts.IsIdentifierStart(point) : TokenFacts.IsIdentifierPart(point))) return false;
            i += width;
        }
        return true;
    }
}
