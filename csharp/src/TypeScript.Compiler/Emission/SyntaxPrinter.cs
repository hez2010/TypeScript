using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Binding;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.Emission;

public sealed record PrinterOptions
{
    public bool RemoveComments { get; init; }
    public Utf8String NewLine { get; init; } = "\n"u8;
    public bool OmitTrailingSemicolon { get; init; }
    public bool NoEmitHelpers { get; init; }
    public int TargetYear { get; init; } = 2015;
    public bool InlineSources { get; init; }
    public bool OmitBraceSourceMapPositions { get; init; }
    public bool OnlyPrintJSDocStyle { get; init; }
    public bool NeverAsciiEscape { get; init; }
    public bool PreserveSourceNewlines { get; init; }
    public bool TerminateUnterminatedLiterals { get; init; }
    public Func<Utf8String, bool>? HasGlobalName { get; init; }
    public Func<SourceFileNode, int, SourceMapPosition?>? MapSourcePosition { get; init; }
}

public readonly record struct SourceMapPosition(Utf8String FileName, SourceText Source, int Position);

/// <summary>Prints parsed and transformed trees without changing their parent or binding state.</summary>
public sealed partial class SyntaxPrinter(PrinterOptions? options = null, EmitContext? context = null)
{
    private readonly PrinterOptions options = options ?? new();
    private readonly EmitContext context = context ?? new();
    private readonly Stack<Part> pending = [];
    private readonly Stack<NodeState> states = [];
    // Node states are strictly stack-disciplined, so a popped state can be reused instead of
    // allocating one per visited node (measured: 100 MB of 960 MB on the large-file fixture).
    private readonly Stack<NodeState> statePool = [];
    private PrinterWriter writer = null!;
    private SourceFileNode? sourceFile;
    private Scanner? sourceScanner;
    private SourceMapGenerator? sourceMap;
    private SourceText? recipeSource;
    private Scanner? recipeScanner;
    private int sourceIndex;
    // Files without "//" or "/*" cannot contain source comments; the per-node comment scans are
    // skipped for them, which is the common case for generated or comment-free sources.
    private bool sourceHasComments;
    // True when recipes can bypass token scanning (no source map and no source comments).
    private bool fastCode;
    private PrintPositions? positions;
    private CancellationToken cancellation;
    private bool commentsDisabled, mapsDisabled;
    private int containerPos, containerEnd, declarationListContainerEnd;
    private readonly Stack<(int Pos, int End)> detachedComments = [];

    /// <summary>
    /// One printer work item. Deliberately a hand-written struct instead of a record: a large file
    /// pushes about a million of these, so the layout is packed to 64 bytes (from roughly 120) by
    /// folding the booleans into one flags byte, turning the nullable ints into -1 sentinels, and
    /// moving the five rarely used references into a payload allocated only when one is set.
    /// </summary>
    private readonly struct Part
    {
        private const byte NewLineFlag = 1 << 0, EndNodeFlag = 1 << 1, RawFlag = 1 << 2, ListCommentFlag = 1 << 3,
            TrailingCommentFlag = 1 << 4, TrailingSemicolonFlag = 1 << 5, EndPositionFlag = 1 << 6, TextSetFlag = 1 << 7;

        private sealed class Payload
        {
            internal NodeList? GenerateList;
            internal SyntaxNode? GenerateNode;
            internal SyntaxNode? Helpers;
            internal SourceFileNode? Directives;
            internal object? PositionTarget;
        }

        private readonly SyntaxNode? node;
        private readonly Utf8String text;
        private readonly IReadOnlyList<Part>? parts;
        private readonly Payload? payload;
        private readonly int indentationChange;
        private readonly int commentPosition;
        private readonly int nameScopeChange;
        private readonly int detachedPosition;
        private readonly byte flags;

        internal Part(SyntaxNode? Node = null, Utf8String? Text = null, IReadOnlyList<Part>? Parts = null,
            int IndentationChange = 0, bool NewLine = false, bool EndNode = false, bool Raw = false, int? CommentPosition = null,
            bool ListComment = false, bool TrailingComment = false, int NameScopeChange = 0, NodeList? GenerateList = null,
            SyntaxNode? GenerateNode = null, bool TrailingSemicolon = false, SyntaxNode? Helpers = null, int? DetachedPosition = null,
            SourceFileNode? Directives = null, object? PositionTarget = null, bool EndPosition = false)
        {
            node = Node;
            text = Text ?? default;
            parts = Parts;
            indentationChange = IndentationChange;
            commentPosition = CommentPosition ?? -1;
            nameScopeChange = NameScopeChange;
            detachedPosition = DetachedPosition ?? -1;
            flags = (byte)((NewLine ? NewLineFlag : 0) | (EndNode ? EndNodeFlag : 0) | (Raw ? RawFlag : 0)
                | (ListComment ? ListCommentFlag : 0) | (TrailingComment ? TrailingCommentFlag : 0)
                | (TrailingSemicolon ? TrailingSemicolonFlag : 0) | (EndPosition ? EndPositionFlag : 0)
                | (Text is null ? 0 : TextSetFlag));
            payload = GenerateList is null && GenerateNode is null && Helpers is null && Directives is null && PositionTarget is null
                ? null
                : new Payload
                {
                    GenerateList = GenerateList, GenerateNode = GenerateNode, Helpers = Helpers,
                    Directives = Directives, PositionTarget = PositionTarget
                };
        }

        internal SyntaxNode? Node => node;
        internal Utf8String? Text => (flags & TextSetFlag) != 0 ? text : null;
        internal IReadOnlyList<Part>? Parts => parts;
        internal int IndentationChange => indentationChange;
        internal bool NewLine => (flags & NewLineFlag) != 0;
        internal bool EndNode => (flags & EndNodeFlag) != 0;
        internal bool Raw => (flags & RawFlag) != 0;
        internal int? CommentPosition => commentPosition < 0 ? null : commentPosition;
        internal bool ListComment => (flags & ListCommentFlag) != 0;
        internal bool TrailingComment => (flags & TrailingCommentFlag) != 0;
        internal int NameScopeChange => nameScopeChange;
        internal NodeList? GenerateList => payload?.GenerateList;
        internal SyntaxNode? GenerateNode => payload?.GenerateNode;
        internal bool TrailingSemicolon => (flags & TrailingSemicolonFlag) != 0;
        internal SyntaxNode? Helpers => payload?.Helpers;
        internal int? DetachedPosition => detachedPosition < 0 ? null : detachedPosition;
        internal SourceFileNode? Directives => payload?.Directives;
        internal object? PositionTarget => payload?.PositionTarget;
        internal bool EndPosition => (flags & EndPositionFlag) != 0;
    }

    private sealed class NodeState
    {
        internal SyntaxNode Node = null!;
        internal EmitFlags Flags;
        internal int Cursor;
        internal bool Comments, Maps, OldCommentsDisabled, OldMapsDisabled, Parenthesized, InExtends, FunctionBody;
        internal int ContainerPos, ContainerEnd, DeclarationListContainerEnd;
        internal EmitRange CommentRange, MapRange;

        /// <summary>Prepares a pooled instance for reuse; every field is overwritten.</summary>
        internal void Reset(SyntaxNode node, EmitFlags flags, int cursor)
        {
            Node = node;
            Flags = flags;
            Cursor = cursor;
            Comments = Maps = OldCommentsDisabled = OldMapsDisabled = Parenthesized = InExtends = FunctionBody = false;
            ContainerPos = ContainerEnd = DeclarationListContainerEnd = 0;
            CommentRange = default;
            MapRange = default;
        }
    }

    public Utf8String Print(SyntaxNode node, SourceFileNode? source = null, SourceMapGenerator? map = null,
        CancellationToken cancellation = default)
    {
        var output = new EmitTextWriter(options.NewLine);
        Write(node, source, output, map, cancellation);
        return output.Text;
    }

    public void Write(SyntaxNode node, SourceFileNode? source, EmitTextWriter output, SourceMapGenerator? map = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(output);
        this.cancellation = cancellation;
        writer = new(output, options.OmitTrailingSemicolon, positions is not null);
        writer.Clear();
        sourceFile = node as SourceFileNode ?? source;
        sourceHasComments = sourceFile is not null && HasSourceComments(sourceFile.Source.Text);
        sourceScanner = sourceFile is null ? null : new(sourceFile.Source);
        sourceMap = map;
        // Token boundaries only matter for comment placement and source-map positions. Without
        // either, recipes can be written verbatim instead of being re-scanned token by token.
        fastCode = map is null && !sourceHasComments;
        commentsDisabled = options.RemoveComments;
        mapsDisabled = sourceFile?.ScriptKind == ScriptKind.JSON;
        containerPos = containerEnd = declarationListContainerEnd = -1;
        if (sourceFile is not null && map is not null && !mapsDisabled)
        {
            sourceIndex = map.AddSource(sourceFile.FileName);
            if (options.InlineSources)
                map.SetSourceContent(sourceIndex, sourceFile.Source.Text);
        }
        pending.Clear();
        states.Clear();
        detachedComments.Clear();
        pending.Push(N(node));
        try
        {
            InitializeNames();
            while (pending.TryPop(out var part))
            {
                cancellation.ThrowIfCancellationRequested();
                if (part.IndentationChange > 0)
                    writer.IncreaseIndent();
                else if (part.IndentationChange < 0)
                    writer.DecreaseIndent();
                if (part.PositionTarget is { } positionTarget)
                    positions?.Record(positionTarget, writer.LastNonTriviaPosition, part.EndPosition);
                else if (part.NameScopeChange != 0)
                {
                    bool reuse = (states.Peek().Flags & EmitFlags.ReuseTempVariableScope) != 0;
                    if (part.NameScopeChange > 0)
                        nameGenerator.PushScope(reuse);
                    else
                        nameGenerator.PopScope(reuse);
                }
                else if (part.GenerateList is { } generateList)
                {
                    foreach (var declaration in generateList)
                        GenerateNames(declaration);
                }
                else if (part.GenerateNode is { } generateNode)
                    GenerateNames(generateNode);
                else if (part.EndNode)
                    EndNode();
                else if (part.TrailingSemicolon)
                    writer.WriteTrailingSemicolon();
                else if (part.Helpers is { } helpers)
                    WriteHelpers(helpers);
                else if (part.DetachedPosition is { } detachedPosition)
                    DetachedComments(detachedPosition);
                else if (part.Directives is { } directives)
                    WriteDirectives(directives);
                else if (part.NewLine)
                    writer.WriteLine();
                else if (part.CommentPosition is { } position)
                {
                    if (part.ListComment)
                        ListComments(position);
                    else if (part.TrailingComment)
                        TrailingComments(position);
                    else
                        LeadingComments(position);
                    if (states.TryPeek(out var owner))
                        owner.Cursor = Math.Max(owner.Cursor, SkipTrivia(position));
                }
                else if (part.Text is { } text)
                {
                    if (part.Raw)
                        writer.Write(text);
                    else
                        Code(text);
                }
                else if (part.Parts is { } parts)
                {
                    for (int i = parts.Count - 1; i >= 0; i--)
                        pending.Push(parts[i]);
                }
                else if (part.Node is { } child)
                {
                    BeginNode(child);
                    pending.Push(new(EndNode: true));
                    Emit(child);
                }
            }
        }
        finally
        {
            pending.Clear();
            states.Clear();
            sourceScanner = null;
            sourceFile = null;
            sourceMap = null;
            nameGenerator = null!;
        }
    }

    /// <summary>
    /// The enclosing node's state (second from the top). Stack enumeration uses a struct
    /// enumerator, so this replaces the per-call LINQ iterators that previously showed up as
    /// allocation in the printer's hottest path.
    /// </summary>
    private NodeState? ParentState()
    {
        int index = 0;
        foreach (var state in states)
            if (index++ == 1)
                return state;
        return null;
    }

    private static bool HasSourceComments(Utf8String text) =>
        text.Span.IndexOf("//"u8) >= 0 || text.Span.IndexOf("/*"u8) >= 0;

    private void BeginNode(SyntaxNode node)
    {
        var flags = context.GetFlags(node);
        bool functionBody = node is BlockNode && states.TryPeek(out var parent) && parent.Node is IFunctionSignature or ClassStaticBlockDeclarationNode;
        bool parenthesized = states.TryPeek(out var enclosing) && NeedsParentheses(enclosing.Node, node);
        bool arrowBody = parenthesized && enclosing?.Node is ArrowFunctionNode { Body: { } body } && body == node;
        if (parenthesized && !arrowBody)
            writer.Write("("u8);
        if (node is not SourceFileNode) positions?.Record(node, writer.LastNonTriviaPosition, false);
        var state = statePool.Count != 0 ? statePool.Pop() : new NodeState();
        state.Reset(node, flags, node.Pos);
        state.Parenthesized = parenthesized;
        state.FunctionBody = functionBody;
        state.InExtends = !parenthesized && enclosing is not null && InExtendsForChild(enclosing.Node, node);
        state.Comments = !functionBody && !commentsDisabled && sourceFile is not null && node is not SourceFileNode;
        state.Maps = !functionBody && !mapsDisabled && sourceFile is not null && sourceMap is not null && node is not SourceFileNode;
        state.OldCommentsDisabled = commentsDisabled;
        state.OldMapsDisabled = mapsDisabled;
        state.ContainerPos = containerPos;
        state.ContainerEnd = containerEnd;
        state.DeclarationListContainerEnd = declarationListContainerEnd;
        state.CommentRange = context.GetCommentRange(node);
        state.MapRange = context.GetSourceMapRange(node);
        states.Push(state);
        if ((flags & EmitFlags.Indented) != 0 && node is not ClassDeclarationNode and not ClassExpressionNode)
            writer.IncreaseIndent();
        if (state.Comments)
        {
            // A file without "//" or "/*" can never yield a source comment, so the whole scan is
            // skipped; synthetic comments (added by transforms) are emitted either way.
            if (sourceHasComments)
            {
                var range = state.CommentRange;
                if ((range.Pos >= 0 || range.End >= 0) && range.Pos != range.End && node.Kind != K.JsxText)
                {
                    if ((flags & EmitFlags.NoLeadingComments) == 0)
                        LeadingComments(range.Pos, node.Kind == K.NotEmittedStatement);
                    if (range.Pos >= 0)
                        containerPos = range.Pos;
                    if (range.End >= 0)
                    {
                        containerEnd = range.End;
                        if (node.Kind == K.VariableDeclarationList)
                            declarationListContainerEnd = range.End;
                    }
                }
            }
            if ((flags & EmitFlags.NoLeadingComments) == 0)
                foreach (var comment in context.LeadingComments(node))
                    SyntheticComment(comment, leading: true);
            commentsDisabled |= (flags & EmitFlags.NoNestedComments) != 0;
        }
        if (arrowBody) writer.Write("("u8);
        if (state.Maps)
        {
            if (node.Kind != K.NotEmittedStatement && (flags & EmitFlags.NoLeadingSourceMap) == 0)
                MapPosition(SkipTrivia(state.MapRange.Pos));
            mapsDisabled |= (flags & EmitFlags.NoNestedSourceMaps) != 0;
        }
    }

    private void EndNode()
    {
        var state = states.Pop();
        mapsDisabled = state.OldMapsDisabled;
        if (state.Maps && state.Node.Kind != K.NotEmittedStatement && (state.Flags & EmitFlags.NoTrailingSourceMap) == 0)
            MapPosition(state.MapRange.End);
        commentsDisabled = state.OldCommentsDisabled;
        if (state.Comments)
        {
            if ((state.Flags & EmitFlags.NoTrailingComments) == 0)
                foreach (var comment in context.TrailingComments(state.Node))
                    SyntheticComment(comment, leading: false);
            containerPos = state.ContainerPos;
            containerEnd = state.ContainerEnd;
            declarationListContainerEnd = state.DeclarationListContainerEnd;
            if (sourceHasComments && (state.Flags & EmitFlags.NoTrailingComments) == 0 && state.Node.Kind is not (K.JsxText or K.NotEmittedStatement))
            {
                TrailingComments(state.CommentRange.End);
                if (context.GetTypeNode(state.Node) is { } type)
                    TrailingComments(type.End);
            }
        }
        if ((state.Flags & EmitFlags.Indented) != 0 && state.Node is not ClassDeclarationNode and not ClassExpressionNode)
            writer.DecreaseIndent();
        if (state.Node is not SourceFileNode) positions?.Record(state.Node, writer.LastNonTriviaPosition, true);
        if (state.Parenthesized)
            writer.Write(")"u8);
        if (states.TryPeek(out var parent) && state.Node.End >= 0 && state.Node.End <= parent.Node.End)
            parent.Cursor = Math.Max(parent.Cursor, Math.Max(state.Node.End, context.GetTypeNode(state.Node)?.End ?? -1));
        if (statePool.Count < 64)
            statePool.Push(state);
    }

    private void MapPosition(int position)
    {
        if (mapsDisabled || sourceFile is null || sourceMap is null || position < 0)
            return;
        var source = sourceFile.Source;
        int index = sourceIndex;
        if (options.MapSourcePosition is { } mapPosition)
        {
            if (mapPosition(sourceFile, position) is not { } mapped)
            {
                sourceMap.AddGeneratedMapping(writer.Line, writer.Column);
                return;
            }
            source = mapped.Source;
            position = mapped.Position;
            index = sourceMap.AddSource(mapped.FileName);
            if (options.InlineSources) sourceMap.SetSourceContent(index, source.Text);
        }
        int end = Math.Min(position, source.Length);
        var (line, _) = source.GetLineAndCharacter(end);
        int column = EmitTextWriter.Utf16Length(source.Text.Span[source.LineStarts[line]..end]) + position - end;
        sourceMap.AddSourceMapping(writer.Line, writer.Column, index, line, column);
    }

    private int SkipTrivia(int position)
    {
        if (position < 0 || sourceScanner is null || position > sourceScanner.End)
            return position;
        sourceScanner.ResetPosition(position);
        sourceScanner.Scan();
        return sourceScanner.TokenStart;
    }

    private void Code(Utf8String text)
    {
        if (fastCode)
        {
            writer.Write(text);
            return;
        }
        // Recipes contain only punctuation, keywords, and spaces. Literal and JSX
        // text use Literal, so embedded comment-like text is never scanned here.
        // The scanner and its source text are reused: constructing them per call cost one
        // text copy, one scanner and two lists for every punctuation/keyword written.
        var recipeText = recipeSource ??= new SourceText([]);
        recipeText.Reset(text.Span);
        var scanner = recipeScanner ??= new Scanner(recipeText);
        scanner.Reset(recipeText);
        int previous = 0;
        while (scanner.Scan() != K.EndOfFile)
        {
            writer.Write(text.Span[previous..scanner.TokenStart]);
            bool matched = false;
            int start = -1, end = -1;
            NodeState? state = states.TryPeek(out var current) ? current : null;
            if (state is not null && sourceScanner is not null && state.Cursor >= 0 && state.Cursor <= sourceScanner.End)
            {
                sourceScanner.ResetPosition(state.Cursor);
                if (sourceScanner.Scan() == scanner.Kind)
                {
                    start = sourceScanner.TokenStart;
                    end = sourceScanner.Position;
                    matched = end <= state.Node.End;
                    if (sourceHasComments && matched && TokenComments(state.Node, scanner.Kind) && context.ParseNode(state.Node)?.Kind == state.Node.Kind && state.Cursor != state.Node.Pos)
                        LeadingComments(state.Cursor);
                }
            }
            var range = state is null ? null : context.GetTokenSourceMapRange(state.Node, scanner.Kind);
            bool braceMap = state is not null && (matched || range is not null || (state.Node.Flags & NodeFlags.Synthesized) != 0 || context.Original(state.Node) is not null)
                && scanner.Kind is K.OpenBraceToken or K.CloseBraceToken && !options.OmitBraceSourceMapPositions
                && (state.Node is JsxExpressionNode or ModuleBlockNode or CaseBlockNode || state.Node is BlockNode
                    && (scanner.Kind == K.CloseBraceToken || !state.FunctionBody));
            if (braceMap && (!matched || state!.Node is BlockNode && scanner.Kind == K.CloseBraceToken))
            {
                int position = scanner.Kind == K.OpenBraceToken ? state!.Node.Pos : state!.Node switch
                {
                    BlockNode n => n.Statements?.End ?? -1,
                    ModuleBlockNode n => n.Statements?.End ?? -1,
                    CaseBlockNode n => n.Clauses?.End ?? -1,
                    JsxExpressionNode n => n.Expression?.End ?? n.Pos + 1,
                    _ => -1
                };
                start = SkipTrivia(position);
                end = start < 0 ? start : start + scanner.TokenText.Length;
            }
            if (braceMap && (state!.Flags & EmitFlags.NoTokenLeadingSourceMaps) == 0)
                MapPosition(SkipTrivia(range?.Pos ?? start));
            writer.Write(scanner.TokenText);
            if (braceMap && (state!.Flags & EmitFlags.NoTokenTrailingSourceMaps) == 0)
                MapPosition(range?.End ?? end);
            if (matched)
            {
                state!.Cursor = end;
                if (sourceHasComments && end != state.Node.End && TokenComments(state.Node, scanner.Kind))
                    TrailingComments(end, state.Node.Kind != K.JsxExpression);
            }
            previous = scanner.Position;
        }
        writer.Write(text.Span[previous..]);
    }

    private void Literal(Utf8String text) => writer.WriteLiteral(text.Span);
    private void Literal(ReadOnlySpan<byte> text) => writer.WriteLiteral(text);
    private void Literal(byte value) => writer.WriteLiteral([value]);
    private static bool TokenComments(SyntaxNode node, K token) => token switch
    {
        K.FunctionKeyword when node is FunctionDeclarationNode or FunctionExpressionNode => false,
        K.ColonToken when node is VariableDeclarationNode => false,
        K.OpenParenToken when node is ForInOrOfStatementNode { AwaitModifier: not null } => false,
        K.OpenParenToken or K.CommaToken when node is IFunctionSignature or CallExpressionNode or NewExpressionNode
            or VariableDeclarationListNode or ArrayLiteralExpressionNode or ObjectLiteralExpressionNode or BindingPatternNode => false,
        K.ConstKeyword or K.LetKeyword or K.VarKeyword or K.UsingKeyword when node is VariableDeclarationListNode => false,
        K.OpenBracketToken when node is ArrayLiteralExpressionNode or BindingPatternNode => false,
        K.OpenBracketToken when node is IndexSignatureDeclarationNode => false,
        K.LessThanToken when node is IFunctionSignature or ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode => false,
        K.OpenBraceToken or K.CloseBraceToken when node is ClassDeclarationNode or ClassExpressionNode or InterfaceDeclarationNode
            or TypeLiteralNode or ObjectLiteralExpressionNode or BindingPatternNode or MappedTypeNode or EnumDeclarationNode => false,
        K.OpenBraceToken or K.CloseBraceToken when node is BlockNode { Parent: IFunctionSignature or ClassStaticBlockDeclarationNode } => false,
        _ => true
    };
    private static Part N(SyntaxNode? node) => new(Node: node);
    private static Part T(Utf8String text) => new(Text: text);
    private static Part Semicolon() => new(TrailingSemicolon: true);
    private static Part S(params ReadOnlySpan<Part> parts)
    {
        return new(Parts: parts.ToArray());
    }
    // Scoped wraps a node in name-scope markers. The small-arity overloads build an exact-size
    // array instead of copying the caller's parts into a parts.Length + 2 array: a container
    // element expands to the same sequence, so nesting the inner container is equivalent and
    // avoids copying every part of large lists (object literals, type literals) a second time.
    private static Part Scoped(Part inner) => new(Parts: [new(NameScopeChange: 1), inner, new(NameScopeChange: -1)]);
    private static Part Scoped(Part first, Part second) =>
        new(Parts: [new(NameScopeChange: 1), first, second, new(NameScopeChange: -1)]);
    private static Part Scoped(Part first, Part second, Part third) =>
        new(Parts: [new(NameScopeChange: 1), first, second, third, new(NameScopeChange: -1)]);
    private static Part Scoped(params ReadOnlySpan<Part> parts)
    {
        var result = new Part[parts.Length + 2];
        result[0] = new(NameScopeChange: 1);
        parts.CopyTo(result.AsSpan(1));
        result[^1] = new(NameScopeChange: -1);
        return new(Parts: result);
    }
    private static Part Generate(NodeList? list) => new(GenerateList: list);
    private void Push(params ReadOnlySpan<Part> parts)
    {
        for (int i = parts.Length - 1; i >= 0; i--)
            pending.Push(parts[i]);
    }

    // Part buffers are pooled per printer. A buffer never escapes: its contents are either copied
    // into a container or pushed onto the work stack before it is returned, so reuse cannot alias
    // live parts, and Clear() drops the element references so a pooled buffer cannot keep a tree
    // alive. This removes the List doubling chain that dominated list-shaped nodes.
    private readonly Stack<List<Part>> partListPool = [];

    private List<Part> RentPartList(int capacity)
    {
        if (partListPool.TryPop(out var list))
        {
            list.EnsureCapacity(capacity);
            return list;
        }
        return new List<Part>(capacity);
    }

    private void ReturnPartList(List<Part> list)
    {
        list.Clear();
        if (partListPool.Count < 8)
            partListPool.Push(list);
    }

    private Part List(IReadOnlyList<SyntaxNode>? nodes, Utf8String before, Utf8String separator, Utf8String after, bool required = false, bool indent = false)
    {
        if (nodes is null && !required)
            return default;
        if (nodes is { Count: 0 } && before == Utf8Literals.Space && after.Length == 0)
            return S(ListPosition(nodes as NodeList), ListPosition(nodes as NodeList, true));
        var parts = RentPartList(4 * (nodes?.Count ?? 0) + 8);
        try
        {
            BuildList(parts, nodes, before, separator, after, required, indent);
            return new(Parts: parts.ToArray());
        }
        finally
        {
            ReturnPartList(parts);
        }
    }

    /// <summary>
    /// Composes the same parts as <see cref="List"/> but pushes them straight onto the work stack
    /// instead of materializing a container. A container would be filled (copying every part) and
    /// then expanded (copying them all again); pushing in reverse yields the identical order.
    /// </summary>
    private void PushList(IReadOnlyList<SyntaxNode>? nodes, Utf8String before, Utf8String separator, Utf8String after, bool required = false, bool indent = false)
    {
        if (nodes is null && !required)
            return;
        if (nodes is { Count: 0 } && before == Utf8Literals.Space && after.Length == 0)
        {
            pending.Push(ListPosition(nodes as NodeList, true));
            pending.Push(ListPosition(nodes as NodeList));
            return;
        }
        var parts = RentPartList(4 * (nodes?.Count ?? 0) + 8);
        try
        {
            BuildList(parts, nodes, before, separator, after, required, indent);
            for (int i = parts.Count - 1; i >= 0; i--)
                pending.Push(parts[i]);
        }
        finally
        {
            ReturnPartList(parts);
        }
    }

    private void BuildList(List<Part> parts, IReadOnlyList<SyntaxNode>? nodes, Utf8String before, Utf8String separator,
        Utf8String after, bool required, bool indent)
    {
        parts.Add(T(before));
        parts.Add(ListPosition(nodes as NodeList));
        indent |= separator == Utf8Literals.CommaSpace && nodes?.Any(node => (context.GetFlags(node) & EmitFlags.StartOnNewLine) != 0) == true;
        if (indent)
            parts.Add(new(IndentationChange: 1));
        if (nodes is NodeList { Count: 0 } empty && before.Length != 0 && before[0] is (byte)'(' or (byte)'[' or (byte)'{')
            parts.Add(new(CommentPosition: empty.Pos, TrailingComment: true));
        if (nodes is not null)
            for (int i = 0; i < nodes.Count; i++)
            {
                bool newLine = separator == Utf8Literals.CommaSpace && (context.GetFlags(nodes[i]) & EmitFlags.StartOnNewLine) != 0;
                if (i != 0)
                    parts.Add(T(newLine ? Utf8Literals.Comma : separator));
                if (newLine) parts.Add(new(NewLine: true));
                if (separator == Utf8Literals.CommaSpace || separator == Utf8Literals.UnionSeparator
                    || separator == Utf8Literals.IntersectionSeparator || before == Utf8Literals.OpenParen || before == Utf8Literals.OpenBraceSpace)
                    parts.Add(new(CommentPosition: (context.GetFlags(nodes[i]) & EmitFlags.NoLeadingComments) == 0 ? context.GetCommentRange(nodes[i]).Pos : -1, ListComment: true));
                parts.Add(N(nodes[i]));
            }
        if (indent)
            parts.Add(new(IndentationChange: -1));
        bool closingLine = (options.PreserveSourceNewlines || states.Peek().Node is ImportAttributesNode) && nodes is { Count: > 0 }
            && after.Length != 0 && ClosingLines(states.Peek().Node, nodes[^1], (nodes as NodeList)?.End ?? -1) > 0;
        if (nodes is NodeList { Count: > 0 } list && after.Length > 1 && after[0] == ',')
        {
            parts.Add(T(Utf8Literals.Comma));
            parts.Add(new(CommentPosition: list.End, TrailingComment: true));
            parts.Add(ListPosition(nodes as NodeList, true));
            if (closingLine) parts.Add(new(NewLine: true));
            parts.Add(T(closingLine ? after[1..].TrimStart((byte)' ') : after[1..]));
        }
        else
        {
            parts.Add(ListPosition(nodes as NodeList, true));
            if (closingLine) parts.Add(new(NewLine: true));
            parts.Add(T(closingLine ? after.TrimStart((byte)' ') : after));
        }
    }

    private Part Modifiers(SyntaxNode node)
    {
        if (node is not IModifiedNode { Modifiers.Count: > 0 } modified)
            return default;
        if (options.PreserveSourceNewlines)
        {
            var preserved = new List<Part>();
            for (int start = 0; start < modified.Modifiers.Count;)
            {
                bool decorators = modified.Modifiers[start] is DecoratorNode;
                int end = start + 1;
                while (end < modified.Modifiers.Count && (modified.Modifiers[end] is DecoratorNode) == decorators) end++;
                if (!decorators || node is ClassDeclarationNode or ClassExpressionNode or PropertyDeclarationNode or MethodDeclarationNode
                    or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ParameterDeclarationNode)
                {
                    for (int i = start; i < end; i++)
                    {
                        preserved.Add(ListBoundary(node, i == start ? null : modified.Modifiers[i - 1], modified.Modifiers[i], decorators && (i != start || node is not ParameterDeclarationNode), !decorators && i != start));
                        preserved.Add(N(modified.Modifiers[i]));
                    }
                    int listEnd = start == 0 && end == modified.Modifiers.Count || end == modified.Modifiers.Count - 1 ? modified.Modifiers.End : -1;
                    preserved.Add(ClosingLines(node, modified.Modifiers[end - 1], listEnd, decorators) > 0 ? new(NewLine: true) : T(" "u8));
                }
                start = end;
            }
            return S(ListPosition(modified.Modifiers), new(Parts: preserved), ListPosition(modified.Modifiers, true));
        }
        var parts = new List<Part>(3 * modified.Modifiers.Count + 1);
        foreach (var modifier in modified.Modifiers)
        {
            if (modifier is DecoratorNode)
            {
                if (node is not (ClassDeclarationNode or ClassExpressionNode or PropertyDeclarationNode or MethodDeclarationNode
                    or GetAccessorDeclarationNode or SetAccessorDeclarationNode or ParameterDeclarationNode))
                    continue;
                parts.Add(new(NewLine: true));
                parts.Add(N(modifier));
                parts.Add(new(NewLine: true));
            }
            else
            {
                parts.Add(N(modifier));
                parts.Add(T(Utf8Literals.Space));
            }
        }
        return new(Parts: parts);
    }
    private static Part Annotation(SyntaxNode? type) => type is null ? default : S(T(Utf8Literals.ColonSpace), N(type));
    private static Part Initializer(SyntaxNode? value) => value is null ? default : S(T(Utf8Literals.AssignmentSeparator), N(value));
    private Part Parameters(NodeList? nodes) => List(nodes, Utf8Literals.OpenParen, Utf8Literals.CommaSpace, Utf8Literals.CloseParen, true);
    private Part TypeArguments(NodeList? nodes) => List(nodes, Utf8Literals.LessThan, Utf8Literals.CommaSpace, Utf8Literals.GreaterThan);
    private Part Braces(NodeList? nodes, Utf8String separator, bool spaceWhenEmpty = false) => nodes is { Count: > 0 }
        ? List(nodes, Utf8Literals.OpenBraceSpace, separator, Utf8Literals.SpaceCloseBrace)
        : List(nodes, Utf8Literals.OpenBrace, default, spaceWhenEmpty ? Utf8Literals.SpaceCloseBrace : Utf8Literals.CloseBrace, true);
    private static Part Body(SyntaxNode? body) => body is null ? Semicolon() : S(T(Utf8Literals.Space), N(body));
    private Part TypeMembers(TypeLiteralNode node) => Scoped(Generate(node.Members),
        (context.GetFlags(node) & EmitFlags.SingleLine) != 0 || node.Members is not { Count: > 0 }
            ? Braces(node.Members, Utf8Literals.Space) : BlockMembers(node.Members));

    /// <summary>Container-free counterpart of <see cref="TypeMembers"/>.</summary>
    private void PushTypeMembers(TypeLiteralNode node)
    {
        pending.Push(new(NameScopeChange: -1));
        if ((context.GetFlags(node) & EmitFlags.SingleLine) != 0 || node.Members is not { Count: > 0 })
            PushBraces(node.Members, Utf8Literals.Space);
        else
            PushBlockMembers(node.Members);
        pending.Push(Generate(node.Members));
        pending.Push(new(NameScopeChange: 1));
    }

    /// <summary>Container-free counterpart of <see cref="Braces"/>.</summary>
    private void PushBraces(NodeList? nodes, Utf8String separator, bool spaceWhenEmpty = false)
    {
        if (nodes is { Count: > 0 })
            PushList(nodes, Utf8Literals.OpenBraceSpace, separator, Utf8Literals.SpaceCloseBrace);
        else
            PushList(nodes, Utf8Literals.OpenBrace, default, spaceWhenEmpty ? Utf8Literals.SpaceCloseBrace : Utf8Literals.CloseBrace, required: true);
    }
    private bool SingleLine(SyntaxNode node) => (context.GetFlags(node) & EmitFlags.SingleLine) != 0
        || (context.GetFlags(node) & EmitFlags.MultiLine) == 0 && sourceFile is not null && node.Pos >= 0 && node.End >= 0
            && LineOf(SkipTrivia(node.Pos)) == LineOf(node.End);

    private Part BlockBody(BlockNode block)
    {
        bool functionBody = states.Count > 1 && ParentState()!.Node is IFunctionSignature or ClassStaticBlockDeclarationNode;
        bool singleLine = (context.GetFlags(block) & EmitFlags.SingleLine) != 0
            || functionBody && !block.MultiLine && (SingleLine(block) || block.Pos < 0 || sourceFile is null)
            || block.Statements is not { Count: > 0 } && !block.MultiLine;
        if (functionBody && options.PreserveSourceNewlines && (context.GetFlags(block) & EmitFlags.SingleLine) == 0
            && (LeadingLines(block, block.Statements?.FirstOrDefault()) > 0 || ClosingLines(block, block.Statements?.LastOrDefault(), block.Statements?.End ?? -1) > 0))
            singleLine = false;
        if ((context.GetFlags(block) & EmitFlags.SingleLine) == 0 && block.Statements?.Any(n => (context.GetFlags(n) & EmitFlags.StartOnNewLine) != 0) == true)
            singleLine = false;
        if (!functionBody)
            return singleLine ? Braces(block.Statements, Utf8Literals.Space, true) : BlockMembers(block.Statements, endComments: true);
        var parts = new List<Part>(3 * (block.Statements?.Count ?? 0) + 10) { new(GenerateNode: block), T("{"u8), new(IndentationChange: 1), new(DetachedPosition: block.Statements?.Pos ?? block.Pos) };
        int offset = AddPrologues(parts, block.Statements);
        parts.Add(new(Helpers: block));
        singleLine &= offset == 0 && !HelpersFor(block).Any(h => h.Text.Length != 0 || h.TextFactory is not null);
        if (singleLine)
            parts.Add(new(IndentationChange: -1));
        parts.Add(ListPosition(block.Statements));
        if (block.Statements is { } statements)
            for (int i = offset; i < statements.Count; i++)
            {
                parts.Add(options.PreserveSourceNewlines ? ListBoundary(block, i == offset ? null : statements[i - 1], statements[i], !singleLine, singleLine)
                    : singleLine ? T(" "u8) : new(NewLine: true));
                if (singleLine)
                    parts.Add(new(CommentPosition: (context.GetFlags(statements[i]) & EmitFlags.NoLeadingComments) == 0 ? context.GetCommentRange(statements[i]).Pos : -1, ListComment: true));
                parts.Add(N(statements[i]));
            }
        if (singleLine)
        {
            parts.Add(T(" "u8));
            parts.Add(new(IndentationChange: 1));
        }
        parts.Add(ListPosition(block.Statements, true));
        parts.Add(new(CommentPosition: block.Statements?.End ?? block.End));
        parts.Add(new(IndentationChange: -1, NewLine: options.PreserveSourceNewlines
            ? ClosingLines(block, block.Statements?.LastOrDefault(), block.Statements?.End ?? -1, !singleLine) > 0 : !singleLine));
        parts.Add(T("}"u8));
        return new(Parts: parts);
    }

    /// <summary>
    /// Container-free counterpart of <see cref="BlockMembers"/>: identical part sequence, pushed in
    /// reverse order so the work stack processes it in the same order without a parts container.
    /// </summary>
    private void PushBlockMembers(NodeList? nodes, bool comma = false, bool endComments = false,
        Utf8String open = default, Utf8String close = default, bool trailingComma = true)
    {
        bool closingLine = !options.PreserveSourceNewlines
            || ClosingLines(states.Peek().Node, nodes?.LastOrDefault(), nodes?.End ?? -1, true) > 0;
        pending.Push(T(close.Length == 0 ? Utf8Literals.CloseBrace : close));
        pending.Push(new(IndentationChange: -1, NewLine: closingLine));
        if (nodes is not null && endComments)
            pending.Push(new(CommentPosition: nodes.End));
        pending.Push(ListPosition(nodes, true));
        if (nodes is not null)
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                if (comma && (i + 1 < nodes.Count || trailingComma && nodes.HasTrailingComma))
                    pending.Push(T(Utf8Literals.Comma));
                pending.Push(N(nodes[i]));
                if (options.PreserveSourceNewlines)
                {
                    pending.Push(new(CommentPosition: nodes[i].Pos, ListComment: true));
                    pending.Push(ListBoundary(states.Peek().Node, i == 0 ? null : nodes[i - 1], nodes[i], true,
                        comma && i != 0 && states.Peek().Node is TupleTypeNode));
                }
                else
                    pending.Push(new(NewLine: true));
            }
        pending.Push(new(IndentationChange: 1));
        pending.Push(ListPosition(nodes));
        pending.Push(T(open.Length == 0 ? Utf8Literals.OpenBrace : open));
    }

    private Part BlockMembers(NodeList? nodes, bool comma = false, bool endComments = false,
        Utf8String open = default, Utf8String close = default, bool trailingComma = true)
    {
        var parts = new List<Part>(4 * (nodes?.Count ?? 0) + 7) { T(open.Length == 0 ? Utf8Literals.OpenBrace : open), ListPosition(nodes), new(IndentationChange: 1) };
        if (nodes is not null)
            for (int i = 0; i < nodes.Count; i++)
            {
                parts.Add(options.PreserveSourceNewlines ? ListBoundary(states.Peek().Node, i == 0 ? null : nodes[i - 1], nodes[i], true,
                    comma && i != 0 && states.Peek().Node is TupleTypeNode) : new(NewLine: true));
                if (options.PreserveSourceNewlines) parts.Add(new(CommentPosition: nodes[i].Pos, ListComment: true));
                parts.Add(N(nodes[i]));
                if (comma && (i + 1 < nodes.Count || trailingComma && nodes.HasTrailingComma))
                    parts.Add(T(Utf8Literals.Comma));
            }
        parts.Add(ListPosition(nodes, true));
        if (nodes is not null && endComments)
            parts.Add(new(CommentPosition: nodes.End));
        parts.Add(new(IndentationChange: -1, NewLine: !options.PreserveSourceNewlines
            || ClosingLines(states.Peek().Node, nodes?.LastOrDefault(), nodes?.End ?? -1, true) > 0));
        parts.Add(T(close.Length == 0 ? Utf8Literals.CloseBrace : close));
        return new(Parts: parts);
    }

    private Part Embedded(SyntaxNode? node) => node is BlockNode || (context.GetFlags(states.Peek().Node) & EmitFlags.SingleLine) != 0
        || options.PreserveSourceNewlines && LeadingLines(states.Peek().Node, node) == 0 ? S(T(Utf8Literals.Space), N(node))
        : S(new(IndentationChange: 1, NewLine: true), N(node), new(IndentationChange: -1));

    private bool NewLineBetween(SyntaxNode? left, SyntaxNode? right) => right is not null && (context.GetFlags(right) & EmitFlags.StartOnNewLine) != 0
        || left is not null && right is not null && left.End >= 0 && right.Pos >= 0
        && LineOf(left.End) != LineOf(SkipTrivia(right.Pos));
    private static Part Separator(bool newLine, bool space = true) => newLine ? new(IndentationChange: 1, NewLine: true)
        : space ? T(Utf8Literals.Space) : default;
    private static Part EndSeparator(bool newLine) => newLine ? new(IndentationChange: -1) : default;

    private Utf8String? OriginalText(SyntaxNode node)
    {
        if (options.TerminateUnterminatedLiterals && IsUnterminated(node)) return null;
        if (sourceFile is null || node.Parent is null || (node.Flags & (NodeFlags.Synthesized | NodeFlags.Reparsed | NodeFlags.JSDoc)) != 0 || node.Pos < 0 || node.End < node.Pos)
            return null;
        if (node is IdentifierNode or PrivateIdentifierNode && SemanticSyntax.Source(node) != context.MostOriginal(sourceFile))
            return null;
        if (node.Pos == node.End)
            return Utf8String.Empty;
        int start = SkipTrivia(node.Pos);
        return start <= node.End && node.End <= sourceFile.Source.Length ? sourceFile.Source.Text[start..node.End] : null;
    }

    private Utf8String NumberText(NumericLiteralNode literal) => (literal.TokenFlags & TokenFlags.IsInvalid) == 0
        && ((literal.TokenFlags & TokenFlags.ContainsSeparator) == 0 || options.TargetYear >= 2021)
        ? OriginalText(literal) ?? literal.Text : literal.Text;
    private static bool IsUnterminated(SyntaxNode node) => ((node switch
    {
        StringLiteralNode literal => literal.TokenFlags, NumericLiteralNode literal => literal.TokenFlags,
        BigIntLiteralNode literal => literal.TokenFlags, RegularExpressionLiteralNode literal => literal.TokenFlags,
        NoSubstitutionTemplateLiteralNode literal => literal.TemplateFlags, TemplateHeadNode literal => literal.TemplateFlags,
        TemplateMiddleNode literal => literal.TemplateFlags, TemplateTailNode literal => literal.TemplateFlags, _ => TokenFlags.None,
    }) & TokenFlags.Unterminated) != 0;
    private static bool HasTrailingComma(NodeList? nodes) => nodes?.HasTrailingComma == true;

    private void EmitSourceFile(SourceFileNode file)
    {
        writer.WriteLine();
        if ((file.Flags & NodeFlags.Synthesized) == 0 && file.ScriptKind != ScriptKind.JSON && file.Source.Text.Span.StartsWith("#!"u8))
        {
            int end = file.Source.Text.Span.IndexOfAny((byte)'\r', (byte)'\n');
            writer.Write(file.Source.Text.Span[..(end < 0 ? file.Source.Length : end)]);
            writer.WriteLine();
        }
        var parts = new List<Part> { new(NameScopeChange: 1), Generate(file.Statements) };
        int offset = file.ScriptKind == ScriptKind.JSON ? 0 : AddPrologues(parts, file.Statements);
        bool detached = file.Statements is not { Count: > 0 } || file.Statements[0] is not ExpressionStatementNode { Expression: StringLiteralNode }
            || (file.Statements[0].Flags & NodeFlags.Synthesized) != 0;
        parts.Add(new(NewLine: true));
        if (detached)
            parts.Add(new(DetachedPosition: file.Statements?.Pos ?? 0));
        if (file.ScriptKind != ScriptKind.JSON)
        {
            parts.Add(new(Helpers: file));
            if (file.IsDeclarationFile)
                parts.Add(new(Directives: file));
        }
        parts.Add(ListPosition(file.Statements));
        if (file.Statements is { } statements)
            for (int i = offset; i < statements.Count; i++)
            {
                parts.Add(options.PreserveSourceNewlines ? ListBoundary(file, i == offset ? null : statements[i - 1], statements[i], true) : new(NewLine: true));
                if (options.PreserveSourceNewlines) parts.Add(new(CommentPosition: statements[i].Pos, ListComment: true));
                parts.Add(N(statements[i]));
                if (!options.PreserveSourceNewlines) parts.Add(new(NewLine: true));
            }
        parts.Add(ListPosition(file.Statements, true));
        if (detached)
            parts.Add(new(CommentPosition: file.Statements?.End ?? file.End));
        if (!options.PreserveSourceNewlines || ClosingLines(file, file.Statements?.LastOrDefault(), file.Statements?.End ?? -1, true) > 0)
            parts.Add(new(NewLine: true));
        parts.Add(new(NameScopeChange: -1));
        pending.Push(new(Parts: parts));
    }
}
