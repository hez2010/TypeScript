using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class SourceEditTracker
{
    internal async ValueTask InsertSpecifierAsync(NamedImportsNode imports, int index, SyntaxNode node)
    {
        var elements = imports.Elements!;
        if (index > 0 && index <= elements.Count) await InsertInListAfterAsync(elements[index - 1], node, elements);
        else InsertBefore(elements[0], node, Line(Start(elements[0])) != Line(Start(imports.Parent!.Parent!)));
    }

    internal async ValueTask InsertInListAfterAsync(SyntaxNode after, SyntaxNode node, NodeList list)
    {
        int index = -1;
        for (int i = 0; i < list.Count; i++) if (list[i] == after) { index = i; break; }
        if (index < 0) return;
        if (index != list.Count - 1)
        {
            var separator = await SyntaxNavigation.GetTokenAtPositionAsync(File, after.End, cancellation);
            if (!Separator(after, separator)) return;
            int start = SkipTrivia(list[index + 1].Pos, comments: true);
            Insert(start, node, new(Suffix: TokenFacts.Text(separator.Kind) + Text[separator.End..start]));
            return;
        }
        int afterStart = Start(after), afterLine = LineStart(afterStart);
        bool multiline = false;
        var separatorKind = K.CommaToken;
        if (list.Count != 1)
        {
            var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(File, after.Pos, cancellation: cancellation);
            if (preceding is not null && Separator(after, preceding)) separatorKind = preceding.Kind;
            multiline = LineStart(Start(list[index - 1])) != afterLine;
        }
        if (HasCommentsBeforeLineBreak(after.End) || Line(list.Pos) != Line(list.End)) multiline = true;
        var separatorText = TokenFacts.Text(separatorKind);
        if (!multiline) { Insert(after.End, node, new(separatorText + " "u8)); return; }
        var separatorNode = Factory.NewToken(separatorKind);
        separatorNode.Pos = after.End; separatorNode.End = after.End + separatorText.Length; separatorNode.Parent = after.Parent;
        Insert(after.End, separatorNode);
        int indentation = 0;
        for (int i = afterLine; i < afterStart;)
        {
            int point = Wtf8.Decode(Text.Span[i..], out int width);
            if (!TokenFacts.IsWhiteSpace(point)) break;
            indentation += point == '\t' ? Math.Max(1, settings.TabSize) - indentation % Math.Max(1, settings.TabSize) : 1;
            i += width;
        }
        int position = SkipTrivia(after.End, lineBreak: true);
        while (position > after.End && TokenFacts.IsLineBreak(Text[position - 1])) position--;
        Insert(position, node, new(NewLine, Indentation: indentation));
    }

    private bool HasCommentsBeforeLineBreak(int position)
    {
        while (position < Text.Length)
        {
            int point = Wtf8.Decode(Text.Span[position..], out int width);
            if (!TokenFacts.IsWhiteSpace(point) || TokenFacts.IsLineBreak(point)) return point == '/';
            position += width;
        }
        return false;
    }

    private static bool Separator(SyntaxNode node, SyntaxNode token) => node.Parent is not null && (token.Kind == K.CommaToken
        || token.Kind == K.SemicolonToken && node.Parent is ObjectLiteralExpressionNode);
}
