internal static class ServerFlags
{
    internal static string[]? Split(string argument)
    {
        if (argument.Length < 2 || argument[0] != '-' || argument == "--") return null;
        string name = argument[(argument[1] == '-' ? 2 : 1)..];
        if (name[0] is '-' or '=') throw new ArgumentException($"bad flag syntax: {argument}");
        return name.Split('=', 2);
    }

    // Go's flag.Int uses strconv.ParseInt with base zero, including literal prefixes and underscores.
    internal static long Integer(string value)
    {
        ReadOnlySpan<char> text = value;
        bool negative = !text.IsEmpty && text[0] == '-';
        if (!text.IsEmpty && text[0] is '+' or '-') text = text[1..];
        int radix = 10;
        bool prefix = false;
        if (text.Length > 1 && text[0] == '0')
        {
            radix = text[1] switch { 'x' or 'X' => 16, 'b' or 'B' => 2, _ => 8 };
            prefix = text[1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O';
            if (prefix) text = text[2..];
        }
        ulong limit = IntPtr.Size == 8 ? 1UL << 63 : 1UL << 31;
        if (!negative) limit--;
        ulong result = 0;
        bool digitBefore = prefix;
        foreach (char character in text)
        {
            if (character == '_' && digitBefore) { digitBefore = false; continue; }
            int digit = character is >= '0' and <= '9' ? character - '0'
                : character is >= 'a' and <= 'f' ? character - 'a' + 10 : character is >= 'A' and <= 'F' ? character - 'A' + 10 : -1;
            if (digit < 0 || digit >= radix || result > (limit - (uint)digit) / (uint)radix) throw Invalid();
            result = result * (uint)radix + (uint)digit;
            digitBefore = true;
        }
        if (text.IsEmpty || !digitBefore) throw Invalid();
        return negative ? unchecked(-(long)result) : (long)result;
        ArgumentException Invalid() => new($"invalid value \"{value}\" for flag -clientProcessId");
    }
}
