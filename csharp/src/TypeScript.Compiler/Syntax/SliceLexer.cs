using System.Buffers;
using System.Globalization;
using System.Text;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;

namespace TypeScript.Compiler.Syntax;

public interface ISourceView
{
    int Length { get; }

    int Read(int position, out int width);

    int ByteOffset(int position);

    Utf8String Slice(int start, int end);
}

public readonly struct Utf8Source(ReadOnlyMemory<byte> bytes) : ISourceView
{
    public int Length => bytes.Length;

    public int Read(int position, out int width) => Wtf8.Decode(bytes.Span[position..], out width);

    public int ByteOffset(int position) => position;

    public Utf8String Slice(int start, int end) => new(bytes.Slice(start, end - start));
}

public readonly record struct SliceToken(
    SyntaxKind Kind,
    int Pos,
    int Start,
    int End,
    Utf8String Text = default,
    uint Flags = 0,
    bool LineBreak = false);
public readonly record struct SliceDiagnostic(DiagnosticCode Code, int Pos, int Length, Utf8String Message);

// Scanner slice for the selected TypeScript productions. Unhandled tokens are
// returned as Unknown and produce a diagnostic, never a successful compilation.
public sealed class SliceLexer<TSource>(TSource source, List<SliceDiagnostic> diagnostics) where TSource : struct, ISourceView
{
    private int position;

    private int Peek(int delta = 0) => position + delta < source.Length ? source.Read(position + delta, out _) : -1;

    private int Take()
    {
        int value = source.Read(position, out int width);
        position += width;
        return value;
    }

    private static bool IdentifierStart(int cp) => cp is '$' or '_' || Rune.IsValid(cp) && Rune.IsLetter(new Rune(cp));

    private static bool IdentifierPart(int cp) =>
        IdentifierStart(cp)
            || cp is >= '0' and <= '9' or 0x200C or 0x200D
            || Rune.IsValid(cp)
                && Rune.GetUnicodeCategory(new Rune(cp)) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.ConnectorPunctuation;

    private static bool Newline(int cp) => cp is '\r' or '\n' or 0x2028 or 0x2029;

    public SliceToken Scan()
    {
        Diagnostics.NativeProfile.Poll();
        int fullStart = position;
        bool lineBreak = false;
        while (position < source.Length)
        {
            int cp = Peek();
            if (cp is ' ' or '\t' or '\v' or '\f' or 0xFEFF || Newline(cp))
            {
                lineBreak |= Newline(cp);
                Take();
                continue;
            }
            if (cp == '/' && Peek(1) == '/')
            {
                while (position < source.Length && !Newline(Peek()))
                    Take();
                continue;
            }
            if (cp == '/' && Peek(1) == '*')
            {
                int comment = position;
                position += 2;
                while (position < source.Length && !(Peek() == '*' && Peek(1) == '/'))
                    lineBreak |= Newline(Take());
                if (position == source.Length)
                    diagnostics.Add(new(DiagnosticCode.AsteriskSlashExpected, source.ByteOffset(comment), 2, Utf8Literals.Expected));
                else
                    position += 2;
                continue;
            }
            break;
        }
        int start = position;
        if (position == source.Length)
            return Token(SyntaxKind.EndOfFile);
        int ch = Take();
        if (IdentifierStart(ch))
        {
            while (position < source.Length && IdentifierPart(Peek()))
                Take();
            Utf8String text = source.Slice(start, position);
            SyntaxKind kind = text switch
            {
                _ when text == "type"u8 => SyntaxKind.TypeKeyword,
                _ when text == "export"u8 => SyntaxKind.ExportKeyword,
                _ when text == "import"u8 => SyntaxKind.ImportKeyword,
                _ when text == "from"u8 => SyntaxKind.FromKeyword,
                _ when text == "as"u8 => SyntaxKind.AsKeyword,
                _ when text == "const"u8 => SyntaxKind.ConstKeyword,
                _ when text == "let"u8 => SyntaxKind.LetKeyword,
                _ when text == "var"u8 => SyntaxKind.VarKeyword,
                _ when text == "string"u8 => SyntaxKind.StringKeyword,
                _ when text == "number"u8 => SyntaxKind.NumberKeyword,
                _ when text == "bigint"u8 => SyntaxKind.BigIntKeyword,
                _ when text == "boolean"u8 => SyntaxKind.BooleanKeyword,
                _ when text == "symbol"u8 => SyntaxKind.SymbolKeyword,
                _ when text == "object"u8 => SyntaxKind.ObjectKeyword,
                _ when text == "any"u8 => SyntaxKind.AnyKeyword,
                _ when text == "unknown"u8 => SyntaxKind.UnknownKeyword,
                _ when text == "never"u8 => SyntaxKind.NeverKeyword,
                _ when text == "void"u8 => SyntaxKind.VoidKeyword,
                _ when text == "undefined"u8 => SyntaxKind.UndefinedKeyword,
                _ when text == "null"u8 => SyntaxKind.NullKeyword,
                _ when text == "true"u8 => SyntaxKind.TrueKeyword,
                _ when text == "false"u8 => SyntaxKind.FalseKeyword,
                _ => SyntaxKind.Identifier,
            };
            return Token(kind, text);
        }
        if (ch is >= '0' and <= '9')
        {
            while (Peek() is >= '0' and <= '9')
                Take();
            if (Peek() == '.')
            {
                Take();
                while (Peek() is >= '0' and <= '9')
                    Take();
            }
            uint flags = 0;
            if (Peek() is 'e' or 'E')
            {
                flags = 16;
                Take();
                if (Peek() is '+' or '-')
                    Take();
                int exponent = position;
                while (Peek() is >= '0' and <= '9')
                    Take();
                if (exponent == position)
                    diagnostics.Add(new(DiagnosticCode.DigitExpected, source.ByteOffset(position), 0, Utf8Literals.DigitExpected));
            }
            Utf8String text = source.Slice(start, position);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                text = Utf8String.Format(number, "R");
            return Token(SyntaxKind.NumericLiteral, text, flags);
        }
        if (ch is '\'' or '"')
        {
            var text = new Utf8StringBuilder();
            uint flags = ch == '\'' ? 1u << 16 : 0;
            bool ended = false;
            while (position < source.Length && !Newline(Peek()))
            {
                int value = Take();
                if (value == ch)
                {
                    ended = true;
                    break;
                }
                if (value == '\\')
                {
                    if (position == source.Length)
                        break;
                    int escaped = Take();
                    if (Newline(escaped))
                    {
                        if (escaped == '\r' && Peek() == '\n')
                            Take();
                        continue;
                    }
                    if (escaped is 'u' or 'x')
                    {
                        bool extended = escaped == 'u' && Peek() == '{';
                        flags |= extended ? 8u : escaped == 'u' ? 1024u : 4096u;
                        if (extended)
                            Take();
                        int digits = extended ? 6 : escaped == 'u' ? 4 : 2;
                        int code = 0, read = 0;
                        while (read < digits && Hex(Peek()) is int digit && digit >= 0)
                        {
                            Take();
                            code = code << 4 | digit;
                            read++;
                        }
                        bool valid = extended ? read > 0 && Peek() == '}' && code <= 0x10FFFF : read == digits;
                        if (extended && Peek() == '}')
                            Take();
                        if (!valid)
                        {
                            flags |= 2048;
                            diagnostics.Add(
                                new(
                                    DiagnosticCode.HexadecimalDigitExpected,
                                    source.ByteOffset(position),
                                    0,
                                    Utf8Literals.HexadecimalDigitExpected));
                        }
                        Append(text, valid ? code : 0xFFFD);
                    }
                    else
                        Append(
                            text,
                            escaped switch
                            {
                                'n' => '\n',
                                'r' => '\r',
                                't' => '\t',
                                'b' => '\b',
                                'f' => '\f',
                                'v' => '\v',
                                '0' => 0,
                                _ => escaped
                            });
                }
                else
                    Append(text, value);
            }
            if (!ended)
            {
                flags |= 4;
                diagnostics.Add(
                    new(DiagnosticCode.UnterminatedStringLiteral, source.ByteOffset(position), 0, Utf8Literals.UnterminatedStringLiteral));
            }
            return Token(SyntaxKind.StringLiteral, text.ToUtf8String(), flags);
        }
        return Token(ch switch
        {
            '{' => SyntaxKind.OpenBraceToken,
            '}' => SyntaxKind.CloseBraceToken,
            '(' => SyntaxKind.OpenParenToken,
            ')' => SyntaxKind.CloseParenToken,
            ':' => SyntaxKind.ColonToken,
            ';' => SyntaxKind.SemicolonToken,
            ',' => SyntaxKind.CommaToken,
            '|' => SyntaxKind.BarToken,
            '=' => SyntaxKind.EqualsToken,
            '-' => SyntaxKind.MinusToken,
            '+' => SyntaxKind.PlusToken,
            _ => SyntaxKind.Unknown,
        });

        SliceToken Token(SyntaxKind kind, Utf8String text = default, uint flags = 0) =>
            new(kind, source.ByteOffset(fullStart), source.ByteOffset(start), source.ByteOffset(position), text, flags, lineBreak);
    }

    private static int Hex(int cp) =>
        cp switch { >= '0' and <= '9' => cp - '0', >= 'a' and <= 'f' => cp - 'a' + 10, >= 'A' and <= 'F' => cp - 'A' + 10, _ => -1 };

    private static void Append(Utf8StringBuilder builder, int cp)
    {
        builder.AppendCodePoint(cp);
    }
}
