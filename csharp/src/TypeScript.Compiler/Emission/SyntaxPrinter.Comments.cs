using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private int LineOf(int position) => sourceFile is not null && position >= 0 && position <= sourceFile.Source.Length
        ? sourceFile.Source.GetLineAndCharacter(position).Line : -1;

    private bool ShouldWrite(SourceCommentRange comment) => !options.OnlyPrintJSDocStyle
        || sourceFile is not null && comment.End - comment.Pos > 4
            && (sourceFile.Source.Text.Span[comment.Pos..].StartsWith("/**"u8)
                || sourceFile.Source.Text.Span[comment.Pos..].StartsWith("/*!"u8));

    private bool IsTripleSlash(SourceCommentRange comment)
    {
        if (sourceFile is null || comment.Kind != K.SingleLineCommentTrivia)
            return false;
        var text = sourceFile.Source.Text[comment.Pos..comment.End];
        if (!text.Span.StartsWith("///"u8))
            return false;
        text = text[3..].Trim();
        return text.Span.StartsWith("<reference"u8) || text.Span.StartsWith("<amd-dependency"u8)
            || text.Span.StartsWith("<amd-module"u8);
    }

    private bool LeadingComments(int position, bool elided = false)
    {
        // A source with no comment markers cannot yield a source comment, so the range scan (and the
        // empty backing list it allocates) is skipped outright for generated or comment-free files.
        if (!sourceHasComments || commentsDisabled || sourceFile is null || position < 0 || position == containerPos || elided && position != 0)
            return false;
        bool omitReferences = !elided && position == 0 && sourceFile.IsDeclarationFile;
        if (detachedComments.TryPeek(out var detached) && detached.Pos == position)
            position = detachedComments.Pop().End;
        var comments = CommentRanges(sourceFile.Source.Text, position, trailing: false)
            .Where(c => ShouldWrite(c) && (!elided || IsTripleSlash(c)) && (!omitReferences || !IsTripleSlash(c))).ToArray();
        if (comments.Length != 0 && position != comments[0].Pos && LineOf(position) != LineOf(comments[0].Pos))
            writer.WriteLine();
        return Comments(comments, before: false);
    }

    private void TrailingComments(int position, bool prefixSpace = true)
    {
        if (!sourceHasComments || commentsDisabled || sourceFile is null || position < 0 || containerEnd != -1
            && (position == containerEnd || position == declarationListContainerEnd))
            return;
        var comments = CommentRanges(sourceFile.Source.Text, position, trailing: true).Where(ShouldWrite).ToArray();
        Comments(comments, before: prefixSpace, none: !prefixSpace);
    }

    private void ListComments(int position)
    {
        if (!sourceHasComments || commentsDisabled || sourceFile is null || position < 0 || containerEnd != -1
            && (position == containerEnd || position == declarationListContainerEnd))
            return;
        Comments(CommentRanges(sourceFile.Source.Text, position, trailing: true), before: false);
    }

    private void DetachedComments(int position)
    {
        if (!sourceHasComments || sourceFile is null || position < 0)
            return;
        var leading = CommentRanges(sourceFile.Source.Text, position, trailing: false);
        if (commentsDisabled)
            leading = position == 0 ? leading.Where(c => sourceFile.Source.Text.Span[c.Pos..].StartsWith("/*!"u8)).ToArray() : [];
        var comments = new List<SourceCommentRange>();
        foreach (var comment in leading)
        {
            if (comments.Count > 0 && LineOf(comment.Pos) >= LineOf(comments[^1].End) + 2)
                break;
            comments.Add(comment);
        }
        if (comments.Count != 0 && LineOf(SkipTrivia(position)) >= LineOf(comments[^1].End) + 2)
        {
            if (LineOf(position) != LineOf(comments[0].Pos))
                writer.WriteLine();
            Comments(comments.Where(ShouldWrite).ToArray(), before: false);
            detachedComments.Push((position, comments[^1].End));
        }
    }

    private bool Comments(IReadOnlyList<SourceCommentRange> comments, bool before, bool none = false)
    {
        if (comments.Count == 0)
            return false;
        if (before && !writer.AtLineStart && !writer.HasTrailingWhitespace)
            writer.Write(" "u8);
        bool separator = false;
        foreach (var comment in comments)
        {
            if (separator)
                writer.Write(" "u8);
            MapPosition(comment.Pos);
            WriteComment(sourceFile!.Source, comment);
            MapPosition(comment.End);
            if (comment.Kind == K.SingleLineCommentTrivia || comment.HasTrailingNewLine && !none)
            {
                writer.WriteLine();
                separator = false;
            }
            else
                separator = !none;
        }
        if (separator && !before)
            writer.Write(" "u8);
        return true;
    }

    private void SyntheticComment(SyntheticComment comment, bool leading)
    {
        if (leading && (comment.HasLeadingNewLine || comment.Kind == K.SingleLineCommentTrivia))
            writer.WriteLine();
        else if (!leading && !writer.AtLineStart)
            writer.Write(" "u8);
        var text = comment.Kind == K.MultiLineCommentTrivia
            ? Utf8String.Concat("/*"u8, comment.Text, "*/"u8) : Utf8String.Concat("//"u8, comment.Text);
        WriteComment(new SourceText(text), new(comment.Kind, 0, text.Length, comment.HasTrailingNewLine));
        if (comment.HasTrailingNewLine || leading && comment.Kind == K.SingleLineCommentTrivia)
            writer.WriteLine();
        else if (leading)
            writer.Write(" "u8);
    }

    private void WriteComment(SourceText source, SourceCommentRange comment)
    {
        if (comment.Kind == K.SingleLineCommentTrivia)
        {
            writer.WriteComment(source.Text[comment.Pos..comment.End]);
            return;
        }
        int firstLine = source.GetLineAndCharacter(comment.Pos).Line;
        int line = firstLine, position = comment.Pos;
        int firstIndent = Indent(source.Text, source.LineStarts[firstLine], comment.Pos);
        while (position < comment.End)
        {
            int nextLine = line + 1 < source.LineStarts.Length ? source.LineStarts[line + 1] : source.Length + 1;
            int end = Math.Min(comment.End, nextLine);
            var text = source.Text[position..end].Trim();
            if (position != comment.Pos)
            {
                int spaces = Math.Max(0, writer.Indentation * 4 - firstIndent + Indent(source.Text, position, Math.Min(nextLine, source.Length)));
                writer.RawWrite(new Utf8StringBuilder().Append((byte)' ', spaces).ToUtf8String());
                // The remainder is already indented, including a zero-column line.
                writer.RawWrite(text);
            }
            else
                writer.WriteComment(text);
            if (text.Length == 0 || end != comment.End)
                writer.WriteLine(force: text.Length == 0);
            position = nextLine;
            line++;
        }
    }

    private static int Indent(Utf8String text, int start, int end)
    {
        int column = 0;
        for (int position = start; position < end;)
        {
            int point = Wtf8.Decode(text.Span[position..], out int width);
            if (!TokenFacts.IsWhiteSpace(point))
                break;
            column = point == '\t' ? column + 4 - column % 4 : column + 1;
            position += width;
        }
        return column;
    }

    internal static IReadOnlyList<SourceCommentRange> CommentRanges(Utf8String text, int position, bool trailing)
    {
        var comments = new List<SourceCommentRange>();
        bool collecting = trailing || position == 0;
        SourceCommentRange? pending = null;
        if (position == 0 && text.Span.StartsWith("#!"u8))
        {
            while (position < text.Length && text[position] is not ((byte)'\r' or (byte)'\n'))
                position++;
        }
        while (position >= 0 && position < text.Length)
        {
            int point = Wtf8.Decode(text.Span[position..], out int width);
            if (point is '\r' or '\n')
            {
                if (trailing)
                    break;
                if (point == '\r' && position + 1 < text.Length && text[position + 1] == '\n')
                    position++;
                position++;
                collecting = true;
                if (pending is { } previous)
                    pending = previous with { HasTrailingNewLine = true };
                continue;
            }
            if (TokenFacts.IsWhiteSpace(point) || point is 0x2028 or 0x2029)
            {
                if (TokenFacts.IsLineBreak(point) && pending is { } previous)
                    pending = previous with { HasTrailingNewLine = true };
                position += width;
                continue;
            }
            if (point != '/' || position + 1 >= text.Length || text[position + 1] is not ((byte)'/' or (byte)'*'))
                break;
            bool singleLine = text[position + 1] == '/';
            int start = position;
            position += 2;
            bool newLine = false;
            if (singleLine)
            {
                while (position < text.Length)
                {
                    point = Wtf8.Decode(text.Span[position..], out width);
                    if (TokenFacts.IsLineBreak(point))
                    {
                        newLine = true;
                        break;
                    }
                    position += width;
                }
            }
            else
            {
                int end = text.Span[position..].IndexOf("*/"u8);
                position = end < 0 ? text.Length : position + end + 2;
            }
            if (collecting)
            {
                if (pending is { } previous)
                    comments.Add(previous);
                pending = new(singleLine ? K.SingleLineCommentTrivia : K.MultiLineCommentTrivia, start, position, newLine);
            }
        }
        if (pending is { } last)
            comments.Add(last);
        return comments;
    }
}
