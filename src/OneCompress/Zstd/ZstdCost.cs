using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Integer cost arithmetic for the encoder's decisions. Costs are in 1/256 bit, so a choice between two ways
/// of coding a block never depends on floating point and is the same on every platform.
/// </summary>
internal static class ZstdCost
{
    /// <summary>One bit in cost units.</summary>
    public const int Bit = 256;

    // Log2Fraction[i] = round(256 * log2(1 + i / 256)), built with integer squaring so no floating point is involved.
    private static readonly ushort[] s_log2Fraction = BuildLog2Fraction();

    private static ushort[] BuildLog2Fraction()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            // y is the mantissa in [1, 2) as a 1.30 fixed-point number; each squaring yields the next fractional bit of the log.
            ulong y = (1UL << 30) + ((ulong)i << 22);
            int value = 0;
            for (int bit = 0; bit < 9; bit++)
            {
                y = (y * y) >> 30;
                value <<= 1;
                if (y >= 2UL << 30)
                {
                    y >>= 1;
                    value |= 1;
                }
            }

            // Nine bits were produced; the last one rounds the eight that are kept.
            table[i] = (ushort)((value >> 1) + (value & 1));
        }

        return table;
    }

    /// <summary>Gets <c>256 * log2(x)</c> rounded, for <paramref name="x"/> of at least 1.</summary>
    public static int Log2(uint x)
    {
        int high = BitOperations.Log2(x);
        uint fraction = (x << (31 - high)) >> 23 & 255;
        return (high << 8) + s_log2Fraction[fraction];
    }
}
