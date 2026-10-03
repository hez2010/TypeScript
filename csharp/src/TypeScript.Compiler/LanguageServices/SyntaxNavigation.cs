using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

/// <summary>Source navigation includes unrepresented punctuation and lazily parsed documentation.</summary>
public static partial class SyntaxNavigation
{
    public static ValueTask<SyntaxNode> GetTouchingPropertyNameAsync(SourceFileNode file, int position, CancellationToken cancellation = default) =>
        GetTokenAsync(file, position, false, true, cancellation);
    public static ValueTask<SyntaxNode> GetTouchingTokenAsync(SourceFileNode file, int position, CancellationToken cancellation = default) =>
        GetTokenAsync(file, position, false, false, cancellation);
    public static ValueTask<SyntaxNode> GetTokenAtPositionAsync(SourceFileNode file, int position, CancellationToken cancellation = default) =>
        GetTokenAsync(file, position, true, false, cancellation);

    public static async ValueTask<int> GetStartAsync(SyntaxNode node, SourceFileNode file, bool includeJSDoc = false,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (node.Pos == node.End && node.Kind != K.EndOfFile) return node.Pos;
        var scanner = new Scanner(file.Source);
        if (IsJSDoc(node) || node.Kind == K.JsxText) return scanner.SkipTriviaAt(node.Pos, stopAtComments: true);
        if (includeJSDoc && (node.Flags & NodeFlags.HasJSDoc) != 0
            && await file.GetDocumentationAsync(node, cancellation).ConfigureAwait(false) is [var first, ..])
            return scanner.SkipTriviaAt(first.Pos, stopAtComments: true);
        return scanner.SkipTriviaAt(node.Pos, inJSDoc: (node.Flags & NodeFlags.JSDoc) != 0);
    }

    private static async ValueTask<SyntaxNode> GetTokenAsync(SourceFileNode file, int position, bool includeTrivia,
        bool includePreceding, CancellationToken cancellation)
    {
        SyntaxNode current = file;
        SyntaxNode? next = null, previous = null, afterLeft = null;
        int left = 0;
        async ValueTask<SyntaxNode?> IncludedPreceding(SyntaxNode subtree)
        {
            var token = await FindPrecedingTokenAsync(file, position, subtree, cancellation: cancellation).ConfigureAwait(false);
            return token is not null && token.End == position && PropertyName(token) ? token : null;
        }
        async ValueTask<int> Test(SyntaxNode node)
        {
            if (node.Kind != K.EndOfFile && node.End == position && includePreceding && Visible(node))
            {
                if (previous is not null && await IncludedPreceding(previous).ConfigureAwait(false) is not null) return 0;
                previous = node;
            }
            if (node.End < position || node.End == position && node.Kind != K.EndOfFile
                && (!IsJSDoc(node) || node.End != file.EndOfFileToken!.End)) return -1;
            int start = includeTrivia ? node.Pos : await GetStartAsync(node, file, true, cancellation).ConfigureAwait(false);
            return start > position ? 1 : 0;
        }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            foreach (var group in await ChildrenAsync(current, file, cancellation).ConfigureAwait(false))
            {
                if (group.Node is { } node)
                {
                    afterLeft ??= node;
                    if (next is not null) continue;
                    int result = await Test(node).ConfigureAwait(false);
                    if (result < 0) { if (!IsJSDoc(node)) left = node.End; afterLeft = null; }
                    else if (result == 0) next = node;
                }
                else
                {
                    var list = group.List!;
                    if (list.Count == 0) continue;
                    afterLeft ??= list.FirstOrDefault(Visible);
                    if (next is not null) continue;
                    if (list.End == position && includePreceding)
                    { left = list.End; afterLeft = null; previous = list.LastOrDefault(Visible); }
                    else if (list.End <= position) { left = list.End; afterLeft = null; }
                    else if (list.Pos <= position)
                    {
                        IReadOnlyList<SyntaxNode> nodes = list;
                        int low = 0, high = nodes.Count - 1;
                        while (low <= high)
                        {
                            int middle = low + (high - low) / 2;
                            node = nodes[middle];
                            if (!Visible(node)) { nodes = list.Where(Visible).ToArray(); low = 0; high = nodes.Count - 1; continue; }
                            int result = await Test(node).ConfigureAwait(false);
                            if (result < 0)
                            {
                                left = node.End; afterLeft = null;
                                for (int i = middle + 1; i < nodes.Count; i++) if (Visible(nodes[i])) { afterLeft = nodes[i]; break; }
                                low = middle + 1;
                            }
                            else if (result > 0) high = middle - 1;
                            else { next = node; break; }
                        }
                    }
                }
            }
            if (previous is not null)
            {
                if (await IncludedPreceding(previous).ConfigureAwait(false) is { } token) return token;
                previous = null;
            }
            if (next is null)
            {
                if (IsToken(current) || SkipChildTokens(current)) return current;
                var scanner = ScanAt(file, left);
                int end = afterLeft?.Pos ?? current.End;
                while (left < end)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var kind = NavigationToken(scanner, current);
                    int start = includeTrivia ? scanner.FullStart : scanner.TokenStart, tokenEnd = scanner.Position;
                    if (tokenEnd > end) break;
                    if (start <= position && position < tokenEnd)
                    {
                        if (kind == K.Identifier || kind > K.LastToken)
                        {
                            if (IsJSDoc(current)) return current;
                            throw new InvalidOperationException($"Did not expect {current.Kind} to have {kind} in its trivia");
                        }
                        return file.GetOrCreateToken(kind, scanner.FullStart, tokenEnd, current, scanner.Flags);
                    }
                    if (includePreceding && tokenEnd == position)
                    {
                        var token = file.GetOrCreateToken(kind, scanner.FullStart, tokenEnd, current, scanner.Flags);
                        if (PropertyName(token)) return token;
                    }
                    if (tokenEnd <= left) break;
                    left = tokenEnd; scanner.Scan();
                }
                return current;
            }
            current = next; left = current.Pos; next = afterLeft = null;
        }
    }

    public static async ValueTask<SyntaxNode?> FindPrecedingTokenAsync(SourceFileNode file, int position, SyntaxNode? startNode = null,
        bool excludeJSDoc = false, CancellationToken cancellation = default)
    {
        var current = startNode ?? file;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (NonWhitespaceToken(current) && current.Kind != K.EndOfFile) return current;
            SyntaxNode? found = null, previous = null;
            foreach (var group in await ChildrenAsync(current, file, cancellation).ConfigureAwait(false))
            {
                if (found is not null) break;
                if (group.Node is { } node)
                {
                    if (position < node.End && (previous is null || previous.End <= position)) found = node;
                    else previous = node;
                }
                else
                {
                    var nodes = group.List!;
                    int low = 0, high = nodes.Count - 1, index = -1;
                    while (low <= high)
                    {
                        int middle = low + (high - low) / 2;
                        if (!Visible(nodes[middle]) || position >= nodes[middle].End) low = middle + 1;
                        else if (middle > 0 && position < nodes[middle - 1].End) high = middle - 1;
                        else { index = middle; found = nodes[middle]; break; }
                    }
                    for (int i = index >= 0 ? index - 1 : nodes.Count - 1; i >= 0; i--)
                        if (Visible(nodes[i])) previous ??= nodes[i];
                }
            }
            if (found is not null)
            {
                int start = await GetStartAsync(found, file, !excludeJSDoc, cancellation).ConfigureAwait(false);
                if (start >= position || !await ValidPrecedingAsync(found, file, cancellation).ConfigureAwait(false))
                {
                    if (position >= found.Pos)
                    {
                        var doc = (await file.GetDocumentationAsync(current, cancellation).ConfigureAwait(false)).LastOrDefault(n => n.Pos >= found.Pos);
                        if (doc is not null)
                        {
                            if (!excludeJSDoc && position < doc.End) { current = doc; continue; }
                            return await RightmostAsync(doc.End, file, current, position, excludeJSDoc, cancellation).ConfigureAwait(false);
                        }
                        return await RightmostAsync(found.Pos, file, current, -1, excludeJSDoc, cancellation).ConfigureAwait(false);
                    }
                    return await RightmostAsync(found.Pos, file, current, position, excludeJSDoc, cancellation).ConfigureAwait(false);
                }
                current = found;
            }
            else return await RightmostAsync(current.End, file, current, position >= current.End ? -1 : position, excludeJSDoc, cancellation).ConfigureAwait(false);
        }
    }

    private static async ValueTask<bool> ValidPrecedingAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation) =>
        node.Kind == K.EndOfFile ? (await file.GetDocumentationAsync(node, cancellation).ConfigureAwait(false)).Count > 0
            : !WhitespaceJsx(node) && node.End != await GetStartAsync(node, file, cancellation: cancellation).ConfigureAwait(false);

    private static async ValueTask<SyntaxNode?> RightmostAsync(int end, SourceFileNode file, SyntaxNode containingNode, int position,
        bool excludeJSDoc, CancellationToken cancellation)
    {
        if (position == -1) position = containingNode.End;
        SyntaxNode? current = containingNode;
        while (current is not null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (NonWhitespaceToken(current)) return current;
            SyntaxNode? rightmost = null;
            var trailing = new List<SyntaxNode>();
            bool hasChildren = false;
            async ValueTask<bool> ShouldVisit(SyntaxNode node) => Visible(node) && node.End <= end
                && await GetStartAsync(node, file, !excludeJSDoc, cancellation).ConfigureAwait(false) < position;
            foreach (var group in await ChildrenAsync(current, file, cancellation).ConfigureAwait(false))
            {
                if (group.Node is { } node)
                {
                    hasChildren = true;
                    if (!await ShouldVisit(node).ConfigureAwait(false)) continue;
                    trailing.Add(node);
                    if (await ValidPrecedingAsync(node, file, cancellation).ConfigureAwait(false)) { rightmost = node; trailing.Clear(); }
                }
                else if (group.List is { Count: > 0 } nodes)
                {
                    hasChildren = true;
                    int low = 0, high = nodes.Count;
                    while (low < high)
                    {
                        int middle = low + (high - low) / 2;
                        if (nodes[middle].End <= end) low = middle + 1; else high = middle;
                    }
                    int valid = -1;
                    for (int i = low - 1; i >= 0; i--)
                        if (await ShouldVisit(nodes[i]).ConfigureAwait(false) && await ValidPrecedingAsync(nodes[i], file, cancellation).ConfigureAwait(false))
                        { valid = i; rightmost = nodes[i]; break; }
                    for (int i = valid + 1; i < low; i++) if (await ShouldVisit(nodes[i]).ConfigureAwait(false)) trailing.Add(nodes[i]);
                }
            }
            if (!SkipChildTokens(current))
            {
                int start = rightmost?.End ?? current.Pos;
                var scanner = ScanAt(file, start);
                SyntaxNode? last = null;
                void ScanTo(int limit)
                {
                    while (start < limit)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var kind = NavigationToken(scanner, current);
                        if (scanner.TokenStart >= limit) break;
                        int tokenEnd = scanner.Position;
                        var token = file.GetOrCreateToken(kind, scanner.FullStart, tokenEnd, current, scanner.Flags);
                        if (!WhitespaceJsx(token)) last = token;
                        if (tokenEnd <= start) break;
                        start = tokenEnd; scanner.Scan();
                    }
                }
                foreach (var node in trailing)
                {
                    ScanTo(Math.Min(node.Pos, position));
                    start = node.End; scanner.ResetPosition(start); scanner.Scan();
                }
                ScanTo(Math.Min(end, position));
                if (last is not null) return last;
            }
            if (!hasChildren) return current != containingNode ? current : null;
            current = rightmost;
            if (current is not null) end = current.End;
        }
        return null;
    }

    public static async ValueTask<SyntaxNode?> FindNextTokenAsync(SyntaxNode previous, SyntaxNode parent, SourceFileNode file,
        CancellationToken cancellation = default)
    {
        var current = parent;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (IsToken(current) && current.Pos == previous.End) return current;
            SyntaxNode? found = null;
            foreach (var group in await ChildrenAsync(current, file, cancellation).ConfigureAwait(false))
            {
                if (group.Node is { } node)
                {
                    if (node.Pos <= previous.End && node.End > previous.End) found = node;
                }
                else if (found is null && group.List is { } nodes)
                {
                    int low = 0, high = nodes.Count - 1;
                    while (low <= high)
                    {
                        int middle = low + (high - low) / 2;
                        node = nodes[middle];
                        if (!Visible(node)) low = middle + 1;
                        else if (node.Pos > previous.End) high = middle - 1;
                        else if (node.End <= previous.Pos) low = middle + 1;
                        else { found = node; break; }
                    }
                }
            }
            if (found is not null) { current = found; continue; }
            if (previous.End >= current.Pos && previous.End < current.End)
            {
                var scanner = ScanAt(file, previous.End);
                if (scanner.FullStart == previous.End)
                    return file.GetOrCreateToken(scanner.Kind, scanner.FullStart, scanner.Position, current, scanner.Flags);
                throw new InvalidOperationException($"Expected to find next token at {previous.End}, got token {scanner.Kind} at {scanner.FullStart}");
            }
            return null;
        }
    }

    public static async ValueTask<SyntaxNode?> FindChildOfKindAsync(SyntaxNode containingNode, K kind, SourceFileNode file,
        CancellationToken cancellation = default)
    {
        int start = containingNode.Pos;
        var scanner = ScanAt(file, start);
        bool reset = false;
        K previousKind = scanner.Kind;
        TokenFlags previousFlags = scanner.Flags;
        SyntaxNode? ScanTo(int end)
        {
            while (start < end)
            {
                cancellation.ThrowIfCancellationRequested();
                // Go's ResetPos retains the preceding token kind and flags at the new zero-width span.
                if (reset)
                {
                    if (previousKind == kind) return file.GetOrCreateToken(kind, start, start, containingNode, previousFlags);
                    scanner.Scan(); reset = false; continue;
                }
                if (scanner.Kind == kind) return file.GetOrCreateToken(kind, scanner.FullStart, scanner.Position, containingNode, scanner.Flags);
                if (scanner.Position <= start) break;
                start = scanner.Position; scanner.Scan();
            }
            return null;
        }
        var children = new List<SyntaxNode>(await file.GetDocumentationAsync(containingNode, cancellation).ConfigureAwait(false));
        for (int i = 0; i < containingNode.ChildCount; i++) children.Add(containingNode.GetChild(i));
        foreach (var child in children)
        {
            if (!Visible(child)) continue;
            if (ScanTo(child.Pos) is { } token) return token;
            if (child.Kind == kind) return child;
            if (!reset) { previousKind = scanner.Kind; previousFlags = scanner.Flags; }
            start = child.End; scanner.ResetPosition(start); reset = true;
        }
        return ScanTo(containingNode.End);
    }

    private static async ValueTask<List<SyntaxChild>> ChildrenAsync(SyntaxNode node, SourceFileNode file, CancellationToken cancellation)
    {
        var result = new List<SyntaxChild>();
        foreach (var doc in await file.GetDocumentationAsync(node, cancellation).ConfigureAwait(false)) result.Add(new(doc, null));
        node.GetChildGroups(result);
        var comment = node.DocumentationComment is { Count: 1 } single ? single : null;
        result.RemoveAll(child => child.Node is { } childNode ? !Visible(childNode) || comment?[0] == childNode : child.List == comment);
        return result;
    }

    private static Scanner ScanAt(SourceFileNode file, int position)
    {
        var scanner = new Scanner(file.Source, jsx: file.ScriptKind is ScriptKind.JSX or ScriptKind.TSX);
        scanner.ResetPosition(position); scanner.Scan(); return scanner;
    }
    private static K NavigationToken(Scanner scanner, SyntaxNode node) => scanner.Kind == K.LessThanLessThanToken
        && node.Kind is K.JsxElement or K.JsxExpression or K.JsxSelfClosingElement or K.JsxText or K.JsxFragment
            ? scanner.RescanJsxToken() : scanner.Kind;
    private static bool Visible(SyntaxNode node) => (node.Flags & NodeFlags.Reparsed) == 0;
    private static bool IsJSDoc(SyntaxNode node) => node.Kind is >= K.FirstJSDocNode and <= K.LastJSDocNode;
    private static bool IsToken(SyntaxNode node) => node.Kind <= K.LastToken;
    private static bool WhitespaceJsx(SyntaxNode node) => node is JsxTextNode { ContainsOnlyTriviaWhiteSpaces: true };
    private static bool NonWhitespaceToken(SyntaxNode node) => IsToken(node) && !WhitespaceJsx(node);
    private static bool PropertyName(SyntaxNode node) => node.Kind is K.Identifier or K.StringLiteral or K.NumericLiteral or K.NoSubstitutionTemplateLiteral
        or K.PrivateIdentifier or >= K.FirstKeyword and <= K.LastKeyword;
    private static bool SkipChildTokens(SyntaxNode node) => node.Kind is K.JSDoc or K.JSDocText or K.JSDocTypeLiteral or K.JSDocSignature
        or K.JSDocLink or K.JSDocLinkCode or K.JSDocLinkPlain or >= K.FirstJSDocTagNode and <= K.LastJSDocTagNode;
}
