using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    /// <summary>Prints a factory tree and returns a separate tree positioned in the emitted UTF-8 text.</summary>
    public static (Utf8String Text, SyntaxNode Node) PrintAndPositionNode(SyntaxNode node, SourceFileNode? source = null,
        Utf8String newLine = default, int indentSize = 4, EmitContext? context = null, CancellationToken cancellation = default,
        PrinterOptions? options = null)
    {
        if (newLine.IsEmpty) newLine = "\n"u8;
        var positions = new PrintPositions();
        var printer = new SyntaxPrinter(options ?? new()
        {
            NewLine = newLine, NeverAsciiEscape = true, PreserveSourceNewlines = true, TerminateUnterminatedLiterals = true,
        }, context) { positions = positions };
        var output = new EmitTextWriter(newLine, indentSize);
        printer.Write(node, source, output, cancellation: cancellation);
        var text = output.Text;
        if (text.Span.EndsWith(newLine.Span)) text = text[..^newLine.Length];
        return (text, positions.Clone(node, cancellation));
    }

    private static Part ListPosition(NodeList? list, bool end = false) => new(PositionTarget: list, EndPosition: end);
    private static Part Token(SyntaxNode node) => S(new(PositionTarget: node), T(Syntax.TokenFacts.Text(node.Kind)), new(PositionTarget: node, EndPosition: true));

    private sealed class PrintPositions
    {
        private readonly Dictionary<object, (int Start, int End)> ranges = new(ReferenceEqualityComparer.Instance);
        internal bool TryGetRange(SyntaxNode node, out (int Start, int End) range) => ranges.TryGetValue(node, out range);

        internal void Record(object target, int position, bool end)
        {
            ranges.TryGetValue(target, out var range);
            ranges[target] = end ? (range.Start, position) : (position, range.End);
        }

        internal SyntaxNode Clone(SyntaxNode root, CancellationToken cancellation)
        {
            var originalGroups = new List<SyntaxChild>();
            var clonedGroups = new List<SyntaxChild>();
            return root.DeepClone<SyntaxNode>(new NodeFactory
            {
                OnClone = (clone, original) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    var range = ranges.GetValueOrDefault(original);
                    clone.Pos = range.Start; clone.End = range.End;
                    originalGroups.Clear(); clonedGroups.Clear();
                    original.GetChildGroups(originalGroups); clone.GetChildGroups(clonedGroups);
                    for (int i = 0; i < originalGroups.Count; i++)
                    {
                        if (originalGroups[i].List is not { } list || clonedGroups[i].List is not { } clonedList) continue;
                        var listRange = original.ModifierList == list ? (-1, -1) : ranges.GetValueOrDefault(list);
                        clonedList.SetRange(listRange.Item1, listRange.Item2);
                    }
                },
            });
        }
    }
}
