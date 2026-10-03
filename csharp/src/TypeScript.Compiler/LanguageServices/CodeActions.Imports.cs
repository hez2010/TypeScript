using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class CodeFixQuery
{
    private static readonly int[] ImportErrorCodes = [
        (int)Messages.Cannot_find_name_0.Code,
        (int)Messages.Cannot_find_name_0_Did_you_mean_1.Code,
        (int)Messages.Cannot_find_name_0_Did_you_mean_the_instance_member_this_0.Code,
        (int)Messages.Cannot_find_name_0_Did_you_mean_the_static_member_1_0.Code,
        (int)Messages.Cannot_find_namespace_0.Code,
        (int)Messages.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead.Code,
        (int)Messages.X_0_only_refers_to_a_type_but_is_being_used_as_a_value_here.Code,
        (int)Messages.No_value_exists_in_scope_for_the_shorthand_property_0_Either_declare_one_or_provide_an_initializer.Code,
        (int)Messages.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_1_or_later.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_include_dom.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha_and_then_add_jest_or_mocha_to_the_types_field_in_your_tsconfig.Code,
        (int)Messages.Cannot_find_name_0_Did_you_mean_to_write_this_in_an_async_function.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery_and_then_add_jquery_to_the_types_field_in_your_tsconfig.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode.Code,
        (int)Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig.Code,
        (int)Messages.Cannot_find_namespace_0_Did_you_mean_1.Code,
        (int)Messages.Cannot_extend_an_interface_0_Did_you_mean_implements.Code,
        (int)Messages.This_JSX_tag_requires_0_to_be_in_scope_but_it_could_not_be_found.Code,
    ];

    private async ValueTask<IReadOnlyList<CodeFix>> ImportsAsync(int code, int position, Utf8String message)
    {
        List<CodeFix> result = [];
        foreach (var fix in await ImportFixesAsync(code, position, message))
        {
            var (edits, description, safe) = await fix.EditsAsync(projection, Options, preferences, cancellation);
            if (safe) result.Add(new(description, edits));
        }
        return result;
    }

    private async ValueTask<CodeFix?> AllImportsAsync()
    {
        if (File.FileName.StartsWith("^/"u8)) return null;
        var adder = Imports.CreateAdder();
        foreach (var diagnostic in await SemanticDiagnosticsAsync())
            if (Matches(diagnostic, ImportErrorCodes) && (await ImportFixesAsync((int)diagnostic.Code, diagnostic.Start, default)).FirstOrDefault() is { } fix)
                adder.Add(fix.Data);
        return adder.HasFixes ? new(Messages.Add_all_missing_imports.Format(preferences.Locale), await adder.EditsAsync()) : null;
    }

    private async ValueTask<IReadOnlyList<ImportFix>> ImportFixesAsync(int code, int position, Utf8String message)
    {
        if (File.FileName.StartsWith("^/"u8)) return [];
        var token = await SyntaxNavigation.GetTokenAtPositionAsync(File, position, cancellation);
        bool umd = code == (int)Messages.X_0_refers_to_a_UMD_global_but_the_current_file_is_a_module_Consider_adding_an_import_instead.Code;
        if (!umd && token is not IdentifierNode) return [];
        if (umd)
        {
            var symbol = token is IdentifierNode ? await checker.GetSymbolAtLocationAsync(token, cancellation) : null;
            if (!IsUmd(symbol) && (QuerySyntax.JsxTag(token) || token.Parent is JsxOpeningFragmentNode))
                symbol = Resolve(checker.JsxNamespaceName(token.Parent!), token.Parent is JsxOpeningFragmentNode ? token.Parent : token, false);
            if (!IsUmd(symbol) || await Imports.ExportForSymbolAsync(symbol!) is not { } export) return [];
            return (await Imports.FixesAsync(export, false, ReferenceSyntax.ValidTypeOnlyUse(token), null))
                .OrderBy(fix => fix, Comparer<ImportFix>.Create(Imports.CompareSorting)).ToArray();
        }
        var names = await ImportNamesAsync((IdentifierNode)token);
        if (code == (int)Messages.X_0_cannot_be_used_as_a_value_because_it_was_imported_using_import_type.Code)
        {
            List<(ImportFix Fix, Utf8String Name)> promotions = [];
            foreach (var (name, typeOnly) in names)
            {
                if (!typeOnly || Resolve(name, token, true) is not { } symbol) continue;
                var declaration = (await checker.GetAutoImportTargetAsync(symbol, cancellation)).TypeOnly;
                if (declaration is null || SemanticSyntax.Source(declaration) != File) continue;
                promotions.Add((new(new(AutoImportFixKind.PromoteTypeOnly, default, default, false, default, default),
                    TypeOnlyAliasDeclaration: declaration), name));
            }
            var filtered = promotions.Count > 1 && !message.IsEmpty
                ? promotions.Where(item => message.Contains("'"u8 + item.Name + "'"u8)).Select(item => item.Fix).ToArray() : [];
            return filtered.Length != 0 ? filtered : promotions.Select(item => item.Fix).ToArray();
        }
        var range = projection.ToRange(SmartIndenter.Start(token, File), SmartIndenter.Start(token, File));
        if (range.Fidelity != MappingFidelity.Exact) return [];
        List<(ImportFix Fix, bool Namespace)> fixes = [];
        foreach (var (name, typeOnly) in names)
        {
            if (typeOnly || name == "default"u8) continue;
            bool jsx = name == ((IdentifierNode)token).Text && QuerySyntax.JsxTag(token);
            foreach (var export in await Imports.SearchAsync(name, caseSensitive: !jsx))
            {
                if (jsx && export.Name != name && !export.Renameable) continue;
                foreach (var fix in await Imports.FixesAsync(export, jsx, ReferenceSyntax.ValidTypeOnlyUse(token), range.Range.Start))
                    fixes.Add((fix, name != ((IdentifierNode)token).Text));
            }
        }
        return fixes.OrderBy(item => item.Namespace).ThenBy(item => item.Fix, Comparer<ImportFix>.Create(Imports.CompareSorting)).Select(item => item.Fix).ToArray();
    }

    private async ValueTask<IReadOnlyList<(Utf8String Name, bool TypeOnly)>> ImportNamesAsync(IdentifierNode token)
    {
        if (QuerySyntax.JsxTag(token) && Options.Jsx is Configuration.JsxEmit.React or Configuration.JsxEmit.ReactNative)
        {
            var name = checker.JsxNamespaceName(File);
            var symbol = Resolve(name, token, true);
            bool intrinsic = token.Text.Length != 0 && token.Text[0] is >= (byte)'a' and <= (byte)'z' || token.Text.Contains("-"u8);
            if (intrinsic || symbol is null || symbol.Declarations.Any(AliasResolver.IsTypeOnly) && (symbol.Flags & SymbolFlags.Value) == 0)
            {
                List<(Utf8String, bool)> result = [];
                if (!intrinsic)
                {
                    var component = Resolve(token.Text, token, false);
                    if (component is null) result.Add((token.Text, false));
                    else if (await TypeOnlyAsync(component)) result.Add((token.Text, true));
                }
                result.Add((name, await TypeOnlyAsync(symbol)));
                return result;
            }
        }
        return [(token.Text, await TypeOnlyAsync(Resolve(token.Text, token, true)))];
    }

    private Symbol? Resolve(Utf8String name, SyntaxNode location, bool excludeGlobals)
        => checker.Symbols.NameResolver(cancellation).Resolve(location, name, SymbolFlags.Value, excludeGlobals: excludeGlobals);
    private async ValueTask<bool> TypeOnlyAsync(Symbol? symbol) => symbol is not null && (await checker.GetAutoImportTargetAsync(symbol, cancellation)).TypeOnly is not null;
    private static bool IsUmd(Symbol? symbol) => symbol?.Declarations.FirstOrDefault() is NamespaceExportDeclarationNode;
}
