using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class SourceEditTracker
{
    internal async ValueTask ReplaceDeclarationsAsync(IReadOnlyList<SyntaxNode> original, IReadOnlyList<SyntaxNode> replacement)
    {
        if (original.Count == 0) return;
        if (replacement.Count == 0)
        {
            ReplaceText(Start(original[0]), DeclarationEnd(original[^1]), default);
            return;
        }
        foreach (var node in replacement) Context.AddFlags(node, EmitFlags.NoLeadingComments);
        Replace(Start(original[0]), DeclarationEnd(original[0]), replacement, new(Suffix: "\n"u8));
        for (int i = 1; i < original.Count; i++)
        {
            var node = original[i];
            int start = AdjustedStart(node, false);
            if (node is ImportDeclarationNode)
            {
                bool first = File.Imports.FirstOrDefault()?.Parent == node
                    || File.Statements?.FirstOrDefault(statement => statement is ImportDeclarationNode or ImportEqualsDeclarationNode) == node;
                int lineStart = LineStart(Start(node));
                start = first || lineStart < node.Pos ? Start(node) : lineStart;
                if (!first && (await File.GetDocumentationAsync(node, cancellation)).FirstOrDefault() is { } documentation)
                    start = LineStart(documentation.Pos);
            }
            else if (Line(node.Pos) == Line(Start(node))) start = node.Pos;
            ReplaceText(start, DeclarationEnd(node), default);
        }
    }

    private int DeclarationEnd(SyntaxNode node)
    {
        foreach (var comment in SyntaxPrinter.CommentRanges(Text, node.End, true))
        {
            if (comment.Kind == K.SingleLineCommentTrivia || Line(comment.Pos) > Line(node.End)) break;
            if (Line(comment.End) > Line(node.End)) return SkipTrivia(comment.End, comments: true, lineBreak: true);
        }
        return SkipTrivia(node.End, lineBreak: true);
    }
}
