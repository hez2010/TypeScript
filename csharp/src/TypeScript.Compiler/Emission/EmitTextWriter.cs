using System.Buffers;
using System.Text;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Emission;

/// <summary>Writes UTF-8 text and tracks the UTF-16 columns required by source maps.</summary>
public sealed class EmitTextWriter(Utf8String newLine = default, int indentSize = 4, bool inlineDisplay = false)
{
    private readonly Utf8StringBuilder text = new();
    private readonly Utf8String newLine = newLine.Length == 0 ? "\n"u8 : newLine;
    private readonly int indentSize = indentSize >= 0 ? indentSize : 4;
    private int lineStart;
    private bool atLineStart = true;
    private bool carriageReturn;

    public int Position => text.Length;
    public int Line { get; private set; }
    public int Indentation { get; private set; }
    public bool AtLineStart => !inlineDisplay && atLineStart;
    public bool HasTrailingComment { get; private set; }
    public int Column => atLineStart ? Indentation * indentSize : Utf16Length(text.WrittenSpan[lineStart..]);
    public Utf8String Text => text.ToUtf8String();
    public bool HasTrailingWhitespace => text.Length != 0 && WhiteSpace(Wtf8.DecodeLast(text.WrittenSpan, out _));

    private static bool WhiteSpace(int point) => Syntax.TokenFacts.IsWhiteSpace(point) || Syntax.TokenFacts.IsLineBreak(point);

    public void IncreaseIndent() => Indentation++;

    public void DecreaseIndent()
    {
        if (Indentation == 0)
            throw new InvalidOperationException("Unbalanced printer indentation");
        Indentation--;
    }

    public void Clear()
    {
        text.Clear();
        lineStart = Line = Indentation = 0;
        atLineStart = true;
        carriageReturn = HasTrailingComment = false;
    }

    public void Write(Utf8String value) => Write(value.Span);

    public void Write(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;
        if (atLineStart)
        {
            if (!inlineDisplay) text.Append((byte)' ', checked(Indentation * indentSize));
            atLineStart = false;
        }
        RawWrite(value);
    }

    public void WriteComment(Utf8String value)
    {
        Write(value);
        if (value.Length != 0)
            HasTrailingComment = true;
    }

    public void RawWrite(Utf8String value) => RawWrite(value.Span);

    public void RawWrite(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return;
        int start = text.Length;
        text.Append(value);
        HasTrailingComment = false;
        for (int position = 0; position < value.Length;)
        {
            int point = Wtf8.Decode(value[position..], out int width);
            position += width;
            if (point == '\n' && carriageReturn)
                lineStart = start + position;
            else if (point is '\r' or '\n' or 0x2028 or 0x2029)
            {
                Line++;
                lineStart = start + position;
            }
            carriageReturn = point == '\r';
        }
        atLineStart = lineStart == text.Length;
    }

    public void WriteLine(bool force = false)
    {
        if (inlineDisplay) { RawWrite(" "u8); return; }
        if (!atLineStart || force)
            RawWrite(newLine);
    }

    internal static int Utf16Length(ReadOnlySpan<byte> value)
    {
        int length = 0;
        while (!value.IsEmpty)
        {
            // Source maps describe emitted UTF-8 bytes. Invalid bytes use the
            // reference's one-byte replacement decoding, including raw WTF-8.
            if (Rune.DecodeFromUtf8(value, out var rune, out int width) == OperationStatus.Done)
                length += rune.Utf16SequenceLength;
            else
            {
                width = 1;
                length++;
            }
            value = value[width..];
        }
        return length;
    }
}
