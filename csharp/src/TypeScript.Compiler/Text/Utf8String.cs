using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace TypeScript.Compiler.Text;

/// <summary>An immutable view of UTF-8 compiler text. Slices retain their owner and use byte offsets.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Utf8StringJsonConverter))]
public readonly struct Utf8String(ReadOnlyMemory<byte> memory) : IEquatable<Utf8String>, IComparable<Utf8String>, IUtf8SpanFormattable
{
    private static readonly long HashSeed = Random.Shared.NextInt64();
    public ReadOnlyMemory<byte> Memory { get; } = memory;
    public ReadOnlySpan<byte> Span => Memory.Span;
    public int Length => Memory.Length;
    public bool IsEmpty => Memory.IsEmpty;
    public static Utf8String Empty => default;
    public Utf8String(byte[] bytes) : this((ReadOnlyMemory<byte>)bytes) { }
    public Utf8String(ReadOnlySpan<byte> bytes) : this(bytes.ToArray()) { }
    public Utf8String(int point, int count) : this(Repeat(point, count)) { }
    public byte this[int index] => Span[index];
    public Utf8String this[Range range] => new(Memory[range]);
    public ReadOnlySpan<byte>.Enumerator GetEnumerator() => Span.GetEnumerator();
    public ReadOnlySpan<byte> AsSpan() => Span;
    public ReadOnlySpan<byte> AsSpan(int start) => Span[start..];
    public ReadOnlySpan<byte> AsSpan(int start, int length) => Span.Slice(start, length);
    public ReadOnlyMemory<byte> AsMemory() => Memory;
    public ReadOnlyMemory<byte> AsMemory(int start) => Memory[start..];
    public ReadOnlyMemory<byte> AsMemory(int start, int length) => Memory.Slice(start, length);
    public Utf8String Substring(int start) => new(Memory[start..]);
    public Utf8String Substring(int start, int length) => new(Memory.Slice(start, length));

    public bool StartsWith(ReadOnlySpan<byte> value, StringComparison comparison = StringComparison.Ordinal) => Span.StartsWith(value, comparison);
    public bool EndsWith(ReadOnlySpan<byte> value, StringComparison comparison = StringComparison.Ordinal) => Span.EndsWith(value, comparison);
    public bool StartsWith(byte value) => !IsEmpty && this[0] == value;
    public bool EndsWith(byte value) => !IsEmpty && this[Length - 1] == value;
    public bool Contains(byte value) => Span.Contains(value);
    public bool Contains(ReadOnlySpan<byte> value, StringComparison comparison = StringComparison.Ordinal) => Span.IndexOf(value, comparison) >= 0;
    public int IndexOf(byte value) => Span.IndexOf(value);
    public int IndexOf(byte value, int start) => Offset(Span[start..].IndexOf(value), start);
    public int IndexOf(ReadOnlySpan<byte> value, StringComparison comparison = StringComparison.Ordinal) => Span.IndexOf(value, comparison);
    public int IndexOf(ReadOnlySpan<byte> value, int start, StringComparison comparison = StringComparison.Ordinal) => Offset(Span[start..].IndexOf(value, comparison), start);
    public int LastIndexOf(byte value) => Span.LastIndexOf(value);
    public int LastIndexOf(byte value, int start) => Span[..(start + 1)].LastIndexOf(value);
    public int LastIndexOf(ReadOnlySpan<byte> value) => Span.LastIndexOf(value);
    public int LastIndexOf(ReadOnlySpan<byte> value, StringComparison comparison) => comparison == StringComparison.Ordinal
        ? Span.LastIndexOf(value) : throw new ArgumentOutOfRangeException(nameof(comparison));
    private static int Offset(int index, int offset) => index < 0 ? -1 : index + offset;
    public static bool IsNullOrEmpty(Utf8String? value) => value is null || value.Value.IsEmpty;
    public static bool IsNullOrWhiteSpace(Utf8String? value) => value is null || value.Value.Trim().IsEmpty;
    public bool Equals(Utf8String value, StringComparison comparison) => Span.Equals(value, comparison);
    public static bool Equals(Utf8String? left, Utf8String? right, StringComparison comparison) =>
        left is { } a && right is { } b ? a.Equals(b, comparison) : left.HasValue == right.HasValue;
    public static int Compare(Utf8String? left, Utf8String? right, StringComparison comparison) =>
        left is { } a && right is { } b ? a.Span.CompareTo(b, comparison) : left.HasValue.CompareTo(right.HasValue);

    public bool Equals(Utf8String other) => Span.SequenceEqual(other.Span);
    public static bool Equals(Utf8String value, ReadOnlySpan<byte> other) => value.Span.SequenceEqual(other);
    public static bool Equals(Utf8String? value, ReadOnlySpan<byte> other) => value is { } text && text.Span.SequenceEqual(other);
    public static bool Equals(ReadOnlySpan<byte> value, ReadOnlySpan<byte> other) => value.SequenceEqual(other);
    public override bool Equals(object? value) => value is Utf8String other && Equals(other);
    public override int GetHashCode() => Hash(Span);
    internal static int Hash(ReadOnlySpan<byte> value) => unchecked((int)XxHash3.HashToUInt64(value, HashSeed));
    public int CompareTo(Utf8String other) => Span.SequenceCompareTo(other.Span);

    // Conversion to System.String belongs at a .NET API boundary.
    public override string ToString() => Wtf8.DecodeString(Span);
    public string ToString(string? format, IFormatProvider? provider) => ToString();
    public bool TryFormat(Span<byte> destination, out int bytesWritten, ReadOnlySpan<char> format = default,
        IFormatProvider? provider = null)
    {
        if (!Span.TryCopyTo(destination))
        {
            bytesWritten = 0;
            return false;
        }
        bytesWritten = Length;
        return true;
    }

    public static implicit operator Utf8String(ReadOnlyMemory<byte> value) => new(value);
    public static implicit operator Utf8String(ReadOnlySpan<byte> value) => Copy(value);
    public static implicit operator ReadOnlySpan<byte>(Utf8String value) => value.Span;
    public static bool operator ==(Utf8String left, Utf8String right) => left.Equals(right);
    public static bool operator !=(Utf8String left, Utf8String right) => !left.Equals(right);
    public static bool operator ==(Utf8String left, ReadOnlySpan<byte> right) => left.Span.SequenceEqual(right);
    public static bool operator !=(Utf8String left, ReadOnlySpan<byte> right) => !left.Span.SequenceEqual(right);
    public static bool operator ==(ReadOnlySpan<byte> left, Utf8String right) => left.SequenceEqual(right.Span);
    public static bool operator !=(ReadOnlySpan<byte> left, Utf8String right) => !left.SequenceEqual(right.Span);
    public static Utf8String operator +(Utf8String left, Utf8String right) => Concat(left, right);
    public static Utf8String operator +(Utf8String left, ReadOnlySpan<byte> right) => Concat(left, right);
    public static Utf8String operator +(ReadOnlySpan<byte> left, Utf8String right) => Concat(left, right);
    public static Utf8String operator +(Utf8String left, long right) => Concat(left, Format(right));
    public static Utf8String operator +(Utf8String left, byte right) => Concat(left, [right]);
    public static Utf8String operator +(Utf8String left, char right)
    {
        Span<byte> bytes = stackalloc byte[4];
        return Concat(left, bytes[..Wtf8.EncodeCodePoint(right, bytes)]);
    }
    public static Utf8String operator +(char left, Utf8String right)
    {
        Span<byte> bytes = stackalloc byte[4];
        return Concat(bytes[..Wtf8.EncodeCodePoint(left, bytes)], right);
    }

    public static Utf8String Copy(ReadOnlySpan<byte> value) => value.IsEmpty ? default : new(value.ToArray());
    public static Utf8String Create<TState>(int length, TState state, SpanAction<byte, TState> action)
    {
        byte[] bytes = new byte[length];
        action(bytes, state);
        return new(bytes);
    }
    public static Utf8String FromCodePoint(int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        return Copy(bytes[..Wtf8.EncodeCodePoint(value, bytes)]);
    }
    public static Utf8String FromString(string value) => value.Length == 0 ? default : new(Wtf8.Encode(value));
    public static Utf8String? FromNullable(string? value) => value is null ? (Utf8String?)null : FromString(value);

    internal static Utf8String EnumName<T>(T value) where T : struct, Enum => EnumNames<T>.Values.TryGetValue(value, out var name)
        ? name : FromString(value.ToString());

    private static class EnumNames<T> where T : struct, Enum
    {
        // Enum names originate in CLR metadata; cache the boundary conversion once.
        internal static readonly FrozenDictionary<T, Utf8String> Values = Enum.GetValues<T>().Distinct()
            .ToFrozenDictionary(value => value, value => FromString(value.ToString()));
    }

    public Utf8String Trim()
    {
        int start = 0, end = Length;
        while (start < end)
        {
            int point = Wtf8.Decode(Span[start..end], out int width);
            if (!Rune.IsValid(point) || !Rune.IsWhiteSpace(new Rune(point)))
                break;
            start += width;
        }
        while (start < end)
        {
            int point = Wtf8.DecodeLast(Span[start..end], out int width);
            if (!Rune.IsValid(point) || !Rune.IsWhiteSpace(new Rune(point)))
                break;
            end -= width;
        }
        return new(Memory[start..end]);
    }

    public Utf8String Replace(byte oldValue, byte newValue)
    {
        if (oldValue == newValue || !Span.Contains(oldValue))
            return this;
        byte[] result = Span.ToArray();
        result.AsSpan().Replace(oldValue, newValue);
        return new(result);
    }

    public Utf8String Replace(ReadOnlySpan<byte> oldValue, ReadOnlySpan<byte> newValue)
    {
        if (oldValue.IsEmpty)
            throw new ArgumentException("The search text is empty", nameof(oldValue));
        int at = Span.IndexOf(oldValue);
        if (at < 0)
            return this;
        var builder = new Utf8StringBuilder(Length);
        ReadOnlySpan<byte> remaining = Span;
        do
        {
            builder.Append(remaining[..at]).Append(newValue);
            remaining = remaining[(at + oldValue.Length)..];
            at = remaining.IndexOf(oldValue);
        } while (at >= 0);
        return builder.Append(remaining).ToUtf8String();
    }

    public Utf8String Trim(byte value) => TrimStart(value).TrimEnd(value);
    public Utf8String TrimStart(byte value)
    {
        int start = 0;
        while (start < Length && this[start] == value)
            start++;
        return this[start..];
    }
    public Utf8String TrimEnd(byte value)
    {
        int end = Length;
        while (end > 0 && this[end - 1] == value)
            end--;
        return this[..end];
    }
    public Utf8String TrimEnd(byte first, byte second)
    {
        int end = Length;
        while (end > 0 && (this[end - 1] == first || this[end - 1] == second))
            end--;
        return this[..end];
    }

    public Utf8String ToLowerInvariant() => MapCase(false);
    public Utf8String ToUpperInvariant() => MapCase(true);
    private Utf8String MapCase(bool upper)
    {
        if (Ascii.IsValid(Span))
        {
            byte[] result = Span.ToArray();
            if (upper)
                Ascii.ToUpperInPlace(result, out _);
            else
                Ascii.ToLowerInPlace(result, out _);
            return new(result);
        }
        var builder = new Utf8StringBuilder(Length);
        ReadOnlySpan<byte> text = Span;
        while (!text.IsEmpty)
        {
            int point = Wtf8.Decode(text, out int width);
            if (Rune.IsValid(point))
                point = (upper ? Rune.ToUpperInvariant(new Rune(point)) : Rune.ToLowerInvariant(new Rune(point))).Value;
            builder.AppendCodePoint(point);
            text = text[width..];
        }
        return builder.ToUtf8String();
    }

    public Utf8String[] Split(byte separator, StringSplitOptions options = StringSplitOptions.None) => Split([separator], options);
    public Utf8String[] SplitAny(ReadOnlySpan<byte> separators, StringSplitOptions options = StringSplitOptions.None)
    {
        var result = new List<Utf8String>();
        int start = 0;
        do
        {
            int next = Span[start..].IndexOfAny(separators);
            var part = next < 0 ? this[start..] : this[start..(start + next)];
            if ((options & StringSplitOptions.TrimEntries) != 0)
                part = part.Trim();
            if ((options & StringSplitOptions.RemoveEmptyEntries) == 0 || !part.IsEmpty)
                result.Add(part);
            if (next < 0)
                break;
            start += next + 1;
        } while (start <= Length);
        return result.ToArray();
    }
    public Utf8String[] Split(ReadOnlySpan<byte> separator, StringSplitOptions options = StringSplitOptions.None)
    {
        var result = new List<Utf8String>();
        int start = 0;
        do
        {
            int next = separator.IsEmpty ? -1 : Span[start..].IndexOf(separator);
            var part = next < 0 ? this[start..] : this[start..(start + next)];
            if ((options & StringSplitOptions.TrimEntries) != 0)
                part = part.Trim();
            if ((options & StringSplitOptions.RemoveEmptyEntries) == 0 || !part.IsEmpty)
                result.Add(part);
            if (next < 0)
                break;
            start += next + separator.Length;
        } while (start <= Length);
        return result.ToArray();
    }

    private static byte[] Repeat(int point, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Span<byte> encoded = stackalloc byte[4];
        int width = Wtf8.EncodeCodePoint(point, encoded);
        byte[] result = new byte[checked(width * count)];
        if (width == 1)
            result.AsSpan().Fill(encoded[0]);
        else
            for (int at = 0; at < result.Length; at += width)
                encoded[..width].CopyTo(result.AsSpan(at));
        return result;
    }

    public static Utf8String Format<T>(T value, ReadOnlySpan<char> format = default) where T : IUtf8SpanFormattable
    {
        Span<byte> buffer = stackalloc byte[64];
        if (value.TryFormat(buffer, out int written, format, CultureInfo.InvariantCulture))
            return Copy(buffer[..written]);
        int capacity = buffer.Length;
        while (true)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(checked(capacity * 2));
            try
            {
                if (value.TryFormat(rented, out written, format, CultureInfo.InvariantCulture))
                    return Copy(rented.AsSpan(0, written));
                capacity = rented.Length;
            }
            finally { ArrayPool<byte>.Shared.Return(rented); }
        }
    }

    internal static Utf8String FromBuilder(Utf8StringBuilder builder) => builder.ToUtf8String();

    public static Utf8String Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] result = new byte[checked(first.Length + second.Length)];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return new(result);
    }

    public static Utf8String Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third)
    {
        byte[] result = new byte[checked(first.Length + second.Length + third.Length)];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        third.CopyTo(result.AsSpan(first.Length + second.Length));
        return new(result);
    }

    public static Utf8String Join(ReadOnlySpan<byte> separator, IEnumerable<Utf8String> values)
    {
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext())
            return default;
        Utf8String first = iterator.Current;
        if (!iterator.MoveNext())
            return first;
        var builder = new Utf8StringBuilder().Append(first);
        do
        { builder.Append(separator).Append(iterator.Current); } while (iterator.MoveNext());
        return builder.ToUtf8String();
    }

    public static Utf8String Join(byte separator, IEnumerable<Utf8String> values) => Join([separator], values);
    public static Utf8String Join(byte separator, Utf8String[] values, int start, int count) => Join([separator], values.Skip(start).Take(count));
    public static Utf8String Concat(IEnumerable<Utf8String> values) => Join([], values);

    internal static Utf8String Frame(ReadOnlySpan<Utf8String> values)
    {
        var builder = new Utf8StringBuilder();
        foreach (var value in values)
            builder.Append(value.Length).Append((byte)':').Append(value);
        return builder.ToUtf8String();
    }

    public static Utf8String ConcatMany(params ReadOnlySpan<Utf8String> values)
    {
        int length = 0;
        foreach (var value in values)
            length = checked(length + value.Length);
        if (length == 0)
            return default;
        byte[] result = new byte[length];
        Span<byte> target = result;
        foreach (var value in values)
        {
            value.Span.CopyTo(target);
            target = target[value.Length..];
        }
        return new(result);
    }
}
