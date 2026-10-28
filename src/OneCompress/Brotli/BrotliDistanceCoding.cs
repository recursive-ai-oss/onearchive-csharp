using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>The distance codes of RFC 7932 section 4: the short codes that refer to the last distances, and the direct and logarithmic codes.</summary>
internal static class BrotliDistanceCoding
{
    /// <summary>
    /// Finds the short distance code (0 to 15) for a distance given the four last distances, most recent first, or -1 if there is none.
    /// Codes 0 to 3 name the last four distances; 4 to 9 are the last distance changed by -1, +1, -2, +2, -3, +3 and 10 to 15 the same for the
    /// second to last (RFC 7932 section 4). The lowest code that gives the distance is returned.
    /// </summary>
    internal static int ShortCode(int distance, ReadOnlySpan<int> ring)
    {
        if (distance == ring[0])
        {
            return 0;
        }

        if (distance == ring[1])
        {
            return 1;
        }

        if (distance == ring[2])
        {
            return 2;
        }

        if (distance == ring[3])
        {
            return 3;
        }

        for (int k = 0; k < 12; k++)
        {
            int baseDistance = k < 6 ? ring[0] : ring[1];
            int step = ((k % 6) >> 1) + 1;
            int candidate = (k & 1) == 0 ? baseDistance - step : baseDistance + step;
            if (candidate == distance && candidate > 0)
            {
                return 4 + k;
            }
        }

        return -1;
    }

    /// <summary>
    /// The symbol and extra bits that code a distance (1 or more) without a short code, for the given NPOSTFIX and NDIRECT
    /// (RFC 7932 section 4): distances up to NDIRECT have a symbol each; the others have a symbol that fixes the number of extra bits and
    /// their top bit and the low NPOSTFIX bits.
    /// </summary>
    internal static int Symbol(int distance, int postfix, int direct, out int extraBits, out int extra)
    {
        if (distance <= direct)
        {
            extraBits = 0;
            extra = 0;
            return 15 + distance;
        }

        int x = distance - direct - 1;
        int low = x & ((1 << postfix) - 1);
        uint y = (uint)((x >> postfix) + 4);
        int nb = BitOperations.Log2(y) - 1;
        int hb = (int)((y >> nb) & 1);
        extraBits = nb;
        extra = (int)(y & ((1u << nb) - 1));
        int hcode = (2 * (nb - 1)) + hb;
        return direct + 16 + ((hcode << postfix) | low);
    }

    /// <summary>The number of symbols in the distance alphabet.</summary>
    internal static int AlphabetSize(int postfix, int direct) => 16 + direct + (48 << postfix);
}
