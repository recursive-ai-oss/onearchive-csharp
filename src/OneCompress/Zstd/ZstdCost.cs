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

    private const int SmallLogLimit = 2048;

    // Log2 of every count below SmallLogLimit, so that the entropy of a small block is a table walk.
    private static readonly ushort[] s_log2Small = BuildLog2Small();

    private static ushort[] BuildLog2Small()
    {
        var table = new ushort[SmallLogLimit];
        for (uint i = 1; i < SmallLogLimit; i++)
        {
            table[i] = (ushort)Log2(i);
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

    /// <summary>
    /// A lower bound, in 1/256 bits, on what any prefix code or finite-state entropy table needs to code the symbols of
    /// <paramref name="histogram"/>: their empirical entropy, reduced by the rounding of the integer logarithm.
    /// </summary>
    /// <param name="histogram">How often each symbol occurs.</param>
    /// <param name="maxSymbol">The largest symbol that occurs.</param>
    /// <param name="total">The sum of the histogram.</param>
    public static long EntropyLowerBound(ReadOnlySpan<uint> histogram, int maxSymbol, int total)
    {
        if (total <= 1)
        {
            return 0;
        }

        long logTotal = Log2((uint)total);
        long sum = 0;
        ushort[] small = s_log2Small;
        for (int s = 0; s <= maxSymbol; s++)
        {
            uint count = histogram[s];
            if (count != 0)
            {
                sum += count * (logTotal - (count < SmallLogLimit ? small[count] : Log2(count)));
            }
        }

        // Each logarithm is off by at most one unit in either direction; leave that much slack per symbol coded.
        return Math.Max(0, sum - (2L * total) - (8 * Bit));
    }
}
