namespace TypeScript.Compiler.Text;

/// <summary>Owns original source bytes and a lossless UTF-16 scanning view.</summary>
public sealed class SourceText
{
    private readonly byte[] bytes;
    private readonly PositionMap map;
    private int[]? lineStarts;
    public string Text { get; }
    public ReadOnlyMemory<byte> Bytes => bytes;
    public int Length => Text.Length;

    public SourceText(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes.ToArray();
        Text = Wtf8.DecodeString(bytes);
        map = new PositionMap(bytes);
    }

    public SourceText(string text)
    {
        Text = text;
        bytes = Wtf8.Encode(text);
        map = new PositionMap(bytes);
    }

    public int ToBytePosition(int utf16Position) => map.Utf16ToUtf8(utf16Position);

    public int ToUtf16Position(int bytePosition) => map.Utf8ToUtf16(bytePosition);

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
