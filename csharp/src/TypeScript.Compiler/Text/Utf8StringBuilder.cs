using System.Globalization;

namespace TypeScript.Compiler.Text;

internal sealed class Utf8StringBuilder(int capacity = 0)
{
    private byte[] buffer = capacity == 0 ? [] : new byte[capacity];
    private int length;
    public int Length
    {
        get => length;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value > length)
                Reserve(value - length)[..(value - length)].Clear();
            length = value;
        }
    }
    public byte this[int index] => WrittenSpan[index];
    public ReadOnlySpan<byte> WrittenSpan => buffer.AsSpan(0, Length);
    public Utf8StringBuilder(Utf8String value) : this(value.Length) => Append(value);

    private Span<byte> Reserve(int length)
    {
        int required = checked(Length + length);
        if (required > buffer.Length)
            Array.Resize(ref buffer, Math.Max(required, Math.Max(buffer.Length * 2, 32)));
        return buffer.AsSpan(Length);
    }

    public Utf8StringBuilder Append(ReadOnlySpan<byte> value)
    {
        value.CopyTo(Reserve(value.Length));
        length += value.Length;
        return this;
    }

    public Utf8StringBuilder Append(Utf8String value) => Append(value.Span);
    public Utf8StringBuilder Append(Utf8String? value) => value is { } text ? Append(text) : this;
    public Utf8StringBuilder Append(Utf8String value, int start, int count) => Append(value.Span.Slice(start, count));
    public Utf8StringBuilder Append(int point, int count)
    {
        if (point < 128)
        {
            Reserve(count)[..count].Fill((byte)point);
            length += count;
        }
        else
            for (int i = 0; i < count; i++)
                AppendCodePoint(point);
        return this;
    }

    public Utf8StringBuilder Append(byte value)
    {
        Reserve(1)[0] = value;
        length++;
        return this;
    }

    public Utf8StringBuilder Append(char value) => AppendCodePoint(value);

    public Utf8StringBuilder AppendCodePoint(int value)
    {
        length += Wtf8.EncodeCodePoint(value, Reserve(4));
        return this;
    }

    public Utf8StringBuilder Append<T>(T value) where T : IUtf8SpanFormattable
    {
        int capacity = 32;
        int written;
        while (!value.TryFormat(Reserve(capacity), out written, default, CultureInfo.InvariantCulture))
            capacity = checked(capacity * 2);
        length += written;
        return this;
    }

    public Utf8StringBuilder Clear()
    {
        Length = 0;
        return this;
    }

    public Utf8String ToUtf8String() => Utf8String.Copy(WrittenSpan);
    public override string ToString() => Wtf8.DecodeString(WrittenSpan);
}
