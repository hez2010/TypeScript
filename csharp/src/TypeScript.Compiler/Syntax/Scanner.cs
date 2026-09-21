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
    string Value, TokenFlags Flags, int DiagnosticCount, int DirectiveCount, int JsDocDepth);

/// <summary>Scans the UTF-16 view. Positions here are UTF-16; SourceText maps them to the byte-based AST/wire contract.</summary>
public sealed partial class Scanner(SourceText source, bool skipTrivia = true, bool jsx = false)
{
    private readonly string text = source.Text;
    private int pos;
    private int jsDocDepth;
    private static readonly SearchValues<char> IdentifierAscii = SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_$");
    public SourceText Source { get; } = source;
    public bool SkipTrivia { get; set; } = skipTrivia;
    public bool Jsx { get; set; } = jsx;
    public int FullStart { get; private set; }
    public int TokenStart { get; private set; }
    public int Position => pos;
    public SyntaxKind Kind { get; private set; }
    public string Value { get; private set; } = "";
    public TokenFlags Flags { get; private set; }
    public List<Diagnostic> Diagnostics { get; } = [];
    public List<CommentDirective> CommentDirectives { get; } = [];
    public ReadOnlySpan<char> TokenText => text.AsSpan(TokenStart, pos - TokenStart);
    public bool HasPrecedingLineBreak => (Flags & TokenFlags.PrecedingLineBreak) != 0;
    private int Char(int offset = 0) => pos + offset < text.Length ? text[pos + offset] : -1;
    private int CodePoint(out int width)
    {
        if (pos >= text.Length) { width = 0; return -1; }
        char ch = text[pos];
        if (char.IsHighSurrogate(ch) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
        { width = 2; return char.ConvertToUtf32(ch, text[pos + 1]); }
        width = 1;
        return ch;
    }
    private void Error(DiagnosticMessage message, int? start = null, int length = 0, params string[] arguments) =>
        Diagnostics.Add(new(message, start ?? pos, length, arguments));
    public ScannerState Mark() => new(pos, FullStart, TokenStart, Kind, Value, Flags, Diagnostics.Count, CommentDirectives.Count, jsDocDepth);
    public void Rewind(ScannerState state)
    {
        pos = state.Position; FullStart = state.FullStart; TokenStart = state.TokenStart; Kind = state.Kind;
        Value = state.Value; Flags = state.Flags; jsDocDepth = state.JsDocDepth;
        Diagnostics.RemoveRange(state.DiagnosticCount, Diagnostics.Count - state.DiagnosticCount);
        CommentDirectives.RemoveRange(state.DirectiveCount, CommentDirectives.Count - state.DirectiveCount);
    }
    public void ResetPosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, text.Length);
        pos = FullStart = TokenStart = position; Kind = SyntaxKind.Unknown; Flags = 0; Value = "";
    }
    public void SetSkipJSDocLeadingAsterisks(bool enabled)
    {
        jsDocDepth += enabled ? 1 : -1;
        if (jsDocDepth < 0) throw new InvalidOperationException("Unbalanced JSDoc scanner context");
    }
    public SyntaxKind Scan()
    {
        FullStart = pos;
        Flags = 0;
        while (true)
        {
            TokenStart = pos;
            int ch = Char();
            if (ch < 0) return Kind = SyntaxKind.EndOfFile;
            if (IsWhiteSpace(ch))
            {
                pos++;
                if (SkipTrivia || ch == 0x85) continue;
                while (IsWhiteSpace(Char())) pos++;
                return Kind = SyntaxKind.WhitespaceTrivia;
            }
            if (IsLineBreak(ch))
            {
                Flags |= TokenFlags.PrecedingLineBreak;
                pos++;
                if (SkipTrivia || ch is 0x2028 or 0x2029) continue;
                if (ch == '\r' && Char() == '\n') pos++;
                return Kind = SyntaxKind.NewLineTrivia;
            }
            if (ch == '/' && Char(1) is '/' or '*')
            {
                bool multi = Char(1) == '*';
                pos += 2;
                bool doc = multi && Char() == '*' && Char(1) != '/';
                bool closed = !multi;
                int lastLine = TokenStart;
                while (pos < text.Length)
                {
                    int next = text.AsSpan(pos).IndexOfAny(multi ? "*\r\n\u2028\u2029" : "\r\n\u2028\u2029");
                    if (next < 0) { pos = text.Length; break; }
                    pos += next;
                    if (!multi) break;
                    if (Char() == '*' && Char(1) == '/') { pos += 2; closed = true; break; }
                    if (IsLineBreak(Char())) { Flags |= TokenFlags.PrecedingLineBreak; lastLine = pos + 1; }
                    pos++;
                }
                if (doc)
                {
                    Flags |= TokenFlags.PrecedingJSDocComment;
                    ReadOnlySpan<char> comment = text.AsSpan(TokenStart, pos - TokenStart);
                    if (HasTag(comment, "deprecated")) Flags |= TokenFlags.PrecedingJSDocWithDeprecated;
                    if (HasTag(comment, "see") || HasTag(comment, "link") || HasTag(comment, "linkcode") || HasTag(comment, "linkplain")) Flags |= TokenFlags.PrecedingJSDocWithSeeOrLink;
                }
                ProcessDirective(multi ? lastLine : TokenStart, pos, multi);
                if (!closed) Error(Messages.Asterisk_Slash_expected);
                if (SkipTrivia) continue;
                if (!closed) Flags |= TokenFlags.Unterminated;
                return Kind = multi ? SyntaxKind.MultiLineCommentTrivia : SyntaxKind.SingleLineCommentTrivia;
            }
            if (ch is '<' or '>' or '=' or '|' && IsConflictMarker(pos))
            {
                ScanConflictMarker();
                if (SkipTrivia) continue;
                return Kind = SyntaxKind.ConflictMarkerTrivia;
            }
            if (ch == '#' && Char(1) == '!')
            {
                if (pos != 0) { Error(Messages.X_can_only_be_used_at_the_start_of_a_file, pos, 2); pos += 2; return Kind = SyntaxKind.Unknown; }
                int end = text.AsSpan(pos).IndexOfAny("\r\n\u2028\u2029");
                pos = end < 0 ? text.Length : pos + end;
                continue;
            }
            if (ch is '\'' or '"') { Value = ScanString(false); return Kind = SyntaxKind.StringLiteral; }
            if (ch == '`') return Kind = ScanTemplate(false);
            if (IsDigit(ch) || ch == '.' && IsDigit(Char(1))) return Kind = ScanNumber();
            if (ch == '#')
            {
                pos++;
                if (!ScanIdentifier(true, false)) { Error(Messages.Invalid_character, pos - 1, 1); Value = "#"; }
                return Kind = SyntaxKind.PrivateIdentifier;
            }
            if (ScanIdentifier(false, false)) return Kind = IdentifierKind(Value);
            if (ch == '>') { pos++; return Kind = SyntaxKind.GreaterThanToken; }
            if (ch == '?' && Char(1) == '.' && IsDigit(Char(2))) { pos++; return Kind = SyntaxKind.QuestionToken; }
            if (ch == '*' && jsDocDepth != 0 && HasPrecedingLineBreak && (Flags & TokenFlags.PrecedingJSDocLeadingAsterisks) == 0 && Char(1) is not ('*' or '='))
            { pos++; Flags |= TokenFlags.PrecedingJSDocLeadingAsterisks; continue; }
            for (int length = Math.Min(3, text.Length - pos); length > 0; length--)
            {
                SyntaxKind punctuation = FromText(text.AsSpan(pos, length));
                if (punctuation is SyntaxKind.Unknown or SyntaxKind.HashToken or SyntaxKind.BacktickToken) continue;
                if (punctuation == SyntaxKind.LessThanSlashToken && (!Jsx || Char(2) == '*')) continue;
                pos += length;
                return Kind = punctuation;
            }
            int point = CodePoint(out int width);
            if (point == 0xFFFD || point is >= 0xD800 and <= 0xDFFF)
            {
                Error(Messages.File_appears_to_be_binary, 0);
                pos = text.Length;
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
            if (at < 0) return false;
            text = text[(at + 1)..];
            if (text.StartsWith(name, StringComparison.Ordinal) && (text.Length == name.Length || text[name.Length] is ' ' or '\t' or '\r' or '\n' or '}' or '*')) return true;
        }
    }
    private void ProcessDirective(int start, int end, bool multiline)
    {
        int at = multiline ? start : start + 2;
        if (multiline) while (at < end && text[at] is ' ' or '\t') at++;
        while (at < end && (text[at] == '/' || multiline && text[at] == '*')) at++;
        while (at < end && text[at] is ' ' or '\t') at++;
        ReadOnlySpan<char> tail = text.AsSpan(at, end - at);
        if (tail.StartsWith("@ts-expect-error", StringComparison.Ordinal)) CommentDirectives.Add(new(start, end, true));
        else if (tail.StartsWith("@ts-ignore", StringComparison.Ordinal)) CommentDirectives.Add(new(start, end, false));
    }
    private bool IsConflictMarker(int at) => at + 7 < text.Length && (at == 0 || IsLineBreak(text[at - 1])) &&
        text.AsSpan(at, 7).IndexOfAnyExcept(text[at]) < 0 && (text[at] == '=' || text[at + 7] == ' ');
    private void ScanConflictMarker()
    {
        Error(Messages.Merge_conflict_marker_encountered, pos, 7);
        char first = text[pos];
        if (first is '<' or '>') while (pos < text.Length && !IsLineBreak(Char())) pos++;
        else while (pos < text.Length)
        {
            if (Char() is '=' or '>' && Char() != first && IsConflictMarker(pos)) break;
            pos++;
        }
    }
    private bool ScanIdentifier(bool privateName, bool jsxName)
    {
        int start = pos;
        int ch = CodePoint(out int width);
        StringBuilder? builder = null;
        if (ch == '\\')
        {
            int escaped = IdentifierEscape(true);
            if (escaped < 0) return false;
            builder = new();
            AppendCodePoint(builder, escaped);
        }
        else if (IsIdentifierStart(ch)) pos += width;
        else return false;
        int part = builder is null ? start : pos;
        while (pos < text.Length)
        {
            int ascii = text.AsSpan(pos).IndexOfAnyExcept(IdentifierAscii);
            pos = ascii < 0 ? text.Length : pos + ascii;
            ch = CodePoint(out width);
            if (IsIdentifierPart(ch, jsxName)) { pos += width; continue; }
            if (ch != '\\') break;
            int before = pos;
            int escaped = IdentifierEscape(false);
            if (escaped < 0) break;
            builder ??= new();
            builder.Append(text.AsSpan(part, before - part));
            AppendCodePoint(builder, escaped);
            part = pos;
        }
        Value = builder is null ? text[start..pos] : builder.Append(text.AsSpan(part, pos - part)).ToString();
        if (privateName) Value = "#" + Value;
        return true;
    }
    private int IdentifierEscape(bool start)
    {
        if (Char(1) != 'u') return -1;
        int before = pos;
        TokenFlags flags = Flags;
        int value = UnicodeEscape(false);
        if (start ? IsIdentifierStart(value) : IsIdentifierPart(value)) return value;
        pos = before; Flags = flags;
        return -1;
    }
    private static void AppendCodePoint(StringBuilder builder, int point)
    {
        if (point <= 0xFFFF) builder.Append((char)point);
        else builder.Append(char.ConvertFromUtf32(point));
    }
    private string ScanString(bool jsxAttribute)
    {
        int quote = Char();
        if (quote == '\'') Flags |= TokenFlags.SingleQuote;
        int start = ++pos;
        int closing = text.AsSpan(start).IndexOf((char)quote);
        if (closing >= 0 && (jsxAttribute || text.AsSpan(start, closing).IndexOfAny('\\', '\r', '\n') < 0))
        { pos += closing + 1; return text.Substring(start, closing); }
        var result = new StringBuilder();
        while (true)
        {
            int ch = Char();
            if (ch < 0 || !jsxAttribute && ch is '\r' or '\n')
            { result.Append(text.AsSpan(start, pos - start)); Flags |= TokenFlags.Unterminated; Error(Messages.Unterminated_string_literal); break; }
            if (ch == quote) { result.Append(text.AsSpan(start, pos - start)); pos++; break; }
            if (ch == '\\' && !jsxAttribute)
            { result.Append(text.AsSpan(start, pos - start)); result.Append(Escape(true)); start = pos; }
            else pos++;
        }
        return result.ToString();
    }
    private SyntaxKind ScanTemplate(bool reportEscapeErrors)
    {
        bool head = Char() == '`';
        int start = ++pos;
        var result = new StringBuilder();
        while (true)
        {
            int next = text.AsSpan(pos).IndexOfAny("`$\\\r");
            pos = next < 0 ? text.Length : pos + next;
            int ch = Char();
            if (ch < 0 || ch == '`')
            {
                result.Append(text.AsSpan(start, pos - start));
                if (ch == '`') pos++;
                else { Flags |= TokenFlags.Unterminated; Error(Messages.Unterminated_template_literal); }
                Value = result.ToString();
                return head ? SyntaxKind.NoSubstitutionTemplateLiteral : SyntaxKind.TemplateTail;
            }
            if (ch == '$' && Char(1) == '{')
            { result.Append(text.AsSpan(start, pos - start)); pos += 2; Value = result.ToString(); return head ? SyntaxKind.TemplateHead : SyntaxKind.TemplateMiddle; }
            if (ch == '\\')
            { result.Append(text.AsSpan(start, pos - start)); result.Append(Escape(reportEscapeErrors)); start = pos; continue; }
            if (ch == '\r')
            { result.Append(text.AsSpan(start, pos - start)); pos++; if (Char() == '\n') pos++; result.Append('\n'); start = pos; continue; }
            pos++;
        }
    }
    private string Escape(bool reportErrors)
    {
        int start = pos++;
        int ch = Char();
        if (ch < 0) { Error(Messages.Unexpected_end_of_text); return ""; }
        pos++;
        switch (ch)
        {
            case '0' when !IsDigit(Char()): return "\0";
            case >= '0' and <= '7':
                if (ch <= '3' && Char() is >= '0' and <= '7') pos++;
                if (Char() is >= '0' and <= '7') pos++;
                Flags |= TokenFlags.ContainsInvalidEscape;
                if (!reportErrors) return text[start..pos];
                int octal = Convert.ToInt32(text[(start + 1)..pos], 8);
                Error(Messages.Octal_escape_sequences_are_not_allowed_Use_the_syntax_0, start, pos - start, "\\x" + octal.ToString("x2", CultureInfo.InvariantCulture));
                return ((char)octal).ToString();
            case '8' or '9':
                Flags |= TokenFlags.ContainsInvalidEscape;
                if (!reportErrors) return text[start..pos];
                Error(Messages.Escape_sequence_0_is_not_allowed, start, pos - start, text[start..pos]);
                return ((char)ch).ToString();
            case 'b': return "\b";
            case 't': return "\t";
            case 'n': return "\n";
            case 'v': return "\v";
            case 'f': return "\f";
            case 'r': return "\r";
            case 'u':
                pos = start;
                int point = UnicodeEscape(reportErrors);
                return point < 0 ? text[start..pos] : point <= 0xFFFF ? ((char)point).ToString() : char.ConvertFromUtf32(point);
            case 'x':
                int value = 0;
                for (int i = 0; i < 2; i++)
                {
                    int digit = HexDigit(Char());
                    if (digit < 0)
                    { Flags |= TokenFlags.ContainsInvalidEscape; if (reportErrors) Error(Messages.Hexadecimal_digit_expected); return text[start..pos]; }
                    value = value * 16 + digit; pos++;
                }
                Flags |= TokenFlags.HexEscape;
                return ((char)value).ToString();
            case '\r': if (Char() == '\n') pos++; return "";
            case '\n' or 0x2028 or 0x2029: return "";
            default:
                if (char.IsHighSurrogate((char)ch) && Char() is >= 0xDC00 and <= 0xDFFF) return string.Concat((char)ch, text[pos++]);
                return ((char)ch).ToString();
        }
    }
    private int UnicodeEscape(bool reportErrors)
    {
        pos += 2;
        int start = pos;
        bool extended = Char() == '{';
        if (extended) pos++;
        else Flags |= TokenFlags.UnicodeEscape;
        long value = 0;
        int count = 0;
        while (count < 4 || extended)
        {
            int digit = HexDigit(Char());
            if (digit < 0) break;
            value = Math.Min(0x110000, value * 16 + digit); pos++; count++;
        }
        if (count < (extended ? 1 : 4))
        { Flags |= TokenFlags.ContainsInvalidEscape; if (reportErrors) Error(Messages.Hexadecimal_digit_expected); return -1; }
        if (extended)
        {
            bool invalid = value > 0x10FFFF;
            if (invalid && reportErrors) Error(Messages.An_extended_Unicode_escape_value_must_be_between_0x0_and_0x10FFFF_inclusive, start + 1, pos - start - 1);
            if (Char() < 0) { if (reportErrors) Error(Messages.Unexpected_end_of_text); invalid = true; }
            else if (Char() == '}') pos++;
            else { if (reportErrors) Error(Messages.Unterminated_Unicode_escape_sequence); invalid = true; }
            if (invalid) { Flags |= TokenFlags.ContainsInvalidEscape; return -1; }
            Flags |= TokenFlags.ExtendedUnicodeEscape;
        }
        return (int)value;
    }
    private string Digits(int radix, bool separators, bool decimalFragment = false)
    {
        int start = pos;
        bool allowed = false, previousSeparator = false;
        while (true)
        {
            int digit = HexDigit(Char());
            if (digit >= 0 && digit < radix) { pos++; allowed = true; previousSeparator = false; continue; }
            if (!separators || Char() != '_') break;
            Flags |= TokenFlags.ContainsSeparator;
            if (!allowed)
            {
                if (decimalFragment) Flags |= TokenFlags.ContainsInvalidSeparator;
                Error(previousSeparator ? Messages.Multiple_consecutive_numeric_separators_are_not_permitted : Messages.Numeric_separators_are_not_allowed_here, pos, 1);
            }
            else { allowed = false; previousSeparator = true; }
            pos++;
        }
        if (previousSeparator)
        { if (decimalFragment) Flags |= TokenFlags.ContainsInvalidSeparator; Error(Messages.Numeric_separators_are_not_allowed_here, pos - 1, 1); }
        return text[start..pos].Replace("_", "", StringComparison.Ordinal);
    }
    private SyntaxKind ScanNumber()
    {
        int start = pos;
        if (Char() == '0' && Char(1) is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            int radix = Char(1) is 'x' or 'X' ? 16 : Char(1) is 'o' or 'O' ? 8 : 2;
            pos += 2;
            string digits = Digits(radix, true);
            if (digits.Length == 0)
            { Error(radix == 16 ? Messages.Hexadecimal_digit_expected : radix == 8 ? Messages.Octal_digit_expected : Messages.Binary_digit_expected); digits = "0"; }
            Flags |= radix == 16 ? TokenFlags.HexSpecifier : radix == 8 ? TokenFlags.OctalSpecifier : TokenFlags.BinarySpecifier;
            BigInteger integer = BigInteger.Zero;
            foreach (char digit in digits) integer = integer * radix + HexDigit(digit);
            if (Char() == 'n')
            {
                pos++;
                Value = (radix == 16 ? "0x" + digits.ToLowerInvariant() : integer.ToString(CultureInfo.InvariantCulture)) + "n";
                return SyntaxKind.BigIntLiteral;
            }
            Value = NumberText((double)integer);
            return SyntaxKind.NumericLiteral;
        }
        string fixedPart;
        if (Char() == '0')
        {
            pos++;
            if (Char() == '_')
            {
                Flags |= TokenFlags.ContainsSeparator | TokenFlags.ContainsInvalidSeparator;
                Error(Messages.Numeric_separators_are_not_allowed_here, pos, 1);
                pos = start; fixedPart = Digits(10, true, true);
            }
            else
            {
                int digitsStart = pos;
                bool octal = true;
                while (IsDigit(Char())) { if (Char() >= '8') octal = false; pos++; }
                fixedPart = text[digitsStart..pos];
                if (fixedPart.Length == 0) fixedPart = "0";
                else if (!octal) Flags |= TokenFlags.ContainsLeadingZero;
                else
                {
                    BigInteger integer = 0;
                    foreach (char digit in fixedPart) integer = integer * 8 + digit - '0';
                    Value = NumberText((double)integer); Flags |= TokenFlags.Octal;
                    bool minus = Kind == SyntaxKind.MinusToken;
                    Error(Messages.Octal_literals_are_not_allowed_Use_the_syntax_0, minus ? start - 1 : start, pos - start + (minus ? 1 : 0), (minus ? "-" : "") + "0o" + fixedPart.TrimStart('0'));
                    return SyntaxKind.NumericLiteral;
                }
            }
        }
        else fixedPart = Digits(10, true, true);
        int fixedEnd = pos;
        string fraction = "", exponent = "";
        if (Char() == '.') { pos++; fraction = Digits(10, true, true); }
        int end = pos;
        if (Char() is 'e' or 'E')
        {
            pos++; Flags |= TokenFlags.Scientific;
            if (Char() is '+' or '-') pos++;
            int digitsStart = pos;
            string digits = Digits(10, true, true);
            if (digits.Length == 0) Error(Messages.Digit_expected);
            else { exponent = text[end..digitsStart] + digits; end = pos; }
        }
        string raw = (Flags & TokenFlags.ContainsSeparator) == 0 ? text[start..end] : fixedPart + (fraction.Length != 0 ? "." + fraction : "") + exponent;
        if ((Flags & TokenFlags.ContainsLeadingZero) != 0)
        { Error(Messages.Decimals_with_leading_zeros_are_not_allowed, start, pos - start); Value = NumberText(double.Parse(raw, CultureInfo.InvariantCulture)); return SyntaxKind.NumericLiteral; }
        SyntaxKind result = SyntaxKind.NumericLiteral;
        if (fixedEnd == pos && Char() == 'n') { pos++; Value = raw + "n"; result = SyntaxKind.BigIntLiteral; }
        else Value = NumberText(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : double.NaN);
        if (IsIdentifierStart(CodePoint(out _)))
        {
            int idStart = pos;
            string saved = Value;
            ScanIdentifier(false, false);
            string identifier = Value; Value = saved;
            if (result != SyntaxKind.BigIntLiteral && identifier == "n")
            {
                if ((Flags & TokenFlags.Scientific) != 0) { Error(Messages.A_bigint_literal_cannot_use_exponential_notation, start, pos - start); return result; }
                if (fixedEnd < idStart) { Error(Messages.A_bigint_literal_must_be_an_integer, start, pos - start); return result; }
            }
            Error(Messages.An_identifier_or_keyword_cannot_immediately_follow_a_numeric_literal, idStart, pos - idStart);
            pos = idStart;
        }
        return result;
    }
}
