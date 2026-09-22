using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace TypeScript.Compiler.Text;

// Go stringutil.DecodeJSStringRune: invalid UTF-8 consumes one byte, not one
// maximal invalid subsequence. Encoding.UTF8's replacement fallback differs.
public static class Wtf8
{
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
            return 0xD000 | ((source[1] & 63) << 6) | (source[2] & 63);
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
        Span<byte> destination = result;
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
            if (status != OperationStatus.InvalidData)
                throw new InvalidOperationException("Unexpected UTF-8 conversion size");
            char surrogate = text[0];
            destination[0] = 0xED;
            destination[1] = (byte)(0x80 | ((surrogate >> 6) & 63));
            destination[2] = (byte)(0x80 | (surrogate & 63));
            destination = destination[3..];
            text = text[1..];
        }
        return result;
    }

    public static string DecodeString(ReadOnlySpan<byte> source)
    {
        if (Utf8.IsValid(source))
            return Encoding.UTF8.GetString(source);
        var buffer = new ArrayBufferWriter<char>(Math.Max(source.Length, 1));
        while (!source.IsEmpty)
        {
            int value = Decode(source, out int consumed);
            Span<char> target = buffer.GetSpan(2);
            if (value <= 0xFFFF)
            {
                target[0] = (char)value;
                buffer.Advance(1);
            }
            else
                buffer.Advance(new Rune(value).EncodeToUtf16(target));
            source = source[consumed..];
        }
        return new string(buffer.WrittenSpan);
    }

    // Retains malformed bytes exactly, as Go CombineSurrogatePairs does.
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
