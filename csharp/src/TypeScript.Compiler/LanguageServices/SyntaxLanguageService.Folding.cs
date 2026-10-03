using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record FoldingRange(uint StartLine, uint StartCharacter, uint EndLine, uint EndCharacter,
    Utf8String? Kind = null, Utf8String? CollapsedText = null);

public static partial class SyntaxLanguageService
{
    public static ValueTask<FoldingRange[]> GetFoldingRangesAsync(SourceFileNode file, PositionEncoding encoding = PositionEncoding.Utf8,
        bool lineFoldingOnly = false, bool collapsedText = false, CancellationToken cancellation = default) =>
        GetFoldingRangesAsync(new(file, null, encoding), lineFoldingOnly, collapsedText, cancellation);

    internal static ValueTask<FoldingRange[]> GetFoldingRangesAsync(DocumentProjection projection, bool lineFoldingOnly,
        bool collapsedText, CancellationToken cancellation) => new FoldingCollector(projection, lineFoldingOnly, collapsedText, cancellation).CollectAsync();

    private sealed class FoldingCollector(DocumentProjection projection, bool lineFoldingOnly,
        bool collapsedText, CancellationToken cancellation)
    {
        private readonly SourceFileNode file = projection.File;
        private readonly List<FoldingRange> ranges = [];
        private ValueTask<int> Start(SyntaxNode node) => SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation);
        private ValueTask<SyntaxNode?> Child(SyntaxNode node, K kind) => SyntaxNavigation.FindChildOfKindAsync(node, kind, file, cancellation);
        private bool SameLine(int start, int end) => file.Source.GetLineAndCharacter(start).Line == file.Source.GetLineAndCharacter(end).Line;
        private void Add(int start, int end, Utf8String? kind = null, Utf8String? banner = null)
        {
            var (range, fidelity) = projection.ToRange(start, end, MappingFeature.FoldingRanges);
            if (fidelity == MappingFidelity.None) return;
            uint endLine = range.End.Line;
            if (lineFoldingOnly && range.End.Character != 0)
            {
                var positions = projection.FromPosition(range.End, MappingFeature.FoldingRanges);
                if (positions.FirstOrDefault(position => position.Fidelity != MappingFidelity.None) is { Position: > 0 } mapped
                    && mapped.Position <= file.Source.Length && file.Source.Text[mapped.Position - 1] is (byte)'}' or (byte)']' or (byte)')' or (byte)'`' or (byte)'>'
                    && endLine > range.Start.Line) endLine--;
            }
            ranges.Add(new(range.Start.Line, range.Start.Character, endLine, range.End.Character, kind, collapsedText ? banner : null));
        }

        internal async ValueTask<FoldingRange[]> CollectAsync()
        {
            cancellation.ThrowIfCancellationRequested();
            var statements = file.Statements;
            if (statements is not null)
            {
                int firstImport = -1;
                for (int i = 0; i <= statements.Count; i++)
                {
                    bool import = i < statements.Count && statements[i].Kind is K.ImportDeclaration or K.ImportEqualsDeclaration;
                    if (import && firstImport < 0) firstImport = i;
                    if (!import && firstImport >= 0)
                    {
                        if (i - firstImport > 1 && await Child(statements[firstImport], K.ImportKeyword).ConfigureAwait(false) is { } keyword)
                            Add(await Start(keyword).ConfigureAwait(false), statements[i - 1].End, "imports"u8);
                        firstImport = -1;
                    }
                    if (i < statements.Count) await VisitAsync(statements[i]).ConfigureAwait(false);
                }
            }
            if (file.EndOfFileToken is { } eof) await VisitAsync(eof).ConfigureAwait(false);
            await RegionsAsync().ConfigureAwait(false);
            return ranges.OrderBy(range => range.StartLine).ThenBy(range => range.StartCharacter)
                .ThenBy(range => range.EndLine).ThenBy(range => range.EndCharacter).Distinct().ToArray();
        }

        private async ValueTask VisitAsync(SyntaxNode root)
        {
            Stack<(SyntaxNode Node, int Depth)> pending = new();
            pending.Push((root, 40));
            while (pending.TryPop(out var item))
            {
                cancellation.ThrowIfCancellationRequested();
                var (node, depth) = item;
                if (depth == 0 || (node.Flags & NodeFlags.Reparsed) != 0) continue;
                if (node.Kind != K.BinaryExpression && node.IsDeclarationNode && (node.Kind != K.TypeParameter || node.Parent is not null)
                    || node.Kind is K.VariableStatement or K.ReturnStatement or K.CallExpression or K.NewExpression or K.EndOfFile)
                    Comments(node.Pos);
                if (node.HasFunctionSignature && node.Parent is BinaryExpressionNode { Left: PropertyAccessExpressionNode left }) Comments(left.Pos);
                var trailing = node switch
                {
                    BlockNode block => block.Statements,
                    ModuleBlockNode block => block.Statements,
                    ClassDeclarationNode declaration => declaration.Members,
                    ClassExpressionNode expression => expression.Members,
                    InterfaceDeclarationNode declaration => declaration.Members,
                    _ => null,
                };
                if (trailing is not null) Comments(trailing.End);
                await SpanAsync(node).ConfigureAwait(false);
                if (node is CallExpressionNode call)
                {
                    PushList(call.TypeArguments, depth - 1); PushList(call.Arguments, depth - 1);
                    if (call.Expression is { } expression) pending.Push((expression, depth));
                }
                else if (node is IfStatementNode { ElseStatement: IfStatementNode otherwise } conditional)
                {
                    pending.Push((otherwise, depth));
                    if (conditional.ThenStatement is { } then) pending.Push((then, depth - 1));
                    if (conditional.Expression is { } expression) pending.Push((expression, depth - 1));
                }
                else for (int i = node.ChildCount - 1; i >= 0; i--) pending.Push((node.GetChild(i), depth - 1));
            }
            void PushList(NodeList? list, int depth)
            {
                if (list is not null) for (int i = list.Count - 1; i >= 0; i--) pending.Push((list[i], depth));
            }
        }

        private void Comments(int position)
        {
            int first = 0, end = 0, count = 0;
            void Flush() { if (count > 1) Add(first, end, "comment"u8); count = 0; }
            foreach (var comment in SyntaxPrinter.CommentRanges(file.Source.Text, position, false))
            {
                cancellation.ThrowIfCancellationRequested();
                if (comment.Kind == K.MultiLineCommentTrivia) { Flush(); Add(comment.Pos, comment.End, "comment"u8); }
                else if (ParseRegion(file.Source.Text[comment.Pos..comment.End]) is not null) Flush();
                else { if (count++ == 0) first = comment.Pos; end = comment.End; }
            }
            Flush();
        }

        private async ValueTask BetweenAsync(SyntaxNode? open, SyntaxNode? close, bool fullStart, bool multiLineOnly = false)
        {
            if (open is not null && close is not null && (!multiLineOnly || !SameLine(open.Pos, close.Pos)))
                Add(fullStart ? open.Pos : await Start(open).ConfigureAwait(false), close.End);
        }

        private async ValueTask DelimitersAsync(SyntaxNode node, K open, K close, bool fullStart, bool multiLineOnly = false) =>
            await BetweenAsync(await Child(node, open).ConfigureAwait(false), await Child(node, close).ConfigureAwait(false), fullStart, multiLineOnly).ConfigureAwait(false);

        private async ValueTask SpanAsync(SyntaxNode node)
        {
            switch (node.Kind)
            {
                case K.Block:
                    if (node.Parent is { HasFunctionSignature: true } function)
                    {
                        var parameters = (function as IFunctionSignature)?.Parameters;
                        var open = parameters is { Count: > 0 } && !SameLine(parameters[0].Pos, parameters[^1].End)
                            ? await Child(function, K.OpenParenToken).ConfigureAwait(false) : null;
                        open ??= await Child(node, K.OpenBraceToken).ConfigureAwait(false);
                        await BetweenAsync(open, await Child(node, K.CloseBraceToken).ConfigureAwait(false), true).ConfigureAwait(false);
                    }
                    else if (node.Parent is TryStatementNode attempt)
                    {
                        var open = await Child(node, K.OpenBraceToken).ConfigureAwait(false);
                        var close = await Child(node, K.CloseBraceToken).ConfigureAwait(false);
                        if (ReferenceEquals(attempt.TryBlock, node) || open is not null && close is not null)
                            await BetweenAsync(open, close, true).ConfigureAwait(false);
                        else Add(await Start(node).ConfigureAwait(false), node.End);
                    }
                    else if (node.Parent?.Kind is K.DoStatement or K.ForInStatement or K.ForOfStatement or K.ForStatement or K.IfStatement
                        or K.WhileStatement or K.WithStatement or K.CatchClause)
                        await DelimitersAsync(node, K.OpenBraceToken, K.CloseBraceToken, true).ConfigureAwait(false);
                    else Add(await Start(node).ConfigureAwait(false), node.End);
                    break;
                case K.ModuleBlock: case K.ClassDeclaration: case K.ClassExpression: case K.InterfaceDeclaration: case K.EnumDeclaration:
                case K.CaseBlock: case K.TypeLiteral: case K.ObjectBindingPattern:
                    await DelimitersAsync(node, K.OpenBraceToken, K.CloseBraceToken, true).ConfigureAwait(false); break;
                case K.TupleType:
                    await DelimitersAsync(node, K.OpenBracketToken, K.CloseBracketToken, node.Parent?.Kind != K.TupleType).ConfigureAwait(false); break;
                case K.ArrayBindingPattern:
                    await DelimitersAsync(node, K.OpenBracketToken, K.CloseBracketToken, node.Parent?.Kind != K.BindingElement).ConfigureAwait(false); break;
                case K.ObjectLiteralExpression: case K.ArrayLiteralExpression:
                    bool array = node.Kind == K.ArrayLiteralExpression;
                    await DelimitersAsync(node, array ? K.OpenBracketToken : K.OpenBraceToken, array ? K.CloseBracketToken : K.CloseBraceToken,
                        node.Parent?.Kind is not (K.ArrayLiteralExpression or K.CallExpression)).ConfigureAwait(false); break;
                case K.CaseClause: case K.DefaultClause:
                    if (((CaseOrDefaultClauseNode)node).Statements is { Count: > 0 } statements) Add(statements.Pos, statements.End);
                    break;
                case K.JsxElement:
                    var jsx = (JsxElementNode)node;
                    if (jsx.OpeningElement is { TagName: { } tag } opening && jsx.ClosingElement is { } closing)
                    {
                        var name = file.Source.Text[(await Start(tag).ConfigureAwait(false))..tag.End];
                        Add(await Start(opening).ConfigureAwait(false), closing.End, banner: "<"u8 + name + ">...</"u8 + name + ">"u8);
                    }
                    break;
                case K.JsxFragment:
                    if (node is JsxFragmentNode { OpeningFragment: { } first, ClosingFragment: { } last })
                        Add(await Start(first).ConfigureAwait(false), last.End, banner: "<>...</>"u8);
                    break;
                case K.JsxSelfClosingElement: case K.JsxOpeningElement:
                    var attributes = node is JsxSelfClosingElementNode selfClosing ? selfClosing.Attributes : ((JsxOpeningElementNode)node).Attributes;
                    if (attributes?.Properties is { Count: > 0 }) Add(await Start(node).ConfigureAwait(false), node.End);
                    break;
                case K.TemplateExpression: case K.NoSubstitutionTemplateLiteral:
                    if (node is not NoSubstitutionTemplateLiteralNode { Text.IsEmpty: true }) Add(await Start(node).ConfigureAwait(false), node.End);
                    break;
                case K.ArrowFunction:
                    if (node is ArrowFunctionNode { Body: { } body } && body.Kind is not (K.Block or K.ParenthesizedExpression) && !SameLine(body.Pos, body.End))
                        Add(body.Pos, body.End);
                    break;
                case K.ParenthesizedExpression:
                    int start = await Start(node).ConfigureAwait(false);
                    if (!SameLine(start, node.End)) Add(start, node.End);
                    break;
                case K.CallExpression:
                    if (((CallExpressionNode)node).Arguments is { Count: > 0 })
                        await DelimitersAsync(node, K.OpenParenToken, K.CloseParenToken, true, true).ConfigureAwait(false);
                    break;
                case K.NamedImports: case K.NamedExports: case K.ImportAttributes:
                    var elements = node switch { NamedImportsNode imports => imports.Elements, NamedExportsNode exports => exports.Elements, ImportAttributesNode imports => imports.Attributes, _ => null };
                    if (elements is { Count: > 0 }) await DelimitersAsync(node, K.OpenBraceToken, K.CloseBraceToken, false, true).ConfigureAwait(false);
                    break;
            }
        }

        private static (bool Start, Utf8String Name)? ParseRegion(Utf8String text)
        {
            text = text.Trim();
            if (!text.StartsWith("//"u8)) return null;
            text = text[2..].Trim();
            if (!text.StartsWith((byte)'#')) return null;
            text = text[1..];
            bool start = !text.StartsWith("end"u8);
            if (!start) text = text[3..];
            return text.StartsWith("region"u8) ? (start, text[6..].Trim()) : null;
        }

        private async ValueTask RegionsAsync()
        {
            Stack<(int Position, Utf8String Banner)> regions = new();
            var starts = file.Source.LineStarts.ToArray();
            for (int i = 0; i < starts.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                int start = starts[i], end = i + 1 < starts.Length ? starts[i + 1] - 1 : file.End;
                if (end > 0 && end < file.Source.Length && file.Source.Text[end] == '\n' && file.Source.Text[end - 1] == '\r') end--;
                var text = file.Source.Text[start..end];
                if (ParseRegion(text) is not { } delimiter || await InCommentAsync(start).ConfigureAwait(false)) continue;
                if (delimiter.Start) regions.Push((start + text.Span.IndexOf("//"u8), delimiter.Name.IsEmpty ? "#region"u8 : delimiter.Name));
                else if (regions.TryPop(out var region)) Add(region.Position, end, "region"u8, region.Banner);
            }
        }

        private async ValueTask<bool> InCommentAsync(int position)
        {
            var token = await SyntaxNavigation.GetTokenAtPositionAsync(file, position, cancellation).ConfigureAwait(false);
            for (var current = token; current is not null; current = current.Parent)
                if (current.Kind == K.JSDoc && current.Parent is { } owner) { token = owner; break; }
            if (await Start(token).ConfigureAwait(false) <= position && position < token.End) return false;
            var previous = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation).ConfigureAwait(false);
            bool Contains(SourceCommentRange range) => range.Pos < position && position < range.End
                || position == range.End && (range.Kind == K.SingleLineCommentTrivia || position == file.Source.Length);
            return previous is not null && SyntaxPrinter.CommentRanges(file.Source.Text, previous.End, true).Any(Contains)
                || SyntaxPrinter.CommentRanges(file.Source.Text, token.Pos, false).Any(Contains);
        }
    }
}
