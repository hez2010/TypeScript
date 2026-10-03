using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SourceFormatter
{
    private enum LineAction { None, Added, Removed }

    private sealed partial class FormatWorker
    {
        private async ValueTask<LineAction> ProcessPairAsync(FormattingRange current, int currentLine, SyntaxNode currentParent,
            FormattingRange previous, int previousLine, SyntaxNode priorParent, SyntaxNode commonParent, DynamicIndentation? dynamicIndent)
        {
            context.Update(previous, priorParent, current, currentParent, commonParent);
            var rules = await FormattingRules.GetAsync(context).ConfigureAwait(false);
            bool trim = options.TrimTrailingWhitespace != false;
            LineAction action = LineAction.None;
            for (int i = rules.Count - 1; i >= 0; i--)
            {
                var rule = rules[i];
                action = ApplyRule(rule, previous, previousLine, current, currentLine);
                if (dynamicIndent is not null && action != LineAction.None && Start(currentParent) == current.Start)
                    dynamicIndent.Recompute(action == LineAction.Added, commonParent);
                trim = trim && (rule.Action & FormattingAction.DeleteSpace) == 0 && !rule.CanDeleteNewLines;
            }
            if (rules.Count == 0) trim &= current.Kind != K.EndOfFile;
            if (currentLine != previousLine && trim) TrimLines(previousLine, currentLine, previous);
            return action;
        }

        private LineAction ApplyRule(FormattingRules.Rule rule, FormattingRange previous, int previousLine, FormattingRange current, int currentLine)
        {
            bool laterLine = currentLine != previousLine;
            switch (rule.Action)
            {
                case FormattingAction.DeleteSpace:
                    if (previous.End != current.Start)
                    {
                        Delete(previous.End, current.Start - previous.End);
                        if (laterLine) return LineAction.Removed;
                    }
                    break;
                case FormattingAction.DeleteToken: Delete(previous.Start, previous.Length); break;
                case FormattingAction.InsertNewLine:
                    if (!rule.CanDeleteNewLines && laterLine) break;
                    if (currentLine - previousLine != 1)
                    {
                        Replace(previous.End, current.Start - previous.End, options.NewLineCharacter.Length == 0 ? "\n"u8 : options.NewLineCharacter);
                        if (!laterLine) return LineAction.Added;
                    }
                    break;
                case FormattingAction.InsertSpace:
                    if (!rule.CanDeleteNewLines && laterLine) break;
                    int distance = current.Start - previous.End;
                    if (distance != 1 || file.Source.Text[previous.End] != ' ')
                    {
                        Replace(previous.End, distance, " "u8);
                        if (laterLine) return LineAction.Removed;
                    }
                    break;
                case FormattingAction.InsertTrailingSemicolon: Replace(previous.End, 0, ";"u8); break;
            }
            return LineAction.None;
        }

        private async ValueTask<LineAction> ProcessRangeAsync(FormattingRange item, int line, SyntaxNode parent, SyntaxNode commonParent, DynamicIndentation? dynamicIndent)
        {
            LineAction action = LineAction.None;
            if (!ContainsError(item))
            {
                if (previousRange == default) TrimLines(Line(range.Start), line, default);
                else action = await ProcessPairAsync(item, line, parent, previousRange, previousStartLine, previousParent, commonParent, dynamicIndent).ConfigureAwait(false);
            }
            previousRange = item; previousTriviaEnd = item.End; previousParent = parent; previousStartLine = line;
            return action;
        }

        private async ValueTask ProcessTriviaAsync(IReadOnlyList<FormattingRange> trivia, SyntaxNode parent, SyntaxNode commonParent, DynamicIndentation dynamicIndent)
        {
            foreach (var item in trivia)
                if (IsComment(item.Kind) && range.Contains(item))
                    await ProcessRangeAsync(item, Line(item.Start), parent, commonParent, dynamicIndent).ConfigureAwait(false);
        }

        private void TrimRemaining(IReadOnlyList<FormattingRange> trivia)
        {
            int start = previousRange != default ? previousRange.End : range.Start;
            foreach (var item in trivia)
            {
                if (!IsComment(item.Kind)) continue;
                if (start < item.Start) TrimLines(Line(start), Line(item.Start - 1) + 1, previousRange);
                start = item.End + 1;
            }
            if (start < range.End) TrimLines(Line(start), Line(range.End) + 1, previousRange);
        }

        private void TrimLines(int first, int last, FormattingRange item)
        {
            for (int line = first; line < last; line++)
            {
                int start = file.Source.LineStarts[line], end = EndLine(file, line);
                if (item != default && (IsComment(item.Kind) || IsLiteral(item.Kind)) && item.Start <= end && item.End > end) continue;
                int position = end;
                while (position >= start)
                {
                    int ch = Wtf8.Decode(file.Source.Text.Span[position..], out int width);
                    if (width != 0 && !TokenFacts.IsWhiteSpace(ch)) break;
                    position--;
                }
                if (position != end) Delete(position + 1, end - position);
            }
        }

        private void InsertIndentation(int position, int indent, bool addedLine)
        {
            var text = IndentationText(indent, options);
            if (addedLine) { Replace(position, 0, text); return; }
            var (line, character) = file.Source.GetLineAndCharacter(position);
            int start = file.Source.LineStarts[line], column = 0;
            for (int i = 0; i < character; i++)
            {
                if (file.Source.Text[start + i] == '\t') { if (options.TabSize > 0) column += options.TabSize - column % options.TabSize; }
                else column++;
            }
            if (indent != column || start + text.Length > file.Source.Length || !text.Span.SequenceEqual(file.Source.Text.Span.Slice(start, text.Length)))
                Replace(start, character, text);
        }

        private async ValueTask<bool> IndentTriviaAsync(IReadOnlyList<FormattingRange> trivia, int commentIndent, bool indentNext,
            Func<FormattingRange, ValueTask> indentSingleLine)
        {
            foreach (var item in trivia)
            {
                switch (item.Kind)
                {
                    case K.MultiLineCommentTrivia:
                        if (range.Contains(item)) IndentMultilineComment(item, commentIndent, !indentNext);
                        indentNext = false; break;
                    case K.SingleLineCommentTrivia:
                        if (indentNext && range.Contains(item)) await indentSingleLine(item).ConfigureAwait(false);
                        indentNext = false; break;
                    case K.NewLineTrivia: indentNext = true; break;
                }
            }
            return indentNext;
        }

        private void IndentMultilineComment(FormattingRange comment, int indent, bool firstLineIndented)
        {
            int startLine = Line(comment.Start), endLine = Line(comment.End);
            if (startLine == endLine)
            {
                if (!firstLineIndented) InsertIndentation(comment.Start, indent, false);
                return;
            }
            int start = comment.Start;
            var parts = new List<FormattingRange>();
            for (int line = startLine; line < endLine; line++)
            {
                parts.Add(new(start, EndLine(file, line))); start = file.Source.LineStarts[line + 1];
            }
            parts.Add(new(start, comment.End));
            var (firstCharacter, firstColumn) = SmartIndenter.FirstNonWhitespace(file.Source.LineStarts[startLine], parts[0].Start, file, options);
            int startIndex = firstLineIndented ? 1 : 0, delta = indent - firstColumn;
            if (firstLineIndented) startLine++;
            for (int i = startIndex; i < parts.Count; i++, startLine++)
            {
                int lineStart = file.Source.LineStarts[startLine];
                var (character, column) = i == 0 ? (firstCharacter, firstColumn) : SmartIndenter.FirstNonWhitespace(parts[i].Start, parts[i].End, file, options);
                int newIndent = column + delta;
                if (newIndent > 0) Replace(lineStart, character, IndentationText(newIndent, options));
                else Delete(lineStart, character);
            }
        }

        private void Delete(int start, int length) { if (length != 0) edits.Add(new(start, start + length, Utf8String.Empty)); }
        private void Replace(int start, int length, Utf8String text) { if (length != 0 || text.Length != 0) edits.Add(new(start, start + length, text)); }

        private async ValueTask ConsumeAsync(FormattingToken info, SyntaxNode parent, DynamicIndentation dynamicIndent, SyntaxNode container, bool listEnd)
        {
            cancellation.ThrowIfCancellationRequested();
            bool lastNewLine = scanner.LastTriviaWasNewLine, indentToken = false;
            if (info.Leading.Length != 0) await ProcessTriviaAsync(info.Leading, parent, childContext, dynamicIndent).ConfigureAwait(false);
            LineAction action = LineAction.None;
            bool inRange = range.Contains(info.Token);
            int line = Line(info.Token.Start);
            if (inRange)
            {
                bool error = ContainsError(info.Token);
                var save = previousRange;
                action = await ProcessRangeAsync(info.Token, line, parent, childContext, dynamicIndent).ConfigureAwait(false);
                if (!error) indentToken = action == LineAction.None ? lastNewLine && (save == default || line != Line(save.End)) : action == LineAction.Added;
            }
            if (info.Trailing.Length != 0)
            {
                previousTriviaEnd = info.Trailing[^1].End;
                foreach (var item in info.Trailing)
                    if (IsComment(item.Kind) && !range.Contains(item)) { previousTriviaEnd = item.Start; break; }
                await ProcessTriviaAsync(info.Trailing, parent, childContext, dynamicIndent).ConfigureAwait(false);
            }
            if (indentToken)
            {
                int tokenIndent = inRange && !ContainsError(info.Token) ? dynamicIndent.ForToken(line, info.Token.Kind, container, listEnd) : -1;
                bool indentNext = true;
                if (info.Leading.Length != 0)
                {
                    int commentIndent = dynamicIndent.ForComment(info.Token.Kind, tokenIndent, container);
                    indentNext = await IndentTriviaAsync(info.Leading, commentIndent, indentNext, item =>
                    { InsertIndentation(item.Start, commentIndent, false); return ValueTask.CompletedTask; }).ConfigureAwait(false);
                }
                if (tokenIndent != -1 && indentNext)
                {
                    InsertIndentation(info.Token.Start, tokenIndent, action == LineAction.Added);
                    lastIndentedLine = line; indentationOnLastLine = tokenIndent;
                }
            }
            scanner.Advance(); childContext = parent;
        }
    }

    public static Utf8String IndentationText(int indent, FormatCodeSettings options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indent);
        int tabs = 0, spaces = indent;
        if (options.ConvertTabsToSpaces != true)
        {
            if (options.TabSize == 0) return Utf8String.Empty;
            tabs = (int)Math.Floor((double)indent / options.TabSize); spaces = indent - tabs * options.TabSize;
        }
        var bytes = new byte[tabs + Math.Max(spaces, 0)];
        bytes.AsSpan(0, tabs).Fill((byte)'\t'); bytes.AsSpan(tabs).Fill((byte)' ');
        return new(bytes);
    }

    private sealed class DynamicIndentation(SyntaxNode node, int nodeLine, int indentation, int delta, FormatCodeSettings options, SourceFileNode file)
    {
        internal int Indent { get; private set; } = indentation;
        private int delta = delta;
        internal int DeltaFor(SyntaxNode child) => SmartIndenter.NodeWillIndentChild(options, node, child, file, true) ? delta : 0;
        internal int ForComment(K kind, int tokenIndent, SyntaxNode container) => kind is K.CloseBraceToken or K.CloseBracketToken or K.CloseParenToken
            ? Indent + DeltaFor(container) : tokenIndent != -1 ? tokenIndent : Indent;
        internal int ForToken(int line, K kind, SyntaxNode container, bool suppressDelta) =>
            !suppressDelta && AddDelta(line, kind, container) ? Indent + DeltaFor(container) : Indent;
        internal void Recompute(bool addedLine, SyntaxNode parent)
        {
            if (!SmartIndenter.ShouldIndentChild(options, parent, node, file)) return;
            Indent += addedLine ? options.IndentSize : -options.IndentSize;
            delta = SmartIndenter.ShouldIndentChild(options, node, null, null) ? options.IndentSize : 0;
        }
        private bool AddDelta(int line, K kind, SyntaxNode container)
        {
            if (kind is K.OpenBraceToken or K.CloseBraceToken or K.CloseParenToken or K.ElseKeyword or K.WhileKeyword or K.AtToken) return false;
            if (kind is K.SlashToken or K.GreaterThanToken && container.Kind is K.JsxOpeningElement or K.JsxClosingElement or K.JsxSelfClosingElement) return false;
            if (kind is K.OpenBracketToken or K.CloseBracketToken && container.Kind != K.MappedType) return false;
            return nodeLine != line && !(HasDecorators(node) && kind == FirstNonDecorator(node));
        }
        private static K FirstNonDecorator(SyntaxNode node)
        {
            if (node.ModifierList is { } modifiers)
            {
                int index = 0;
                while (index < modifiers.Count && modifiers[index].Kind != K.Decorator) index++;
                for (; index < modifiers.Count; index++) if (modifiers[index].Kind != K.Decorator) return modifiers[index].Kind;
            }
            return node.Kind switch
            {
                K.ClassDeclaration => K.ClassKeyword, K.InterfaceDeclaration => K.InterfaceKeyword, K.FunctionDeclaration => K.FunctionKeyword,
                K.EnumDeclaration => K.EnumDeclaration, K.GetAccessor => K.GetKeyword, K.SetAccessor => K.SetKeyword,
                K.MethodDeclaration when ((MethodDeclarationNode)node).AsteriskToken is not null => K.AsteriskToken,
                K.MethodDeclaration or K.PropertyDeclaration or K.Parameter => node.DeclarationName?.Kind ?? K.Unknown, _ => K.Unknown,
            };
        }
    }
}
