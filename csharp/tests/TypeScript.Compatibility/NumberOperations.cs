using TypeScript.Compiler.Semantics;

namespace TypeScript.Compatibility;

internal static class NumberOperations
{
    public static bool ApproximatePower(double actual, Utf8String bits)
    {
        if (bits == "nan"u8)
            return double.IsNaN(actual);
        ulong expectedBits = ulong.Parse(
            bits,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture);
        double expected = BitConverter.UInt64BitsToDouble(expectedBits);
        if (!double.IsFinite(actual)
            || !double.IsFinite(expected)
            || actual == 0
            || expected == 0
            || double.IsNegative(actual) != double.IsNegative(expected))
            return false;
        ulong actualBits = BitConverter.DoubleToUInt64Bits(actual);
        ulong distance = actualBits > expectedBits ? actualBits - expectedBits : expectedBits - actualBits;
        return distance <= 4;
    }

    public static double[] Evaluate(double x, double y) => [
        JsNumber.LeftShift(x, y), JsNumber.SignedRightShift(x, y), JsNumber.UnsignedRightShift(x, y),
        JsNumber.BitwiseNot(x), JsNumber.BitwiseOr(x, y), JsNumber.BitwiseAnd(x, y), JsNumber.BitwiseXor(x, y),
        JsNumber.Remainder(x, y), JsNumber.Exponentiate(x, y), Math.Floor(x), Math.Abs(x),
    ];
}
