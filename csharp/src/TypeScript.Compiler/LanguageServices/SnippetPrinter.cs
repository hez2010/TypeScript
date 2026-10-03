using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.LanguageServices;

internal sealed class SnippetPrinter(FormatCodeSettings settings, int targetYear, EmitContext? emitContext = null)
{
    internal EmitContext Context { get; } = emitContext ?? new();
    internal NodeFactory Factory => Context.Factory;

    internal BlockNode Body(bool snippet)
    {
        var statement = Factory.NewEmptyStatement();
        if (snippet) Context.SetSnippetTabStop(statement, 0);
        return Factory.NewBlock(new(snippet ? [statement] : []), true);
    }

    internal async ValueTask<Utf8String> PrintAsync(SyntaxNode node, SourceFileNode source, CancellationToken cancellation)
    {
        var (text, positioned) = SyntaxPrinter.PrintAndPositionNode(node, newLine: settings.NewLineCharacter, context: Context,
            cancellation: cancellation, options: new() { RemoveComments = true, NewLine = settings.NewLineCharacter, TargetYear = targetYear });
        var file = new SourceFileNode
        {
            FileName = source.FileName, ScriptKind = source.ScriptKind, Source = new(text), Pos = 0, End = text.Length,
            Statements = new([positioned], positioned.Pos, positioned.End),
            EndOfFileToken = new TokenNode(SyntaxKind.EndOfFile) { Pos = text.Length, End = text.Length },
        };
        file.SetParents();
        var changes = (await SourceFormatter.FormatNodeAsync(positioned, file, 0, 0, settings, cancellation)).ToList();
        HashSet<int> tabStops = [];
        Stack<(SyntaxNode Original, SyntaxNode Printed)> pending = []; pending.Push((node, positioned));
        while (pending.TryPop(out var pair))
        {
            cancellation.ThrowIfCancellationRequested();
            if (Context.GetSnippetTabStop(pair.Original) is not null)
                for (int i = pair.Printed.Pos; i < pair.Printed.End; i++) if (text[i] == '$') tabStops.Add(i);
            for (int i = 0; i < pair.Original.ChildCount; i++) pending.Push((pair.Original.GetChild(i), pair.Printed.GetChild(i)));
        }
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '$' && !tabStops.Contains(i)) changes.Add(new(i, i, "\\"u8));
        var result = new Utf8StringBuilder();
        int end = 0;
        foreach (var change in changes.OrderBy(change => change.Start).ThenBy(change => change.End))
        {
            result.Append(text.Span[end..change.Start]); result.Append(change.NewText); end = change.End;
        }
        result.Append(text.Span[end..]);
        return result.ToUtf8String();
    }
}
