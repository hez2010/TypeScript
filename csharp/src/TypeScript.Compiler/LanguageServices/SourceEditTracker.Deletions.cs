using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class SourceEditTracker
{
    private readonly Dictionary<SyntaxNode, NodeList> deletedListNodes = [];

    internal async ValueTask DeleteInListAsync(SyntaxNode node, NodeList list)
    {
        int index = list.ToList().IndexOf(node);
        if (index < 0) throw new ArgumentException("Node is not in the list", nameof(node));
        if (list.Count == 1) { ReplaceText(node.Pos, SkipTrivia(node.End, lineBreak: true), default); return; }
        if (!deletedListNodes.TryAdd(node, list)) throw new InvalidOperationException("Deleting a node twice");
        int start = SkipTrivia(node.Pos, comments: true), end = AdjustedEnd(node);
        if (index < list.Count - 1)
        {
            var next = list[index + 1];
            end = SkipTrivia(next.Pos, comments: true);
            if (index > 0 && Line(SkipTrivia(node.End, lineBreak: true)) != Line(end))
            {
                var token = await SyntaxNavigation.FindPrecedingTokenAsync(File, Start(next), cancellation: cancellation);
                var previous = await SyntaxNavigation.FindPrecedingTokenAsync(File, Start(node), cancellation: cancellation);
                if (token is not null && previous is not null && Separator(node, token) && Separator(list[index - 1], previous))
                {
                    int position = SkipTrivia(token.End, comments: true, lineBreak: true);
                    if (Line(Start(previous)) == Line(Start(token)))
                        end = position > 0 && TokenFacts.IsLineBreak(Text[position - 1]) ? position - 1 : position;
                    else if (position < Text.Length && TokenFacts.IsLineBreak(Text[position])) end = position;
                }
            }
        }
        ReplaceText(start, end, default);
    }

    private void FinishListDeletions()
    {
        foreach (var (node, list) in deletedListNodes)
        {
            if (node != list[^1]) continue;
            for (int i = list.Count - 2; i >= 0; i--)
                if (!deletedListNodes.ContainsKey(list[i]))
                { ReplaceText(list[i].End, SkipTrivia(list[i + 1].Pos, comments: true), default); break; }
        }
        deletedListNodes.Clear();
    }
}
