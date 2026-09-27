using TypeScript.Compiler.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Resolution;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<Symbol?> ResolveImportModuleAsync(
        SyntaxNode location,
        SyntaxNode? specifier,
        Type? attributes,
        CancellationToken cancellation,
        bool implicitImport = false,
        DiagnosticCode missingModuleCode = DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations,
        bool ignoreErrors = false,
        ReferenceResolutionMode? resolutionMode = null,
        bool reportUnresolved = true)
    {
        cancellation.ThrowIfCancellationRequested();
        TextSlice? moduleName = specifier switch
        {
            StringLiteralNode text => text.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            _ => (TextSlice?)null
        };
        if (moduleName is not { } name)
            return null;
        if (!ignoreErrors && name.Span.StartsWith("@types/", StringComparison.Ordinal))
            Error(specifier!, DiagnosticCode.CannotImportTypeDeclarationFilesConsiderImporting0InsteadOf1, name[7..], name);
        var file = program.Symbols.Binding(location)!.SourceFile;
        var reference = program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(
            r => resolutionMode is { } mode ? r.Specifier == name && r.Mode == mode
                : implicitImport ? r.Node is null && r.Specifier == name : r.Node == specifier);
        var module = program.Symbols.Globals.GetValueOrDefault(TextSlice.Concat("\"", name, "\""));
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
                int star = pattern.Pattern.Span.IndexOf('*');
                if (star < 0 || name.Length < pattern.Pattern.Length - 1
                    || !name.Span.StartsWith(pattern.Pattern.Span.Slice(0, star), StringComparison.Ordinal)
                    || !name.Span.EndsWith(pattern.Pattern.Span.Slice(star + 1), StringComparison.Ordinal))
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
                var pattern = best.MaxBy(p => p.Pattern.Span.IndexOf('*'))!;
                var target = program.Symbols.Merger.GetMergedSymbol(pattern.Symbol)!;
                module = program.Symbols.PatternTargets.GetValueOrDefault(name) == target
                    ? program.Symbols.PatternAugmentations.GetValueOrDefault(name) ?? target : target;
            }
        }
        if (module is null
            && !ignoreErrors && (reportUnresolved || missingModuleCode == DiagnosticCode.InvalidModuleNameInAugmentationModule0CannotBeFound
            && reference?.Resolution is { IsResolved: true, Extension: ".js" or ".jsx" or ".mjs" or ".cjs" }))
            ReportUnresolvedImport(implicitImport ? location : specifier!, name, file, reference, missingModuleCode);
        return program.Symbols.Merger.GetMergedSymbol(module);
    }

    private void ReportUnresolvedImport(SyntaxNode node, TextSlice name, SourceFileNode file,
        Programs.ModuleReference? reference, DiagnosticCode missingModuleCode)
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
                if (missingModuleCode == DiagnosticCode.InvalidModuleNameInAugmentationModule0CannotBeFound)
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
                        if (!ModuleResolver.Relative(name.Span) && resolved.PackageId is { Name.Length: > 0 } package)
                            diagnostic = diagnostic with { MessageChain = [MissingPackageTypes(node, name, resolved, package.Name)] };
                        Error(node, diagnostic);
                    }
                    else
                        program.Suggestion(node, DiagnosticCode.CouldNotFindADeclarationFileForModule01ImplicitlyHasAnAnyType, name);
                }
                return;
            }
        }
        bool resolveJson = compiler.Configuration.Options.Boolean("resolveJsonModule")
            ?? (compiler.ModuleResolutionKind == "bundler" || ModuleKind is 102 or 199);
        if (!resolveJson && name.Span.EndsWith(".json", StringComparison.Ordinal))
        {
            program.Error(node, Messages.Cannot_find_module_0_Consider_using_resolveJsonModule_to_import_module_with_json_extension, name);
            return;
        }
        TextSlice normalized = name.Replace('\\', '/');
        bool relative = normalized.Span is "." or ".." || normalized.Span.StartsWith("./", StringComparison.Ordinal)
            || normalized.Span.StartsWith("../", StringComparison.Ordinal);
        if (reference?.Mode == ReferenceResolutionMode.Import && compiler.ModuleResolutionKind is "node16" or "nodenext"
            && relative && CompilerPath.Extension(normalized).Length == 0)
        {
            TextSlice path = CompilerPath.Resolve(CompilerPath.DirectoryName(file.FileName), (name).ToString());
            if (SuggestedImportExtension(path) is { Length: > 0 } extension)
            {
                program.Error(
                    node,
                    Messages.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Did_you_mean_0,
                    TextSlice.Concat(name, extension));
                return;
            }
            program.Error(
                node,
                Messages.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Consider_adding_an_extension_to_the_import_path);
            return;
        }
        if (!sideEffect
            && missingModuleCode == DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations
            && node is StringLiteralNode
            && NodeCoreModules.Contains(name))
            missingModuleCode = compiler.Configuration.Options.Strings("types")?.Contains("*", StringComparer.Ordinal) == true
                ? DiagnosticCode.CannotFindName0DoYouNeedToInstallTypeDefinitionsForNodeTryNpmISaveDevTypesSlashnode
                : DiagnosticCode.CannotFindName0DoYouNeedToInstallTypeDefinitionsForNodeTryNpmISaveDevTypesSlashnodeAndThenAddNodeToTheTypesFieldInYourTsconfig;
        program.Error(
            node,
            DiagnosticLocalization.GetMessage(
                sideEffect && missingModuleCode == DiagnosticCode.CannotFindModule0OrItsCorrespondingTypeDeclarations
                    ? DiagnosticCode.CannotFindModuleOrTypeDeclarationsForSideEffectImportOf0
                    : missingModuleCode),
            name);
    }

    private Dictionary<TextSlice, bool>? resolvedPackages;

    private Diagnostic MissingPackageTypes(SyntaxNode node, TextSlice moduleName, ResolvedModule resolved, TextSlice packageName)
    {
        TextSlice mangled = ModuleResolver.Mangle((packageName).ToString());
        if (resolved.AlternateResult.Length != 0)
            return CheckerDiagnostic.Create(
                node,
                DiagnosticLocalization.GetMessage(
                    DiagnosticCode.ThereAreTypesAt0ButThisResultCouldNotBeResolvedWhenRespectingPackageJsonExportsThe1LibraryMayNeedToUpdateItsPackageJsonOrTypings),
                resolved.AlternateResult,
                resolved.AlternateResult.Contains("/node_modules/@types/", StringComparison.Ordinal) ? TextSlice.Concat("@types/", mangled) : packageName);
        if (resolvedPackages is null)
        {
            resolvedPackages = new();
            foreach (var file in program.Symbols.Program.SourceFiles)
                foreach (var reference in file.Resolutions)
                    if (!reference.TypeReference && reference.Resolution.PackageId is { Name.Length: > 0 } package)
                        resolvedPackages[package.Name] = resolvedPackages.GetValueOrDefault(package.Name)
                            || reference.Resolution.Extension == ".d.ts";
        }
        if (resolvedPackages.ContainsKey(TextSlice.Concat("@types/", mangled)))
            return CheckerDiagnostic.Create(
                node,
                DiagnosticLocalization.GetMessage(
                    DiagnosticCode.IfThe0PackageActuallyExposesThisModuleConsiderSendingAPullRequestToAmendHttpsColonSlashSlashgithubComSlashDefinitelyTypedSlashDefinitelyTypedSlashtreeSlashmasterSlashtypesSlash1),
                packageName,
                mangled);
        if (resolvedPackages.GetValueOrDefault(packageName))
            return CheckerDiagnostic.Create(
                node,
                DiagnosticLocalization.GetMessage(
                    DiagnosticCode.IfThe0PackageActuallyExposesThisModuleTryAddingANewDeclarationDTsFileContainingDeclareModule1),
                packageName,
                moduleName);
        return CheckerDiagnostic.Create(
            node,
            DiagnosticLocalization.GetMessage(
                DiagnosticCode.TryNpmISaveDevTypesSlash1IfItExistsOrAddANewDeclarationDTsFileContainingDeclareModule0),
            moduleName,
            mangled);
    }

    internal TextSlice SuggestedImportExtension(TextSlice path)
    {
        foreach (var extension in new[] { ".mts", ".ts", ".cts", ".mjs", ".js", ".cjs", ".tsx", ".jsx", ".json" })
            if (program.Symbols.Program.FileExists(((TextSlice.Concat(path, extension))).ToString()))
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

    private void CheckResolvedImport(SyntaxNode location, SyntaxNode specifier, TextSlice name, SourceFileNode source,
        Programs.ModuleReference reference)
    {
        var options = program.Symbols.Program.Configuration.Options;
        if (JsxMode == 0 && reference.Resolution.Extension is ".tsx" or ".jsx")
            Error(specifier, DiagnosticCode.Module0WasResolvedTo1ButJsxIsNotSet, name, reference.Resolution.FileName);
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
        bool declarationExtension = name.Span.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase)
            || name.Span.EndsWith(".d.mts", StringComparison.OrdinalIgnoreCase) || name.Span.EndsWith(".d.cts", StringComparison.OrdinalIgnoreCase);
        if (reference.Resolution.UsingTsExtension && emitted)
        {
            if (declarationExtension)
            {
                TextSlice extension = TypeScriptImportExtension(name);
                TextSlice suggested = name[..^extension.Length];
                if (ModuleKind is >= 5 and <= 99 || reference.Mode == ReferenceResolutionMode.Import)
                {
                    bool preferTs = options.Boolean("allowImportingTsExtensions") == true
                        || options.Boolean("rewriteRelativeImportExtensions") == true;
                    suggested += extension.Span is ".mts" or ".d.mts" ? preferTs ? ".mts" : ".mjs"
                        : extension.Span is ".cts" or ".d.cts" ? preferTs ? ".cts" : ".cjs" : preferTs ? ".ts" : ".js";
                }
                Error(
                    specifier,
                    DiagnosticCode.ADeclarationFileCannotBeImportedWithoutImportTypeDidYouMeanToImportAnImplementationFile0Instead,
                    suggested);
            }
            else if (!source.IsDeclarationFile && options.Boolean("allowImportingTsExtensions") != true
                && options.Boolean("rewriteRelativeImportExtensions") != true)
                Error(
                    specifier,
                    DiagnosticCode.AnImportPathCanOnlyEndWithA0ExtensionWhenAllowImportingTsExtensionsIsEnabled,
                    TypeScriptImportExtension(name));
        }
        var target = program.Symbols.Program.GetFile(reference.Resolution.FileName);
        if (target is not null && options.Boolean("rewriteRelativeImportExtensions") == true
            && (location.Flags & NodeFlags.Ambient) == 0 && !declarationExtension
            && (emitted || import is ImportDeclarationNode { ImportClause: null }))
        {
            var compiler = program.Symbols.Program;
            bool rewrite = RelativeModulePath(name) && CompilerPath.Extension(name).Span is ".ts" or ".tsx" or ".mts" or ".cts";
            if (!reference.Resolution.UsingTsExtension && rewrite)
            {
                TextSlice relative = CompilerPath.Relative(CompilerPath.DirectoryName(source.FileName), reference.Resolution.FileName,
                    compiler.UseCaseSensitiveFileNames);
                Error(
                    specifier,
                    DiagnosticCode.ThisRelativeImportPathIsUnsafeToRewriteBecauseItLooksLikeAFileNameButActuallyResolvesTo0,
                    RelativeModuleName(relative) ? relative : TextSlice.Concat("./", relative));
            }
            else if (reference.Resolution.UsingTsExtension && !rewrite && compiler.SourceFileMayBeEmitted(target.Syntax))
                Error(
                    specifier,
                    DiagnosticCode.ThisImportUsesA0ExtensionToResolveToAnInputTypeScriptFileButWillNotBeRewrittenDuringEmitBecauseItIsNotARelativePath,
                    CompilerPath.Extension(name));
            else if (reference.Resolution.UsingTsExtension && rewrite
                && (compiler.ProjectReferences.Sources.GetValueOrDefault(target.Syntax.FileName)
                    ?? compiler.ProjectReferences.Outputs.GetValueOrDefault(target.Syntax.FileName)) is { } redirect)
            {
                var project = redirect.Project;
                TextSlice otherRoot = project.Options.String("rootDir") ?? (project.Options.Boolean("composite") == true
                    ? CompilerPath.DirectoryName(project.FileName) : Programs.ProjectReferences.CommonDirectory(
                        project.FileNames.Where(f => !CompilerPath.IsDeclarationFile(f)), compiler.UseCaseSensitiveFileNames));
                TextSlice ownRoot = compiler.CommonSourceDirectory;
                TextSlice roots = CompilerPath.Relative((ownRoot).ToString(), (otherRoot).ToString(), compiler.UseCaseSensitiveFileNames);
                TextSlice outputs = CompilerPath.Relative(options.String("outDir") ?? (ownRoot).ToString(),
                    project.Options.String("outDir") ?? (otherRoot).ToString(), compiler.UseCaseSensitiveFileNames);
                if (roots != outputs)
                    Error(
                        specifier,
                        DiagnosticCode.ThisImportPathIsUnsafeToRewriteBecauseItResolvesToAnotherProjectAndTheRelativePathBetweenTheProjectsOutputFilesIsNotTheSameAsTheRelativePathBetweenItsInputFiles);
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
            && a.Value is StringLiteralNode { Text.Span: "import" or "require" }) == true;
        if (!sync || mode)
            return;
        DiagnosticCode code = import switch
        {
            ImportEqualsDeclarationNode => DiagnosticCode.Module0CannotBeImportedUsingThisConstructTheSpecifierOnlyResolvesToAnESModuleWhichCannotBeImportedWithRequireUseAnECMAScriptImportInstead,
            ImportTypeNode => DiagnosticCode.TypeImportOfAnECMAScriptModuleFromACommonJSModuleMustHaveAResolutionModeAttribute,
            ImportDeclarationNode { ImportClause: { } clause } when SemanticSyntax.TypeOnly(clause) => DiagnosticCode.TypeOnlyImportOfAnECMAScriptModuleFromACommonJSModuleMustHaveAResolutionModeAttribute,
            _ => DiagnosticCode.TheCurrentFileIsACommonJSModuleWhoseImportsWillProduceRequireCallsHoweverTheReferencedFileIsAnECMAScriptModuleAndCannotBeImportedWithRequireConsiderWritingADynamicImport0CallInstead
        };
        var diagnostic = CheckerDiagnostic.Create(specifier, DiagnosticLocalization.GetMessage(code), name);
        if (code != DiagnosticCode.Module0CannotBeImportedUsingThisConstructTheSpecifierOnlyResolvesToAnESModuleWhichCannotBeImportedWithRequireUseAnECMAScriptImportInstead
            && !source.IsDeclarationFile
            && CompilerPath.Extension(source.FileName) is ".ts" or ".js" or ".tsx" or ".jsx")
        {
            var metadata = program.Symbols.Program.GetFile(source.FileName)!;
            TextSlice extension = CompilerPath.Extension(source.FileName) switch { ".ts" => ".mts", ".js" => ".mjs", _ => "" };
            bool package = metadata.PackageDirectory.Length != 0 && metadata.PackageType.Length == 0;
            DiagnosticCode detailCode = package
                ? extension.Length != 0
                    ? DiagnosticCode.ToConvertThisFileToAnECMAScriptModuleChangeItsFileExtensionTo0OrAddTheFieldTypeColonModuleTo1
                    : DiagnosticCode.ToConvertThisFileToAnECMAScriptModuleAddTheFieldTypeColonModuleTo0
                : extension.Length != 0
                    ? DiagnosticCode.ToConvertThisFileToAnECMAScriptModuleChangeItsFileExtensionTo0OrCreateALocalPackageJsonFileWithTypeColonModule
                    : DiagnosticCode.ToConvertThisFileToAnECMAScriptModuleCreateALocalPackageJsonFileWithTypeColonModule;
            TextSlice[] arguments = package ? extension.Length != 0
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

    private static TextSlice TypeScriptImportExtension(TextSlice name)
    {
        foreach (TextSlice declaration in new[] { ".d.ts", ".d.cts", ".d.mts" })
            if (name.Span.EndsWith(declaration, StringComparison.Ordinal))
                return declaration;
        TextSlice extension = CompilerPath.Extension(name);
        if (extension.Span is ".ts" or ".tsx" or ".mts" or ".cts")
            return extension;
        foreach (TextSlice candidate in new[] { ".ts", ".tsx", ".d.ts", ".cts", ".d.cts", ".mts", ".d.mts" })
            if (name.Span.Contains(candidate, StringComparison.Ordinal))
                return candidate;
        return extension;
    }
}
