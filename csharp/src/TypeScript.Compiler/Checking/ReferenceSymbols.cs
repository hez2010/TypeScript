using TypeScript.Compiler.Text;
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
        bool wildcard = symbols.Program.Configuration.Options.Types?.Contains(Utf8Literals.Asterisk, Utf8StringComparer.Ordinal) == true;
        return node.Text.Span switch
        {
            _ when node.Text.Span.SequenceEqual("document"u8) || node.Text.Span.SequenceEqual("console"u8) => Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_include_dom,
            _ when node.Text.Span.SequenceEqual("$"u8) => wildcard
                            ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery
                            : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery_and_then_add_jquery_to_the_types_field_in_your_tsconfig,
            _ when node.Text.Span.SequenceEqual("beforeEach"u8) || node.Text.Span.SequenceEqual("describe"u8) || node.Text.Span.SequenceEqual("suite"u8) || node.Text.Span.SequenceEqual("it"u8) || node.Text.Span.SequenceEqual("test"u8) => wildcard
                            ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha
                            : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha_and_then_add_jest_or_mocha_to_the_types_field_in_your_tsconfig,
            _ when node.Text.Span.SequenceEqual("process"u8) || node.Text.Span.SequenceEqual("require"u8) || node.Text.Span.SequenceEqual("Buffer"u8) || node.Text.Span.SequenceEqual("module"u8) || node.Text.Span.SequenceEqual("NodeJS"u8) => wildcard
                            ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode
                            : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig,
            _ when node.Text.Span.SequenceEqual("Bun"u8) => wildcard
                            ? Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun
                            : Messages.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun_and_then_add_bun_to_the_types_field_in_your_tsconfig,
            _ when node.Text.Span.SequenceEqual("Map"u8) || node.Text.Span.SequenceEqual("Set"u8) || node.Text.Span.SequenceEqual("Promise"u8) || node.Text.Span.SequenceEqual("ast.Symbol"u8) || node.Text.Span.SequenceEqual("WeakMap"u8) || node.Text.Span.SequenceEqual("WeakSet"u8) || node.Text.Span.SequenceEqual("Iterator"u8) || node.Text.Span.SequenceEqual("AsyncIterator"u8) || node.Text.Span.SequenceEqual("SharedArrayBuffer"u8) || node.Text.Span.SequenceEqual("Atomics"u8) || node.Text.Span.SequenceEqual("AsyncIterable"u8) || node.Text.Span.SequenceEqual("AsyncIterableIterator"u8) || node.Text.Span.SequenceEqual("AsyncGenerator"u8) || node.Text.Span.SequenceEqual("AsyncGeneratorFunction"u8) || node.Text.Span.SequenceEqual("BigInt"u8) || node.Text.Span.SequenceEqual("Reflect"u8) || node.Text.Span.SequenceEqual("BigInt64Array"u8) || node.Text.Span.SequenceEqual("BigUint64Array"u8) => Messages.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_1_or_later,
            _ when node.Text.Span.SequenceEqual("await"u8) && node.Parent is CallExpressionNode => Messages.Cannot_find_name_0_Did_you_mean_to_write_this_in_an_async_function,
            _ when node.Parent is ShorthandPropertyAssignmentNode => Messages.No_value_exists_in_scope_for_the_shorthand_property_0_Either_declare_one_or_provide_an_initializer,
            _ => Messages.Cannot_find_name_0,
        };
    }
}
