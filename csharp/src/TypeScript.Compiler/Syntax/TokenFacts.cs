using System.Globalization;

namespace TypeScript.Compiler.Syntax;

public static partial class TokenFacts
{
    public static bool IsLineBreak(int ch) => ch is '\n' or '\r' or 0x2028 or 0x2029;

    public static bool IsWhiteSpace(int ch) =>
        ch is ' ' or '\t' or '\v' or '\f' or 0x85 or 0xA0 or 0x1680 or >= 0x2000 and <= 0x200B or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    public static bool IsDigit(int ch) => ch is >= '0' and <= '9';

    public static int HexDigit(int ch) =>
        ch switch { >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10, >= 'A' and <= 'F' => ch - 'A' + 10, _ => -1 };

    public static bool IsIdentifierStart(int ch) =>
        ch is '_' or '$' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' || ch >= 128 && InRanges(IdentifierStartBoundaries, ch);

    public static bool IsIdentifierPart(int ch, bool jsx = false) =>
        IsIdentifierStart(ch) || IsDigit(ch) || jsx && ch == '-' || ch >= 128 && InRanges(IdentifierPartBoundaries, ch);

    private static bool InRanges(ReadOnlySpan<int> boundaries, int ch)
    {
        int index = boundaries.BinarySearch(ch);
        return index >= 0 ? (index & 1) == 0 : (~index & 1) != 0;
    }

    public static SyntaxKind IdentifierKind(ReadOnlySpan<char> text)
    {
        SyntaxKind kind = text.Length is >= 2 and <= 12 ? FromText(text) : SyntaxKind.Unknown;
        return kind >= SyntaxKind.BreakKeyword ? kind : SyntaxKind.Identifier;
    }

    public static string NumberText(double number)
    {
        if (double.IsNaN(number))
            return "NaN";
        if (double.IsPositiveInfinity(number))
            return "Infinity";
        if (double.IsNegativeInfinity(number))
            return "-Infinity";
        if (number == 0)
            return "0";
        string text = number.ToString("R", CultureInfo.InvariantCulture);
        int e = text.IndexOf('E');
        if (e < 0)
            return text;
        int exponent = int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture);
        if (exponent is >= -6 and < 21)
        {
            bool negative = text[0] == '-';
            string digits = text[(negative ? 1 : 0)..e].Replace(".", "", StringComparison.Ordinal);
            int point = exponent + 1;
            string expanded = point <= 0
                ? "0." + new string('0', -point) + digits
                : point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, ".");
            return negative ? "-" + expanded : expanded;
        }
        return text[..e] + "e" + (exponent >= 0 ? "+" : "-") + Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
    }
}
