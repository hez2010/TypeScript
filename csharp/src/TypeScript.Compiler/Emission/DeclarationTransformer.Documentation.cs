using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

internal sealed partial class DeclarationTransformer
{
    private async ValueTask PreservePropertyDocumentationAsync(SyntaxNode result, SyntaxNode original)
    {
        var documentation = (await source.GetDocumentationAsync(original, Cancellation)).FirstOrDefault();
        if (documentation?.Comment is not { } parts) return;
        var text = new Utf8StringBuilder();
        foreach (var part in parts)
        {
            if (part is JSDocTextNode literal)
                foreach (var value in literal.Text) text.Append(value);
            else if (part.Kind is SyntaxKind.JSDocLink or SyntaxKind.JSDocLinkCode or SyntaxKind.JSDocLinkPlain)
                text.Append(source.Source.Text[part.Pos..part.End]);
        }
        var description = text.ToUtf8String().ToString().TrimEnd();
        if (description.Length != 0)
            Context.AddLeadingComment(result, new(SyntaxKind.MultiLineCommentTrivia,
                Utf8String.FromString("*\n * " + description.Replace("\n", "\n * ", StringComparison.Ordinal) + "\n "), HasTrailingNewLine: true));
    }
}
