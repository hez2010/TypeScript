using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace TypeScript.Compiler.Text;

// Go stringutil.DecodeJSStringRune: invalid UTF-8 consumes one byte, not one
// maximal invalid subsequence. Encoding.UTF8's replacement fallback differs.
public static class Wtf8
{
    public static int EncodeCodePoint(int value, Span<byte> destination)
    {
        if (value is >= 0xD800 and <= 0xDFFF)
        {
            destination[0] = 0xED;
            destination[1] = (byte)(0x80 | value >> 6 & 63);
            destination[2] = (byte)(0x80 | value & 63);
            return 3;
        }
        return new Rune(value).EncodeToUtf8(destination);
    }

    public static int DecodeLast(ReadOnlySpan<byte> source, out int consumed)
    {
        if (source.IsEmpty)
        { consumed = 0; return 0xFFFD; }
        int start = source.Length - 1;
        while (start > 0 && source.Length - start < 4 && (source[start] & 0xC0) == 0x80)
            start--;
        int point = Decode(source[start..], out consumed);
        if (consumed == source.Length - start)
            return point;
        consumed = 1;
        return 0xFFFD;
    }

    // Continuation bytes, overlong headers and out-of-range headers cannot
    // begin a code point. Each consumes one replacement character in Go.
    private static readonly SearchValues<byte> InvalidLeadingBytes = SearchValues.Create(
        Enumerable.Range(0x80, 0xC2 - 0x80).Concat(Enumerable.Range(0xF5, 0x100 - 0xF5))
            .Select(static value => (byte)value).ToArray());

    public static int Decode(ReadOnlySpan<byte> source, out int consumed)
    {
        if (source.IsEmpty)
        {
            consumed = 0;
            return 0xFFFD;
        }
        if (source.Length >= 3 && source[0] == 0xED && source[1] is >= 0xA0 and <= 0xBF && (source[2] & 0xC0) == 0x80)
        {
            consumed = 3;
            return 0xD000 | (source[1] & 63) << 6 | source[2] & 63;
        }
        if (Rune.DecodeFromUtf8(source, out Rune rune, out consumed) == OperationStatus.Done)
            return rune.Value;
        consumed = 1;
        return 0xFFFD;
    }

    public static byte[] Encode(ReadOnlySpan<char> text)
    {
        // UTF-8 replacement uses three bytes per unpaired surrogate, exactly
        // the width needed by WTF-8, so the BCL gives an exact allocation size.
        byte[] result = new byte[Encoding.UTF8.GetByteCount(text)];
        Encode(text, result);
        return result;
    }

    public static int Encode(ReadOnlySpan<char> text, Span<byte> destination)
    {
        int capacity = destination.Length;
        while (!text.IsEmpty)
        {
            OperationStatus status = Utf8.FromUtf16(
                text,
                destination,
                out int charsRead,
                out int bytesWritten,
                replaceInvalidSequences: false);
            text = text[charsRead..];
            destination = destination[bytesWritten..];
            if (status == OperationStatus.Done)
                break;
            if (status == OperationStatus.DestinationTooSmall || destination.Length < 3)
                throw new ArgumentException("Destination is too short", nameof(destination));
            if (status != OperationStatus.InvalidData)
                throw new InvalidOperationException("Unexpected UTF-8 conversion status");
            char surrogate = text[0];
            destination[0] = 0xED;
            destination[1] = (byte)(0x80 | surrogate >> 6 & 63);
            destination[2] = (byte)(0x80 | surrogate & 63);
            destination = destination[3..];
            text = text[1..];
        }
        return capacity - destination.Length;
    }

    public static string DecodeString(ReadOnlySpan<byte> source)
        => DecodeString(source, out _);

    internal static string DecodeString(ReadOnlySpan<byte> source, out bool validUtf8)
    {
        validUtf8 = Utf8.IsValid(source);
        return validUtf8 ? Encoding.UTF8.GetString(source) : DecodeMalformed(source);
    }

    private static string DecodeMalformed(ReadOnlySpan<byte> source)
    {
        char[] buffer = ArrayPool<char>.Shared.Rent(source.Length);
        try
        {
            Span<char> target = buffer.AsSpan(0, source.Length);
            int capacity = target.Length;
            while (!source.IsEmpty)
            {
                if (InvalidLeadingBytes.Contains(source[0]))
                {
                    int count = source.IndexOfAnyExcept(InvalidLeadingBytes);
                    if (count < 0)
                        count = source.Length;
                    target[..count].Fill('\uFFFD');
                    target = target[count..];
                    source = source[count..];
                    continue;
                }
                // Keep BCL bulk transcoding for valid runs even when a file
                // also contains malformed bytes or WTF-8 surrogate sequences.
                OperationStatus status = Utf8.ToUtf16(source, target,
                    out int bytesRead, out int charsWritten, replaceInvalidSequences: false);
                target = target[charsWritten..];
                source = source[bytesRead..];
                if (status == OperationStatus.Done)
                    break;
                if (status != OperationStatus.InvalidData)
                    throw new InvalidOperationException("Unexpected UTF-16 conversion status");
                int value = Decode(source, out int consumed);
                if (value <= 0xFFFF)
                {
                    target[0] = (char)value;
                    target = target[1..];
                }
                else
                    target = target[new Rune(value).EncodeToUtf16(target)..];
                source = source[consumed..];
            }
            return new string(buffer.AsSpan(0, capacity - target.Length));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    // Retains malformed bytes exactly, as Go CombineSurrogatePairs does.
    internal static Utf8String CombineSurrogatePairs(Utf8String value) => !value.Span.Contains((byte)0xED)
        ? value : new(CombineSurrogatePairs(value.Span));

    public static byte[] CombineSurrogatePairs(ReadOnlySpan<byte> source)
    {
        if (!source.Contains((byte)0xED))
            return source.ToArray();
        var buffer = new ArrayBufferWriter<byte>(Math.Max(source.Length, 1));
        while (!source.IsEmpty)
        {
            int value = Decode(source, out int consumed);
            if (value is >= 0xD800 and <= 0xDBFF)
            {
                int low = Decode(source[consumed..], out int lowSize);
                if (low is >= 0xDC00 and <= 0xDFFF)
                {
                    buffer.Advance(new Rune((char)value, (char)low).EncodeToUtf8(buffer.GetSpan(4)));
                    source = source[(consumed + lowSize)..];
                    continue;
                }
            }
            buffer.Write(source[..consumed]);
            source = source[consumed..];
        }
        return buffer.WrittenSpan.ToArray();
    }
}
