namespace TypeScript.Compiler.Text;

internal static class Utf8Ascii
{
    internal static bool IsLetter(int value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    internal static bool IsDigit(int value) => value is >= '0' and <= '9';
    internal static bool IsLetterOrDigit(int value) => IsLetter(value) || IsDigit(value);
    internal static int ToUpper(int value) => value is >= 'a' and <= 'z' ? value - ('a' - 'A') : value;
}
