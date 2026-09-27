using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Symbol?> ResolveImportModuleAsync(SyntaxNode location, SyntaxNode? specifier, Type? attributes,
        CancellationToken cancellation, bool implicitImport = false, int missingModuleCode = 2307, bool ignoreErrors = false,
        ReferenceResolutionMode? resolutionMode = null, bool reportUnresolved = true)
    {
        cancellation.ThrowIfCancellationRequested();
        string? name = specifier switch
        {
            StringLiteralNode text => text.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            _ => null
        };
        if (name is null)
            return null;
        if (!ignoreErrors && name.StartsWith("@types/", StringComparison.Ordinal))
            Error(specifier!, 6137, name[7..], name);
        var file = program.Symbols.Binding(location)!.SourceFile;
        var reference = program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(
            r => resolutionMode is { } mode ? r.Specifier == name && r.Mode == mode
                : implicitImport ? r.Node is null && r.Specifier == name : r.Node == specifier);
        var module = program.Symbols.Globals.GetValueOrDefault('"' + name + '"');
        if (module is null && reference?.Resolution.IsResolved == true
            && !(reference.Resolution.IsArbitraryExtension && !file.IsDeclarationFile
                && program.Symbols.Program.Configuration.Options.Boolean("allowArbitraryExtensions") != true))
        {
            if (!implicitImport && !ignoreErrors)
                CheckResolvedImport(location, specifier!, name, file, reference);
            module = program.Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol;
        }
        attributes ??= context.EmptyObjectType;
        if (module is null || !await Views.EmptyAnonymousAsync(attributes, cancellation))
        {
            var candidates = new List<(PatternModule Module, Type Type)>();
            foreach (var pattern in program.Symbols.PatternModules)
            {
                int star = pattern.Pattern.IndexOf('*');
                if (star < 0 || name.Length < pattern.Pattern.Length - 1
                    || !name.StartsWith(pattern.Pattern[..star], StringComparison.Ordinal)
                    || !name.EndsWith(pattern.Pattern[(star + 1)..], StringComparison.Ordinal))
                    continue;
                var required = await ModuleImportAttributesAsync(pattern.Symbol, cancellation);
                if (await AssignableAsync(attributes, required, cancellation))
                    candidates.Add((pattern, required));
            }
            var best = new List<PatternModule>();
            for (int i = 0; i < candidates.Count; i++)
            {
                bool wider = false;
                for (int j = 0; j < candidates.Count; j++)
                    if (i != j
                        && await Relations.RelatedAsync(candidates[j].Type, candidates[i].Type, RelationKind.StrictSubtype, cancellation)
                        && !await Relations.RelatedAsync(candidates[j].Type, candidates[i].Type, RelationKind.Identity, cancellation))
                    {
                        wider = true;
                        break;
                    }
                if (!wider)
                    best.Add(candidates[i].Module);
            }
            if (best.Count != 0)
            {
                var pattern = best.MaxBy(p => p.Pattern.IndexOf('*'))!;
                var target = program.Symbols.Merger.GetMergedSymbol(pattern.Symbol)!;
                module = program.Symbols.PatternTargets.GetValueOrDefault(name) == target
                    ? program.Symbols.PatternAugmentations.GetValueOrDefault(name) ?? target : target;
            }
        }
        if (module is null && !ignoreErrors && (reportUnresolved || missingModuleCode == 2664
            && reference?.Resolution is { IsResolved: true, Extension: ".js" or ".jsx" or ".mjs" or ".cjs" }))
            ReportUnresolvedImport(implicitImport ? location : specifier!, name, file, reference, missingModuleCode);
        return program.Symbols.Merger.GetMergedSymbol(module);
    }

    private void ReportUnresolvedImport(SyntaxNode node, string name, SourceFileNode file,
        Programs.ModuleReference? reference, int missingModuleCode)
    {
        bool sideEffect = node.Parent is ImportDeclarationNode { ImportClause: null };
        var compiler = program.Symbols.Program;
        if (reference?.Resolution.IsArbitraryExtension == true && !file.IsDeclarationFile
            && compiler.Configuration.Options.Boolean("allowArbitraryExtensions") != true)
        {
            program.Error(
                node,
                Messages.Module_0_was_resolved_to_1_but_allowArbitraryExtensions_is_not_set,
                name,
                reference.Resolution.FileName);
            return;
        }
        if (reference?.Resolution.IsResolved == true)
        {
            var resolved = reference.Resolution;
            if (compiler.GetFile(resolved.FileName) is not null)
            {
                if (!sideEffect)
                    program.Error(node, Messages.File_0_is_not_a_module, resolved.FileName);
                return;
            }
            if (JsxMode == 0 && resolved.Extension is ".jsx" or ".tsx")
                return;
            if (resolved.Extension is ".js" or ".jsx" or ".mjs" or ".cjs")
            {
                if (missingModuleCode == 2664)
                    program.Error(
                        node,
                        Messages.Invalid_module_name_in_augmentation_Module_0_resolves_to_an_untyped_module_at_1_which_cannot_be_augmented,
                        name,
                        resolved.FileName);
                else if (!sideEffect)
                {
                    if (NoImplicitAny)
                    {
                        var diagnostic = CheckerDiagnostic.Create(node,
                            Messages.Could_not_find_a_declaration_file_for_module_0_1_implicitly_has_an_any_type, name, resolved.FileName);
                        if (!ModuleResolver.Relative(name) && resolved.PackageId is { Name.Length: > 0 } package)
                            diagnostic = diagnostic with { MessageChain = [MissingPackageTypes(node, name, resolved, package.Name)] };
                        Error(node, diagnostic);
                    }
                    else
                        program.Suggestion(node, 7016, name);
                }
                return;
            }
        }
        bool resolveJson = compiler.Configuration.Options.Boolean("resolveJsonModule")
            ?? (compiler.ModuleResolutionKind == "bundler" || ModuleKind is 102 or 199);
        if (!resolveJson && name.EndsWith(".json", StringComparison.Ordinal))
        {
            program.Error(node, Messages.Cannot_find_module_0_Consider_using_resolveJsonModule_to_import_module_with_json_extension, name);
            return;
        }
        string normalized = CompilerPath.NormalizeSlashes(name);
        bool relative = normalized is "." or ".." || normalized.StartsWith("./", StringComparison.Ordinal)
            || normalized.StartsWith("../", StringComparison.Ordinal);
        if (reference?.Mode == ReferenceResolutionMode.Import && compiler.ModuleResolutionKind is "node16" or "nodenext"
            && relative && CompilerPath.Extension(normalized).Length == 0)
        {
            string path = CompilerPath.Resolve(CompilerPath.DirectoryName(file.FileName), name);
            if (SuggestedImportExtension(path) is { Length: > 0 } extension)
            {
                program.Error(
                    node,
                    Messages.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Did_you_mean_0,
                    name + extension);
                return;
            }
            program.Error(
                node,
                Messages.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Consider_adding_an_extension_to_the_import_path);
            return;
        }
        if (!sideEffect && missingModuleCode == 2307 && node is StringLiteralNode && NodeCoreModules.Contains(name))
            missingModuleCode = compiler.Configuration.Options.Strings("types")?.Contains("*", StringComparer.Ordinal) == true
                ? 2580
                : 2591;
        program.Error(node, DiagnosticLocalization.GetMessage(sideEffect && missingModuleCode == 2307 ? 2882 : missingModuleCode), name);
    }

    private Dictionary<string, bool>? resolvedPackages;

    private Diagnostic MissingPackageTypes(SyntaxNode node, string moduleName, ResolvedModule resolved, string packageName)
    {
        string mangled = ModuleResolver.Mangle(packageName);
        if (resolved.AlternateResult.Length != 0)
            return CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(6278), resolved.AlternateResult,
                resolved.AlternateResult.Contains("/node_modules/@types/", StringComparison.Ordinal) ? "@types/" + mangled : packageName);
        if (resolvedPackages is null)
        {
            resolvedPackages = new(StringComparer.Ordinal);
            foreach (var file in program.Symbols.Program.SourceFiles)
                foreach (var reference in file.Resolutions)
                    if (!reference.TypeReference && reference.Resolution.PackageId is { Name.Length: > 0 } package)
                        resolvedPackages[package.Name] = resolvedPackages.GetValueOrDefault(package.Name)
                            || reference.Resolution.Extension == ".d.ts";
        }
        if (resolvedPackages.ContainsKey("@types/" + mangled))
            return CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(7040), packageName, mangled);
        if (resolvedPackages.GetValueOrDefault(packageName))
            return CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(7058), packageName, moduleName);
        return CheckerDiagnostic.Create(node, DiagnosticLocalization.GetMessage(7035), moduleName, mangled);
    }

    internal string SuggestedImportExtension(string path)
    {
        foreach (var extension in new[] { ".mts", ".ts", ".cts", ".mjs", ".js", ".cjs", ".tsx", ".jsx", ".json" })
            if (program.Symbols.Program.FileExists(path + extension))
                return extension switch
                {
                    ".mts" => ".mjs",
                    ".cts" => ".cjs",
                    ".ts" => ".js",
                    ".tsx" => JsxMode == 1 ? ".jsx" : ".js",
                    _ => extension
                };
        return "";
    }

    private void CheckResolvedImport(SyntaxNode location, SyntaxNode specifier, string name, SourceFileNode source,
        Programs.ModuleReference reference)
    {
        var options = program.Symbols.Program.Configuration.Options;
        if (JsxMode == 0 && reference.Resolution.Extension is ".tsx" or ".jsx")
            Error(specifier, 6142, name, reference.Resolution.FileName);
        var import = DeclarationOrder.Ancestor(
            location,
            n => n is ImportDeclarationNode or ExportDeclarationNode or ImportEqualsDeclarationNode or ImportTypeNode
            || n is CallExpressionNode call && IsImportCall(call));
        bool emitted = import switch
        {
            ImportDeclarationNode declaration => declaration.ImportClause is { } clause && !SemanticSyntax.TypeOnly(clause),
            ExportDeclarationNode declaration => !declaration.IsTypeOnly,
            ImportEqualsDeclarationNode declaration => !declaration.IsTypeOnly,
            CallExpressionNode => true,
            _ => false
        };
        bool declarationExtension = name.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase);
        if (reference.Resolution.UsingTsExtension && emitted)
        {
            if (declarationExtension)
            {
                string extension = TypeScriptImportExtension(name);
                string suggested = name[..^extension.Length];
                if (ModuleKind is >= 5 and <= 99 || reference.Mode == ReferenceResolutionMode.Import)
                {
                    bool preferTs = options.Boolean("allowImportingTsExtensions") == true
                        || options.Boolean("rewriteRelativeImportExtensions") == true;
                    suggested += extension is ".mts" or ".d.mts" ? preferTs ? ".mts" : ".mjs"
                        : extension is ".cts" or ".d.cts" ? preferTs ? ".cts" : ".cjs" : preferTs ? ".ts" : ".js";
                }
                Error(specifier, 2846, suggested);
            }
            else if (!source.IsDeclarationFile && options.Boolean("allowImportingTsExtensions") != true
                && options.Boolean("rewriteRelativeImportExtensions") != true)
                Error(specifier, 5097, TypeScriptImportExtension(name));
        }
        var target = program.Symbols.Program.GetFile(reference.Resolution.FileName);
        if (target is not null && options.Boolean("rewriteRelativeImportExtensions") == true
            && (location.Flags & NodeFlags.Ambient) == 0 && !declarationExtension
            && (emitted || import is ImportDeclarationNode { ImportClause: null }))
        {
            var compiler = program.Symbols.Program;
            bool rewrite = RelativeModulePath(name) && CompilerPath.Extension(name) is ".ts" or ".tsx" or ".mts" or ".cts";
            if (!reference.Resolution.UsingTsExtension && rewrite)
            {
                string relative = CompilerPath.Relative(CompilerPath.DirectoryName(source.FileName), reference.Resolution.FileName,
                    compiler.UseCaseSensitiveFileNames);
                Error(specifier, 2876, RelativeModuleName(relative) ? relative : "./" + relative);
            }
            else if (reference.Resolution.UsingTsExtension && !rewrite && compiler.SourceFileMayBeEmitted(target.Syntax))
                Error(specifier, 2877, CompilerPath.Extension(name));
            else if (reference.Resolution.UsingTsExtension && rewrite
                && (compiler.ProjectReferences.Sources.GetValueOrDefault(target.Syntax.FileName)
                    ?? compiler.ProjectReferences.Outputs.GetValueOrDefault(target.Syntax.FileName)) is { } redirect)
            {
                var project = redirect.Project;
                string otherRoot = project.Options.String("rootDir") ?? (project.Options.Boolean("composite") == true
                    ? CompilerPath.DirectoryName(project.FileName) : Programs.ProjectReferences.CommonDirectory(
                        project.FileNames.Where(f => !CompilerPath.IsDeclarationFile(f)), compiler.UseCaseSensitiveFileNames));
                string ownRoot = compiler.CommonSourceDirectory;
                string roots = CompilerPath.Relative(ownRoot, otherRoot, compiler.UseCaseSensitiveFileNames);
                string outputs = CompilerPath.Relative(options.String("outDir") ?? ownRoot,
                    project.Options.String("outDir") ?? otherRoot, compiler.UseCaseSensitiveFileNames);
                if (roots != outputs)
                    Error(specifier, 2878);
            }
        }
        if (target is null || ModuleKind is not (100 or 101) || target.ImpliedFormat != ReferenceResolutionMode.Import)
            return;
        bool sync = import is ImportEqualsDeclarationNode || import is not CallExpressionNode
            && program.Symbols.Program.GetFile(source.FileName)!.ImpliedFormat == ReferenceResolutionMode.Require;
        var attributes = import is ImportTypeNode importType
            ? importType.Attributes
            : import is null ? null : AliasTargets.Attributes(import);
        bool mode = attributes?.Attributes?.OfType<ImportAttributeNode>().Any(a => ImportAttributeName(a.Name!) == "resolution-mode"
            && a.Value is StringLiteralNode { Text: "import" or "require" }) == true;
        if (!sync || mode)
            return;
        int code = import switch
        {
            ImportEqualsDeclarationNode => 1471,
            ImportTypeNode => 1542,
            ImportDeclarationNode { ImportClause: { } clause } when SemanticSyntax.TypeOnly(clause) => 1541,
            _ => 1479
        };
        var diagnostic = CheckerDiagnostic.Create(specifier, DiagnosticLocalization.GetMessage(code), name);
        if (code != 1471 && !source.IsDeclarationFile && CompilerPath.Extension(source.FileName) is ".ts" or ".js" or ".tsx" or ".jsx")
        {
            var metadata = program.Symbols.Program.GetFile(source.FileName)!;
            string extension = CompilerPath.Extension(source.FileName) switch { ".ts" => ".mts", ".js" => ".mjs", _ => "" };
            bool package = metadata.PackageDirectory.Length != 0 && metadata.PackageType.Length == 0;
            int detailCode = package ? extension.Length != 0 ? 1481 : 1482 : extension.Length != 0 ? 1480 : 1483;
            string[] arguments = package ? extension.Length != 0
                ? [extension, CompilerPath.Combine(metadata.PackageDirectory, "package.json")]
                : [CompilerPath.Combine(metadata.PackageDirectory, "package.json")]
                : extension.Length != 0 ? [extension] : [];
            diagnostic = diagnostic with
            {
                MessageChain = [CheckerDiagnostic.Create(
                specifier,
                DiagnosticLocalization.GetMessage(detailCode),
                arguments)]
            };
        }
        Error(specifier, diagnostic);
    }

    private static string TypeScriptImportExtension(string name)
    {
        foreach (string declaration in new[] { ".d.ts", ".d.cts", ".d.mts" })
            if (name.EndsWith(declaration, StringComparison.Ordinal))
                return declaration;
        string extension = CompilerPath.Extension(name);
        if (extension is ".ts" or ".tsx" or ".mts" or ".cts")
            return extension;
        foreach (string candidate in new[] { ".ts", ".tsx", ".d.ts", ".cts", ".d.cts", ".mts", ".d.mts" })
            if (name.Contains(candidate, StringComparison.Ordinal))
                return candidate;
        return extension;
    }
}
