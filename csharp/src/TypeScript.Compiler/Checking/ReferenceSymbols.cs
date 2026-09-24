using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Diagnostics;

namespace TypeScript.Compiler.Checking;

internal sealed class ReferenceSymbols(CheckerSymbols symbols, CheckerLinks links)
{
    internal Symbol Resolve(IdentifierNode node, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var data = links.SymbolNodes.Get(node);
        if (data.ResolvedSymbol is not null)
            return data.ResolvedSymbol;
        var symbol = node.Pos == node.End ? null : symbols.NameResolver(cancellation).Resolve(node, node.Text,
            SymbolFlags.Value | SymbolFlags.ExportValue, MissingName(node), ReferenceSyntax.AccessKind(node) != 1, false);
        cancellation.ThrowIfCancellationRequested();
        return data.ResolvedSymbol = symbol ?? symbols.UnknownSymbol;
    }

    internal DiagnosticMessage MissingName(IdentifierNode node)
    {
        bool wildcard = symbols.Program.Configuration.Options.Strings("types")?.Contains("*", StringComparer.Ordinal) == true;
        return node.Text switch
        {
            "document" or "console" => Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_include_dom,
            "$" => wildcard
                ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery
                : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery_and_then_add_jquery_to_the_types_field_in_your_tsconfig,
            "beforeEach" or "describe" or "suite" or "it" or "test" => wildcard
                ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha
                : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha_and_then_add_jest_or_mocha_to_the_types_field_in_your_tsconfig,
            "process" or "require" or "Buffer" or "module" or "NodeJS" => wildcard
                ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode
                : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig,
            "Bun" => wildcard
                ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun
                : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun_and_then_add_bun_to_the_types_field_in_your_tsconfig,
            "Map" or "Set" or "Promise" or "ast.Symbol" or "WeakMap" or "WeakSet" or "Iterator" or "AsyncIterator"
                or "SharedArrayBuffer" or "Atomics" or "AsyncIterable" or "AsyncIterableIterator" or "AsyncGenerator"
                or "AsyncGeneratorFunction" or "BigInt" or "Reflect" or "BigInt64Array" or "BigUint64Array" => Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_1_or_later,
            "await" when node.Parent is CallExpressionNode => Messages.Cannot_find_name_0_Did_you_mean_to_write_this_in_an_async_function,
            _ when node.Parent is ShorthandPropertyAssignmentNode => Messages.No_value_exists_in_scope_for_the_shorthand_property_0_Either_declare_one_or_provide_an_initializer,
            _ => Messages.Cannot_find_name_0,
        };
    }
}
