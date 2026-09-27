using System.Buffers;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;

namespace TypeScript.Compiler.Text;

/// <summary>An immutable view of compiler text. Source slices retain their owner and compare by content.</summary>
public readonly struct TextSlice(ReadOnlyMemory<char> memory) : IEquatable<TextSlice>, IComparable<TextSlice>, ISpanFormattable
{
    private static readonly long HashSeed = Random.Shared.NextInt64();
    public ReadOnlyMemory<char> Memory { get; } = memory;
    public ReadOnlySpan<char> Span => Memory.Span;
    public int Length => Memory.Length;
    public bool IsEmpty => Memory.IsEmpty;
    public char this[int index] => Span[index];
    public TextSlice this[Range range] => new(Memory[range]);
    public ReadOnlySpan<char>.Enumerator GetEnumerator() => Span.GetEnumerator();

    public bool Equals(TextSlice other) => Span.SequenceEqual(other.Span);
    public override bool Equals(object? value) => value is TextSlice other && Equals(other);
    public override int GetHashCode() => unchecked((int)XxHash3.HashToUInt64(MemoryMarshal.AsBytes(Span), HashSeed));
    public int CompareTo(TextSlice other) => Span.SequenceCompareTo(other.Span);
    public override string ToString() => Memory.ToString();
    public string ToString(string? format, IFormatProvider? provider) => ToString();
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
    {
        if (!Span.TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }
        charsWritten = Length;
        return true;
    }

    public static implicit operator TextSlice(string value) => new(value.AsMemory());
    public static implicit operator TextSlice(ReadOnlyMemory<char> value) => new(value);
    public static implicit operator ReadOnlySpan<char>(TextSlice value) => value.Span;
    public static bool operator ==(TextSlice left, TextSlice right) => left.Equals(right);
    public static bool operator !=(TextSlice left, TextSlice right) => !left.Equals(right);
    public static TextSlice operator +(TextSlice left, TextSlice right) => Concat(left, right);
    public static TextSlice operator +(TextSlice left, string right) => Concat(left, right);
    public static TextSlice operator +(string left, TextSlice right) => Concat(left, right);
    public static TextSlice operator +(TextSlice left, long right) => Concat(left, Format(right));
    public static TextSlice operator +(TextSlice left, char right)
    {
        char[] result = new char[checked(left.Length + 1)];
        left.Span.CopyTo(result);
        result[^1] = right;
        return new(result);
    }
    public static TextSlice operator +(char left, TextSlice right)
    {
        char[] result = new char[checked(right.Length + 1)];
        result[0] = left;
        right.Span.CopyTo(result.AsSpan(1));
        return new(result);
    }

    public static TextSlice Copy(ReadOnlySpan<char> value) => new(value.ToArray());
    public static TextSlice? FromNullable(string? value) => value is null ? null : new(value.AsMemory());

    public TextSlice Trim() => new(Memory.Trim());

    public TextSlice Replace(char oldValue, char newValue)
    {
        if (oldValue == newValue || !Span.Contains(oldValue))
            return this;
        char[] result = Span.ToArray();
        result.AsSpan().Replace(oldValue, newValue);
        return new(result);
    }

    public static TextSlice Format<T>(T value, ReadOnlySpan<char> format = default) where T : ISpanFormattable
    {
        Span<char> buffer = stackalloc char[64];
        if (value.TryFormat(buffer, out int written, format, CultureInfo.InvariantCulture))
            return Copy(buffer[..written]);
        int capacity = buffer.Length;
        while (true)
        {
            char[] rented = ArrayPool<char>.Shared.Rent(checked(capacity * 2));
            try
            {
                if (value.TryFormat(rented, out written, format, CultureInfo.InvariantCulture))
                    return Copy(rented.AsSpan(0, written));
                capacity = rented.Length;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(rented);
            }
        }
    }

    internal static TextSlice FromBuilder(StringBuilder builder)
    {
        char[] result = new char[builder.Length];
        builder.CopyTo(0, result, 0, result.Length);
        return new(result);
    }

    public static TextSlice Concat(ReadOnlySpan<char> first, ReadOnlySpan<char> second)
    {
        char[] result = new char[checked(first.Length + second.Length)];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return new(result);
    }

    public static TextSlice Concat(ReadOnlySpan<char> first, ReadOnlySpan<char> second, ReadOnlySpan<char> third)
    {
        char[] result = new char[checked(first.Length + second.Length + third.Length)];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        third.CopyTo(result.AsSpan(first.Length + second.Length));
        return new(result);
    }

    public static TextSlice Join(ReadOnlySpan<char> separator, IEnumerable<TextSlice> values)
    {
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext())
            return default;
        TextSlice first = iterator.Current;
        if (!iterator.MoveNext())
            return first;
        var builder = new StringBuilder().Append(first.Span);
        do
        {
            builder.Append(separator).Append(iterator.Current.Span);
        } while (iterator.MoveNext());
        return FromBuilder(builder);
    }

    public static TextSlice Join(char separator, IEnumerable<TextSlice> values) => Join([separator], values);
    public static TextSlice Join(ReadOnlySpan<char> separator, IEnumerable<string> values) =>
        Join(separator, values.Select(static value => (TextSlice)value));
    public static TextSlice Join(char separator, IEnumerable<string> values) => Join([separator], values);
    public static TextSlice Concat(IEnumerable<TextSlice> values) => Join([], values);

    // Length framing keeps empty values, NULs and lone surrogates unambiguous in cache keys.
    internal static TextSlice Frame(ReadOnlySpan<TextSlice> values)
    {
        Span<char> digits = stackalloc char[11];
        int length = 0;
        foreach (TextSlice value in values)
        {
            value.Length.TryFormat(digits, out int count, provider: CultureInfo.InvariantCulture);
            length = checked(length + count + 1 + value.Length);
        }
        char[] result = new char[length];
        Span<char> target = result;
        foreach (TextSlice value in values)
        {
            value.Length.TryFormat(target, out int count, provider: CultureInfo.InvariantCulture);
            target[count] = ':';
            target = target[(count + 1)..];
            value.Span.CopyTo(target);
            target = target[value.Length..];
        }
        return new(result);
    }

    public static TextSlice ConcatMany(params ReadOnlySpan<TextSlice> values)
    {
        int length = 0;
        foreach (var value in values)
            length = checked(length + value.Length);
        if (length == 0)
            return default;
        char[] result = new char[length];
        Span<char> target = result;
        foreach (var value in values)
        {
            value.Span.CopyTo(target);
            target = target[value.Length..];
        }
        return new(result);
    }
}
