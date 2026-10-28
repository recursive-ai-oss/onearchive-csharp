using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The cost, in 1/16 bit, of coding a bit with an adaptive probability. Encoders compare these costs to choose between parses; they never
/// reach the output, but they decide which bytes are written, so they are computed with integers only.
/// </summary>
/// <remarks>
/// The table has one entry per 16 probability values (128 entries): entry <c>i</c> is round(16 * log2(2048 / (16 i + 8))), computed
/// with the repeated-squaring method for the binary logarithm in fixed point, so no floating-point function is involved and every
/// platform gets the same table. The price of a 1 bit is the price of a 0 bit under the complementary probability.
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class LzmaPrices
{
    /// <summary>The number of price units that make one bit.</summary>
    public const int UnitsPerBit = 16;

    private const int ProbabilityShift = 4;

    private static readonly int[] Table = BuildTable();

    /// <summary>Gets the price of coding <paramref name="bit"/> with an adaptive probability.</summary>
    /// <param name="probability">The probability of a 0 bit, 1 to 2047, in units of 1/2048.</param>
    /// <param name="bit">0 or 1.</param>
    /// <returns>The cost in 1/16 bit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Get(int probability, int bit) => Table[(bit == 0 ? probability : 2048 - probability) >> ProbabilityShift];

    /// <summary>Gets the price of a 0 bit.</summary>
    /// <param name="probability">The probability of a 0 bit.</param>
    /// <returns>The cost in 1/16 bit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Zero(int probability) => Table[probability >> ProbabilityShift];

    /// <summary>Gets the price of a 1 bit.</summary>
    /// <param name="probability">The probability of a 0 bit.</param>
    /// <returns>The cost in 1/16 bit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int One(int probability) => Table[(2048 - probability) >> ProbabilityShift];

    private static int[] BuildTable()
    {
        var table = new int[128];
        for (int i = 0; i < table.Length; i++)
        {
            // log2(2048 / w) = 11 - log2(w), w = 16 i + 8, in 1/256 bit.
            uint w = (uint)((i << ProbabilityShift) + 8);
            int log2 = Log2Fixed8(w);
            int price256 = (11 << 8) - log2;
            table[i] = (price256 * UnitsPerBit + 128) >> 8;
        }

        return table;
    }

    // floor(256 * log2(w)) for w >= 1: the integer part from the bit length, then one fraction bit per squaring of the mantissa.
    private static int Log2Fixed8(uint w)
    {
        int k = 31 - BitOperations.LeadingZeroCount(w);
        ulong y = (ulong)w << (30 - k); // in [2^30, 2^31): the mantissa 1.xxx with 30 fraction bits
        int result = k << 8;
        for (int bit = 7; bit >= 0; bit--)
        {
            y = (y * y) >> 30;
            if (y >= 1UL << 31)
            {
                y >>= 1;
                result |= 1 << bit;
            }
        }

        return result;
    }
}
