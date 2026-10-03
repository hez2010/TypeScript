using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal readonly record struct FormattingRange(int Start, int End, K Kind = K.Unknown)
{
    internal int Length => End - Start;
    internal bool Contains(int start, int end) => Start <= start && end <= End;
    internal bool Contains(FormattingRange range) => Contains(range.Start, range.End);
    internal bool Overlaps(int start, int end) => Math.Max(Start, start) < Math.Min(End, end);
}

internal readonly record struct FormattingToken(FormattingRange[] Leading, FormattingRange Token, FormattingRange[] Trailing);

internal sealed class FormattingScanner
{
    private readonly Scanner scanner;
    private readonly int start, end;
    private int savedPosition;
    private FormattingToken? last;
    private ScanAction lastAction;
    private readonly List<FormattingRange> leading = [], trailing = [];
    internal bool LastTriviaWasNewLine { get; private set; } = true;
    internal IReadOnlyList<FormattingRange> LeadingTrivia => leading;
    internal int TokenStart => last?.Token.Start ?? scanner.FullStart;
    internal bool OnToken => (last?.Token.Kind ?? scanner.Kind) is not K.EndOfFile and not (>= K.FirstTriviaToken and <= K.LastTriviaToken);
    internal bool OnEof => (last?.Token.Kind ?? scanner.Kind) == K.EndOfFile;
    internal FormattingRange EofRange => new(scanner.FullStart, scanner.Position, K.EndOfFile);

    internal FormattingScanner(SourceFileNode file, int start, int end)
    {
        this.start = start; this.end = end;
        scanner = new(file.Source, skipTrivia: false, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX);
        scanner.ResetPosition(start);
    }

    internal void Advance()
    {
        last = null;
        if (scanner.FullStart != start) LastTriviaWasNewLine = trailing.Count != 0 && trailing[^1].Kind == K.NewLineTrivia;
        else scanner.Scan();
        leading.Clear(); trailing.Clear();
        int pos = scanner.FullStart;
        while (pos < end && scanner.Kind is >= K.FirstTriviaToken and <= K.LastTriviaToken)
        {
            var kind = scanner.Kind; scanner.Scan();
            leading.Add(new(pos, scanner.FullStart, kind)); pos = scanner.FullStart;
        }
        savedPosition = scanner.FullStart;
    }

    internal FormattingToken Read(SyntaxNode node)
    {
        ScanAction action = node.Kind switch
        {
            K.GreaterThanEqualsToken or K.GreaterThanGreaterThanEqualsToken or K.GreaterThanGreaterThanGreaterThanEqualsToken
                or K.GreaterThanGreaterThanGreaterThanToken or K.GreaterThanGreaterThanToken => ScanAction.GreaterThan,
            K.RegularExpressionLiteral => ScanAction.Slash,
            K.TemplateMiddle or K.TemplateTail => ScanAction.Template,
            _ => RescanJsxIdentifier(node) ? ScanAction.JsxIdentifier
                : node.Kind == K.JsxText || node.Kind == K.JsxElement && last?.Token.Kind == K.JsxText ? ScanAction.JsxText
                : node.Parent is JsxAttributeNode attribute && attribute.Initializer == node ? ScanAction.JsxAttribute : ScanAction.Scan,
        };
        if (last is { } prior && action == lastAction) return (last = Fix(prior, node)).Value;
        if (scanner.FullStart != savedPosition) { scanner.ResetPosition(savedPosition); scanner.Scan(); }
        K kind = scanner.Kind; lastAction = ScanAction.Scan;
        switch (action)
        {
            case ScanAction.GreaterThan when kind == K.GreaterThanToken:
                lastAction = action; kind = scanner.RescanGreaterThanToken(); break;
            case ScanAction.Slash when kind is K.SlashToken or K.SlashEqualsToken:
                lastAction = action; kind = scanner.RescanSlashToken(); break;
            case ScanAction.Template when kind == K.CloseBraceToken:
                lastAction = action; kind = scanner.RescanTemplateToken(false); break;
            case ScanAction.JsxIdentifier: lastAction = action; kind = scanner.ScanJsxIdentifier(); break;
            case ScanAction.JsxText: lastAction = action; kind = scanner.RescanJsxToken(false); break;
            case ScanAction.JsxAttribute: lastAction = action; kind = scanner.RescanJsxAttributeValue(); break;
        }
        var token = new FormattingRange(scanner.FullStart, scanner.Position, kind);
        trailing.Clear();
        while (scanner.FullStart < end)
        {
            kind = scanner.Scan();
            if (kind is not (>= K.FirstTriviaToken and <= K.LastTriviaToken)) break;
            trailing.Add(new(scanner.FullStart, scanner.Position, kind));
            if (kind == K.NewLineTrivia) { scanner.Scan(); break; }
        }
        return (last = Fix(new(leading.ToArray(), token, trailing.ToArray()), node)).Value;
    }

    internal void SkipTo(int position)
    {
        scanner.ResetPosition(position); savedPosition = scanner.FullStart; lastAction = ScanAction.Scan;
        last = null; LastTriviaWasNewLine = false; leading.Clear(); trailing.Clear();
    }

    private static FormattingToken Fix(FormattingToken info, SyntaxNode node) => node.Kind <= K.LastToken
        ? info with { Token = info.Token with { Kind = node.Kind } } : info;

    private static bool RescanJsxIdentifier(SyntaxNode node)
    {
        if (node.Kind != K.Identifier && node.Kind is not (>= K.FirstKeyword and <= K.LastKeyword)) return false;
        if (node.Parent?.Kind is K.JsxAttribute or K.JsxOpeningElement or K.JsxClosingElement or K.JsxSelfClosingElement or K.JsxNamespacedName) return true;
        if (node.Parent is not PropertyAccessExpressionNode) return false;
        for (var current = node; current.Parent is { } parent; current = parent)
        {
            if (parent is JsxOpeningElementNode opening && opening.TagName == current
                || parent is JsxClosingElementNode closing && closing.TagName == current
                || parent is JsxSelfClosingElementNode self && self.TagName == current) return true;
            if (parent is not PropertyAccessExpressionNode property || property.Expression != current) break;
        }
        return false;
    }

    private enum ScanAction { Scan, GreaterThan, Slash, Template, JsxIdentifier, JsxText, JsxAttribute }
}
