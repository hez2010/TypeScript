using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<bool> IsImportUsedAsync(SourceFileNode file, IdentifierNode definition, bool preserveJsx, CancellationToken cancellation)
    {
        using var query = await EnterQueryAsync(file, cancellation);
        if (preserveJsx && (definition.Text == JsxNamespaceName(file)
            || ValidJsxFactory(JsxPragma(file, Utf8Literals.Jsxfrag) ?? program.Symbols.Program.Configuration.Options.JsxFragmentFactory) is { } fragment
                && definition.Text == JsxFactoryRoot(fragment))) return true;
        var symbol = await SymbolAtLocationAsync(definition, cancellation);
        if (symbol is null) return true;
        foreach (int position in ReferenceNavigation.Positions(file, definition.Text, file, cancellation))
        {
            var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation);
            if (node is not IdentifierNode identifier || node == definition || identifier.Text != definition.Text) continue;
            if (await SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation) == symbol) return true;
            if (node.Parent is ShorthandPropertyAssignmentNode shorthand && await program.EntityNames.ResolveAsync(shorthand.Name,
                SymbolFlags.Value | SymbolFlags.Alias, true, cancellation: cancellation) == symbol) return true;
            if (node.Parent is ExportSpecifierNode export && (export.PropertyName is { } property ? property == node
                : export.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: null })
                && await ExportSpecifierLocalTargetSymbolAsync(export, cancellation) == symbol) return true;
        }
        return false;
    }
}
