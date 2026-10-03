using System.Runtime.CompilerServices;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public static partial class SourceFormatter
{
    private sealed partial class FormatWorker(SourceFileNode file, FormatCodeSettings options, FormatRequestKind kind, FormattingRange range,
        SyntaxNode enclosing, int initialIndentation, int initialDelta, CancellationToken cancellation, bool ignoreErrors = false)
    {
        private readonly FormattingContext context = new(file, kind, options, cancellation);
        private readonly List<SourceTextChange> edits = [];
        private readonly SmartIndenter.Indentation indentation = new(file, options, cancellation);
        private readonly FormattingRange[] errors = ignoreErrors ? [] : file.ParseDiagnostics
            .Where(d => range.Overlaps(d.Start, d.Start + d.Length)).OrderBy(d => d.Start).Select(d => new FormattingRange(d.Start, d.Start + d.Length)).ToArray();
        private int errorIndex;
        private FormattingScanner scanner = null!;
        private FormattingRange previousRange;
        private int previousTriviaEnd, previousStartLine;
        private SyntaxNode previousParent = file, childContext = file;
        private int lastIndentedLine = -1, indentationOnLastLine = -1;
        private int Line(int position) => file.Source.GetLineAndCharacter(position).Line;
        private int Start(SyntaxNode node) => SmartIndenter.Start(node, file);
        private int CurrentIndent(int position) => SmartIndenter.FirstNonWhitespaceColumn(LineStart(file, position), position, file, options);

        internal async ValueTask<SourceTextChange[]> ExecuteAsync(FormattingScanner scan)
        {
            scanner = scan; scanner.Advance();
            if (scanner.OnToken)
                await ProcessNodeAsync(enclosing, enclosing, Line(Start(enclosing)), Line(UndecoratedStart(enclosing, file)), initialIndentation, initialDelta).ConfigureAwait(false);
            var remaining = scanner.LeadingTrivia;
            if (remaining.Count != 0)
            {
                int indent = initialIndentation + (SmartIndenter.NodeWillIndentChild(options, enclosing, null, file, false) ? options.IndentSize : 0);
                await IndentTriviaAsync(remaining, indent, true, async item =>
                {
                    await ProcessRangeAsync(item, Line(item.Start), enclosing, enclosing, null).ConfigureAwait(false);
                    InsertIndentation(item.Start, indent, false);
                }).ConfigureAwait(false);
                if (options.TrimTrailingWhitespace == true) TrimRemaining(remaining);
            }
            if (previousRange != default && scanner.TokenStart >= range.End)
            {
                var token = scanner.OnEof ? scanner.EofRange : scanner.OnToken ? scanner.Read(enclosing).Token : default;
                if (token.Start == previousTriviaEnd)
                {
                    var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(file, token.End, cancellation: cancellation).ConfigureAwait(false);
                    var parent = preceding?.Parent ?? previousParent;
                    await ProcessPairAsync(token, Line(token.Start), parent, previousRange, previousStartLine, previousParent, parent, null).ConfigureAwait(false);
                }
            }
            return edits.ToArray();
        }

        private bool ContainsError(FormattingRange item)
        {
            while (errorIndex < errors.Length)
            {
                var error = errors[errorIndex];
                if (item.End <= error.Start) return false;
                if (item.Overlaps(error.Start, error.End)) return true;
                errorIndex++;
            }
            return false;
        }

        private async ValueTask ProcessNodeAsync(SyntaxNode node, SyntaxNode nodeContext, int nodeLine, int undecoratedLine, int indent, int delta)
        {
            await Task.CompletedTask.ConfigureAwait(RuntimeHelpers.TryEnsureSufficientExecutionStack() ? ConfigureAwaitOptions.None : ConfigureAwaitOptions.ForceYielding);
            cancellation.ThrowIfCancellationRequested();
            if (!range.Overlaps(Start(node), node.End)) return;
            var dynamicIndent = new DynamicIndentation(node, nodeLine, indent, delta, options, file);
            childContext = nodeContext;
            var children = new List<SyntaxChild>(); node.GetChildGroups(children);
            foreach (var child in children)
            {
                if (child.Node is { } childNode)
                    await ProcessChildAsync(node, childNode, -1, dynamicIndent, undecoratedLine, false, false).ConfigureAwait(false);
                else if (child.List is { } list)
                {
                    if (list == node.ModifierList)
                    {
                        foreach (var modifier in list)
                            await ProcessChildAsync(node, modifier, -1, dynamicIndent, undecoratedLine, false, false).ConfigureAwait(false);
                    }
                    else await ProcessListAsync(node, list, dynamicIndent, nodeLine).ConfigureAwait(false);
                }
            }
            while (scanner.OnToken && scanner.TokenStart < range.End)
            {
                var info = scanner.Read(node);
                if (info.Token.End > Math.Min(node.End, range.End)) break;
                await ConsumeAsync(info, node, dynamicIndent, node, false).ConfigureAwait(false);
            }
        }

        private async ValueTask<int> ProcessChildAsync(SyntaxNode node, SyntaxNode child, int inheritedIndent, DynamicIndentation dynamicIndent,
            int parentLine, bool listItem, bool firstListItem)
        {
            if (child.Pos == child.End && child.Kind != K.EndOfFile || (child.Flags & NodeFlags.Reparsed) != 0) return inheritedIndent;
            int childStart = Start(child), childLine = Line(childStart), undecoratedLine = Line(UndecoratedStart(child, file));
            bool errorMember = (child.Flags & NodeFlags.ThisNodeHasError) != 0 && IsMemberListElement(node, child);
            int childIndent = -1;
            if (!errorMember && listItem && range.Contains(node.Pos, node.End))
            {
                childIndent = ComputeListItemIndent(childStart, child.End, parentLine, inheritedIndent);
                if (childIndent != -1) inheritedIndent = childIndent;
            }
            if (!range.Overlaps(child.Pos, child.End))
            {
                if (child.End < range.Start) scanner.SkipTo(child.End);
                return inheritedIndent;
            }
            if (child.Pos == child.End) return inheritedIndent;
            while (scanner.OnToken && scanner.TokenStart < range.End)
            {
                var info = scanner.Read(node);
                if (info.Token.End > range.End) return inheritedIndent;
                if (info.Token.End > childStart)
                {
                    if (info.Token.Start > childStart) scanner.SkipTo(child.Pos);
                    break;
                }
                await ConsumeAsync(info, node, dynamicIndent, node, false).ConfigureAwait(false);
            }
            if (!scanner.OnToken || scanner.TokenStart >= range.End) return inheritedIndent;
            if (child.Kind <= K.LastToken)
            {
                var info = scanner.Read(child);
                if (child.Kind != K.JsxText)
                {
                    await ConsumeAsync(info, node, dynamicIndent, child, false).ConfigureAwait(false);
                    return inheritedIndent;
                }
            }
            int effectiveParentLine = child.Kind == K.Decorator ? childLine : parentLine;
            var (indent, delta) = errorMember ? (CurrentIndent(childStart), 0)
                : await ComputeIndentAsync(child, childLine, childIndent, node, dynamicIndent, effectiveParentLine).ConfigureAwait(false);
            await ProcessNodeAsync(child, childContext, childLine, undecoratedLine, indent, delta).ConfigureAwait(false);
            childContext = node;
            if (firstListItem && node.Kind == K.ArrayLiteralExpression && inheritedIndent == -1) inheritedIndent = indent;
            return inheritedIndent;
        }

        private async ValueTask ProcessListAsync(SyntaxNode node, NodeList list, DynamicIndentation parentIndent, int parentLine)
        {
            K open = OpenToken(node, list);
            var dynamicIndent = parentIndent;
            int startLine = parentLine;
            if (!range.Overlaps(list.Pos, list.End))
            {
                if (list.End < range.Start && (list.Count == 0 || (list[0].Flags & NodeFlags.Reparsed) == 0)) scanner.SkipTo(list.End);
                return;
            }
            if (open != K.Unknown)
            {
                while (scanner.OnToken && scanner.TokenStart < range.End)
                {
                    var info = scanner.Read(node);
                    if (info.Token.End > list.Pos) break;
                    if (info.Token.Kind == open)
                    {
                        startLine = Line(info.Token.Start);
                        await ConsumeAsync(info, node, parentIndent, node, false).ConfigureAwait(false);
                        int indent = indentationOnLastLine != -1 ? indentationOnLastLine : CurrentIndent(info.Token.Start);
                        dynamicIndent = new(node, parentLine, indent, options.IndentSize, options, file);
                    }
                    else await ConsumeAsync(info, node, parentIndent, node, false).ConfigureAwait(false);
                }
            }
            int inherited = -1;
            for (int i = 0; i < list.Count; i++)
                inherited = await ProcessChildAsync(node, list[i], inherited, dynamicIndent, startLine, true, i == 0).ConfigureAwait(false);
            K close = CloseToken(open);
            if (close != K.Unknown && scanner.OnToken && scanner.TokenStart < range.End)
            {
                var info = scanner.Read(node);
                if (info.Token.Kind == K.CommaToken)
                {
                    await ConsumeAsync(info, node, dynamicIndent, node, false).ConfigureAwait(false);
                    if (!scanner.OnToken) return;
                    info = scanner.Read(node);
                }
                if (info.Token.Kind == close && node.Pos <= info.Token.Start && info.Token.End <= node.End)
                    await ConsumeAsync(info, node, dynamicIndent, node, true).ConfigureAwait(false);
            }
        }

        private async ValueTask<(int Indent, int Delta)> ComputeIndentAsync(SyntaxNode node, int line, int inherited, SyntaxNode parent,
            DynamicIndentation parentIndent, int parentLine)
        {
            int delta = SmartIndenter.ShouldIndentChild(options, node, null, null) ? options.IndentSize : 0;
            if (parentLine == line)
                return (line == lastIndentedLine ? indentationOnLastLine : parentIndent.Indent, Math.Min(options.IndentSize, parentIndent.DeltaFor(node) + delta));
            if (inherited != -1) return (inherited, delta);
            if (node.Kind == K.OpenParenToken && line == lastIndentedLine) return (indentationOnLastLine, parentIndent.DeltaFor(node));
            if (await indentation.StartsWithElseAsync(parent, node, line).ConfigureAwait(false) || UnindentedBranch(parent, node, line) || SameLineArgument(parent, node, line))
                return (parentIndent.Indent, delta);
            return (parentIndent.Indent == -1 ? -1 : parentIndent.Indent + parentIndent.DeltaFor(node), delta);
        }

        private int ComputeListItemIndent(int start, int end, int parentLine, int inherited)
        {
            if (range.Overlaps(start, end) || range.Contains(start, end)) return inherited;
            int column = CurrentIndent(start);
            return Line(start) != parentLine || start == column ? Math.Max(options.BaseIndentSize, column) : -1;
        }

        private bool UnindentedBranch(SyntaxNode parent, SyntaxNode child, int line)
        {
            if (parent is not ConditionalExpressionNode { Condition: { } condition, WhenTrue: { } whenTrue, WhenFalse: { } whenFalse }) return false;
            if (child == whenTrue) return line == Line(condition.End);
            return child == whenFalse && Line(condition.End) == Line(Start(whenTrue)) && Line(whenTrue.End) == line;
        }
        private bool SameLineArgument(SyntaxNode parent, SyntaxNode child, int line)
        {
            var args = parent switch { CallExpressionNode n => n.Arguments, NewExpressionNode n => n.Arguments, _ => null };
            int index = args?.IndexOf(child) ?? -1;
            return index > 0 && Line(args![index - 1].End) == line;
        }
    }
}
