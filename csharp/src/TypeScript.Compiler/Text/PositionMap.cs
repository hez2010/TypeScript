namespace TypeScript.Compiler.Text;

// Preserve mappings at interior byte/code-unit offsets as well as boundaries.
public sealed class PositionMap
{
    private readonly int[] utf8Ends;
    private readonly int[] utf16Ends;
    private readonly int[] deltas;
    private int lastUtf8 = -1, lastUtf16 = -1;

    public PositionMap(ReadOnlySpan<byte> text)
    {
        var bytes = new List<int>();
        var chars = new List<int>();
        var differences = new List<int>();
        int delta = 0;
        for (int i = 0; i < text.Length;)
        {
            // The BCL supplies portable SIMD and a scalar fallback for ASCII runs.
            int nonAscii = text[i..].IndexOfAnyExceptInRange((byte)0, (byte)127);
            if (nonAscii < 0)
                break;
            i += nonAscii;
            int value = Wtf8.Decode(text[i..], out int size);
            i += size;
            delta += size - (value >= 0x10000 ? 2 : 1);
            bytes.Add(i);
            chars.Add(i - delta);
            differences.Add(delta);
        }
        utf8Ends = [.. bytes];
        utf16Ends = [.. chars];
        deltas = [.. differences];
    }

    public bool IsAsciiOnly => deltas.Length == 0;

    public int Utf8ToUtf16(int offset) => offset - DeltaAt(utf8Ends, offset, ref lastUtf8);

    public int Utf16ToUtf8(int offset) => offset + DeltaAt(utf16Ends, offset, ref lastUtf16);

    private int DeltaAt(int[] ends, int offset, ref int previous)
    {
        // AST positions cluster in ASCII runs between Unicode characters. The
        // cached interval is only a hint: validate both bounds so concurrent
        // readers sharing immutable source text can safely use an older hint.
        int index = previous;
        if ((index < 0 || ends[index] <= offset) && (index + 1 == ends.Length || offset < ends[index + 1]))
            return index < 0 ? 0 : deltas[index];
        index = ends.AsSpan().BinarySearch(offset);
        if (index < 0)
            index = ~index - 1;
        previous = index;
        return index < 0 ? 0 : deltas[index];
    }
}
