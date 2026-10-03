using System.Text;
using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Checking;
using TypeScript.Compiler.Emission;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;
using K = TypeScript.Compiler.Syntax.SyntaxKind;

namespace TypeScript.Compiler.LanguageServices;

internal readonly record struct NodeEditOptions(Utf8String Prefix = default, Utf8String Suffix = default,
    int? Indentation = null, int? Delta = null, Utf8String Joiner = default);

internal sealed partial class SourceEditTracker(DocumentProjection projection, FormatCodeSettings settings, CancellationToken cancellation)
{
    private sealed record Change(int Start, int End, Utf8String Text, IReadOnlyList<SyntaxNode>? Nodes = null, NodeEditOptions Options = default);
    private readonly List<Change> changes = [];
    internal EmitContext Context { get; } = new();
    internal NodeFactory Factory => Context.Factory;
    internal bool IsMappable { get; private set; } = true;
    private SourceFileNode File => projection.File;
    private Utf8String Text => File.Source.Text;
    private Utf8String NewLine => settings.NewLineCharacter;

    internal void ReplaceText(int start, int end, Utf8String text) => changes.Add(new(start, end, text));
    internal void InsertText(int position, Utf8String text) => ReplaceText(position, position, text);
    internal void InsertAtOriginalPosition(DocumentPosition position, Utf8String text)
    {
        var mapped = projection.FromPosition(position, MappingFeature.All).FirstOrDefault(candidate => candidate.Fidelity == MappingFidelity.Exact, new(0, MappingFidelity.None));
        if (mapped.Fidelity != MappingFidelity.Exact) { IsMappable = false; return; }
        InsertText(mapped.Position, text);
    }
    internal void ReplaceNode(SyntaxNode oldNode, SyntaxNode newNode) => Replace(Start(oldNode), oldNode.End, [newNode]);
    internal void Insert(int position, SyntaxNode node, NodeEditOptions options = default) => Replace(position, position, [node], options);
    internal void Insert(int position, IReadOnlyList<SyntaxNode> nodes, NodeEditOptions options = default) => Replace(position, position, nodes, options);
    internal void Replace(int start, int end, IReadOnlyList<SyntaxNode> nodes, NodeEditOptions options = default)
        => changes.Add(new(start, end, default, nodes, options));

    internal async ValueTask<DocumentTextEdit[]> GetChangesAsync()
    {
        if (!IsMappable) return [];
        FinishListDeletions();
        await FinishMemberInsertionsAsync();
        List<(int Start, int End, DocumentTextEdit Edit)> result = [];
        foreach (var change in changes)
        {
            cancellation.ThrowIfCancellationRequested();
            var mapped = projection.ToRange(change.Start, change.End);
            if (mapped.Fidelity != MappingFidelity.Exact) return Unmappable();
            var text = change.Text;
            if (change.Nodes is { } nodes)
            {
                Utf8String? formatted = null;
                foreach (var position in projection.FromPosition(mapped.Range.Start, MappingFeature.All))
                {
                    if (position.Fidelity != MappingFidelity.Exact) continue;
                    List<Utf8String> pieces = [];
                    foreach (var node in nodes)
                    {
                        var piece = await SourceFormatter.FormatNodeForInsertionAsync(node, File, position.Position, settings, Context,
                            change.Options.Indentation, change.Options.Delta, change.Options.Prefix == NewLine, cancellation);
                        if (nodes.Count > 1 && piece.EndsWith(NewLine)) piece = piece[..^NewLine.Length];
                        pieces.Add(piece);
                    }
                    var candidate = Utf8String.Join(change.Options.Joiner.IsEmpty ? NewLine : change.Options.Joiner, pieces);
                    if (change.Options.Indentation is null && LineStart(position.Position) != position.Position) candidate = TrimLeadingWhitespace(candidate);
                    candidate = change.Options.Prefix + candidate + (candidate.EndsWith(change.Options.Suffix) ? default : change.Options.Suffix);
                    if (formatted is { } previous && previous != candidate) return Unmappable();
                    formatted = candidate;
                }
                if (formatted is not { } value) return Unmappable();
                text = ReindentInsertion(change, value);
            }
            var original = projection.OriginalRange(mapped.Range);
            result.Add((original.Start, original.End, new(mapped.Range, text)));
        }
        var ordered = result.OrderBy(item => item.Start).ThenBy(item => item.End).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i - 1].End > ordered[i].Start) throw new InvalidOperationException("Source edits overlap");
        return ordered.Select(item => item.Edit).ToArray();

        DocumentTextEdit[] Unmappable() { IsMappable = false; return []; }
    }

    private Utf8String ReindentInsertion(Change change, Utf8String text)
    {
        if (text.IsEmpty || change.Start != change.End || change.Options.Indentation is not null || !text.EndsWith(NewLine)) return text;
        int position = change.Start;
        if (projection.Map is { } map)
        {
            var mapped = map.VirtualToOriginalPosition(position);
            if (mapped.Fidelity != MappingFidelity.Exact) return text;
            position = mapped.Position;
        }
        var original = projection.OriginalText;
        if (position < 0 || position > original.Length) return text;
        int start = position;
        while (start > 0 && original[start - 1] is not ((byte)'\r' or (byte)'\n')) start--;
        var before = original[start..position];
        if (before.IsEmpty) return LeadingIndentation(text).IsEmpty ? LeadingIndentation(original[start..]) + text : text;
        return LeadingIndentation(before) == before ? text + before : text;
    }

    private static Utf8String LeadingIndentation(Utf8String text)
    {
        int end = 0;
        while (end < text.Length && text[end] is (byte)' ' or (byte)'\t') end++;
        return text[..end];
    }

    private static Utf8String TrimLeadingWhitespace(Utf8String text)
    {
        int start = 0;
        while (start < text.Length)
        {
            int point = Wtf8.Decode(text.Span[start..], out int width);
            if (!Rune.IsValid(point) || !Rune.IsWhiteSpace(new(point))) break;
            start += width;
        }
        return text[start..];
    }

    internal void InsertAfter(SyntaxNode after, IReadOnlyList<SyntaxNode> nodes)
    {
        if (nodes.Count == 0) return;
        if (after is PropertySignatureDeclarationNode or PropertyDeclarationNode && nodes[0].DeclarationName is ComputedPropertyNameNode
            && Text[after.End - 1] != ';') InsertText(after.End, ";"u8);
        var options = after.Kind switch
        {
            K.Parameter => new NodeEditOptions(),
            K.ClassDeclaration or K.ModuleDeclaration => new(NewLine, NewLine),
            K.VariableDeclaration or K.StringLiteral or K.Identifier => new(", "u8),
            K.PropertyAssignment => new(Suffix: ","u8 + NewLine),
            K.ExportKeyword => new(" "u8),
            _ => new(Suffix: NewLine),
        };
        if (after.End == File.End && TypeNodeFlow.Statement(after)) options = options with { Prefix = NewLine + options.Prefix };
        Insert(AdjustedEnd(after), nodes, options);
    }

    internal void InsertBefore(SyntaxNode before, SyntaxNode node, bool blankLine = false, bool excludeLeadingTrivia = false)
    {
        Utf8String suffix = before.Kind switch
        {
            K.VariableDeclaration or K.NamedImports => ", "u8,
            K.Parameter => node is ParameterDeclarationNode ? ", "u8 : Utf8String.Empty,
            K.StringLiteral when before.Parent is ImportDeclarationNode => ", "u8,
            K.ImportSpecifier => ","u8 + (blankLine ? NewLine : (Utf8String)" "u8),
            _ => blankLine ? NewLine + NewLine : NewLine,
        };
        Insert(AdjustedStart(before, excludeLeadingTrivia), node, new(Suffix: suffix));
    }

    internal void InsertAtTop(IReadOnlyList<SyntaxNode> nodes, bool blankLine)
    {
        if (nodes.Count == 0) return;
        int position = InsertionAtTop(), originalPosition = position;
        if (projection.Map is { } map)
            foreach (var segment in map.Segments)
            {
                if (segment.Kind != MappingKind.Verbatim || segment.VirtualEnd <= position) continue;
                position = Math.Max(position, segment.VirtualStart);
                originalPosition = segment.OriginalStart + position - segment.VirtualStart;
                break;
            }
        var suffix = position >= Text.Length || !TokenFacts.IsLineBreak(Text[position]) ? NewLine : default;
        if (blankLine) suffix += NewLine;
        Insert(position, nodes, new(originalPosition != 0 ? NewLine : default, suffix));
    }

    private int Start(SyntaxNode node) => SmartIndenter.Start(node, File);
    private int Line(int position) => File.Source.GetLineAndCharacter(position).Line;
    private int LineStart(int position) => File.Source.LineStarts[Line(position)];
    private int SkipTrivia(int position, bool comments = false, bool lineBreak = false)
        => new Scanner(File.Source).SkipTriviaAt(position, stopAtComments: comments, stopAfterLineBreak: lineBreak);

    private int AdjustedStart(SyntaxNode node, bool excludeTrivia)
    {
        int start = Start(node);
        if (excludeTrivia || node.Pos == start || Line(node.Pos) == Line(start)) return start;
        int line = Line(node.Pos) + (node.Pos > 0 ? 1 : 0);
        return LineStart(SkipTrivia(File.Source.LineStarts[line], comments: true));
    }

    private int AdjustedEnd(SyntaxNode node)
    {
        int end = SkipTrivia(node.End, lineBreak: true);
        return end != node.End && TokenFacts.IsLineBreak(Text[end - 1]) ? end : node.End;
    }

    private int InsertionAtTop()
    {
        SyntaxNode? prologue = null;
        foreach (var node in File.Statements ?? new([]))
            if (node is ExpressionStatementNode { Expression: StringLiteralNode }) prologue = node; else break;
        if (prologue is not null) return AdvanceLineBreak(prologue.End);
        int position = 0;
        if (Text.StartsWith("#!"u8))
        {
            while (position < Text.Length && !TokenFacts.IsLineBreak(Text[position])) position++;
            position = AdvanceLineBreak(position);
        }
        SourceCommentRange? previous = null;
        bool pinned = false;
        int firstLine = File.Statements is { Count: > 0 } statements ? Line(Start(statements[0])) : -1;
        foreach (var comment in SyntaxPrinter.CommentRanges(Text, position, false))
        {
            bool recognized = comment.Kind == K.MultiLineCommentTrivia ? comment.End - comment.Pos > 5 && Text[comment.Pos..].StartsWith("/*!"u8)
                : RecognizedTripleSlash(Text[comment.Pos..comment.End]);
            if (recognized) { previous = comment; pinned = true; continue; }
            if (previous is { } last && (pinned || Line(comment.Pos) >= Line(last.End) + 2)) break;
            if (firstLine >= 0 && firstLine < Line(comment.End) + 2) break;
            previous = comment; pinned = false;
        }
        return previous is { } final ? AdvanceLineBreak(final.End) : position;
    }

    private int AdvanceLineBreak(int position)
    {
        if (position < Text.Length && TokenFacts.IsLineBreak(Text[position]))
        {
            byte point = Text[position++];
            if (point == '\r' && position < Text.Length && Text[position] == '\n') position++;
        }
        return position;
    }

    private static bool RecognizedTripleSlash(Utf8String text)
    {
        if (!text.StartsWith("///"u8)) return false;
        int position = 3;
        WhiteSpace();
        if (!Match("<"u8)) return false;
        if (Match("reference"u8))
        {
            if (!WhiteSpace() || !(Match("path"u8) || Match("types"u8) || Match("lib"u8) || Match("no-default-lib"u8))) return false;
            if (!AttributeValue()) return false;
        }
        else if (Match("amd-dependency"u8))
        {
            if (!WhiteSpace() || !Match("path"u8) || !AttributeValue()) return false;
        }
        else if (Match("amd-module"u8)) WhiteSpace();
        else return false;
        return text[position..].Contains("/>"u8);

        bool Match(Utf8String value)
        {
            if (!text[position..].StartsWith(value)) return false;
            position += value.Length; return true;
        }
        bool WhiteSpace()
        {
            int start = position;
            while (position < text.Length)
            {
                int point = Wtf8.Decode(text.Span[position..], out int width);
                if (!TokenFacts.IsWhiteSpace(point) || TokenFacts.IsLineBreak(point)) break;
                position += width;
            }
            return start != position;
        }
        bool AttributeValue()
        {
            WhiteSpace();
            if (!Match("="u8)) return false;
            WhiteSpace();
            if (position == text.Length || text[position] is not ((byte)'\'' or (byte)'"')) return false;
            byte quote = text[position++];
            while (position < text.Length && text[position] != quote) position++;
            if (position == text.Length) return false;
            position++; return true;
        }
    }
}
