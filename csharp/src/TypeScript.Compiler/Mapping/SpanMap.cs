using System.Text.Json;

namespace TypeScript.Compiler.Mapping;

public enum MappingKind
{
    Verbatim,
    Atom,
    Alias
}
public enum MappingFidelity
{
    Exact,
    Atom,
    Approximate,
    None
}
[Flags]
public enum MappingFeature
{
    None = 0,
    Hover = 1 << 0,
    SignatureHelp = 1 << 1,
    Completion = 1 << 2,
    Definition = 1 << 3,
    TypeDefinition = 1 << 4,
    Implementation = 1 << 5,
    References = 1 << 6,
    DocumentHighlights = 1 << 7,
    Rename = 1 << 8,
    CallHierarchy = 1 << 9,
    CodeActions = 1 << 10,
    Formatting = 1 << 11,
    InlayHints = 1 << 12,
    SemanticTokens = 1 << 13,
    FoldingRanges = 1 << 14,
    SelectionRanges = 1 << 15,
    LinkedEditing = 1 << 16,
    AutoInsert = 1 << 17,
    DocumentSymbols = 1 << 18,
    CodeLens = 1 << 19,
    All = (CodeLens << 1) - 1
}
public readonly record struct MappingSegment(int VirtualStart, int VirtualEnd, int OriginalStart, int OriginalEnd,
    MappingKind Kind, MappingFeature Features = MappingFeature.All);
public readonly record struct MappedPosition(int Position, MappingFidelity Fidelity);
public readonly record struct MappedSpan(int Start, int End, MappingFidelity Fidelity);
public enum MappingErrorKind
{
    Overlap,
    OutOfBounds,
    VerbatimMismatch,
    Kind,
    Feature
}
public sealed class MappingException(MappingErrorKind kind, int virtualPosition, int originalPosition = 0)
    : IOException($"Invalid content mapping ({kind}) at virtual {virtualPosition}, original {originalPosition}")
{
    public MappingErrorKind Kind { get; } = kind;
    public int VirtualPosition { get; } = virtualPosition;
    public int OriginalPosition { get; } = originalPosition;
}

/// <summary>Sparse byte-coordinate projections. Gaps are synthesized; reverse lookups retain every projection.</summary>
public sealed class SpanMap
{
    private readonly MappingSegment[] segments;
    private readonly Lazy<OriginalIndex> original;
    public IReadOnlyList<MappingSegment> Segments { get; }

    public SpanMap(IEnumerable<MappingSegment> segments)
    {
        this.segments = segments.ToArray();
        Segments = Array.AsReadOnly(this.segments);
        original = new(() => new(this.segments));
    }

    public void Validate(ReadOnlySpan<byte> virtualText, ReadOnlySpan<byte> originalText)
    {
        int previousEnd = 0;
        foreach (var s in segments)
        {
            if (s.VirtualStart < previousEnd || s.VirtualEnd < s.VirtualStart || s.VirtualEnd > virtualText.Length)
                throw new MappingException(MappingErrorKind.Overlap, s.VirtualStart);
            previousEnd = s.VirtualEnd;
            if (s.OriginalStart < 0 || s.OriginalEnd < s.OriginalStart || s.OriginalEnd > originalText.Length)
                throw new MappingException(MappingErrorKind.OutOfBounds, s.VirtualStart, s.OriginalEnd);
            if (s.Kind is not (MappingKind.Verbatim or MappingKind.Atom or MappingKind.Alias))
                throw new MappingException(MappingErrorKind.Kind, s.VirtualStart, s.OriginalStart);
            if (s.Kind == MappingKind.Verbatim && (s.VirtualEnd - s.VirtualStart != s.OriginalEnd - s.OriginalStart
                || !virtualText[s.VirtualStart..s.VirtualEnd].SequenceEqual(originalText[s.OriginalStart..s.OriginalEnd])))
                throw new MappingException(MappingErrorKind.VerbatimMismatch, s.VirtualStart, s.OriginalStart);
            if ((s.Features & ~MappingFeature.All) != 0)
                throw new MappingException(MappingErrorKind.Feature, s.VirtualStart, s.OriginalStart);
        }
    }

    private (int Index, bool Inside) At(int position)
    {
        int low = 0, high = segments.Length;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (segments[mid].VirtualStart < position)
                low = mid + 1;
            else
                high = mid;
        }
        if (low < segments.Length && segments[low].VirtualStart == position)
            return (low, true);
        int previous = low - 1;
        return (previous, previous >= 0 && (position < segments[previous].VirtualEnd
            || previous == segments.Length - 1 && position == segments[previous].VirtualEnd));
    }

    private int Insertion(int previous) => previous < 0 ? 0 : segments[previous].OriginalEnd;

    private static int MapLow(MappingSegment s, int position) => s.Kind == MappingKind.Verbatim
            ? Math.Clamp(s.OriginalStart + (position - s.VirtualStart), s.OriginalStart, s.OriginalEnd) : s.OriginalStart;

    private static int MapHigh(MappingSegment s, int position) => s.Kind == MappingKind.Verbatim
            ? Math.Clamp(s.OriginalStart + (position - s.VirtualStart), s.OriginalStart, s.OriginalEnd) : s.OriginalEnd;

    private static int ReverseLow(MappingSegment s, int position) => s.Kind == MappingKind.Verbatim
            ? Math.Clamp(s.VirtualStart + (position - s.OriginalStart), s.VirtualStart, s.VirtualEnd) : s.VirtualStart;

    private static int ReverseHigh(MappingSegment s, int position) => s.Kind == MappingKind.Verbatim
            ? Math.Clamp(s.VirtualStart + (position - s.OriginalStart), s.VirtualStart, s.VirtualEnd) : s.VirtualEnd;

    private static bool Supports(MappingSegment s, MappingFeature feature) => (s.Features & feature) != 0;

    public MappedPosition VirtualToOriginalPosition(int position, MappingFeature? feature = null)
    {
        var (index, inside) = At(position);
        if (!inside)
            return new(Insertion(index), MappingFidelity.None);
        var segment = segments[index];
        return new(MapLow(segment, position), feature is { } selected && !Supports(segment, selected) ? MappingFidelity.None
            : segment.Kind == MappingKind.Verbatim ? MappingFidelity.Exact : MappingFidelity.Atom);
    }

    public bool TryMapExactPosition(int position, out int mapped)
    {
        var result = VirtualToOriginalPosition(position);
        mapped = result.Position;
        if (result.Fidelity != MappingFidelity.Exact)
            return false;
        var (index, _) = At(position);
        return index == 0 || segments[index - 1].VirtualEnd != position || segments[index - 1].Kind == MappingKind.Verbatim
            && segments[index - 1].OriginalEnd == segments[index].OriginalStart;
    }

    public MappedSpan VirtualToOriginalSpan(int start, int end, MappingFeature? feature = null)
    {
        end = Math.Max(start, end);
        if (start == end)
        {
            var mapped = VirtualToOriginalPosition(start, feature);
            return new(mapped.Position, mapped.Position, mapped.Fidelity);
        }
        var (first, firstIn) = At(start);
        var (last, lastIn) = At(end - 1);
        MappedSpan result;
        if (first == last && firstIn == lastIn)
        {
            if (!firstIn)
                result = new(Insertion(first), Insertion(first), MappingFidelity.None);
            else
            {
                var s = segments[first];
                result = new(
                    MapLow(s, start),
                    MapHigh(s, end),
                    s.Kind == MappingKind.Verbatim ? MappingFidelity.Exact : MappingFidelity.Atom);
            }
        }
        else
        {
            int low = firstIn ? MapLow(segments[first], start) : Insertion(first);
            int high = lastIn ? MapHigh(segments[last], end) : Insertion(last);
            result = new(low, Math.Max(low, high), MappingFidelity.Approximate);
        }
        if (feature is { } selected)
        {
            int covered = start, index = first;
            if (!firstIn)
                return result with { Fidelity = MappingFidelity.None };
            while (index < segments.Length && covered < end)
            {
                var s = segments[index++];
                if (s.VirtualStart > covered || s.VirtualEnd <= covered || !Supports(s, selected))
                    return result with { Fidelity = MappingFidelity.None };
                covered = s.VirtualEnd;
            }
            if (covered < end)
                return result with { Fidelity = MappingFidelity.None };
        }
        return result;
    }

    public MappingSegment? AliasForVirtualSpan(int start, int end)
    {
        var (index, inside) = At(start);
        return inside && segments[index] is { Kind: MappingKind.Alias } s && s.VirtualStart == start && s.VirtualEnd == end ? s : null;
    }

    public MappedPosition[] OriginalToVirtualPositions(int position, MappingFeature feature)
    {
        var results = new List<MappedPosition>();
        foreach (var s in original.Value.At(position, true))
        {
            if (!Supports(s, feature))
                continue;
            var mapped = s.Kind == MappingKind.Verbatim ? new MappedPosition(ReverseLow(s, position), MappingFidelity.Exact)
                : new(position == s.OriginalEnd && position != s.OriginalStart ? s.VirtualEnd : s.VirtualStart, MappingFidelity.Atom);
            if (!results.Contains(mapped))
                results.Add(mapped);
        }
        return results.OrderBy(r => r.Position).ToArray();
    }

    public MappedSpan[] OriginalToVirtualSpans(int start, int end, MappingFeature feature)
    {
        end = Math.Max(start, end);
        if (start == end)
            return OriginalToVirtualPositions(start, feature).Select(p => new MappedSpan(p.Position, p.Position, p.Fidelity)).ToArray();
        var starts = original.Value.At(start, false);
        var ends = original.Value.At(end - 1, false);
        if (starts.Count == 0 || ends.Count == 0)
            return [];
        var containing = starts.Where(s => end <= s.OriginalEnd && Supports(s, feature))
            .Select(
                s => new MappedSpan(
                    ReverseLow(s, start),
                    ReverseHigh(s, end),
                    s.Kind == MappingKind.Verbatim ? MappingFidelity.Exact : MappingFidelity.Atom))
            .OrderBy(s => s.Start).ToArray();
        if (containing.Length > 0)
            return containing;
        int[] lows = starts.Where(s => Supports(s, feature)).Select(s => ReverseLow(s, start)).Order().ToArray();
        int[] highs = ends.Where(s => Supports(s, feature)).Select(s => ReverseHigh(s, end)).Order().ToArray();
        var results = new List<MappedSpan>();
        for (int i = 0; i < lows.Length; i++)
        {
            int lo = 0, hi = highs.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (highs[mid] < lows[i])
                    lo = mid + 1;
                else
                    hi = mid;
            }
            if (lo < highs.Length && !(i + 1 < lows.Length && lows[i + 1] <= highs[lo]))
                results.Add(new(lows[i], highs[lo], MappingFidelity.Approximate));
        }
        return results.ToArray();
    }

    public MappedSpan[] OriginalToVirtualIntersectingSpans(int start, int end, MappingFeature feature)
    {
        if (start == end)
            return OriginalToVirtualSpans(start, end, feature);
        var results = new List<MappedSpan>();
        foreach (var s in segments)
        {
            if (!Supports(s, feature))
                continue;
            int low = Math.Max(start, s.OriginalStart), high = Math.Min(end, s.OriginalEnd);
            if (low < high)
                results.Add(
                    new(
                        ReverseLow(s, low),
                        ReverseHigh(s, high),
                        s.Kind == MappingKind.Verbatim ? MappingFidelity.Exact : MappingFidelity.Atom));
        }
        return results.ToArray();
    }

    private sealed class OriginalIndex
    {
        private readonly MappingSegment[] segments;
        private readonly int[] maxEnds;
        private readonly int leaves = 1;

        internal OriginalIndex(MappingSegment[] segments)
        {
            this.segments = segments.OrderBy(s => s.OriginalStart).ThenBy(s => s.OriginalEnd).ThenBy(s => s.VirtualStart).ToArray();
            while (leaves < segments.Length)
                leaves = checked(leaves * 2);
            maxEnds = new int[checked(leaves * 2)];
            for (int i = 0; i < segments.Length; i++)
                maxEnds[leaves + i] = this.segments[i].OriginalEnd;
            for (int i = leaves - 1; i > 0; i--)
                maxEnds[i] = Math.Max(maxEnds[i * 2], maxEnds[i * 2 + 1]);
        }

        internal List<MappingSegment> At(int position, bool includeEnd)
        {
            int limit = 0, high = segments.Length;
            while (limit < high)
            {
                int mid = limit + (high - limit) / 2;
                if (segments[mid].OriginalStart <= position)
                    limit = mid + 1;
                else
                    high = mid;
            }
            var found = new List<MappingSegment>();
            var stack = new Stack<(int Node, int Start, int End)>();
            stack.Push((1, 0, leaves));
            while (stack.TryPop(out var item))
            {
                if (item.Start >= limit || maxEnds[item.Node] < position)
                    continue;
                if (item.End - item.Start == 1)
                {
                    var s = segments[item.Start];
                    if (includeEnd || s.OriginalEnd > position || s.OriginalStart == position)
                        found.Add(s);
                }
                else
                {
                    int mid = item.Start + (item.End - item.Start) / 2;
                    stack.Push((item.Node * 2 + 1, mid, item.End));
                    stack.Push((item.Node * 2, item.Start, mid));
                }
            }
            return found;
        }
    }

    public static SpanMap Read(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new([]);
        var segments = new List<MappingSegment>();
        foreach (var tuple in value.EnumerateArray())
        {
            if (tuple.GetArrayLength() is not (5 or 6))
                throw new InvalidDataException("A span map tuple must have five or six values");
            int start = tuple[0].GetInt32(), original = tuple[2].GetInt32();
            segments.Add(new(start, unchecked(start + tuple[1].GetInt32()), original, unchecked(original + tuple[3].GetInt32()),
                (MappingKind)tuple[4].GetInt32(), tuple.GetArrayLength() == 6 ? (MappingFeature)tuple[5].GetInt32() : MappingFeature.All));
        }
        return new(segments);
    }

    public void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var s in segments)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(s.VirtualStart);
            writer.WriteNumberValue(s.VirtualEnd - s.VirtualStart);
            writer.WriteNumberValue(s.OriginalStart);
            writer.WriteNumberValue(s.OriginalEnd - s.OriginalStart);
            writer.WriteNumberValue((int)s.Kind);
            if (s.Features != MappingFeature.All)
                writer.WriteNumberValue((int)s.Features);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
