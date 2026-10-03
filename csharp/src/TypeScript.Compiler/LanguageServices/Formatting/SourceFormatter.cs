using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

/// <summary>A source edit whose start and end are UTF-8 byte offsets.</summary>
public readonly record struct SourceTextChange(int Start, int End, Utf8String NewText);

/// <summary>Formats source ranges while retaining comments, token spelling, and syntax outside the requested range.</summary>
public static partial class SourceFormatter
{
    public static ValueTask<SourceTextChange[]> FormatDocumentAsync(SourceFileNode file, FormatCodeSettings? options = null, CancellationToken cancellation = default) =>
        FormatSpanAsync(file, 0, file.End, FormatRequestKind.Document, options, cancellation);

    public static ValueTask<SourceTextChange[]> FormatSelectionAsync(SourceFileNode file, int start, int end, FormatCodeSettings? options = null,
        CancellationToken cancellation = default) => FormatSpanAsync(file, LineStart(file, start), end, FormatRequestKind.Selection, options, cancellation);

    public static async ValueTask<SourceTextChange[]> FormatOnTypeAsync(SourceFileNode file, int position, Utf8String character,
        FormatCodeSettings? options = null, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, file.Source.Length);
        var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation).ConfigureAwait(false);
        if (await SyntaxNavigation.GetEnclosingCommentAsync(file, position, preceding, cancellation).ConfigureAwait(false) is not null) return [];
        if (character == "\n"u8)
        {
            int line = file.Source.GetLineAndCharacter(position).Line;
            if (line == 0) return [];
            int start = file.Source.LineStarts[line - 1], end = EndLine(file, line);
            while (end > start)
            {
                int ch = Wtf8.Decode(file.Source.Text.Span[end..], out int width);
                if (width != 0 && !TokenFacts.IsWhiteSpace(ch)) break;
                end--;
            }
            if (TokenFacts.IsLineBreak(Wtf8.Decode(file.Source.Text.Span[end..], out _))) end--;
            return await FormatSpanAsync(file, start, end + 1, FormatRequestKind.OnEnter, options, cancellation).ConfigureAwait(false);
        }
        K kind = character == ";"u8 ? K.SemicolonToken : character == "{"u8 ? K.OpenBraceToken : character == "}"u8 ? K.CloseBraceToken : K.Unknown;
        if (kind == K.Unknown) return [];
        var token = preceding;
        if (token is null || token.Kind != kind || token.End != position) return [];
        var node = Outermost(kind == K.OpenBraceToken ? token.Parent! : token);
        return await FormatSpanAsync(file, LineStart(file, SmartIndenter.Start(node, file)), kind == K.OpenBraceToken ? position : node.End,
            kind == K.SemicolonToken ? FormatRequestKind.OnSemicolon : kind == K.OpenBraceToken ? FormatRequestKind.OnOpeningCurlyBrace : FormatRequestKind.OnClosingCurlyBrace,
            options, cancellation).ConfigureAwait(false);
    }

    public static async ValueTask<SourceTextChange[]> FormatSpanAsync(SourceFileNode file, int start, int end, FormatRequestKind kind,
        FormatCodeSettings? options = null, CancellationToken cancellation = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, file.Source.Length);
        cancellation.ThrowIfCancellationRequested();
        options ??= new();
        SyntaxNode enclosing = file;
        while (true)
        {
            SyntaxNode? found = null;
            for (int i = 0; i < enclosing.ChildCount; i++)
            {
                var child = enclosing.GetChild(i);
                if ((child.Flags & NodeFlags.Reparsed) == 0 && SmartIndenter.Start(child, file) <= start && end <= child.End)
                { found = child; break; }
            }
            if (found is null) break;
            enclosing = found;
        }
        int scanStart = SmartIndenter.Start(enclosing, file);
        if (scanStart != start || enclosing.End != end)
        {
            var preceding = await SyntaxNavigation.FindPrecedingTokenAsync(file, start, excludeJSDoc: true, cancellation: cancellation).ConfigureAwait(false);
            scanStart = preceding is null || preceding.End >= start ? enclosing.Pos : preceding.End;
        }
        int indent = await SmartIndenter.GetIndentationForNodeAsync(enclosing, file, options, (start, end), cancellation).ConfigureAwait(false);
        int delta = 0, previousLine = -1;
        SyntaxNode? prior = null;
        for (var node = enclosing; node is not null; prior = node, node = node.Parent)
        {
            int line = file.Source.GetLineAndCharacter(SmartIndenter.Start(node, file)).Line;
            if (previousLine != -1 && previousLine != line) break;
            if (SmartIndenter.ShouldIndentChild(options, node, prior, file)) { delta = options.IndentSize; break; }
            previousLine = line;
        }
        var worker = new FormatWorker(file, options, kind, new(start, end), enclosing, indent, delta, cancellation);
        return await worker.ExecuteAsync(new(file, scanStart, end)).ConfigureAwait(false);
    }

    public static ValueTask<SourceTextChange[]> FormatNodeAsync(SyntaxNode node, SourceFileNode file, int initialIndentation, int delta,
        FormatCodeSettings? options = null, CancellationToken cancellation = default) =>
        new FormatWorker(file, options ?? new(), FormatRequestKind.Selection, new(node.Pos, node.End), node, initialIndentation, delta, cancellation, ignoreErrors: true)
            .ExecuteAsync(new(file, node.Pos, node.End));

    private static int LineStart(SourceFileNode file, int position) => file.Source.LineStarts[file.Source.GetLineAndCharacter(position).Line];
    internal static int EndLine(SourceFileNode file, int line)
    {
        int position = file.Source.LineStarts[line];
        while (position < file.Source.Length)
        {
            int ch = Wtf8.Decode(file.Source.Text.Span[position..], out int width);
            if (TokenFacts.IsLineBreak(ch)) break;
            position += width;
        }
        return position - 1;
    }

    private static SyntaxNode Outermost(SyntaxNode node)
    {
        var current = node;
        while (current.Parent is { } parent && parent.End == node.End && !IsListElement(parent, current)) current = parent;
        return current;
    }
    private static bool IsListElement(SyntaxNode parent, SyntaxNode node)
    {
        var list = parent switch
        {
            ClassDeclarationNode n => n.Members, InterfaceDeclarationNode n => n.Members,
            ModuleDeclarationNode { Body: ModuleBlockNode block } => block.Statements,
            SourceFileNode n => n.Statements, BlockNode n => n.Statements, ModuleBlockNode n => n.Statements,
            CatchClauseNode { Block: BlockNode block } => block.Statements, _ => null,
        };
        return list is not null && list.Pos <= node.Pos && node.End <= list.End;
    }
    private static bool IsMemberListElement(SyntaxNode parent, SyntaxNode node)
    {
        var list = parent switch
        {
            ClassDeclarationNode n => n.Members, ClassExpressionNode n => n.Members, InterfaceDeclarationNode n => n.Members,
            EnumDeclarationNode n => n.Members, TypeLiteralNode n => n.Members, MappedTypeNode n => n.Members, _ => null,
        };
        return list is not null && list.Pos <= node.Pos && node.End <= list.End;
    }
    private static K OpenToken(SyntaxNode node, NodeList list) => node switch
    {
        IFunctionSignature n when n.TypeParameters == list => K.LessThanToken,
        IFunctionSignature n when n.Parameters == list => K.OpenParenToken,
        CallExpressionNode n => n.TypeArguments == list ? K.LessThanToken : n.Arguments == list ? K.OpenParenToken : K.Unknown,
        NewExpressionNode n => n.TypeArguments == list ? K.LessThanToken : n.Arguments == list ? K.OpenParenToken : K.Unknown,
        ClassDeclarationNode n when n.TypeParameters == list => K.LessThanToken,
        ClassExpressionNode n when n.TypeParameters == list => K.LessThanToken,
        InterfaceDeclarationNode n when n.TypeParameters == list => K.LessThanToken,
        TypeAliasDeclarationNode n when n.TypeParameters == list => K.LessThanToken,
        TypeReferenceNode n when n.TypeArguments == list => K.LessThanToken,
        TaggedTemplateExpressionNode n when n.TypeArguments == list => K.LessThanToken,
        TypeQueryNode n when n.TypeArguments == list => K.LessThanToken,
        ExpressionWithTypeArgumentsNode n when n.TypeArguments == list => K.LessThanToken,
        ImportTypeNode n when n.TypeArguments == list => K.LessThanToken,
        TypeLiteralNode => K.OpenBraceToken, _ => K.Unknown,
    };
    private static K CloseToken(K kind) => kind switch { K.OpenParenToken => K.CloseParenToken, K.LessThanToken => K.GreaterThanToken, K.OpenBraceToken => K.CloseBraceToken, _ => K.Unknown };
    private static bool HasDecorators(SyntaxNode node) => node.ModifierList?.Any(m => m.Kind == K.Decorator) == true;
    private static int UndecoratedStart(SyntaxNode node, SourceFileNode file) => node.ModifierList?.LastOrDefault(m => m.Kind == K.Decorator) is { } decorator
        ? new Scanner(file.Source).SkipTriviaAt(decorator.End) : SmartIndenter.Start(node, file);
    private static bool IsComment(K kind) => kind is K.SingleLineCommentTrivia or K.MultiLineCommentTrivia;
    private static bool IsLiteral(K kind) => kind is K.StringLiteral or K.RegularExpressionLiteral or >= K.FirstTemplateToken and <= K.LastTemplateToken;
}
