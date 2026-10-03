using System.Buffers;
using System.Text;

namespace TypeScript.Compiler.LanguageServices;

public enum PositionEncoding { Utf8, Utf16 }
public readonly record struct DocumentPosition(uint Line, uint Character);
public readonly record struct DocumentRange(DocumentPosition Start, DocumentPosition End);
public readonly record struct DocumentEdit(Utf8String Text, DocumentRange? Range = null);

/// <summary>LSP coordinates over UTF-8 source. Only CR, LF and CRLF start an LSP line.</summary>
public sealed class DocumentLineMap
{
    private readonly Utf8String text;
    private readonly int[] starts;
    private readonly bool asciiOnly;

    public DocumentLineMap(Utf8String text)
    {
        this.text = text;
        List<int> lines = [0];
        asciiOnly = true;
        for (int i = 0; i < text.Length; i++)
        {
            byte value = text.Span[i];
            asciiOnly &= value < 128;
            if (value == '\r')
            {
                if (i + 1 < text.Length && text.Span[i + 1] == '\n') i++;
                lines.Add(i + 1);
            }
            else if (value == '\n') lines.Add(i + 1);
        }
        starts = lines.ToArray();
    }

    public int LineCount => starts.Length;
    public int LineStart(int line) => starts[line];

    public int ToOffset(DocumentPosition position, PositionEncoding encoding)
    {
        if (position.Line >= starts.Length) return text.Length;
        int line = (int)position.Line, start = starts[line];
        int end = line + 1 < starts.Length ? starts[line + 1] : text.Length;
        if (asciiOnly || encoding == PositionEncoding.Utf8)
            return (int)Math.Min((long)start + position.Character, end);
        uint character = 0;
        int offset = start;
        while (offset < end)
        {
            int width = Decode(text.Span[offset..end], out int units);
            if ((ulong)character + (uint)units > position.Character) break;
            character += (uint)units;
            offset += width;
        }
        return offset;
    }

    public DocumentPosition ToPosition(int offset, PositionEncoding encoding)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = Array.BinarySearch(starts, offset);
        if (line < 0) line = ~line - 1;
        int start = starts[line], character = offset - start;
        if (!asciiOnly && encoding != PositionEncoding.Utf8)
        {
            character = 0;
            while (start < offset)
            {
                start += Decode(text.Span[start..offset], out int units);
                character += units;
            }
        }
        return new((uint)line, (uint)character);
    }

    public DocumentRange ToRange(int start, int end, PositionEncoding encoding) =>
        new(ToPosition(start, encoding), ToPosition(end, encoding));

    // LSP's reference converter uses Unicode UTF-8 decoding: each invalid byte is one replacement.
    private static int Decode(ReadOnlySpan<byte> bytes, out int units)
    {
        if (Rune.DecodeFromUtf8(bytes, out Rune rune, out int width) == OperationStatus.Done)
        {
            units = rune.Utf16SequenceLength;
            return width;
        }
        units = 1;
        return 1;
    }
}
