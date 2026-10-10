namespace TypeScript.Compiler.Text;

/// <summary>Owns UTF-8 source text. Syntax and scanner positions are byte offsets.</summary>
public sealed class SourceText
{
    private byte[] bytes;
    private int ascii;
    private int[]? lineStarts;
    public Utf8String Text { get; private set; }
    public ReadOnlyMemory<byte> Bytes => bytes;
    internal byte[] Buffer => bytes;
    public int Length => Text.Length;
    internal bool IsAsciiOnly
    {
        get
        {
            int value = Volatile.Read(ref ascii);
            if (value == 0)
                Volatile.Write(ref ascii, value = System.Text.Ascii.IsValid(bytes) ? 1 : 2);
            return value == 1;
        }
    }

    public SourceText(ReadOnlySpan<byte> bytes) : this(bytes.ToArray()) { }

    // The host has already supplied an owned buffer, or a permanently immutable library buffer.
    internal static SourceText FromOwnedBytes(byte[] bytes) => new(bytes);

    private SourceText(byte[] bytes)
    {
        this.bytes = bytes;
        Text = new(bytes);
    }

    public SourceText(Utf8String text) : this(text.Span.ToArray()) { }

    /// <summary>
    /// Re-points this instance at new content, reusing the buffer when it is large enough. Intended
    /// for private scratch instances (the printer's recipe scanner) so per-call text scanning does
    /// not allocate a copy of the text on every call. Not safe for shared instances.
    /// </summary>
    internal void Reset(ReadOnlySpan<byte> value)
    {
        if (bytes.Length < value.Length)
            bytes = new byte[Math.Max(value.Length, 64)];
        value.CopyTo(bytes);
        Text = new Utf8String(bytes.AsMemory(0, value.Length));
        lineStarts = null;
        ascii = 0;
    }

    public ReadOnlySpan<int> LineStarts
    {
        get
        {
            if (lineStarts is null)
            {
                var starts = new List<int> { 0 };
                for (int pos = 0; pos < Text.Length; pos++)
                {
                    int ch = bytes[pos];
                    if (ch >= 128)
                    {
                        ch = Wtf8.Decode(bytes.AsSpan(pos), out int width);
                        pos += width - 1;
                    }
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
        int line = LineStarts.BinarySearch(bytePosition);
        if (line < 0)
            line = ~line - 1;
        return (line, bytePosition - LineStarts[line]);
    }
}
