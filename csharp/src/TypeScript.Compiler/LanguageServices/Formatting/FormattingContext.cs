using TypeScript.Compiler.Ast;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public enum FormatRequestKind { Document, Selection, OnEnter, OnSemicolon, OnOpeningCurlyBrace, OnClosingCurlyBrace }

internal sealed class FormattingContext(SourceFileNode file, FormatRequestKind requestKind, FormatCodeSettings options, CancellationToken cancellation)
{
    internal SourceFileNode File { get; } = file;
    internal FormatRequestKind RequestKind { get; } = requestKind;
    internal FormatCodeSettings Options { get; } = options;
    internal CancellationToken Cancellation { get; } = cancellation;
    internal FormattingRange Current { get; private set; }
    internal FormattingRange Next { get; private set; }
    internal SyntaxNode CurrentParent { get; private set; } = file;
    internal SyntaxNode NextParent { get; private set; } = file;
    internal SyntaxNode Node { get; private set; } = file;
    private bool? nodeOneLine, nextOneLine, tokensOneLine, blockOneLine, nextBlockOneLine;

    internal void Update(FormattingRange current, SyntaxNode currentParent, FormattingRange next, SyntaxNode nextParent, SyntaxNode commonParent)
    {
        Current = current; CurrentParent = currentParent; Next = next; NextParent = nextParent; Node = commonParent;
        nodeOneLine = nextOneLine = tokensOneLine = blockOneLine = nextBlockOneLine = null;
    }

    internal bool NodeOnOneLine => nodeOneLine ??= SmartIndenter.SameLine(SmartIndenter.Start(Node, File), Node.End, File);
    internal bool NextOnOneLine => nextOneLine ??= SmartIndenter.SameLine(SmartIndenter.Start(NextParent, File), NextParent.End, File);
    internal bool TokensOnOneLine => tokensOneLine ??= SmartIndenter.SameLine(Current.Start, Next.End, File);
    internal async ValueTask<bool> BlockOnOneLineAsync() => blockOneLine ??= await BlockOnOneLineAsync(Node).ConfigureAwait(false);
    internal async ValueTask<bool> NextBlockOnOneLineAsync() => nextBlockOneLine ??= await BlockOnOneLineAsync(NextParent).ConfigureAwait(false);
    private async ValueTask<bool> BlockOnOneLineAsync(SyntaxNode node)
    {
        var open = await SyntaxNavigation.FindChildOfKindAsync(node, K.OpenBraceToken, File, Cancellation).ConfigureAwait(false);
        var close = await SyntaxNavigation.FindChildOfKindAsync(node, K.CloseBraceToken, File, Cancellation).ConfigureAwait(false);
        return open is not null && close is not null && SmartIndenter.SameLine(open.End, SmartIndenter.Start(close, File), File);
    }
}
