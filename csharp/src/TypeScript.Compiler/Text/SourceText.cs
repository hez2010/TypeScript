namespace TypeScript.Compiler.Text;

/// <summary>Owns original source bytes and a lossless UTF-16 scanning view.</summary>
public sealed class SourceText
{
    private readonly byte[] bytes;
    private readonly PositionMap map;
    private int[]? lineStarts;
    public TextSlice Text { get; }
    public ReadOnlyMemory<byte> Bytes => bytes;
    public int Length => Text.Length;
    internal bool IsAsciiOnly => map.IsAsciiOnly;

    public SourceText(ReadOnlySpan<byte> bytes) : this(bytes.ToArray()) { }

    // The host has already supplied an owned buffer, or a permanently immutable library buffer.
    internal static SourceText FromOwnedBytes(byte[] bytes) => new(bytes);

    private SourceText(byte[] bytes)
    {
        this.bytes = bytes;
        Text = Wtf8.DecodeString(bytes, out bool validUtf8);
        map = validUtf8 && bytes.Length == Text.Length ? PositionMap.Ascii : new PositionMap(bytes, validUtf8);
    }

    public SourceText(string text) : this((TextSlice)text) { }

    public SourceText(TextSlice text)
    {
        Text = text;
        bytes = Wtf8.Encode(text);
        map = bytes.Length == text.Length ? PositionMap.Ascii : new PositionMap(bytes);
    }

    public int ToBytePosition(int utf16Position) => map.Utf16ToUtf8(utf16Position);

    public int ToUtf16Position(int bytePosition) => map.Utf8ToUtf16(bytePosition);

    // Starts and ends follow different sequences during a tree walk. Separate
    // interval hints keep one endpoint from evicting the other's nearby range.
    internal (int Start, int End) ToByteRange(int start, int end) => map.Utf16ToUtf8(start, end);

    public ReadOnlySpan<int> LineStarts
    {
        get
        {
            if (lineStarts is null)
            {
                var starts = new List<int> { 0 };
                for (int pos = 0; pos < Text.Length; pos++)
                {
                    char ch = Text[pos];
                    if (ch == '\r' && pos + 1 < Text.Length && Text[pos + 1] == '\n')
                        pos++;
                    if (ch is '\r' or '\n' or '\u2028' or '\u2029')
                        starts.Add(pos + 1);
                }
                Interlocked.CompareExchange(ref lineStarts, starts.ToArray(), null);
            }
            return lineStarts;
        }
    }

    public (int Line, int Character) GetLineAndCharacter(int bytePosition)
    {
        int pos = ToUtf16Position(bytePosition);
        int line = LineStarts.BinarySearch(pos);
        if (line < 0)
            line = ~line - 1;
        return (line, pos - LineStarts[line]);
    }
}
