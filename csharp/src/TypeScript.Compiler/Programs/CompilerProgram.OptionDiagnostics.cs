using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Configuration;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Hosts;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using ModuleOptionKind = TypeScript.Compiler.Configuration.ModuleKind;
using ResolutionOptionKind = TypeScript.Compiler.Configuration.ModuleResolutionKind;

namespace TypeScript.Compiler.Programs;

public sealed partial class CompilerProgram
{
    private IEnumerable<Diagnostic> CompilerOptionDiagnostics()
    {
        var options = Configuration.Options;
        var source = Configuration.SourceFile;
        var root = (source?.Statements?.FirstOrDefault() as ExpressionStatementNode)?.Expression as ObjectLiteralExpressionNode;
        var compilerProperty = Property(root, "compilerOptions"u8);
        var syntax = compilerProperty?.Initializer as ObjectLiteralExpressionNode;
        List<Diagnostic> diagnostics = [];
        void Add(DiagnosticMessage message, Utf8String first, Utf8String second = default, Utf8String third = default) =>
            diagnostics.Add(Create(message, Property(syntax, first, second)?.Name, third.Length != 0 ? [first, second, third] : [first, second]));
        void Value(DiagnosticMessage message, Utf8String option, params Utf8String[] args) =>
            diagnostics.Add(Create(message, Property(syntax, option)?.Initializer, args));
        void Removed(Utf8String name, Utf8String value = default, Utf8String suggestion = default)
        {
            var property = Property(syntax, name);
            var diagnostic = Create(value.Length == 0 ? Messages.Option_0_has_been_removed_Please_remove_it_from_your_configuration
                : Messages.Option_0_1_has_been_removed_Please_remove_it_from_your_configuration,
                value.Length == 0 ? property?.Name : property?.Initializer, value.Length == 0 ? [name] : [name, value]);
            if (suggestion.Length != 0) diagnostic = diagnostic with { MessageChain = [new(Messages.Use_0_instead, -1, 0, [suggestion])] };
            diagnostics.Add(diagnostic);
        }
        if (options.BaseUrl is { Length: > 0 } baseUrl)
        {
            Utf8String suggestion = default;
            if (source is not null)
            {
                var relative = CompilerPath.Relative(CompilerPath.DirectoryName(source.FileName), baseUrl, UseCaseSensitiveFileNames);
                if (!relative.StartsWith("./"u8, StringComparison.Ordinal) && !relative.StartsWith("../"u8, StringComparison.Ordinal)) relative = "./"u8 + relative;
                suggestion = "\"paths\": {\"*\": ["u8 + JsonStrings.Raw(OptionValues.String(CompilerPath.Combine(relative, "*"u8))) + "]}"u8;
            }
            Removed("baseUrl"u8, suggestion: suggestion);
        }
        if (options.OutFile is { Length: > 0 }) Removed("outFile"u8);
        if (options.Target == ScriptTarget.ES5) Removed("target"u8, "ES5"u8);
        if (options.Module is ModuleOptionKind.AMD or ModuleOptionKind.System or ModuleOptionKind.UMD) Removed("module"u8, Utf8String.FromString(options.Module.ToString()));
        if (options.ModuleResolution == ResolutionOptionKind.Classic) Removed("moduleResolution"u8, "Classic"u8);
        if (options.AlwaysStrict == false) Removed("alwaysStrict"u8, "false"u8);
        if (options.ESModuleInterop == false) Removed("esModuleInterop"u8, "false"u8);
        if (options.AllowSyntheticDefaultImports == false) Removed("allowSyntheticDefaultImports"u8, "false"u8);
        if (options.ModuleResolution == ResolutionOptionKind.Node10) Removed("moduleResolution"u8, "node10"u8);
        if (options.DownlevelIteration is not null) Removed("downlevelIteration"u8);
        bool strictNull = options.StrictNullChecks ?? options.Strict ?? true;
        bool allowJs = options.AllowJs ?? options.CheckJs == true;
        bool declarations = options.Declaration == true || options.Composite == true;
        if (options.StrictPropertyInitialization == true && !strictNull) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1, "strictPropertyInitialization"u8, "strictNullChecks"u8);
        if (options.ExactOptionalPropertyTypes == true && !strictNull) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1, "exactOptionalPropertyTypes"u8, "strictNullChecks"u8);
        if (options.IsolatedDeclarations == true)
        {
            if (allowJs) Add(Messages.Option_0_cannot_be_specified_with_option_1, "allowJs"u8, "isolatedDeclarations"u8);
            if (!declarations) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "isolatedDeclarations"u8, "declaration"u8, "composite"u8);
        }
        if (options.InlineSourceMap == true)
        {
            if (options.SourceMap == true) Add(Messages.Option_0_cannot_be_specified_with_option_1, "sourceMap"u8, "inlineSourceMap"u8);
            if (options.MapRoot is { Length: > 0 }) Add(Messages.Option_0_cannot_be_specified_with_option_1, "mapRoot"u8, "inlineSourceMap"u8);
        }
        if (options.Composite == true)
        {
            if (options.Declaration == false) Add(Messages.Composite_projects_may_not_disable_declaration_emit, "declaration"u8);
            if (options.Incremental == false) Add(Messages.Composite_projects_may_not_disable_incremental_compilation, "declaration"u8);
        }
        if (options.TsBuildInfoFile is not { Length: > 0 } && options.Incremental == true && Configuration.FileName.Length == 0)
            diagnostics.Add(Create(Messages.Option_incremental_is_only_valid_with_a_known_configuration_file_like_tsconfig_json_or_when_tsBuildInfoFile_is_explicitly_provided, null, []));
        if (options.SourceMap != true && options.InlineSourceMap != true)
        {
            if (options.InlineSources == true) Add(Messages.Option_0_can_only_be_used_when_either_option_inlineSourceMap_or_option_sourceMap_is_provided, "inlineSources"u8);
            if (options.SourceRoot is { Length: > 0 }) Add(Messages.Option_0_can_only_be_used_when_either_option_inlineSourceMap_or_option_sourceMap_is_provided, "sourceRoot"u8);
        }
        if (options.MapRoot is { Length: > 0 } && options.SourceMap != true && options.DeclarationMap != true)
            Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "mapRoot"u8, "sourceMap"u8, "declarationMap"u8);
        if (options.DeclarationMap == true && !declarations)
            Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "declarationMap"u8, "declaration"u8, "composite"u8);
        if (options.Lib is not null && options.NoLib == true) Add(Messages.Option_0_cannot_be_specified_with_option_1, "lib"u8, "noLib"u8);
        if ((options.IsolatedModules == true || options.VerbatimModuleSyntax == true) && options.PreserveConstEnums == false)
            Add(Messages.Option_preserveConstEnums_cannot_be_disabled_when_0_is_enabled, options.VerbatimModuleSyntax == true ? "verbatimModuleSyntax"u8 : "isolatedModules"u8, "preserveConstEnums"u8);
        if (options.CheckJs == true && !allowJs) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1, "checkJs"u8, "allowJs"u8);
        if (options.EmitDeclarationOnly == true && !declarations) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "emitDeclarationOnly"u8, "declaration"u8, "composite"u8);
        if (options.EmitDecoratorMetadata == true && options.ExperimentalDecorators != true) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1, "emitDecoratorMetadata"u8, "experimentalDecorators"u8);
        bool automaticJsx = options.Jsx is JsxEmit.ReactJSX or JsxEmit.ReactJSXDev;
        Utf8String jsx = options.Jsx == JsxEmit.ReactJSXDev ? "react-jsxdev"u8 : options.Jsx == JsxEmit.React ? "react"u8 : "react-jsx"u8;
        if (options.JsxFactory is { Length: > 0 } factory)
        {
            if (options.ReactNamespace is { Length: > 0 }) Add(Messages.Option_0_cannot_be_specified_with_option_1, "reactNamespace"u8, "jsxFactory"u8);
            if (automaticJsx) Add(Messages.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxFactory"u8, jsx);
            if (!EntityName(factory)) Value(Messages.Invalid_value_for_jsxFactory_0_is_not_a_valid_identifier_or_qualified_name, "jsxFactory"u8, factory);
        }
        else if (options.ReactNamespace is { Length: > 0 } ns && !Identifier(ns))
            Value(Messages.Invalid_value_for_reactNamespace_0_is_not_a_valid_identifier, "reactNamespace"u8, ns);
        if (options.JsxFragmentFactory is { Length: > 0 } fragment)
        {
            if (options.JsxFactory is not { Length: > 0 }) Add(Messages.Option_0_cannot_be_specified_without_specifying_option_1, "jsxFragmentFactory"u8, "jsxFactory"u8);
            if (automaticJsx) Add(Messages.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxFragmentFactory"u8, jsx);
            if (!EntityName(fragment)) Value(Messages.Invalid_value_for_jsxFragmentFactory_0_is_not_a_valid_identifier_or_qualified_name, "jsxFragmentFactory"u8, fragment);
        }
        if (options.ReactNamespace is { Length: > 0 } && automaticJsx) Add(Messages.Option_0_cannot_be_specified_when_option_jsx_is_1, "reactNamespace"u8, jsx);
        if (options.JsxImportSource is { Length: > 0 } && options.Jsx == JsxEmit.React) Add(Messages.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxImportSource"u8, jsx);
        if (options.AllowImportingTsExtensions == true && options.NoEmit != true && options.EmitDeclarationOnly != true && options.RewriteRelativeImportExtensions != true)
            Value(Messages.Option_allowImportingTsExtensions_can_only_be_used_when_one_of_noEmit_emitDeclarationOnly_or_rewriteRelativeImportExtensions_is_set, "allowImportingTsExtensions"u8);
        var module = options.EmitModule;
        var resolution = options.EmitModuleResolutionKind;
        bool nodeResolution = resolution is >= ResolutionOptionKind.Node16 and <= ResolutionOptionKind.NodeNext;
        bool nodeModule = module is >= ModuleOptionKind.Node16 and <= ModuleOptionKind.NodeNext;
        if (!nodeResolution && resolution != ResolutionOptionKind.Bundler)
            foreach (Utf8String option in new Utf8String[] { "resolvePackageJsonExports"u8, "resolvePackageJsonImports"u8, "customConditions"u8 })
                if (options.Boolean(option) == true || option == "customConditions"u8 && options.CustomConditions is not null)
                    Add(Messages.Option_0_can_only_be_used_when_moduleResolution_is_set_to_node16_nodenext_or_bundler, option);
        if (resolution == ResolutionOptionKind.Bundler && module is not (>= ModuleOptionKind.ES2015 and <= ModuleOptionKind.ESNext or ModuleOptionKind.Preserve or ModuleOptionKind.CommonJS))
            Value(Messages.Option_0_can_only_be_used_when_module_is_set_to_preserve_commonjs_or_es2015_or_later, "moduleResolution"u8, "bundler"u8);
        if (nodeModule && !nodeResolution)
            Value(Messages.Option_moduleResolution_must_be_set_to_0_or_left_unspecified_when_option_module_is_set_to_1, "moduleResolution"u8,
                module == ModuleOptionKind.NodeNext ? "NodeNext"u8 : "Node16"u8, Utf8String.FromString(module.ToString()));
        else if (nodeResolution && !nodeModule)
            Value(Messages.Option_module_must_be_set_to_0_when_option_moduleResolution_is_set_to_1, "module"u8,
                Utf8String.FromString(resolution.ToString()), Utf8String.FromString(resolution.ToString()));
        return diagnostics;

        Diagnostic Create(DiagnosticMessage message, SyntaxNode? node, Utf8String[] args)
        {
            node ??= compilerProperty?.Name;
            if (node is null || source is null) return new(message, -1, 0, args);
            var scanner = new Scanner(source.Source); scanner.ResetPosition(Math.Max(0, node.Pos)); scanner.Scan();
            int start = scanner.TokenStart;
            return new(message, start, Math.Max(0, node.End - start), args) { FileName = source.FileName };
        }
        static bool Identifier(Utf8String text)
        {
            if (text.Length == 0) return false;
            bool first = true;
            foreach (var rune in text.Span.EnumerateRunes())
            {
                if (first ? !TokenFacts.IsIdentifierStart(rune.Value) : !TokenFacts.IsIdentifierPart(rune.Value)) return false;
                first = false;
            }
            return true;
        }
        static bool EntityName(Utf8String text) => Parser.ParseIsolatedEntityName(text) is not null;
    }
}
