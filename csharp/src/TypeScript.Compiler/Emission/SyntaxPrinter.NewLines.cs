using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private static bool Positioned(SyntaxNode node) => node.Pos >= 0 && node.End >= 0 && (node.Flags & NodeFlags.Synthesized) == 0;
    private int TriviaEnd(int pos, bool comments) => sourceScanner?.SkipTriviaAt(pos, stopAtComments: comments) ?? pos;
    private int EffectiveLines(Func<bool, int> count)
    {
        int lines = count(true);
        return lines == 0 ? count(false) : lines;
    }
    private int LeadingLines(SyntaxNode parent, SyntaxNode? first, bool multiLine = false)
    {
        if (first is null) return sourceFile is not null && SingleLine(parent) ? 0 : 1;
        if (first is JsxTextNode) return 0;
        if (sourceFile is not null && parent.Pos >= 0 && Positioned(first) && first.Parent is null)
            return EffectiveLines(comments =>
            {
                int start = TriviaEnd(first.Pos, comments), previous = start;
                while (previous >= parent.Pos && previous < sourceFile.Source.Length
                    && (TokenFacts.IsWhiteSpace(sourceFile.Source.Text[previous]) || TokenFacts.IsLineBreak(sourceFile.Source.Text[previous]))) previous--;
                return LineOf(start) - LineOf(previous >= 0 ? previous : parent.Pos);
            });
        return (context.GetFlags(first) & EmitFlags.StartOnNewLine) != 0 || multiLine ? 1 : 0;
    }
    private int SeparatingLines(SyntaxNode? previous, SyntaxNode? next, bool preferNewLine = false, bool multiLine = false)
    {
        if (previous is null || next is null || next is JsxTextNode) return 0;
        if (sourceFile is not null && Positioned(previous) && Positioned(next))
            return next.Pos >= previous.End && context.MostOriginal(previous).Parent is { } parent
                && parent == context.MostOriginal(next).Parent
                ? EffectiveLines(comments => LineOf(TriviaEnd(next.Pos, comments)) - LineOf(previous.End)) : preferNewLine ? 1 : 0;
        return ((context.GetFlags(previous) | context.GetFlags(next)) & EmitFlags.StartOnNewLine) != 0 || preferNewLine || multiLine ? 1 : 0;
    }
    private int ClosingLines(SyntaxNode parent, SyntaxNode? last, int listEnd = -1, bool multiLine = false)
    {
        if (last is null) return sourceFile is not null && SingleLine(parent) ? 0 : 1;
        if (sourceFile is not null && parent.Pos >= 0 && Positioned(last) && (last.Parent is null || last.Parent == parent))
        {
            int end = Math.Max(last.End, listEnd);
            return EffectiveLines(comments => LineOf(Math.Min(parent.End, TriviaEnd(end, comments))) - LineOf(end));
        }
        return multiLine ? 1 : 0;
    }
    private Part ListBoundary(SyntaxNode parent, SyntaxNode? previous, SyntaxNode next, bool multiLine, bool space = false)
    {
        int count = previous is null ? LeadingLines(parent, next, multiLine) : SeparatingLines(previous, next, multiLine: multiLine);
        return count > 0 ? new(NewLine: true) : space ? T(" "u8) : default;
    }
    private Part LineOrSpace(SyntaxNode parent, SyntaxNode previous, SyntaxNode next)
    {
        if ((context.GetFlags(parent) & EmitFlags.SingleLine) != 0) return T(" "u8);
        if (!options.PreserveSourceNewlines) return new(NewLine: true);
        int lines = sourceFile is not null && Positioned(parent) && Positioned(previous) && Positioned(next)
            ? EffectiveLines(comments => LineOf(TriviaEnd(next.Pos, comments)) - LineOf(previous.End)) : 0;
        return lines > 0 ? new(NewLine: true) : T(" "u8);
    }
}
