using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    /// <summary>
    /// Smallest statement count worth splitting. Below this the per-segment printer state
    /// (scanner, work stacks, writer buffer) costs more than the borrowed parallelism saves.
    /// </summary>
    private const int SegmentMinStatements = 512;

    /// <summary>Upper bound on segments per file. Parallelism beyond this only adds GC pressure.</summary>
    private const int SegmentMaxCount = 8;

    /// <summary>
    /// Segmented printing splits one file's top-level statements across printers so a single large
    /// file can use more than one core. It is deliberately conservative: every per-file printer
    /// behaviour that would have to be shared across segments (comments and comment-driven ASI
    /// protection, generated names, emit helpers, source maps, display positions) disables it and
    /// the caller prints the file serially. What remains is the common shape that cannot benefit
    /// from file-level parallelism — one large, comment-free, helper-free source file.
    /// </summary>
    internal bool TryWriteSegmented(SourceFileNode tree, EmitTextWriter output, SourceMapGenerator? map,
        int maxSegments, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(output);
        if (maxSegments < 2 || map is not null || positions is not null || !CanSegment(tree)) return false;
        var statements = tree.Statements!;
        int prologue = PrologueOffset(tree);
        int k = SegmentCountFor(statements.Count - prologue, maxSegments);
        if (k < 2) return false;

        // The header prints prologues, helpers and directives. Helper text factories can mint names
        // while it runs, so the eligibility check is repeated afterwards before any segment starts.
        var headerWriter = new EmitTextWriter(options.NewLine);
        var header = new SyntaxPrinter(options, context);
        header.PrintFileHeader(tree, headerWriter, cancellation);
        if (context.HasHelpers) return false;

        var ranges = PlanSegments(statements, prologue, k);
        var segments = new EmitTextWriter[k];
        Parallel.For(0, k, new ParallelOptions { CancellationToken = cancellation, MaxDegreeOfParallelism = k }, i =>
        {
            var member = new SyntaxPrinter(options, context);
            var writer = new EmitTextWriter(options.NewLine);
            member.PrintStatementRange(tree, writer, ranges[i].Start, ranges[i].Count, cancellation);
            segments[i] = writer;
        });

        var footerWriter = new EmitTextWriter(options.NewLine);
        var footer = new SyntaxPrinter(options, context);
        footer.PrintFileFooter(tree, footerWriter, cancellation);

        output.RawWrite(headerWriter.Text.Span);
        foreach (var segment in segments)
            output.RawWrite(segment.Text.Span);
        output.RawWrite(footerWriter.Text.Span);
        return true;
    }

    private bool CanSegment(SourceFileNode tree) =>
        !options.PreserveSourceNewlines
        && options.MapSourcePosition is null
        && tree.ScriptKind != ScriptKind.JSON
        && tree.Statements is { Count: > 0 }
        && !HasSourceComments(tree.Source.Text)
        && context.CommentCount == 0
        && !context.HasHelpers;

    /// <summary>Segments to use for a statement count, or 0 when the range should stay serial.</summary>
    internal static int SegmentCountFor(int statementCount, int maxSegments) =>
        statementCount < SegmentMinStatements ? 0
            : Math.Min(Math.Min(maxSegments, SegmentMaxCount), statementCount / SegmentMinStatements);

    /// <summary>
    /// Splits statements [prologue, count) into <paramref name="count"/> ranges of equal statement
    /// count. Statements in this shape are the same size, so count balancing is enough.
    /// </summary>
    private static (int Start, int Count)[] PlanSegments(NodeList statements, int prologue, int count)
    {
        int total = statements.Count - prologue;
        var ranges = new (int Start, int Count)[count];
        int perSegment = (total + count - 1) / count;
        for (int i = 0; i < count; i++)
        {
            int start = prologue + i * perSegment;
            int end = Math.Min(prologue + total, start + perSegment);
            ranges[i] = (start, Math.Max(0, end - start));
        }
        return ranges;
    }

    /// <summary>Statements consumed by prologue directives; identical predicate to <see cref="AddPrologues"/>.</summary>
    private static int PrologueOffset(SourceFileNode tree)
    {
        var statements = tree.Statements;
        if (statements is null) return 0;
        int offset = 0;
        while (offset < statements.Count && statements[offset] is ExpressionStatementNode { Expression: StringLiteralNode })
            offset++;
        return offset;
    }

    private void PrintFileHeader(SourceFileNode tree, EmitTextWriter output, CancellationToken cancellation)
    {
        var parts = new List<Part> { new(NewLine: true) };
        if ((tree.Flags & NodeFlags.Synthesized) == 0 && tree.ScriptKind != ScriptKind.JSON
            && tree.Source.Text.Span.StartsWith("#!"u8))
        {
            int end = tree.Source.Text.Span.IndexOfAny((byte)'\r', (byte)'\n');
            parts.Add(new(Text: tree.Source.Text.Span[..(end < 0 ? tree.Source.Length : end)]));
            parts.Add(new(NewLine: true));
        }
        parts.Add(new(NameScopeChange: 1));
        parts.Add(Generate(tree.Statements));
        AddPrologues(parts, tree.Statements);
        parts.Add(new(NewLine: true));
        if (DetachedCommentStart(tree)) parts.Add(new(DetachedPosition: tree.Statements?.Pos ?? 0));
        if (tree.ScriptKind != ScriptKind.JSON)
        {
            parts.Add(new(Helpers: tree));
            if (tree.IsDeclarationFile) parts.Add(new(Directives: tree));
        }
        parts.Add(ListPosition(tree.Statements));
        RunParts(tree, parts, output, cancellation);
    }

    private void PrintStatementRange(SourceFileNode tree, EmitTextWriter output, int start, int count,
        CancellationToken cancellation)
    {
        var statements = tree.Statements!;
        int prologue = PrologueOffset(tree);
        var parts = new List<Part>(count + 2);
        for (int i = start; i < start + count && i < statements.Count; i++)
        {
            parts.Add(new(NewLine: true));
            parts.Add(N(statements[i]));
            parts.Add(new(NewLine: true));
        }
        RunParts(tree, parts, output, cancellation);
    }

    private void PrintFileFooter(SourceFileNode tree, EmitTextWriter output, CancellationToken cancellation)
    {
        var parts = new List<Part>
        {
            ListPosition(tree.Statements, true),
            new(NewLine: true),
            new(NameScopeChange: -1)
        };
        if (DetachedCommentStart(tree)) parts.Insert(1, new(CommentPosition: tree.Statements?.End ?? tree.End));
        RunParts(tree, parts, output, cancellation);
    }

    /// <summary>
    /// Runs one part group exactly like the serial path runs the file node: the file's own node state
    /// (comment ranges, container positions) is pushed first so a range printed on its own sees the
    /// same enclosing state the serial print would have.
    /// </summary>
    private void RunParts(SourceFileNode tree, List<Part> parts, EmitTextWriter output, CancellationToken cancellation)
    {
        BeginWrite(tree, tree, output, null, cancellation);
        try
        {
            InitializeNames();
            BeginNode(tree);
            pending.Push(new(EndNode: true));
            pending.Push(new(Parts: parts));
            DrainPending();
        }
        finally
        {
            EndWrite();
        }
    }

    private static bool DetachedCommentStart(SourceFileNode file) =>
        file.Statements is not { Count: > 0 } || file.Statements[0] is not ExpressionStatementNode { Expression: StringLiteralNode }
        || (file.Statements[0].Flags & NodeFlags.Synthesized) != 0;
}
