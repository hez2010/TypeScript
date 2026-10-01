using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Syntax;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

public sealed partial class SyntaxPrinter
{
    private IEnumerable<EmitHelper> HelpersFor(SyntaxNode node)
    {
        bool skip = options.NoEmitHelpers || sourceFile is not null && context.HasRecordedExternalHelpers(sourceFile);
        return context.GetHelpers(node).Where(helper => helper.Scoped || !skip)
            .OrderBy(helper => helper.Priority is null).ThenBy(helper => helper.Priority);
    }

    private void WriteHelpers(SyntaxNode node)
    {
        foreach (var helper in HelpersFor(node))
        {
            cancellation.ThrowIfCancellationRequested();
            WriteLines(helper.TextFactory?.Invoke(nameGenerator.MakeFileLevelOptimisticUniqueName) ?? helper.Text);
        }
    }

    private void WriteLines(Utf8String text)
    {
        var lines = new List<Utf8String>();
        int start = 0, indentation = int.MaxValue;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] is not ((byte)'\r' or (byte)'\n'))
                continue;
            var line = text[start..i];
            if (line.Length != 0)
            {
                int spaces = 0;
                while (spaces < line.Length)
                {
                    int point = Wtf8.Decode(line.Span[spaces..], out int width);
                    if (!TokenFacts.IsWhiteSpace(point) && !TokenFacts.IsLineBreak(point))
                        break;
                    spaces += width;
                }
                indentation = Math.Min(indentation, spaces);
            }
            lines.Add(line);
            if (i + 1 < text.Length && text[i] == '\r' && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (indentation == int.MaxValue)
            return;
        foreach (var line in lines)
            if (line.Length > indentation)
            {
                writer.WriteLine();
                writer.Write(line[indentation..]);
            }
    }

    private static int AddPrologues(List<Part> parts, NodeList? statements)
    {
        int offset = 0;
        if (statements is not null)
            while (offset < statements.Count && statements[offset] is ExpressionStatementNode { Expression: StringLiteralNode })
            {
                parts.Add(new(NewLine: true));
                parts.Add(N(statements[offset++]));
            }
        return offset;
    }

    private void WriteDirectives(SourceFileNode file)
    {
        WriteReferences("path"u8, file.ReferencedFiles);
        WriteReferences("types"u8, file.TypeReferenceDirectives);
        WriteReferences("lib"u8, file.LibReferenceDirectives);
    }

    private void WriteReferences(Utf8String kind, IReadOnlyList<FileReference> references)
    {
        foreach (var reference in references)
        {
            var text = new Utf8StringBuilder();
            text.Append("/// <reference "u8);
            text.Append(kind);
            text.Append("=\""u8);
            text.Append(reference.FileName);
            text.Append("\" "u8);
            if (reference.ResolutionMode != ReferenceResolutionMode.Unspecified)
                text.Append(reference.ResolutionMode == ReferenceResolutionMode.Import ? "resolution-mode=\"import\" "u8 : "resolution-mode=\"require\" "u8);
            if (reference.Preserve)
                text.Append("preserve=\"true\" "u8);
            text.Append("/>"u8);
            writer.WriteComment(text.ToUtf8String());
            writer.WriteLine();
        }
    }
}
