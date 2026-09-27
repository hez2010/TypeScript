namespace TypeScript.Compiler.Text;

public sealed class TextSliceComparer : IEqualityComparer<TextSlice>, IComparer<TextSlice>
{
    public static TextSliceComparer Ordinal { get; } = new(StringComparison.Ordinal);
    public static TextSliceComparer OrdinalIgnoreCase { get; } = new(StringComparison.OrdinalIgnoreCase);
    private readonly StringComparison comparison;

    private TextSliceComparer(StringComparison comparison) => this.comparison = comparison;

    public bool Equals(TextSlice left, TextSlice right) => left.Span.Equals(right.Span, comparison);
    public int GetHashCode(TextSlice value) => comparison == StringComparison.Ordinal
        ? value.GetHashCode() : string.GetHashCode(value.Span, comparison);
    public int Compare(TextSlice left, TextSlice right) => left.Span.CompareTo(right.Span, comparison);
}
