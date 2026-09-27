using System.Globalization;
using System.Numerics;

namespace TypeScript.Compiler.Semantics;

// Port of internal/jsnum. Arithmetic keeps IEEE-754 signed zero and NaN.
public static class JsNumber
{
    public const double MaxSafeInteger = 9007199254740991;
    private static readonly double InvalidNumber = BitConverter.UInt64BitsToDouble(0x7ff8000000000001);

    public static double FromString(ReadOnlySpan<char> text)
    {
        var value = text;
        while (!value.IsEmpty && StringWhiteSpace(value[0]))
            value = value[1..];
        while (!value.IsEmpty && StringWhiteSpace(value[^1]))
            value = value[..^1];
        if (value.IsEmpty)
            return 0;
        if (value.SequenceEqual("Infinity") || value.SequenceEqual("+Infinity"))
            return double.PositiveInfinity;
        if (value.SequenceEqual("-Infinity"))
            return double.NegativeInfinity;
        if (value.Length > 2 && value[0] == '0')
        {
            int bits = value[1] switch { 'b' or 'B' => 1, 'o' or 'O' => 3, 'x' or 'X' => 4, _ => 0 };
            if (bits != 0)
            {
                value = value[2..];
                int first = value.Length;
                for (int i = 0; i < value.Length; i++)
                {
                    int digit = Digit(value[i]);
                    if (digit < 0 || digit >= 1 << bits)
                        return InvalidNumber;
                    if (digit != 0 && first == value.Length)
                        first = i;
                }
                if (first == value.Length)
                    return 0;
                // Any integer at least 2^1024 overflows binary64. Validate every
                // digit first, then bound the integer used for exact rounding.
                if ((long)(value.Length - first - 1) * bits >= 1024)
                    return double.PositiveInfinity;
                BigInteger integer = 0;
                foreach (char digit in value[first..])
                    integer = (integer << bits) + Digit(digit);
                return double.Parse(integer.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }
        foreach (char ch in value)
            if (ch is not (>= '0' and <= '9' or '+' or '-' or '.' or 'e' or 'E'))
                return InvalidNumber;
        return double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out double result) ? result : InvalidNumber;
    }

    private static int Digit(char value) => value is >= '0' and <= '9' ? value - '0'
        : value is >= 'a' and <= 'f' ? value - 'a' + 10 : value is >= 'A' and <= 'F' ? value - 'A' + 10 : -1;

    private static bool StringWhiteSpace(char value) => value is '\n' or '\r' or '\t' or '\v' or '\f' or '\u2028' or '\u2029' or '\uFEFF'
        || char.GetUnicodeCategory(value) == UnicodeCategory.SpaceSeparator;

    public static int ToInt32(double value)
    {
        if (!double.IsFinite(value))
            return 0;
        return unchecked((int)(long)(Math.Truncate(value) % 4294967296.0));
    }

    public static uint ToUInt32(double value) => unchecked((uint)ToInt32(value));

    public static double LeftShift(double x, double y) => ToInt32(x) << (int)(ToUInt32(y) & 31);

    public static double SignedRightShift(double x, double y) => ToInt32(x) >> (int)(ToUInt32(y) & 31);

    public static double UnsignedRightShift(double x, double y) => ToUInt32(x) >> (int)(ToUInt32(y) & 31);

    public static double BitwiseNot(double x) => ~ToInt32(x);

    public static double BitwiseOr(double x, double y) => ToInt32(x) | ToInt32(y);

    public static double BitwiseAnd(double x, double y) => ToInt32(x) & ToInt32(y);

    public static double BitwiseXor(double x, double y) => ToInt32(x) ^ ToInt32(y);

    public static double Remainder(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x))
            return InvalidNumber;
        if (double.IsInfinity(y))
            return x;
        if (y == 0)
            return InvalidNumber;
        return x == 0 ? x : x % y;
    }

    public static double Exponentiate(double value, double exponent)
    {
        if (((value == 1 || value == -1) && double.IsInfinity(exponent)) || (value == 1 && double.IsNaN(exponent)))
            return InvalidNumber;
        if (value >= long.MinValue && value <= (double)long.MaxValue && value == Math.Truncate(value)
            && exponent >= 0 && exponent <= (double)long.MaxValue && exponent == Math.Truncate(exponent) && !double.IsInfinity(exponent))
        {
            double magnitude = exponent * Math.Log2(Math.Abs(value));
            if (magnitude > 53 && magnitude <= Math.Log2(double.MaxValue))
            {
                // Match the reference's int64 conversion at the rounded upper boundary.
                long integer = value == (double)long.MaxValue ? long.MinValue : (long)value;
                var exact = BigInteger.Pow(new BigInteger(integer), (int)exponent);
                var absolute = BigInteger.Abs(exact);
                int discarded = (int)absolute.GetBitLength() - 256;
                if (discarded > 0)
                {
                    var leading = absolute >> discarded;
                    var remainder = absolute - (leading << discarded);
                    var half = BigInteger.One << (discarded - 1);
                    if (remainder > half || remainder == half && !leading.IsEven)
                        leading++;
                    exact = (leading << discarded) * exact.Sign;
                }
                return double.Parse(exact.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
        }
        if (exponent == 0 || value == 1)
            return 1;
        if (exponent == 1)
            return value;
        if (double.IsNaN(value) || double.IsNaN(exponent))
            return InvalidNumber;
        if (value != 0 && double.IsFinite(value) && exponent is 0.5 or -0.5)
            return exponent == 0.5 ? Math.Sqrt(value) : 1 / Math.Sqrt(value);
        if (value < 0 && double.IsFinite(value) && double.IsFinite(exponent) && exponent != Math.Truncate(exponent))
            return InvalidNumber;
        return Math.Pow(value, exponent);
    }
}
