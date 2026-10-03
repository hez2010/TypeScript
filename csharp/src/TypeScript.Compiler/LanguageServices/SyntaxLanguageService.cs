using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

public sealed record SelectionRange(DocumentRange Range, SelectionRange? Parent = null);
public sealed record LinkedEditingRanges(DocumentRange[] Ranges, Utf8String WordPattern);

/// <summary>Editor queries that depend on a source tree without acquiring a checker.</summary>
public static partial class SyntaxLanguageService
{
    public const int MaximumSelectionDepth = 1000;

    public static async ValueTask<SelectionRange> GetSelectionRangeAsync(SourceFileNode file, int position,
        PositionEncoding encoding = PositionEncoding.Utf8, CancellationToken cancellation = default)
        => (await GetSelectionRangeAsync(new(file, null, encoding), position, cancellation).ConfigureAwait(false))!;

    internal static async ValueTask<SelectionRange?> GetSelectionRangeAsync(DocumentProjection projection, int position, CancellationToken cancellation)
    {
        var file = projection.File;
        SelectionRange? root = projection.IsMapped ? null : new(projection.ToRange(file.Pos, file.End).Range);
        DocumentRange last = root?.Range ?? default;
        var ranges = new DocumentRange[MaximumSelectionDepth - 1];
        int count = 0, oldest = 0;
        void Push(int start, int end)
        {
            if (start == end || start > position || position > end) return;
            var (range, fidelity) = projection.ToRange(start, end, MappingFeature.SelectionRanges);
            if (fidelity == MappingFidelity.None) return;
            if (range == last) return;
            last = range;
            if (count < ranges.Length) ranges[count++] = range;
            else { ranges[oldest] = range; oldest = (oldest + 1) % ranges.Length; }
        }
        async ValueTask<bool> SnapsTo(SyntaxNode node) => position < node.End || position == node.End
            && (await SyntaxNavigation.GetTouchingPropertyNameAsync(file, position, cancellation).ConfigureAwait(false)).Pos < node.End;

        SyntaxNode? current = file;
        while (current is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            SyntaxNode? next = null;
            SyntaxNode parent = current;
            async ValueTask Visit(SyntaxNode node)
            {
                if (next is not null) return;
                var comments = SyntaxPrinter.CommentRanges(file.Source.Text, node.End, true);
                if (comments.Count != 0 && comments[0] is { Kind: K.SingleLineCommentTrivia } comment)
                {
                    Push(comment.Pos, comment.End);
                    int start = comment.Pos;
                    while (start < comment.End && file.Source.Text[start] == '/') start++;
                    Push(start, comment.End);
                }
                int tokenStart = await SyntaxNavigation.GetStartAsync(node, file, true, cancellation).ConfigureAwait(false);
                if (tokenStart > position || position >= node.End) return;
                int nodeStart = await SyntaxNavigation.GetStartAsync(node, file, cancellation: cancellation).ConfigureAwait(false);
                if (node is BlockNode && parent.HasFunctionSignature
                    && file.Source.GetLineAndCharacter(nodeStart).Line != file.Source.GetLineAndCharacter(node.End).Line)
                    Push(nodeStart, node.End);
                if (parent is TemplateSpanNode { Literal: { } literal })
                {
                    int start = node.Pos - 2;
                    int end = await SyntaxNavigation.GetStartAsync(literal, file, cancellation: cancellation).ConfigureAwait(false) + 1;
                    if (start >= 0 && end <= file.Source.Length && start < end) Push(start, end);
                }
                if (!SkipSelectionNode(node, parent))
                {
                    Push(nodeStart, node.End);
                    if (node is MappedTypeNode)
                    {
                        SyntaxNode selectionParent = node;
                        while (true)
                        {
                            SyntaxNode? selectionChild = null;
                            foreach (var child in SelectionChildren(selectionParent, file, cancellation))
                            {
                                int start = await SyntaxNavigation.GetStartAsync(child, file, true, cancellation).ConfigureAwait(false);
                                if (start > position) break;
                                if (!await SnapsTo(child).ConfigureAwait(false)) continue;
                                Push(start, child.End); selectionChild = child; break;
                            }
                            if (selectionChild is not SyntaxListNode) break;
                            selectionParent = selectionChild;
                        }
                    }
                    if (node.Kind is K.StringLiteral or K.TemplateExpression or K.NoSubstitutionTemplateLiteral && nodeStart + 1 < node.End - 1)
                        Push(nodeStart + 1, node.End - 1);
                }
                next = node;
            }
            foreach (var doc in await file.GetDocumentationAsync(current, cancellation).ConfigureAwait(false))
                await Visit(doc).ConfigureAwait(false);
            var groups = new List<SyntaxChild>();
            current.GetChildGroups(groups);
            foreach (var group in groups)
            {
                if (group.Node is { } node) await Visit(node).ConfigureAwait(false);
                else if (group.List is { } list)
                {
                    if (list.Count != 0 && parent.Kind is not (K.VariableDeclarationList or K.TemplateExpression))
                    {
                        int start = await SyntaxNavigation.GetStartAsync(list[0], file, cancellation: cancellation).ConfigureAwait(false);
                        int end = list[^1].End;
                        if (start <= position && position < end) Push(start, end);
                    }
                    foreach (var child in list) await Visit(child).ConfigureAwait(false);
                }
            }
            current = next;
        }
        for (int i = 0; i < count; i++) root = new(ranges[(oldest + i) % ranges.Length], root);
        return root;
    }

    private static bool SkipSelectionNode(SyntaxNode node, SyntaxNode parent) => node.Kind is K.Block or K.TemplateSpan
        or K.TemplateHead or K.TemplateTail or K.JSDocTypeExpression or K.JSDocSignature or K.JSDocTypeLiteral
        || node is VariableDeclarationListNode && parent is VariableStatementNode
        || node is VariableDeclarationNode && parent is VariableDeclarationListNode { Declarations.Count: 1 };

    private static List<SyntaxNode> SelectionChildren(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        List<SyntaxNode> children = [];
        if (node.ChildCount == 0) return children;
        int position = node.Pos;
        void ScanTo(int end)
        {
            var scanner = new Scanner(file.Source, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX);
            scanner.ResetPosition(position); scanner.Scan();
            while (position < end)
            {
                cancellation.ThrowIfCancellationRequested();
                if (scanner.Position <= position) break;
                children.Add(file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, node, scanner.Flags));
                position = scanner.Position; scanner.Scan();
            }
        }
        for (int i = 0; i < node.ChildCount; i++)
        {
            var child = node.GetChild(i);
            ScanTo(child.Pos); children.Add(child); position = child.End;
        }
        ScanTo(node.End);
        if (node is not MappedTypeNode mapped || children.Count < 2
            || children[0].Kind != K.OpenBraceToken || children[^1].Kind != K.CloseBraceToken) return children;
        SyntaxNode open = children[0], close = children[^1];
        var content = children.GetRange(1, children.Count - 2);
        content = Group(content, child => ReferenceEquals(child, mapped.ReadonlyToken) || child.Kind == K.ReadonlyKeyword
            || ReferenceEquals(child, mapped.QuestionToken) || child.Kind == K.QuestionToken);
        content = Group(content, child => child.Kind is K.OpenBracketToken or K.TypeParameter or K.CloseBracketToken);
        int colon = content.FindIndex(child => child.Kind == K.ColonToken);
        if (colon >= 0)
        {
            List<SyntaxNode> split = [];
            if (colon != 0) split.Add(List(content.GetRange(0, colon)));
            split.Add(content[colon]);
            if (colon + 1 < content.Count) split.Add(List(content.GetRange(colon + 1, content.Count - colon - 1)));
            content = split;
        }
        return [open, List(content), close];

        static SyntaxListNode List(List<SyntaxNode> nodes) => new() { Children = nodes.ToArray(), Pos = nodes[0].Pos, End = nodes[^1].End };
        static List<SyntaxNode> Group(List<SyntaxNode> nodes, Func<SyntaxNode, bool> belongs)
        {
            List<SyntaxNode> result = [], group = [];
            foreach (var child in nodes)
            {
                if (belongs(child)) group.Add(child);
                else
                {
                    if (group.Count != 0) { result.Add(List(group)); group.Clear(); }
                    result.Add(child);
                }
            }
            if (group.Count != 0) result.Add(List(group));
            return result;
        }
    }

    public static async ValueTask<LinkedEditingRanges?> GetLinkedEditingRangesAsync(SourceFileNode file, int position,
        PositionEncoding encoding = PositionEncoding.Utf8, CancellationToken cancellation = default)
        => await GetLinkedEditingRangesAsync(new(file, null, encoding), position, cancellation).ConfigureAwait(false);

    internal static async ValueTask<LinkedEditingRanges?> GetLinkedEditingRangesAsync(DocumentProjection projection, int position, CancellationToken cancellation)
    {
        var file = projection.File;
        var token = await SyntaxNavigation.FindPrecedingTokenAsync(file, position, cancellation: cancellation).ConfigureAwait(false);
        if (token?.Parent is not { } parent || parent is SourceFileNode) return null;
        Utf8String pattern = "[a-zA-Z0-9:\\-\\._$]*"u8;
        LinkedEditingRanges? Ranges(int openStart, int openEnd, int closeStart, int closeEnd)
        {
            var open = projection.ToRange(openStart, openEnd, MappingFeature.LinkedEditing);
            var close = projection.ToRange(closeStart, closeEnd, MappingFeature.LinkedEditing);
            return open.Fidelity != MappingFidelity.Exact || close.Fidelity != MappingFidelity.Exact ? null
                : new([open.Range, close.Range], pattern);
        }
        if (parent.Parent is JsxFragmentNode { OpeningFragment: { } open, ClosingFragment: { } close })
        {
            if (((open.Flags | close.Flags) & (NodeFlags.ThisNodeHasError | NodeFlags.ThisNodeOrAnySubNodesHasError)) != 0) return null;
            int openStart = await SyntaxNavigation.GetStartAsync(open, file, cancellation: cancellation).ConfigureAwait(false) + 1;
            int closeStart = await SyntaxNavigation.GetStartAsync(close, file, cancellation: cancellation).ConfigureAwait(false) + 2;
            return position != openStart && position != closeStart ? null
                : Ranges(openStart, openStart, closeStart, closeStart);
        }
        SyntaxNode? tag = parent;
        while (tag is not null && tag.Kind is not (K.JsxOpeningElement or K.JsxClosingElement)) tag = tag.Parent;
        if (tag?.Parent is not JsxElementNode { OpeningElement: { TagName: { } openName } opening, ClosingElement: { TagName: { } closeName } closing })
            return null;
        int openNameStart = await SyntaxNavigation.GetStartAsync(openName, file, cancellation: cancellation).ConfigureAwait(false);
        int closeNameStart = await SyntaxNavigation.GetStartAsync(closeName, file, cancellation: cancellation).ConfigureAwait(false);
        if (openNameStart == await SyntaxNavigation.GetStartAsync(opening, file, cancellation: cancellation).ConfigureAwait(false)
            || closeNameStart == await SyntaxNavigation.GetStartAsync(closing, file, cancellation: cancellation).ConfigureAwait(false)
            || openName.End == opening.End || closeName.End == closing.End
            || !(openNameStart <= position && position <= openName.End || closeNameStart <= position && position <= closeName.End)
            || file.Source.Text[openNameStart..openName.End] != file.Source.Text[closeNameStart..closeName.End]) return null;
        return Ranges(openNameStart, openName.End, closeNameStart, closeName.End);
    }
}
