using System.Buffers;
using System.Text;

namespace TypeScript.Compiler.Text;

public sealed class Utf8StringComparer : IEqualityComparer<Utf8String>, IComparer<Utf8String>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, Utf8String>
{
    public static Utf8StringComparer Ordinal { get; } = new(false);
    public static Utf8StringComparer OrdinalIgnoreCase { get; } = new(true);
    private readonly bool ignoreCase;

    private Utf8StringComparer(bool ignoreCase) => this.ignoreCase = ignoreCase;

    public bool Equals(Utf8String left, Utf8String right) => Equals(left.Span, right.Span);
    public int Compare(Utf8String left, Utf8String right) => Compare(left.Span, right.Span);
    public int GetHashCode(Utf8String value) => GetHashCode(value.Span);
    public Utf8String Create(ReadOnlySpan<byte> value) => Utf8String.Copy(value);
    public bool Equals(ReadOnlySpan<byte> left, Utf8String right) => Equals(left, right.Span);

    internal bool Equals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (!ignoreCase)
            return left.SequenceEqual(right);
        if (Ascii.IsValid(left) && Ascii.IsValid(right))
            return Ascii.EqualsIgnoreCase(left, right);
        return Compare(left, right) == 0;
    }

    internal int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (!ignoreCase)
            return left.SequenceCompareTo(right);
        while (!left.IsEmpty && !right.IsEmpty)
        {
            int first = Fold(Wtf8.Decode(left, out int firstWidth));
            int second = Fold(Wtf8.Decode(right, out int secondWidth));
            if (first != second)
                return first < second ? -1 : 1;
            left = left[firstWidth..];
            right = right[secondWidth..];
        }
        return left.Length.CompareTo(right.Length);
    }

    public int GetHashCode(ReadOnlySpan<byte> value)
    {
        if (!ignoreCase)
            return Utf8String.Hash(value);
        int capacity = checked(value.Length * 3);
        byte[]? rented = null;
        Span<byte> bytes = capacity <= 256 ? stackalloc byte[256] : rented = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            if (Ascii.ToUpper(value, bytes, out int written) == OperationStatus.Done)
                return Utf8String.Hash(bytes[..written]);
            written = 0;
            while (!value.IsEmpty)
            {
                int point = Fold(Wtf8.Decode(value, out int width));
                written += Wtf8.EncodeCodePoint(point, bytes[written..]);
                value = value[width..];
            }
            return Utf8String.Hash(bytes[..written]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    internal static int Fold(int point) => Rune.IsValid(point) ? Rune.ToUpperInvariant(new Rune(point)).Value : point;
}
