using TypeScript.Compiler.Mapping;

namespace TypeScript.Compiler.LanguageServices;

public readonly record struct DocumentTextEdit(DocumentRange Range, Utf8String NewText);

public sealed partial class LanguageServiceDocument
{
    public async ValueTask<DocumentTextEdit[]?> GetFormattingEditsAsync(FormatCodeSettings? options = null, DocumentRange? range = null,
        CancellationToken cancellation = default)
    {
        options ??= new();
        var canonical = projections[0];
        if (!canonical.IsMapped)
        {
            var changes = range is { } selected
                ? await SourceFormatter.FormatSelectionAsync(canonical.File, canonical.OriginalRange(selected).Start, canonical.OriginalRange(selected).End, options, cancellation).ConfigureAwait(false)
                : await SourceFormatter.FormatDocumentAsync(canonical.File, options, cancellation).ConfigureAwait(false);
            return ConvertEdits(canonical, changes);
        }
        var (start, end) = range is { } original ? canonical.OriginalRange(original) : (0, canonical.OriginalLength);
        List<(DocumentProjection Projection, MappingSegment Segment, int Start, int End)> candidates = [];
        foreach (var projection in projections)
            foreach (var segment in projection.Map?.Segments ?? [])
            {
                if (segment.Kind != MappingKind.Verbatim || (segment.Features & MappingFeature.Formatting) == 0) continue;
                int low = Math.Max(start, segment.OriginalStart), high = Math.Min(end, segment.OriginalEnd);
                if (low < high) candidates.Add((projection, segment, low, high));
            }
        int previousEnd = -1;
        List<DocumentTextEdit> edits = [];
        foreach (var candidate in candidates.OrderBy(c => c.Start).ThenByDescending(c => c.End))
        {
            cancellation.ThrowIfCancellationRequested();
            int low = Math.Max(candidate.Start, previousEnd);
            if (low >= candidate.End) continue;
            previousEnd = candidate.End;
            var projection = candidate.Projection;
            int virtualStart = candidate.Segment.VirtualStart + low - candidate.Segment.OriginalStart;
            int virtualEnd = candidate.Segment.VirtualStart + candidate.End - candidate.Segment.OriginalStart;
            foreach (var change in await SourceFormatter.FormatSelectionAsync(projection.File, virtualStart, virtualEnd, options, cancellation).ConfigureAwait(false))
            {
                if (change.Start < virtualStart || change.End > virtualEnd) continue;
                var mapped = projection.ToRange(change.Start, change.End, MappingFeature.Formatting);
                if (mapped.Fidelity == MappingFidelity.Exact) edits.Add(new(mapped.Range, change.NewText));
            }
        }
        return edits.OrderBy(e => e.Range.Start.Line).ThenBy(e => e.Range.Start.Character).ThenBy(e => e.Range.End.Line).ThenBy(e => e.Range.End.Character)
            .ThenBy(e => e.NewText, Comparer<Utf8String>.Create((a, b) => a.Span.SequenceCompareTo(b.Span))).ToArray();
    }

    public async ValueTask<DocumentTextEdit[]?> GetFormattingEditsAfterKeystrokeAsync(DocumentPosition position, Utf8String character,
        FormatCodeSettings? options = null, CancellationToken cancellation = default)
    {
        var candidates = FromPosition(position, MappingFeature.Formatting);
        if (candidates.Count != 1 || candidates[0].Position.Fidelity != MappingFidelity.Exact) return null;
        var (projection, mapped) = candidates[0];
        var changes = await SourceFormatter.FormatOnTypeAsync(projection.File, mapped.Position, character, options, cancellation).ConfigureAwait(false);
        return ConvertEdits(projection, changes);
    }

    private static DocumentTextEdit[]? ConvertEdits(DocumentProjection projection, IReadOnlyList<SourceTextChange> changes)
    {
        var edits = new DocumentTextEdit[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var mapped = projection.ToRange(change.Start, change.End);
            if (mapped.Fidelity != MappingFidelity.Exact) return null;
            edits[i] = new(mapped.Range, change.NewText);
        }
        return edits;
    }
}
