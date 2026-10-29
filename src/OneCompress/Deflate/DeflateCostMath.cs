using System.Numerics;

namespace RecursiveAI.OneCompress.Deflate;

// Integer arithmetic for the optimal parsers' cost models, shared by Deflate and Deflate64 so both price symbols the same way on every platform.
internal static class DeflateCostMath
{
    // log2(x) with 8 fractional bits, by integer squaring, so the result is the same on every platform.
    public static int Log2Q8(uint x)
    {
        int exponent = BitOperations.Log2(x);
        ulong mantissa = (ulong)x << (31 - exponent);
        int fraction = 0;
        for (int bit = 0; bit < 8; bit++)
        {
            mantissa = (mantissa * mantissa) >> 31;
            fraction <<= 1;
            if (mantissa >= (1UL << 32))
            {
                fraction |= 1;
                mantissa >>= 1;
            }
        }

        return (exponent << 8) | fraction;
    }
}
