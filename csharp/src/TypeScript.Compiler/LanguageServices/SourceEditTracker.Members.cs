using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal sealed partial class SourceEditTracker
{
    private readonly Dictionary<SyntaxNode, NodeList> insertedMembers = [];

    internal ValueTask InsertMemberAtStartAsync(SyntaxNode owner, NodeList members, SyntaxNode member)
    {
        int indentation = -1;
        var previous = owner;
        foreach (var existing in members)
        {
            if (Line(Start(previous)) == Line(Start(existing))) { indentation = -1; break; }
            int column = IndentationColumn(Start(existing));
            if (column < 0 || indentation >= 0 && indentation != column) { indentation = -1; break; }
            indentation = column; previous = existing;
        }
        if (indentation < 0) indentation = Math.Max(0, IndentationColumn(Start(owner))) + (settings.IndentSize > 0 ? settings.IndentSize : 4);
        insertedMembers.TryAdd(owner, members);
        Insert(members.Pos, member, new(NewLine, Indentation: indentation));
        return ValueTask.CompletedTask;
    }

    private int IndentationColumn(int position)
    {
        int column = 0, tabSize = settings.TabSize > 0 ? settings.TabSize : 4;
        for (int i = LineStart(position); i < position;)
        {
            int point = Wtf8.Decode(Text.Span[i..], out int width);
            if (!TokenFacts.IsWhiteSpace(point)) return -1;
            column += point == '\t' ? tabSize - column % tabSize : 1; i += width;
        }
        return column;
    }

    private async ValueTask FinishMemberInsertionsAsync()
    {
        foreach (var (owner, members) in insertedMembers)
        {
            var open = await SyntaxNavigation.FindChildOfKindAsync(owner, K.OpenBraceToken, File, cancellation);
            var close = await SyntaxNavigation.FindChildOfKindAsync(owner, K.CloseBraceToken, File, cancellation);
            if (open is null || close is null || Line(open.End) != Line(close.End)) continue;
            if (members.Count == 0 && open.End != close.End - 1) ReplaceText(open.End, close.End - 1, default);
            InsertText(close.End - 1, NewLine);
        }
        insertedMembers.Clear();
    }
}
