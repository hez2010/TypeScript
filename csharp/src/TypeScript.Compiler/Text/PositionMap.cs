using System.Numerics;
using System.Text.Unicode;

namespace TypeScript.Compiler.Text;

// Preserve mappings at interior byte/code-unit offsets as well as boundaries.
public sealed class PositionMap
{
    internal static PositionMap Ascii { get; } = new([], true);
    private readonly record struct Boundary(int Utf8, int Utf16);
    private readonly record struct ByteOffset(int Value) : IComparable<Boundary>
    {
        public int CompareTo(Boundary other) => Value.CompareTo(other.Utf8);
    }
    private readonly record struct CharOffset(int Value) : IComparable<Boundary>
    {
        public int CompareTo(Boundary other) => Value.CompareTo(other.Utf16);
    }
    private readonly Boundary[] boundaries;
    private int lastUtf8 = -1, lastUtf16 = -1;
    private int lastRangeStart = -1, lastRangeEnd = -1;

    public PositionMap(ReadOnlySpan<byte> text) : this(text, Utf8.IsValid(text)) { }

    internal PositionMap(ReadOnlySpan<byte> text, bool validUtf8)
    {
        boundaries = validUtf8 ? new Boundary[CountLeadingBytes(text)] : [];
        if (validUtf8 && boundaries.Length == 0)
        {
            IsAsciiOnly = true;
            return;
        }
        List<Boundary>? positions = validUtf8 ? null : new();
        int index = 0;
        int delta = 0;
        for (int i = 0; i < text.Length;)
        {
            if (text[i] < 128)
            {
                // The BCL supplies portable SIMD for ASCII runs. Adjacent
                // Unicode characters do not need to restart an ASCII search.
                int nonAscii = text[i..].IndexOfAnyExceptInRange((byte)0, (byte)127);
                if (nonAscii < 0)
                    break;
                i += nonAscii;
            }
            int size, chars;
            if (validUtf8)
            {
                // Validation already established each sequence's width.
                size = text[i] >= 0xF0 ? 4 : text[i] >= 0xE0 ? 3 : 2;
                chars = size == 4 ? 2 : 1;
            }
            else
            {
                int value = Wtf8.Decode(text[i..], out size);
                chars = value >= 0x10000 ? 2 : 1;
            }
            i += size;
            if (size != chars)
            {
                delta += size - chars;
                var boundary = new Boundary(i, i - delta);
                if (positions is null)
                    boundaries[index++] = boundary;
                else
                    positions.Add(boundary);
            }
        }
        if (positions is not null)
            boundaries = [.. positions];
    }

    private static int CountLeadingBytes(ReadOnlySpan<byte> text)
    {
        int count = 0;
        // Valid UTF-8 has exactly one C0-or-higher header for each non-ASCII
        // code point. Counting headers in vectors sizes the final map directly.
        if (Vector.IsHardwareAccelerated)
        {
            var header = new Vector<byte>(0xC0);
            var one = new Vector<byte>(1);
            while (text.Length >= Vector<byte>.Count)
            {
                var bytes = new Vector<byte>(text);
                var leading = Vector.GreaterThanOrEqual(bytes, header);
                if (leading != Vector<byte>.Zero)
                    count += Vector.Sum(leading & one);
                text = text[Vector<byte>.Count..];
            }
        }
        foreach (byte value in text)
            if (value >= 0xC0) count++;
        return count;
    }

    public bool IsAsciiOnly { get; }

    public int Utf8ToUtf16(int offset) => offset - DeltaAt(new ByteOffset(offset), ref lastUtf8);

    public int Utf16ToUtf8(int offset) => offset + DeltaAt(new CharOffset(offset), ref lastUtf16);

    internal (int Start, int End) Utf16ToUtf8(int start, int end) =>
        (start + DeltaAt(new CharOffset(start), ref lastRangeStart),
         end + DeltaAt(new CharOffset(end), ref lastRangeEnd));

    private int DeltaAt<T>(T offset, ref int previous) where T : struct, IComparable<Boundary>
    {
        // AST positions cluster in ASCII runs between Unicode characters. The
        // cached interval is only a hint: validate both bounds so concurrent
        // readers sharing immutable source text can safely use an older hint.
        int index = previous;
        if ((index < 0 || offset.CompareTo(boundaries[index]) >= 0)
            && (index + 1 == boundaries.Length || offset.CompareTo(boundaries[index + 1]) < 0))
            return index < 0 ? 0 : boundaries[index].Utf8 - boundaries[index].Utf16;
        index = boundaries.AsSpan().BinarySearch(offset);
        if (index < 0)
            index = ~index - 1;
        previous = index;
        return index < 0 ? 0 : boundaries[index].Utf8 - boundaries[index].Utf16;
    }
}
