using System.Text;
using TypeScript.Compiler.Diagnostics;
using static TypeScript.Compiler.Syntax.TokenFacts;

namespace TypeScript.Compiler.Syntax;

public sealed partial class Scanner
{
    public SyntaxKind RescanGreaterThanToken()
    {
        if (Kind != SyntaxKind.GreaterThanToken)
            return Kind;
        pos = TokenStart;
        int length = Math.Min(4, end - pos);
        for (; length > 0; length--)
        {
            SyntaxKind kind = FromText(text.AsSpan(pos, length));
            if (kind != SyntaxKind.Unknown)
            {
                pos += length;
                return Kind = kind;
            }
        }
        throw new InvalidOperationException("Expected greater-than token");
    }

    public SyntaxKind RescanLessThanToken()
    {
        if (Kind == SyntaxKind.LessThanLessThanToken)
        {
            pos = TokenStart + 1;
            Kind = SyntaxKind.LessThanToken;
        }
        return Kind;
    }

    public SyntaxKind RescanTemplateToken(bool tagged)
    {
        pos = TokenStart;
        return Kind = ScanTemplate(!tagged);
    }

    public SyntaxKind RescanAsteriskEqualsToken()
    {
        if (Kind == SyntaxKind.AsteriskEqualsToken)
        {
            pos = TokenStart + 1;
            Kind = SyntaxKind.EqualsToken;
        }
        return Kind;
    }

    public SyntaxKind RescanQuestionToken()
    {
        pos = TokenStart + 1;
        return Kind = SyntaxKind.QuestionToken;
    }

    public SyntaxKind RescanHashToken()
    {
        pos = TokenStart + 1;
        return Kind = SyntaxKind.HashToken;
    }

    public SyntaxKind RescanSlashToken(bool reportErrors = false)
    {
        if (Kind is not (SyntaxKind.SlashToken or SyntaxKind.SlashEqualsToken))
            return Kind;
        pos = TokenStart + 1;
        bool inClass = false;
        while (true)
        {
            int ch = CodePoint(out int width);
            if (ch < 0 || IsLineBreak(ch))
            {
                Flags |= TokenFlags.Unterminated;
                break;
            }
            if (ch == '/' && !inClass)
            {
                pos++;
                break;
            }
            if (ch == '\\')
            {
                pos++;
                int escaped = CodePoint(out width);
                if (escaped < 0 || IsLineBreak(escaped))
                {
                    Flags |= TokenFlags.Unterminated;
                    break;
                }
                pos += width;
                continue;
            }
            if (ch == '[')
                inClass = true;
            else if (ch == ']')
                inClass = false;
            pos += width;
        }
        if ((Flags & TokenFlags.Unterminated) != 0)
        {
            int end = pos, classes = 0, groups = 0;
            bool escaped = false, quantifier = false;
            pos = TokenStart + 1;
            while (pos < end)
            {
                int ch = Char();
                if (escaped)
                    escaped = false;
                else if (ch == '\\')
                    escaped = true;
                else if (ch == '[')
                    classes++;
                else if (ch == ']' && classes != 0)
                    classes--;
                else if (classes == 0)
                {
                    if (ch == '{')
                        quantifier = true;
                    else if (ch == '}' && quantifier)
                        quantifier = false;
                    else if (!quantifier)
                    {
                        if (ch == '(')
                            groups++;
                        else if (ch == ')' && groups != 0)
                            groups--;
                        else if (ch is ')' or ']' or '}')
                            break;
                    }
                }
                pos++;
            }
            while (pos > TokenStart + 1 && (IsWhiteSpace(text[pos - 1]) || IsLineBreak(text[pos - 1]) || text[pos - 1] == ';'))
                pos--;
            Error(Messages.Unterminated_regular_expression_literal, TokenStart, pos - TokenStart);
            Value = text[TokenStart..pos];
            return Kind = SyntaxKind.RegularExpressionLiteral;
        }
        int flagsStart = pos;
        while (IsIdentifierPart(CodePoint(out int width)))
            pos += width;
        Value = text[TokenStart..pos];
        if (reportErrors && (Flags & TokenFlags.Unterminated) == 0)
            ValidateRegularExpression(flagsStart);
        return Kind = SyntaxKind.RegularExpressionLiteral;
    }

    public SyntaxKind ScanJsxToken(bool allowMultiline = true)
    {
        FullStart = TokenStart = pos;
        switch (Char())
        {
            case -1:
                return Kind = SyntaxKind.EndOfFile;
            case '<':
                pos++;
                if (Char() == '/')
                {
                    pos++;
                    return Kind = SyntaxKind.LessThanSlashToken;
                }
                return Kind = SyntaxKind.LessThanToken;
            case '{':
                pos++;
                return Kind = SyntaxKind.OpenBraceToken;
        }
        int firstNonWhitespace = 0;
        while (pos < end)
        {
            int ch = CodePoint(out int width);
            if (ch == '{')
                break;
            if (ch == '<')
            {
                if (IsConflictMarker(pos))
                {
                    ScanConflictMarker();
                    return Kind = SyntaxKind.ConflictMarkerTrivia;
                }
                break;
            }
            if (ch == '>')
                Error(Messages.Unexpected_token_Did_you_mean_or_gt, pos, 1);
            else if (ch == '}')
                Error(Messages.Unexpected_token_Did_you_mean_or_rbrace, pos, 1);
            if (IsLineBreak(ch) && firstNonWhitespace == 0)
                firstNonWhitespace = -1;
            else if (!allowMultiline && IsLineBreak(ch) && firstNonWhitespace > 0)
                break;
            else if (!IsWhiteSpace(ch) && !IsLineBreak(ch))
                firstNonWhitespace = pos;
            pos += width;
        }
        Value = text[FullStart..pos];
        return Kind = firstNonWhitespace == -1 ? SyntaxKind.JsxTextAllWhiteSpaces : SyntaxKind.JsxText;
    }

    public SyntaxKind RescanJsxToken(bool allowMultiline = true)
    {
        pos = TokenStart;
        return ScanJsxToken(allowMultiline);
    }

    public SyntaxKind ScanJsxIdentifier()
    {
        if (Kind < SyntaxKind.Identifier)
            return Kind;
        bool privateName = text[TokenStart] == '#';
        pos = TokenStart + (privateName ? 1 : 0);
        return Kind = ScanIdentifier(privateName, true, out SyntaxKind identifierKind) ? identifierKind : IdentifierKind(Value);
    }

    public SyntaxKind ScanJsxAttributeValue()
    {
        FullStart = pos;
        while (IsWhiteSpace(Char()) || IsLineBreak(Char()))
            pos++;
        TokenStart = pos;
        if (Char() is not ('\'' or '"'))
            return Scan();
        Value = ScanString(true);
        return Kind = SyntaxKind.StringLiteral;
    }

    public SyntaxKind RescanJsxAttributeValue()
    {
        pos = FullStart;
        return ScanJsxAttributeValue();
    }

    public SyntaxKind ScanJSDocToken()
    {
        FullStart = pos;
        Flags = 0;
        int ch = CodePoint(out int width);
        if (ch < 0)
            return Kind = SyntaxKind.EndOfFile;
        TokenStart = pos;
        pos += width;
        if (ch is ' ' or '\t' or '\v' or '\f')
        {
            while (IsWhiteSpace(Char()))
                pos++;
            return Kind = SyntaxKind.WhitespaceTrivia;
        }
        if (ch is '\r' or '\n')
        {
            if (ch == '\r' && Char() == '\n')
                pos++;
            Flags |= TokenFlags.PrecedingLineBreak;
            return Kind = SyntaxKind.NewLineTrivia;
        }
        if (ch is '@' or '*' or '{' or '}' or '[' or ']' or '(' or ')' or '<' or '>' or '=' or ',' or '.' or '`' or '#')
            return Kind = FromText(text.AsSpan(TokenStart, width));
        pos = TokenStart;
        if (ScanIdentifier(false, true, out SyntaxKind identifierKind))
            return Kind = identifierKind;
        pos += width;
        return Kind = SyntaxKind.Unknown;
    }

    public SyntaxKind ScanJSDocCommentTextToken(bool inBackticks)
    {
        FullStart = TokenStart = pos;
        Flags = 0;
        if (pos >= end)
            return Kind = SyntaxKind.EndOfFile;
        while (pos < end)
        {
            int ch = CodePoint(out int width);
            if (IsLineBreak(ch) || ch == '`')
                break;
            if (!inBackticks && (ch == '{' || ch == '@' && pos > 0 && IsWhiteSpace(text[pos - 1]) && IsIdentifierStart(Char(1))))
                break;
            pos += width;
        }
        if (pos == TokenStart)
            return ScanJSDocToken();
        Value = text[TokenStart..pos];
        return Kind = SyntaxKind.JSDocCommentTextToken;
    }

    public bool CanFollowJSDocAt() => pos == end || IsIdentifierStart(CodePoint(out _)) || IsWhiteSpace(Char()) || IsLineBreak(Char());
}
