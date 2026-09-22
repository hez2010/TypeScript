namespace TypeScript.Compiler.Semantics;

// Port of internal/jsnum. Arithmetic keeps IEEE-754 signed zero and NaN.
public static class JsNumber
{
    public const double MaxSafeInteger = 9007199254740991;

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

    public static double Remainder(double x, double y) => x % y;

    public static double Exponentiate(double value, double exponent)
    {
        if (((value == 1 || value == -1) && double.IsInfinity(exponent)) || (value == 1 && double.IsNaN(exponent)))
            return double.NaN;
        // ECMAScript permits implementation-approximated exponentiation.
        // Preserve the required special cases above; use the optimized BCL.
        return Math.Pow(value, exponent);
    }
}
