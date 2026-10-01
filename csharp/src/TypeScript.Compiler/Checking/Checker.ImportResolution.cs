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
        Utf8String? moduleName = specifier switch
        {
            StringLiteralNode text => text.Text,
            NoSubstitutionTemplateLiteralNode template => template.Text,
            _ => (Utf8String?)null
        };
        if (moduleName is not { } name)
            return null;
        if (!ignoreErrors && name.Span.StartsWith("@types/"u8, StringComparison.Ordinal))
            Error(specifier!, DiagnosticCode.CannotImportTypeDeclarationFilesConsiderImporting0InsteadOf1, name[7..], name);
        var file = program.Symbols.Binding(location)!.SourceFile;
        var reference = program.Symbols.Program.GetFile(file.FileName)!.Resolutions.FirstOrDefault(
            r => resolutionMode is { } mode ? r.Specifier == name && r.Mode == mode
                : implicitImport ? r.Node is null && r.Specifier == name : r.Node == specifier);
        var module = program.Symbols.Globals.GetValueOrDefault(Utf8String.Concat("\""u8, name, "\""u8));
        if (module is null && reference?.Resolution.IsResolved == true
            && !(reference.Resolution.IsArbitraryExtension && !file.IsDeclarationFile
                && program.Symbols.Program.Configuration.Options.AllowArbitraryExtensions != true))
        {
            if (!ignoreErrors)
                CheckResolvedImport(location, implicitImport ? location : specifier!, name, file, reference);
            module = program.Symbols.Program.GetFile(reference.Resolution.FileName)?.Binding.Symbol;
        }
        attributes ??= context.EmptyObjectType;
        if (module is null || !await Views.EmptyAnonymousAsync(attributes, cancellation))
        {
            var candidates = new List<(PatternModule Module, Type Type)>();
            foreach (var pattern in program.Symbols.PatternModules)
            {
                int star = pattern.Pattern.Span.IndexOf((byte)'*');
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
                var pattern = best.MaxBy(p => p.Pattern.Span.IndexOf((byte)'*'))!;
                var target = program.Symbols.Merger.GetMergedSymbol(pattern.Symbol)!;
                module = program.Symbols.PatternTargets.GetValueOrDefault(name) == target
                    ? program.Symbols.PatternAugmentations.GetValueOrDefault(name) ?? target : target;
            }
        }
        if (module is null
            && !ignoreErrors && (reportUnresolved || missingModuleCode == DiagnosticCode.InvalidModuleNameInAugmentationModule0CannotBeFound
            && reference?.Resolution is { IsResolved: true, Extension: var matchedText } && (matchedText == ".js"u8 || matchedText == ".jsx"u8 || matchedText == ".mjs"u8 || matchedText == ".cjs"u8)))
            ReportUnresolvedImport(implicitImport ? location : specifier!, name, file, reference, missingModuleCode);
        return program.Symbols.Merger.GetMergedSymbol(module);
    }

    private void ReportUnresolvedImport(SyntaxNode node, Utf8String name, SourceFileNode file,
        Programs.ModuleReference? reference, DiagnosticCode missingModuleCode)
    {
        bool sideEffect = node.Parent is ImportDeclarationNode { ImportClause: null };
        var compiler = program.Symbols.Program;
        if (reference?.Resolution.IsArbitraryExtension == true && !file.IsDeclarationFile
            && compiler.Configuration.Options.AllowArbitraryExtensions != true)
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
            if (JsxMode == 0 && (resolved.Extension == ".jsx"u8 || resolved.Extension == ".tsx"u8))
                return;
            if (compiler.ProjectReferences.Sources.TryGetValue(resolved.FileName, out var redirect))
            {
                program.Error(node, Messages.Output_file_0_has_not_been_built_from_source_file_1, redirect.Output, resolved.FileName);
                return;
            }
            if (resolved.Extension == ".js"u8 || resolved.Extension == ".jsx"u8 || resolved.Extension == ".mjs"u8 || resolved.Extension == ".cjs"u8)
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
                            diagnostic = diagnostic with { MessageChain = [MissingPackageTypes(node, name, resolved, package.Name) with
                            { Repopulation = new(2, name, (int)reference.Mode, package.Name == name ? default : package.Name) }] };
                        Error(node, diagnostic);
                    }
                    else
                        program.Suggestion(node, DiagnosticCode.CouldNotFindADeclarationFileForModule01ImplicitlyHasAnAnyType, name);
                }
                return;
            }
        }
        bool resolveJson = compiler.Configuration.Options.ResolveJsonModule
            ?? compiler.ModuleResolutionKind == Utf8Literals.Bundler || ModuleKind is 102 or 199;
        if (!resolveJson && name.Span.EndsWith(".json"u8, StringComparison.Ordinal))
        {
            program.Error(node, Messages.Cannot_find_module_0_Consider_using_resolveJsonModule_to_import_module_with_json_extension, name);
            return;
        }
        Utf8String normalized = name.Replace((byte)'\\', (byte)'/');
        bool relative = normalized.Span.SequenceEqual("."u8) || normalized.Span.SequenceEqual(".."u8) || normalized.Span.StartsWith("./"u8, StringComparison.Ordinal)
            || normalized.Span.StartsWith("../"u8, StringComparison.Ordinal);
        if (reference?.Mode == ReferenceResolutionMode.Import && (compiler.ModuleResolutionKind == "node16"u8 || compiler.ModuleResolutionKind == "nodenext"u8)
            && relative && CompilerPath.Extension(normalized).Length == 0)
        {
            Utf8String path = CompilerPath.Resolve(CompilerPath.DirectoryName(file.FileName), name);
            if (SuggestedImportExtension(path) is { Length: > 0 } extension)
            {
                program.Error(
                    node,
                    Messages.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Did_you_mean_0,
                    Utf8String.Concat(name, extension));
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
            missingModuleCode = compiler.Configuration.Options.Types?.Contains(Utf8Literals.Asterisk, Utf8StringComparer.Ordinal) == true
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

    private Diagnostic MissingPackageTypes(SyntaxNode node, Utf8String moduleName, ResolvedModule resolved, Utf8String packageName)
    {
        var details = program.Symbols.Program.ModuleNotFoundDetails(resolved, moduleName, packageName);
        return CheckerDiagnostic.Create(node, details.Message, details.Arguments);
    }

    internal Utf8String SuggestedImportExtension(Utf8String path)
    {
        foreach (var extension in new Utf8String[] { Utf8Literals.Mts, Utf8Literals.Ts, Utf8Literals.Cts, Utf8Literals.Mjs, Utf8Literals.Js, Utf8Literals.Cjs, Utf8Literals.Tsx, Utf8Literals.Jsx, Utf8Literals.Json })
            if (program.Symbols.Program.FileExists(Utf8String.Concat(path, extension)))
                return extension switch
                {
                    _ when extension == ".mts"u8 => Utf8Literals.Mjs,
                    _ when extension == ".cts"u8 => Utf8Literals.Cjs,
                    _ when extension == ".ts"u8 => Utf8Literals.Js,
                    _ when extension == ".tsx"u8 => JsxMode == 1 ? Utf8Literals.Jsx : Utf8Literals.Js,
                    _ => extension
                };
        return Utf8String.Empty;
    }

    private void CheckResolvedImport(SyntaxNode location, SyntaxNode specifier, Utf8String name, SourceFileNode source,
        Programs.ModuleReference reference)
    {
        var options = program.Symbols.Program.Configuration.Options;
        if (JsxMode == 0 && (reference.Resolution.Extension == ".tsx"u8 || reference.Resolution.Extension == ".jsx"u8))
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
        bool declarationExtension = name.Span.EndsWith(".d.ts"u8, StringComparison.OrdinalIgnoreCase)
            || name.Span.EndsWith(".d.mts"u8, StringComparison.OrdinalIgnoreCase) || name.Span.EndsWith(".d.cts"u8, StringComparison.OrdinalIgnoreCase);
        if (reference.Resolution.UsingTsExtension && emitted)
        {
            if (declarationExtension)
            {
                Utf8String extension = TypeScriptImportExtension(name);
                Utf8String suggested = name[..^extension.Length];
                if (ModuleKind is >= 5 and <= 99 || reference.Mode == ReferenceResolutionMode.Import)
                {
                    bool preferTs = options.AllowImportingTsExtensions == true
                        || options.RewriteRelativeImportExtensions == true;
                    suggested += (extension.Span.SequenceEqual(".mts"u8) || extension.Span.SequenceEqual(".d.mts"u8)) ? preferTs ? Utf8Literals.Mts : Utf8Literals.Mjs
                        : (extension.Span.SequenceEqual(".cts"u8) || extension.Span.SequenceEqual(".d.cts"u8)) ? preferTs ? Utf8Literals.Cts : Utf8Literals.Cjs : preferTs ? Utf8Literals.Ts : Utf8Literals.Js;
                }
                Error(
                    specifier,
                    DiagnosticCode.ADeclarationFileCannotBeImportedWithoutImportTypeDidYouMeanToImportAnImplementationFile0Instead,
                    suggested);
            }
            else if (!source.IsDeclarationFile && options.AllowImportingTsExtensions != true
                && options.RewriteRelativeImportExtensions != true)
                Error(
                    specifier,
                    DiagnosticCode.AnImportPathCanOnlyEndWithA0ExtensionWhenAllowImportingTsExtensionsIsEnabled,
                    TypeScriptImportExtension(name));
        }
        var target = program.Symbols.Program.GetFile(reference.Resolution.FileName);
        if (target is not null && options.RewriteRelativeImportExtensions == true
            && (location.Flags & NodeFlags.Ambient) == 0 && !declarationExtension
            && (emitted || import is ImportDeclarationNode { ImportClause: null }))
        {
            var compiler = program.Symbols.Program;
            bool rewrite = RelativeModulePath(name) && CompilerPath.Extension(name).Span is var matchedText4 && (matchedText4.SequenceEqual(".ts"u8) || matchedText4.SequenceEqual(".tsx"u8) || matchedText4.SequenceEqual(".mts"u8) || matchedText4.SequenceEqual(".cts"u8));
            if (!reference.Resolution.UsingTsExtension && rewrite)
            {
                Utf8String relative = CompilerPath.Relative(CompilerPath.DirectoryName(source.FileName), reference.Resolution.FileName,
                    compiler.UseCaseSensitiveFileNames);
                Error(
                    specifier,
                    DiagnosticCode.ThisRelativeImportPathIsUnsafeToRewriteBecauseItLooksLikeAFileNameButActuallyResolvesTo0,
                    RelativeModuleName(relative) ? relative : Utf8String.Concat("./"u8, relative));
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
                Utf8String otherRoot = project.Options.RootDir ?? (project.Options.Composite == true
                    ? CompilerPath.DirectoryName(project.FileName) : Programs.ProjectReferences.CommonDirectory(
                        project.FileNames.Where(f => !CompilerPath.IsDeclarationFile(f)), compiler.UseCaseSensitiveFileNames));
                Utf8String ownRoot = compiler.CommonSourceDirectory;
                Utf8String roots = CompilerPath.Relative(ownRoot, otherRoot, compiler.UseCaseSensitiveFileNames);
                Utf8String outputs = CompilerPath.Relative(options.OutDir ?? ownRoot,
                    project.Options.OutDir ?? otherRoot, compiler.UseCaseSensitiveFileNames);
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
        bool mode = attributes?.Attributes?.OfType<ImportAttributeNode>().Any(a => ImportAttributeName(a.Name!) == Utf8Literals.ResolutionMode
            && a.Value is StringLiteralNode { Text.Span: var matchedText5 } && (matchedText5.SequenceEqual("import"u8) || matchedText5.SequenceEqual("require"u8))) == true;
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
            && CompilerPath.Extension(source.FileName) is var matchedText6 && (matchedText6 == ".ts"u8 || matchedText6 == ".js"u8 || matchedText6 == ".tsx"u8 || matchedText6 == ".jsx"u8))
        {
            var details = Programs.CompilerProgram.ModeMismatchDetails(program.Symbols.Program.GetFile(source.FileName)!);
            diagnostic = diagnostic with
            {
                MessageChain = [CheckerDiagnostic.Create(
                specifier,
                details.Message,
                details.Arguments) with { Repopulation = new(1) }]
            };
        }
        Error(specifier, diagnostic);
    }

    private static Utf8String TypeScriptImportExtension(Utf8String name)
    {
        foreach (Utf8String declaration in new Utf8String[] { Utf8Literals.DTs, Utf8Literals.DCts, Utf8Literals.DMts })
            if (name.Span.EndsWith(declaration, StringComparison.Ordinal))
                return declaration;
        Utf8String extension = CompilerPath.Extension(name);
        if (extension.Span.SequenceEqual(".ts"u8) || extension.Span.SequenceEqual(".tsx"u8) || extension.Span.SequenceEqual(".mts"u8) || extension.Span.SequenceEqual(".cts"u8))
            return extension;
        foreach (Utf8String candidate in new Utf8String[] { Utf8Literals.Ts, Utf8Literals.Tsx, Utf8Literals.DTs, Utf8Literals.Cts, Utf8Literals.DCts, Utf8Literals.Mts, Utf8Literals.DMts })
            if (name.Span.Contains(candidate, StringComparison.Ordinal))
                return candidate;
        return extension;
    }
}
