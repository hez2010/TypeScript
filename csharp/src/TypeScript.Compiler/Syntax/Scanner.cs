using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using TypeScript.Compiler.Diagnostics;
using TypeScript.Compiler.Text;
using static TypeScript.Compiler.Syntax.TokenFacts;

namespace TypeScript.Compiler.Syntax;

public readonly record struct CommentDirective(int Start, int End, bool ExpectError);
public readonly record struct ScannerState(int Position, int FullStart, int TokenStart, SyntaxKind Kind,
    TextSlice Value, TokenFlags Flags, int DiagnosticCount, int DirectiveCount, int JsDocDepth);

/// <summary>Scans the UTF-16 view. Positions here are UTF-16; SourceText maps them to the byte-based AST/wire contract.</summary>
public sealed partial class Scanner(SourceText source, bool skipTrivia = true, bool jsx = false)
{
    private readonly TextSlice text = source.Text;
    private readonly string input = source.Text.ToString();
    private int end = source.Length;
    private int pos;
    private int jsDocDepth;
    private static readonly SearchValues<char> IdentifierAscii = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_$");
    public SourceText Source { get; } = source;
    public bool SkipTrivia { get; set; } = skipTrivia;
    public bool Jsx { get; set; } = jsx;
    public int FullStart { get; private set; }
    public int TokenStart { get; private set; }
    public int Position => pos;
    public int End => end;
    public SyntaxKind Kind { get; private set; }
    public TextSlice Value { get; private set; } = "";
    public TokenFlags Flags { get; private set; }
    public List<Diagnostic> Diagnostics { get; } = [];
    public List<CommentDirective> CommentDirectives { get; } = [];
    public ReadOnlySpan<char> TokenText => input.AsSpan().Slice(TokenStart, pos - TokenStart);
    public bool HasPrecedingLineBreak => (Flags & TokenFlags.PrecedingLineBreak) != 0;

    private int Char(int offset = 0) => pos + offset < end ? input.AsSpan()[pos + offset] : -1;

    private int CodePoint(out int width)
    {
        if (pos >= end)
        {
            width = 0;
            return -1;
        }
        ReadOnlySpan<char> characters = input.AsSpan();
        char ch = characters[pos];
        if (char.IsHighSurrogate(ch) && pos + 1 < end && char.IsLowSurrogate(characters[pos + 1]))
        {
            width = 2;
            return char.ConvertToUtf32(ch, characters[pos + 1]);
        }
        width = 1;
        return ch;
    }

    private void Error(DiagnosticMessage message, int? start = null, int length = 0, params TextSlice[] arguments) =>
            Diagnostics.Add(new(message, start ?? pos, length, arguments));

    public ScannerState Mark() =>
        new(pos, FullStart, TokenStart, Kind, Value, Flags, Diagnostics.Count, CommentDirectives.Count, jsDocDepth);

    public void Rewind(ScannerState state)
    {
        pos = state.Position;
        FullStart = state.FullStart;
        TokenStart = state.TokenStart;
        Kind = state.Kind;
        Value = state.Value;
        Flags = state.Flags;
        jsDocDepth = state.JsDocDepth;
        Diagnostics.RemoveRange(state.DiagnosticCount, Diagnostics.Count - state.DiagnosticCount);
        CommentDirectives.RemoveRange(state.DirectiveCount, CommentDirectives.Count - state.DirectiveCount);
    }

    public void ResetPosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, end);
        pos = FullStart = TokenStart = position;
        Kind = SyntaxKind.Unknown;
        Flags = 0;
        Value = "";
    }

    public void SetTextRange(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Source.Length);
        this.end = end;
        ResetPosition(start);
    }

    public void SetSkipJSDocLeadingAsterisks(bool enabled)
    {
        jsDocDepth += enabled ? 1 : -1;
        if (jsDocDepth < 0)
            throw new InvalidOperationException("Unbalanced JSDoc scanner context");
    }

    public SyntaxKind Scan()
    {
        FullStart = pos;
        Flags = 0;
        while (true)
        {
            TokenStart = pos;
            int ch = Char();
            if (ch < 0)
                return Kind = SyntaxKind.EndOfFile;
            if (IsWhiteSpace(ch))
            {
                pos++;
                if (SkipTrivia || ch == 0x85)
                    continue;
                while (IsWhiteSpace(Char()))
                    pos++;
                return Kind = SyntaxKind.WhitespaceTrivia;
            }
            if (IsLineBreak(ch))
            {
                Flags |= TokenFlags.PrecedingLineBreak;
                pos++;
                if (SkipTrivia || ch is 0x2028 or 0x2029)
                    continue;
                if (ch == '\r' && Char() == '\n')
                    pos++;
                return Kind = SyntaxKind.NewLineTrivia;
            }
            if (ch == '/' && Char(1) is '/' or '*')
            {
                bool multi = Char(1) == '*';
                pos += 2;
                bool doc = multi && Char() == '*' && Char(1) != '/';
                bool closed = !multi;
                int lastLine = TokenStart;
                while (pos < end)
                {
                    int next = input.AsSpan().Slice(pos, end - pos).IndexOfAny(multi ? "*\r\n\u2028\u2029" : "\r\n\u2028\u2029");
                    if (next < 0)
                    {
                        pos = end;
                        break;
                    }
                    pos += next;
                    if (!multi)
                        break;
                    if (Char() == '*' && Char(1) == '/')
                    {
                        pos += 2;
                        closed = true;
                        break;
                    }
                    if (IsLineBreak(Char()))
                    {
                        Flags |= TokenFlags.PrecedingLineBreak;
                        lastLine = pos + 1;
                    }
                    pos++;
                }
                if (doc)
                {
                    Flags |= TokenFlags.PrecedingJSDocComment;
                    ReadOnlySpan<char> comment = input.AsSpan().Slice(TokenStart, pos - TokenStart);
                    if (HasTag(comment, "deprecated"))
                        Flags |= TokenFlags.PrecedingJSDocWithDeprecated;
                    if (HasTag(comment, "see") || HasTag(comment, "link") || HasTag(comment, "linkcode") || HasTag(comment, "linkplain"))
                        Flags |= TokenFlags.PrecedingJSDocWithSeeOrLink;
                }
                ProcessDirective(multi ? lastLine : TokenStart, pos, multi);
                if (!closed)
                    Error(Messages.Asterisk_Slash_expected);
                if (SkipTrivia)
                    continue;
                if (!closed)
                    Flags |= TokenFlags.Unterminated;
                return Kind = multi ? SyntaxKind.MultiLineCommentTrivia : SyntaxKind.SingleLineCommentTrivia;
            }
            if (ch is '<' or '>' or '=' or '|' && IsConflictMarker(pos))
            {
                ScanConflictMarker();
                if (SkipTrivia)
                    continue;
                return Kind = SyntaxKind.ConflictMarkerTrivia;
            }
            if (ch == '#' && Char(1) == '!')
            {
                if (pos != 0)
                {
                    Error(Messages.X_can_only_be_used_at_the_start_of_a_file, pos, 2);
                    pos += 2;
                    return Kind = SyntaxKind.Unknown;
                }
                int nextLine = input.AsSpan().Slice(pos, end - pos).IndexOfAny("\r\n\u2028\u2029");
                pos = nextLine < 0 ? end : pos + nextLine;
                continue;
            }
            if (ch is '\'' or '"')
            {
                Value = ScanString(false);
                return Kind = SyntaxKind.StringLiteral;
            }
            if (ch == '`')
                return Kind = ScanTemplate(false);
            if (IsDigit(ch) || ch == '.' && IsDigit(Char(1)))
                return Kind = ScanNumber();
            if (ch == '#')
            {
                pos++;
                if (!ScanIdentifier(true, false, out _))
                {
                    Error(Messages.Invalid_character, pos - 1, 1);
                    Value = "#";
                }
                return Kind = SyntaxKind.PrivateIdentifier;
            }
            if (ScanIdentifier(false, false, out SyntaxKind identifierKind))
                return Kind = identifierKind;
            if (ch == '>')
            {
                pos++;
                return Kind = SyntaxKind.GreaterThanToken;
            }
            if (ch == '?' && Char(1) == '.' && IsDigit(Char(2)))
            {
                pos++;
                return Kind = SyntaxKind.QuestionToken;
            }
            if (ch == '*'
                && jsDocDepth != 0
                && HasPrecedingLineBreak
                && (Flags & TokenFlags.PrecedingJSDocLeadingAsterisks) == 0
                && Char(1) is not ('*' or '='))
            {
                pos++;
                Flags |= TokenFlags.PrecedingJSDocLeadingAsterisks;
                continue;
            }
            for (int length = Math.Min(3, end - pos); length > 0; length--)
            {
                SyntaxKind punctuation = FromText(input.AsSpan().Slice(pos, length));
                if (punctuation is SyntaxKind.Unknown or SyntaxKind.HashToken or SyntaxKind.BacktickToken)
                    continue;
                if (punctuation == SyntaxKind.LessThanSlashToken && (!Jsx || Char(2) == '*'))
                    continue;
                pos += length;
                return Kind = punctuation;
            }
            int point = CodePoint(out int width);
            if (point == 0xFFFD || point is >= 0xD800 and <= 0xDFFF)
            {
                Error(Messages.File_appears_to_be_binary, 0);
                pos = end;
                return Kind = SyntaxKind.NonTextFileMarkerTrivia;
            }
            Error(Messages.Invalid_character, pos, width);
            pos += width;
            return Kind = SyntaxKind.Unknown;
        }
    }

    private static bool HasTag(ReadOnlySpan<char> text, ReadOnlySpan<char> name)
    {
        while (true)
        {
            int at = text.IndexOf('@');
            if (at < 0)
                return false;
            text = text[(at + 1)..];
            if (text.StartsWith(name, StringComparison.Ordinal)
                && (text.Length == name.Length || text[name.Length] is ' ' or '\t' or '\r' or '\n' or '}' or '*'))
                return true;
        }
    }

    private void ProcessDirective(int start, int end, bool multiline)
    {
        int at = multiline ? start : start + 2;
        if (multiline)
            while (at < end && text[at] is ' ' or '\t')
                at++;
        while (at < end && (text[at] == '/' || multiline && text[at] == '*'))
            at++;
        while (at < end && text[at] is ' ' or '\t')
            at++;
        ReadOnlySpan<char> tail = input.AsSpan().Slice(at, end - at);
        if (tail.StartsWith("@ts-expect-error", StringComparison.Ordinal))
            CommentDirectives.Add(new(start, end, true));
        else if (tail.StartsWith("@ts-ignore", StringComparison.Ordinal))
            CommentDirectives.Add(new(start, end, false));
    }

    private bool IsConflictMarker(int at) => at + 7 < end && (at == 0 || IsLineBreak(text[at - 1])) &&
            input.AsSpan().Slice(at, 7).IndexOfAnyExcept(text[at]) < 0 && (text[at] == '=' || text[at + 7] == ' ');

    private void ScanConflictMarker()
    {
        Error(Messages.Merge_conflict_marker_encountered, pos, 7);
        char first = text[pos];
        if (first is '<' or '>')
            while (pos < end && !IsLineBreak(Char()))
                pos++;
        else
            while (pos < end)
            {
                if (Char() is '=' or '>' && Char() != first && IsConflictMarker(pos))
                    break;
                pos++;
            }
    }

    private bool ScanIdentifier(bool privateName, bool jsxName, out SyntaxKind identifierKind)
    {
        identifierKind = SyntaxKind.Unknown;
        int start = pos;
        int ch = CodePoint(out int width);
        StringBuilder? builder = null;
        if (ch == '\\')
        {
            int escaped = IdentifierEscape(true);
            if (escaped < 0)
                return false;
            builder = new();
            AppendCodePoint(builder, escaped);
        }
        else if (IsIdentifierStart(ch))
            pos += width;
        else
            return false;
        int part = builder is null ? start : pos;
        while (pos < end)
        {
            int ascii = input.AsSpan().Slice(pos, end - pos).IndexOfAnyExcept(IdentifierAscii);
            pos = ascii < 0 ? end : pos + ascii;
            ch = CodePoint(out width);
            if (IsIdentifierPart(ch, jsxName))
            {
                pos += width;
                continue;
            }
            if (ch != '\\')
                break;
            int before = pos;
            int escaped = IdentifierEscape(false);
            if (escaped < 0)
                break;
            builder ??= new();
            builder.Append(input.AsSpan().Slice(part, before - part));
            AppendCodePoint(builder, escaped);
            part = pos;
        }
        if (builder is null)
        {
            int valueStart = privateName ? start - 1 : start;
            ReadOnlySpan<char> valueText = input.AsSpan().Slice(valueStart, pos - valueStart);
            identifierKind = IdentifierKind(valueText);
            if (identifierKind != SyntaxKind.Identifier)
                Value = TokenFacts.Text(identifierKind);
            else
                Value = text.Memory.Slice(valueStart, pos - valueStart);
        }
        else
        {
            builder.Append(input.AsSpan().Slice(part, pos - part));
            Value = TextSlice.FromBuilder(builder);
            if (privateName)
                Value = TextSlice.Concat("#", Value);
            identifierKind = IdentifierKind(Value);
        }
        return true;
    }

    private int IdentifierEscape(bool start)
    {
        if (Char(1) != 'u')
            return -1;
        int before = pos;
        TokenFlags flags = Flags;
        int value = UnicodeEscape(false);
        if (start ? IsIdentifierStart(value) : IsIdentifierPart(value))
            return value;
        pos = before;
        Flags = flags;
        return -1;
    }

    private static void AppendCodePoint(StringBuilder builder, int point)
    {
        if (point <= 0xFFFF)
            builder.Append((char)point);
        else
        {
            Span<char> pair = stackalloc char[2];
            builder.Append(pair[..new Rune(point).EncodeToUtf16(pair)]);
        }
    }

    private TextSlice ScanString(bool jsxAttribute)
    {
        int quote = Char();
        if (quote == '\'')
            Flags |= TokenFlags.SingleQuote;
        int start = ++pos;
        int closing = input.AsSpan().Slice(start, end - start).IndexOf((char)quote);
        if (closing >= 0 && (jsxAttribute || input.AsSpan().Slice(start, closing).IndexOfAny('\\', '\r', '\n') < 0))
        {
            pos += closing + 1;
            return text.Memory.Slice(start, closing);
        }
        var result = new StringBuilder();
        while (true)
        {
            int ch = Char();
            if (ch < 0 || !jsxAttribute && ch is '\r' or '\n')
            {
                result.Append(input.AsSpan().Slice(start, pos - start));
                Flags |= TokenFlags.Unterminated;
                Error(Messages.Unterminated_string_literal);
                break;
            }
            if (ch == quote)
            {
                result.Append(input.AsSpan().Slice(start, pos - start));
                pos++;
                break;
            }
            if (ch == '\\' && !jsxAttribute)
            {
                result.Append(input.AsSpan().Slice(start, pos - start));
                Escape(result, true);
                start = pos;
            }
            else
                pos++;
        }
        return TextSlice.FromBuilder(result);
    }

    private SyntaxKind ScanTemplate(bool reportEscapeErrors)
    {
        bool head = Char() == '`';
        int start = ++pos;
        StringBuilder? result = null;
        while (true)
        {
            int next = input.AsSpan().Slice(pos, end - pos).IndexOfAny("`$\\\r");
            pos = next < 0 ? end : pos + next;
            int ch = Char();
            if (ch < 0 || ch == '`')
            {
                ReadOnlySpan<char> tail = input.AsSpan().Slice(start, pos - start);
                if (ch == '`')
                    pos++;
                else
                {
                    Flags |= TokenFlags.Unterminated;
                    Error(Messages.Unterminated_template_literal);
                }
                Value = result is null ? text.Memory.Slice(start, tail.Length) : TextSlice.FromBuilder(result.Append(tail));
                return head ? SyntaxKind.NoSubstitutionTemplateLiteral : SyntaxKind.TemplateTail;
            }
            if (ch == '$' && Char(1) == '{')
            {
                ReadOnlySpan<char> tail = input.AsSpan().Slice(start, pos - start);
                pos += 2;
                Value = result is null ? text.Memory.Slice(start, tail.Length) : TextSlice.FromBuilder(result.Append(tail));
                return head ? SyntaxKind.TemplateHead : SyntaxKind.TemplateMiddle;
            }
            if (ch == '\\')
            {
                (result ??= new()).Append(input.AsSpan().Slice(start, pos - start));
                Escape(result, reportEscapeErrors);
                start = pos;
                continue;
            }
            if (ch == '\r')
            {
                (result ??= new()).Append(input.AsSpan().Slice(start, pos - start));
                pos++;
                if (Char() == '\n')
                    pos++;
                result.Append('\n');
                start = pos;
                continue;
            }
            pos++;
        }
    }

    private void Escape(StringBuilder result, bool reportErrors)
    {
        int start = pos++;
        int ch = Char();
        if (ch < 0)
        {
            Error(Messages.Unexpected_end_of_text);
            return;
        }
        pos++;
        switch (ch)
        {
            case '0' when !IsDigit(Char()):
                result.Append('\0');
                return;
            case >= '0' and <= '7':
                if (ch <= '3' && Char() is >= '0' and <= '7')
                    pos++;
                if (Char() is >= '0' and <= '7')
                    pos++;
                Flags |= TokenFlags.ContainsInvalidEscape;
                if (!reportErrors)
                {
                    result.Append(input.AsSpan().Slice(start, pos - start));
                    return;
                }
                int octal = 0;
                foreach (char digit in input.AsSpan().Slice(start + 1, pos - start - 1))
                    octal = octal * 8 + digit - '0';
                Error(
                    Messages.Octal_escape_sequences_are_not_allowed_Use_the_syntax_0,
                    start,
                    pos - start,
                    TextSlice.Concat("\\x", octal.ToString("x2", CultureInfo.InvariantCulture)));
                result.Append((char)octal);
                return;
            case '8' or '9':
                Flags |= TokenFlags.ContainsInvalidEscape;
                if (!reportErrors)
                {
                    result.Append(input.AsSpan().Slice(start, pos - start));
                    return;
                }
                Error(Messages.Escape_sequence_0_is_not_allowed, start, pos - start, text[start..pos]);
                result.Append((char)ch);
                return;
            case 'b':
                result.Append('\b');
                return;
            case 't':
                result.Append('\t');
                return;
            case 'n':
                result.Append('\n');
                return;
            case 'v':
                result.Append('\v');
                return;
            case 'f':
                result.Append('\f');
                return;
            case 'r':
                result.Append('\r');
                return;
            case 'u':
                pos = start;
                int point = UnicodeEscape(reportErrors);
                if (point < 0)
                    result.Append(input.AsSpan().Slice(start, pos - start));
                else
                    AppendCodePoint(result, point);
                return;
            case 'x':
                int value = 0;
                for (int i = 0; i < 2; i++)
                {
                    int digit = HexDigit(Char());
                    if (digit < 0)
                    {
                        Flags |= TokenFlags.ContainsInvalidEscape;
                        if (reportErrors)
                            Error(Messages.Hexadecimal_digit_expected);
                        {
                            result.Append(input.AsSpan().Slice(start, pos - start));
                            return;
                        }
                    }
                    value = value * 16 + digit;
                    pos++;
                }
                Flags |= TokenFlags.HexEscape;
                result.Append((char)value);
                return;
            case '\r':
                if (Char() == '\n')
                    pos++;
                return;
            case '\n' or 0x2028 or 0x2029:
                return;
            default:
                result.Append((char)ch);
                if (char.IsHighSurrogate((char)ch) && Char() is >= 0xDC00 and <= 0xDFFF)
                    result.Append(text[pos++]);
                return;
        }
    }

    private int UnicodeEscape(bool reportErrors)
    {
        pos += 2;
        int start = pos;
        bool extended = Char() == '{';
        if (extended)
            pos++;
        else
            Flags |= TokenFlags.UnicodeEscape;
        long value = 0;
        int count = 0;
        while (count < 4 || extended)
        {
            int digit = HexDigit(Char());
            if (digit < 0)
                break;
            value = Math.Min(0x110000, value * 16 + digit);
            pos++;
            count++;
        }
        if (count < (extended ? 1 : 4))
        {
            Flags |= TokenFlags.ContainsInvalidEscape;
            if (reportErrors)
                Error(Messages.Hexadecimal_digit_expected);
            return -1;
        }
        if (extended)
        {
            bool invalid = value > 0x10FFFF;
            if (invalid && reportErrors)
                Error(Messages.An_extended_Unicode_escape_value_must_be_between_0x0_and_0x10FFFF_inclusive, start + 1, pos - start - 1);
            if (Char() < 0)
            {
                if (reportErrors)
                    Error(Messages.Unexpected_end_of_text);
                invalid = true;
            }
            else if (Char() == '}')
                pos++;
            else
            {
                if (reportErrors)
                    Error(Messages.Unterminated_Unicode_escape_sequence);
                invalid = true;
            }
            if (invalid)
            {
                Flags |= TokenFlags.ContainsInvalidEscape;
                return -1;
            }
            Flags |= TokenFlags.ExtendedUnicodeEscape;
        }
        return (int)value;
    }

    private ReadOnlySpan<char> Digits(int radix, bool separators, bool decimalFragment = false)
    {
        int start = pos;
        bool allowed = false, previousSeparator = false;
        int separatorsCount = 0;
        while (true)
        {
            int digit = HexDigit(Char());
            if (digit >= 0 && digit < radix)
            {
                pos++;
                allowed = true;
                previousSeparator = false;
                continue;
            }
            if (!separators || Char() != '_')
                break;
            Flags |= TokenFlags.ContainsSeparator;
            separatorsCount++;
            if (!allowed)
            {
                if (decimalFragment)
                    Flags |= TokenFlags.ContainsInvalidSeparator;
                Error(
                    previousSeparator
                        ? Messages.Multiple_consecutive_numeric_separators_are_not_permitted
                        : Messages.Numeric_separators_are_not_allowed_here,
                    pos,
                    1);
            }
            else
            {
                allowed = false;
                previousSeparator = true;
            }
            pos++;
        }
        if (previousSeparator)
        {
            if (decimalFragment)
                Flags |= TokenFlags.ContainsInvalidSeparator;
            Error(Messages.Numeric_separators_are_not_allowed_here, pos - 1, 1);
        }
        ReadOnlySpan<char> digits = input.AsSpan().Slice(start, pos - start);
        if (separatorsCount == 0)
            return digits;
        char[] result = new char[digits.Length - separatorsCount];
        int written = 0;
        foreach (char digit in digits)
            if (digit != '_')
                result[written++] = digit;
        return result;
    }

    private SyntaxKind ScanNumber()
    {
        int start = pos;
        if (Char() == '0' && Char(1) is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            int radix = Char(1) is 'x' or 'X' ? 16 : Char(1) is 'o' or 'O' ? 8 : 2;
            pos += 2;
            ReadOnlySpan<char> digits = Digits(radix, true);
            if (digits.Length == 0)
            {
                Error(
                    radix == 16
                        ? Messages.Hexadecimal_digit_expected
                        : radix == 8 ? Messages.Octal_digit_expected : Messages.Binary_digit_expected);
                digits = "0";
            }
            Flags |= radix == 16 ? TokenFlags.HexSpecifier : radix == 8 ? TokenFlags.OctalSpecifier : TokenFlags.BinarySpecifier;
            // The leading zero makes the BCL's signed hexadecimal/binary grammar unsigned.
            BigInteger integer = RadixInteger(digits, radix);
            if (Char() == 'n')
            {
                pos++;
                if (radix == 16)
                {
                    char[] value = new char[checked(digits.Length + 3)];
                    "0x".AsSpan().CopyTo(value);
                    digits.ToLowerInvariant(value.AsSpan(2, digits.Length));
                    value[^1] = 'n';
                    Value = new(value);
                }
                else
                    Value = TextSlice.Concat(TextSlice.Format(integer), "n");
                return SyntaxKind.BigIntLiteral;
            }
            Value = NumberText((double)integer);
            return SyntaxKind.NumericLiteral;
        }
        ReadOnlySpan<char> fixedPart;
        if (Char() == '0')
        {
            pos++;
            if (Char() == '_')
            {
                Flags |= TokenFlags.ContainsSeparator | TokenFlags.ContainsInvalidSeparator;
                Error(Messages.Numeric_separators_are_not_allowed_here, pos, 1);
                pos = start;
                fixedPart = Digits(10, true, true);
            }
            else
            {
                int digitsStart = pos;
                bool octal = true;
                while (IsDigit(Char()))
                {
                    if (Char() >= '8')
                        octal = false;
                    pos++;
                }
                fixedPart = input.AsSpan().Slice(digitsStart, pos - digitsStart);
                if (fixedPart.Length == 0)
                    fixedPart = "0";
                else if (!octal)
                    Flags |= TokenFlags.ContainsLeadingZero;
                else
                {
                    BigInteger integer = OctalInteger(fixedPart);
                    Value = NumberText((double)integer);
                    Flags |= TokenFlags.Octal;
                    bool minus = Kind == SyntaxKind.MinusToken;
                    ReadOnlySpan<char> octalText = fixedPart.TrimStart('0');
                    Error(
                        Messages.Octal_literals_are_not_allowed_Use_the_syntax_0,
                        minus ? start - 1 : start,
                        pos - start + (minus ? 1 : 0),
                        TextSlice.Concat(minus ? "-0o" : "0o", octalText.Length == 0 ? "0" : octalText));
                    return SyntaxKind.NumericLiteral;
                }
            }
        }
        else
            fixedPart = Digits(10, true, true);
        int fixedEnd = pos;
        ReadOnlySpan<char> fraction = "", exponent = "";
        if (Char() == '.')
        {
            pos++;
            fraction = Digits(10, true, true);
        }
        int end = pos;
        if (Char() is 'e' or 'E')
        {
            pos++;
            Flags |= TokenFlags.Scientific;
            if (Char() is '+' or '-')
                pos++;
            int digitsStart = pos;
            ReadOnlySpan<char> digits = Digits(10, true, true);
            if (digits.Length == 0)
                Error(Messages.Digit_expected);
            else
            {
                exponent = (Flags & TokenFlags.ContainsSeparator) == 0
                    ? input.AsSpan().Slice(end, pos - end)
                    : TextSlice.Concat(input.AsSpan().Slice(end, digitsStart - end), digits);
                end = pos;
            }
        }
        ReadOnlySpan<char> raw = (Flags & TokenFlags.ContainsSeparator) == 0
            ? input.AsSpan().Slice(start, end - start)
            : TextSlice.Concat(fixedPart, fraction.Length != 0 ? TextSlice.Concat(".", fraction) : "", exponent);
        if ((Flags & TokenFlags.ContainsLeadingZero) != 0)
        {
            Error(Messages.Decimals_with_leading_zeros_are_not_allowed, start, pos - start);
            Value = NumberText(double.Parse(raw, CultureInfo.InvariantCulture));
            return SyntaxKind.NumericLiteral;
        }
        SyntaxKind result = SyntaxKind.NumericLiteral;
        if (fixedEnd == pos && Char() == 'n')
        {
            pos++;
            Value = TextSlice.Concat(raw, "n");
            result = SyntaxKind.BigIntLiteral;
        }
        else
            Value = NumberText(
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : double.NaN);
        if (IsIdentifierStart(CodePoint(out _)))
        {
            int idStart = pos;
            TextSlice saved = Value;
            ScanIdentifier(false, false, out _);
            TextSlice identifier = Value;
            Value = saved;
            if (result != SyntaxKind.BigIntLiteral && identifier == "n")
            {
                if ((Flags & TokenFlags.Scientific) != 0)
                {
                    Error(Messages.A_bigint_literal_cannot_use_exponential_notation, start, pos - start);
                    return result;
                }
                if (fixedEnd < idStart)
                {
                    Error(Messages.A_bigint_literal_must_be_an_integer, start, pos - start);
                    return result;
                }
            }
            Error(Messages.An_identifier_or_keyword_cannot_immediately_follow_a_numeric_literal, idStart, pos - idStart);
            pos = idStart;
        }
        return result;
    }

    private static BigInteger RadixInteger(ReadOnlySpan<char> digits, int radix)
    {
        if (radix == 8)
            return OctalInteger(digits);
        char[]? rented = null;
        int length = checked(digits.Length + 1);
        Span<char> buffer = length <= 256 ? stackalloc char[256] : rented = ArrayPool<char>.Shared.Rent(length);
        try
        {
            buffer[0] = '0';
            digits.CopyTo(buffer[1..]);
            return BigInteger.Parse(buffer[..length],
                radix == 16 ? NumberStyles.AllowHexSpecifier : NumberStyles.AllowBinarySpecifier, CultureInfo.InvariantCulture);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
        }
    }

    private static BigInteger OctalInteger(ReadOnlySpan<char> digits)
    {
        // BigInteger.Parse has no octal mode. Pack three bits per digit, then let
        // the BCL construct the integer; repeated big-integer multiplication is quadratic.
        int length = checked((int)(((long)digits.Length * 3 + 7) / 8));
        byte[]? rented = null;
        Span<byte> bytes = length <= 256 ? stackalloc byte[256] : rented = ArrayPool<byte>.Shared.Rent(length);
        bytes = bytes[..length];
        bytes.Clear();
        try
        {
            long bit = 0;
            for (int i = digits.Length - 1; i >= 0; i--, bit += 3)
            {
                int index = checked((int)(bit >> 3));
                int value = (digits[i] - '0') << (int)(bit & 7);
                bytes[index] |= (byte)value;
                if (index + 1 < bytes.Length)
                    bytes[index + 1] |= (byte)(value >> 8);
            }
            return new BigInteger(bytes, isUnsigned: true);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
