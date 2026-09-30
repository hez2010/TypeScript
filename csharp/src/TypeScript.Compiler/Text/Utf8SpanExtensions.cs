namespace TypeScript.Compiler.Text;

internal static class Utf8SpanExtensions
{
    internal static bool Contains(this ReadOnlySpan<byte> value, ReadOnlySpan<byte> part, StringComparison comparison) => value.IndexOf(part, comparison) >= 0;
    internal static bool ContainsLineBreak(this ReadOnlySpan<byte> value)
    {
        foreach (var rune in value.EnumerateRunes())
            if (rune.Value is '\r' or '\n' or 0x2028 or 0x2029)
                return true;
        return false;
    }
    internal static Utf8RuneEnumerator EnumerateRunes(this ReadOnlySpan<byte> value) => new(value);

    internal ref struct Utf8RuneEnumerator(ReadOnlySpan<byte> text)
    {
        private ReadOnlySpan<byte> remaining = text;
        public System.Text.Rune Current { get; private set; }
        public Utf8RuneEnumerator GetEnumerator() => this;
        public bool MoveNext()
        {
            if (remaining.IsEmpty)
                return false;
            if (System.Text.Rune.DecodeFromUtf8(remaining, out var rune, out int width) != System.Buffers.OperationStatus.Done)
            {
                rune = System.Text.Rune.ReplacementChar;
                width = 1;
            }
            Current = rune;
            remaining = remaining[width..];
            return true;
        }
    }
    internal static Utf8String ToUtf8String(this ReadOnlySpan<byte> value) => Utf8String.Copy(value);
    internal static ReadOnlySpan<byte> TrimStart(this ReadOnlySpan<byte> value)
    {
        int start = 0;
        while (start < value.Length)
        {
            int point = Wtf8.Decode(value[start..], out int width);
            if (!System.Text.Rune.IsValid(point) || !System.Text.Rune.IsWhiteSpace(new System.Text.Rune(point)))
                break;
            start += width;
        }
        return value[start..];
    }
    internal static ReadOnlySpan<byte> TrimEnd(this ReadOnlySpan<byte> value)
    {
        int end = value.Length;
        while (end > 0)
        {
            int point = Wtf8.DecodeLast(value[..end], out int width);
            if (!System.Text.Rune.IsValid(point) || !System.Text.Rune.IsWhiteSpace(new System.Text.Rune(point)))
                break;
            end -= width;
        }
        return value[..end];
    }
    internal static ReadOnlySpan<byte> Trim(this ReadOnlySpan<byte> value) => value.TrimStart().TrimEnd();

    internal static bool Equals(this ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, StringComparison comparison) =>
        Comparer(comparison).Equals(left, right);

    internal static int CompareTo(this ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, StringComparison comparison) =>
        Comparer(comparison).Compare(left, right);

    internal static bool StartsWith(this ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix, StringComparison comparison)
    {
        if (comparison == StringComparison.Ordinal)
            return value.StartsWith(prefix);
        _ = Comparer(comparison);
        if (value.Length >= prefix.Length && System.Text.Ascii.IsValid(prefix)
            && System.Text.Ascii.IsValid(value[..prefix.Length]))
            return System.Text.Ascii.EqualsIgnoreCase(value[..prefix.Length], prefix);
        while (!prefix.IsEmpty)
        {
            if (value.IsEmpty)
                return false;
            if (Utf8StringComparer.Fold(Wtf8.Decode(value, out int width)) !=
                Utf8StringComparer.Fold(Wtf8.Decode(prefix, out int prefixWidth)))
                return false;
            value = value[width..];
            prefix = prefix[prefixWidth..];
        }
        return true;
    }

    internal static bool EndsWith(this ReadOnlySpan<byte> value, ReadOnlySpan<byte> suffix, StringComparison comparison)
    {
        if (comparison == StringComparison.Ordinal)
            return value.EndsWith(suffix);
        _ = Comparer(comparison);
        if (value.Length >= suffix.Length && System.Text.Ascii.IsValid(suffix)
            && System.Text.Ascii.IsValid(value[^suffix.Length..]))
            return System.Text.Ascii.EqualsIgnoreCase(value[^suffix.Length..], suffix);
        while (!suffix.IsEmpty)
        {
            if (value.IsEmpty)
                return false;
            if (Utf8StringComparer.Fold(Wtf8.DecodeLast(value, out int width)) !=
                Utf8StringComparer.Fold(Wtf8.DecodeLast(suffix, out int suffixWidth)))
                return false;
            value = value[..^width];
            suffix = suffix[..^suffixWidth];
        }
        return true;
    }

    internal static int IndexOf(this ReadOnlySpan<byte> value, ReadOnlySpan<byte> part, StringComparison comparison)
    {
        if (comparison == StringComparison.Ordinal)
            return value.IndexOf(part);
        for (int i = 0; i <= value.Length;)
        {
            if (value[i..].StartsWith(part, comparison))
                return i;
            if (i == value.Length)
                break;
            _ = Wtf8.Decode(value[i..], out int width);
            i += width;
        }
        return -1;
    }

    private static Utf8StringComparer Comparer(StringComparison comparison) => comparison switch
    {
        StringComparison.Ordinal => Utf8StringComparer.Ordinal,
        StringComparison.OrdinalIgnoreCase => Utf8StringComparer.OrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison))
    };
}
