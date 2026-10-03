using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.LanguageServices;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Checking;

internal sealed partial class Checker
{
    internal async ValueTask<SyntaxNode[]> GetReferencesToSymbolInFileAsync(SourceFileNode file, Symbol symbol, CancellationToken cancellation = default)
    {
        using var query = await EnterQueryAsync(file, cancellation).ConfigureAwait(false);
        var name = symbol.Name;
        if (name.IsEmpty) return [];
        var text = file.Source.Text;
        List<SyntaxNode> references = [];
        int position = text.IndexOf(name.Span);
        while (position >= 0 && position < file.End)
        {
            cancellation.ThrowIfCancellationRequested();
            int end = position + name.Length;
            if ((position == 0 || !TokenFacts.IsIdentifierPart(text[position - 1]))
                && (end == text.Length || !TokenFacts.IsIdentifierPart(text[end])))
            {
                var node = await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation).ConfigureAwait(false);
                if (node is IdentifierNode identifier && identifier.Text == name)
                {
                    var referenced = await SymbolAtLocationAsync(QuerySyntax.Reparsed(node), cancellation).ConfigureAwait(false);
                    bool match = referenced == symbol;
                    if (!match && node.Parent is ShorthandPropertyAssignmentNode shorthand)
                        match = await program.EntityNames.ResolveAsync(shorthand.Name, SymbolFlags.Value | SymbolFlags.Alias, true, cancellation: cancellation).ConfigureAwait(false) == symbol;
                    if (!match && node.Parent is ExportSpecifierNode export
                        && (export.PropertyName is { } propertyName ? propertyName == node
                            : export.Parent?.Parent is ExportDeclarationNode { ModuleSpecifier: null }))
                        match = await ExportSpecifierLocalTargetSymbolAsync(export, cancellation).ConfigureAwait(false) == symbol;
                    if (match) references.Add(node);
                }
            }
            int start = end + 1;
            if (start > text.Length) break;
            int next = text.Span[start..].IndexOf(name.Span);
            position = next < 0 ? -1 : start + next;
        }
        return references.ToArray();
    }
}
