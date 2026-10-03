using TypeScript.Compiler.Ast;
using TypeScript.Compiler.Mapping;
using TypeScript.Compiler.Programs;

namespace TypeScript.Compiler.LanguageServices;

/// <summary>Editor queries over a program's canonical source and its content-mapper projections.</summary>
public sealed partial class LanguageServiceDocument
{
    private readonly DocumentProjection[] projections;
    private readonly IReadOnlyDictionary<SourceFileNode, DocumentProjection>? relatedProjections;

    public LanguageServiceDocument(CompilerProgram program, ProgramFile file, PositionEncoding encoding = PositionEncoding.Utf8)
        : this([new(file.Syntax, file.Mapping, encoding), .. file.SupplementalSourceFiles
            .Select(name => program.GetFile(name) ?? throw new InvalidOperationException($"Missing supplemental source {name}"))
            .Select(extra => new DocumentProjection(extra.Syntax, extra.Mapping, encoding, file.Syntax.FileName))]) { }

    public LanguageServiceDocument(SourceFileNode file, PositionEncoding encoding = PositionEncoding.Utf8)
        : this([new(file, null, encoding)]) { }

    internal LanguageServiceDocument(DocumentProjection[] projections, IReadOnlyDictionary<SourceFileNode, DocumentProjection>? relatedProjections = null)
    { this.projections = projections; this.relatedProjections = relatedProjections; }

    public async ValueTask<SelectionRange[]?> GetSelectionRangesAsync(IReadOnlyList<DocumentPosition> positions, CancellationToken cancellation = default)
    {
        List<SelectionRange> results = [];
        foreach (var position in positions)
        {
            cancellation.ThrowIfCancellationRequested();
            var candidates = FromPosition(position, MappingFeature.SelectionRanges);
            if (candidates.Count != 1 || candidates[0].Position.Fidelity is not (MappingFidelity.Exact or MappingFidelity.Atom)) return null;
            var (projection, mapped) = candidates[0];
            if (await SyntaxLanguageService.GetSelectionRangeAsync(projection, mapped.Position, cancellation).ConfigureAwait(false) is { } selection)
                results.Add(selection);
        }
        return results.ToArray();
    }

    public async ValueTask<LinkedEditingRanges?> GetLinkedEditingRangesAsync(DocumentPosition position, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var candidates = FromPosition(position, MappingFeature.LinkedEditing);
        if (candidates.Count != 1 || candidates[0].Position.Fidelity != MappingFidelity.Exact) return null;
        var (projection, mapped) = candidates[0];
        return await SyntaxLanguageService.GetLinkedEditingRangesAsync(projection, mapped.Position, cancellation).ConfigureAwait(false);
    }

    public async ValueTask<FoldingRange[]> GetFoldingRangesAsync(bool lineFoldingOnly = false, bool collapsedText = false, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        List<FoldingRange> ranges = [];
        foreach (var projection in projections)
            ranges.AddRange(await SyntaxLanguageService.GetFoldingRangesAsync(projection, lineFoldingOnly, collapsedText, cancellation).ConfigureAwait(false));
        return ranges.OrderBy(range => range.StartLine).ThenBy(range => range.StartCharacter)
            .ThenBy(range => range.EndLine).ThenBy(range => range.EndCharacter).Distinct().ToArray();
    }

    private DocumentProjection ProjectionForFile(CompilerProgram program, SourceFileNode file) =>
        projections.FirstOrDefault(p => p.File == file) ?? relatedProjections?.GetValueOrDefault(file)
            ?? new(file, program.GetFile(file.FileName)?.Mapping, projections[0].Encoding, ReferenceOriginalFileName(program, file));

    private List<(DocumentProjection Projection, MappedPosition Position)> FromPosition(DocumentPosition position, MappingFeature feature)
    {
        List<(DocumentProjection, MappedPosition)> result = [];
        foreach (var projection in projections)
            foreach (var mapped in projection.FromPosition(position, feature)) result.Add((projection, mapped));
        return result;
    }
}

internal sealed class DocumentProjection(SourceFileNode file, MappedSourceFile? mapping, PositionEncoding encoding, Utf8String originalFileName = default)
{
    internal SourceFileNode File { get; } = file;
    internal PositionEncoding Encoding { get; } = encoding;
    internal Utf8String OriginalFileName { get; } = originalFileName.IsEmpty ? file.FileName : originalFileName;
    internal bool IsMapped => mapping is not null;
    internal Utf8String MapperIdentity => mapping?.MapperIdentity ?? default;
    internal DocumentRange ToOriginalRange(int start, int end) => lines.ToRange(start, end, Encoding);
    internal SpanMap? Map => mapping?.Map;
    internal Utf8String OriginalText => mapping?.Original.Text ?? File.Source.Text;
    internal int OriginalLength => mapping?.Original.Length ?? File.Source.Length;
    private readonly DocumentLineMap lines = new(mapping?.Original.Text ?? file.Source.Text);

    internal (int Start, int End) OriginalRange(DocumentRange range) => (lines.ToOffset(range.Start, Encoding), lines.ToOffset(range.End, Encoding));

    internal MappedPosition[] FromPosition(DocumentPosition position, MappingFeature feature)
    {
        int offset = lines.ToOffset(position, Encoding);
        return mapping?.Map.OriginalToVirtualPositions(offset, feature) ?? [new(offset, MappingFidelity.Exact)];
    }

    internal MappedSpan[] FromRange(DocumentRange range, MappingFeature feature)
    {
        int start = lines.ToOffset(range.Start, Encoding), end = lines.ToOffset(range.End, Encoding);
        return mapping?.Map.OriginalToVirtualIntersectingSpans(start, end, feature) ?? [new(start, end, MappingFidelity.Exact)];
    }

    internal (DocumentRange Range, MappingFidelity Fidelity) ToRange(int start, int end, MappingFeature? feature = null)
    {
        var span = mapping?.Map.VirtualToOriginalSpan(start, end, feature) ?? new(start, end, MappingFidelity.Exact);
        return (lines.ToRange(span.Start, span.End, Encoding), span.Fidelity);
    }
}
