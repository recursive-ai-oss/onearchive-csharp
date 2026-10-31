
namespace RecursiveAI.OneCompress.Lzx;

/// <summary>
/// The position-slot tables of LZX: how a match offset is split into a slot (coded in the main tree) and a footer of raw bits.
/// </summary>
/// <remarks>
/// <para>[MS-PATCH] 2.6.2 lists the slots: slots 0 to 3 have no footer bits, from slot 4 on every pair of slots shares a footer width
/// that grows by one bit per pair up to 17 bits, and each slot's base position is the sum of the ranges before it. The tables are
/// computed from that rule rather than copied, and the values agree with the listing in the specification.</para>
/// <para>A window of 2<sup>n</sup> bytes uses the first <see cref="GetSlotCount"/> slots (30 for 2<sup>15</sup> up to 50 for
/// 2<sup>21</sup>, the range cabinets use).</para>
/// </remarks>
public static class LzxPositionSlots
{
    /// <summary>The smallest window size exponent of a cabinet: 2<sup>15</sup> bytes.</summary>
    public const int MinWindowBits = 15;

    /// <summary>The largest window size exponent of a cabinet: 2<sup>21</sup> bytes.</summary>
    public const int MaxWindowBits = 21;

    /// <summary>The largest number of footer bits of any slot.</summary>
    public const int MaxFooterBits = 17;

    // Slots for windows 2^15 .. 2^21 ([MS-PATCH] 2.1.6, extended down to 2^15 by the cabinet format's smaller windows: 30 and 32 follow
    // the same pattern of two more slots per doubling).
    private static readonly byte[] s_slotCounts = [30, 32, 34, 36, 38, 42, 50];

    private static readonly byte[] s_footerBits = BuildFooterBits();

    private static readonly int[] s_bases = BuildBases();

    /// <summary>Gets the number of position slots a window of 2<sup>windowBits</sup> bytes uses.</summary>
    /// <param name="windowBits">The window exponent, 15 to 21.</param>
    /// <returns>The slot count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowBits"/> is outside 15 to 21.</exception>
    public static int GetSlotCount(int windowBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBits, MinWindowBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowBits, MaxWindowBits);
        return s_slotCounts[windowBits - MinWindowBits];
    }

    /// <summary>Gets the number of raw footer bits that follow a slot.</summary>
    /// <param name="slot">The slot, 0 to 49.</param>
    /// <returns>0 to 17.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the table.</exception>
    public static int GetFooterBits(int slot) => s_footerBits[slot];

    /// <summary>Gets the formatted offset where a slot's range starts. A formatted offset is the real offset plus 2; 0 to 2 are the repeated-offset codes.</summary>
    /// <param name="slot">The slot, 0 to 49.</param>
    /// <returns>The base position.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside the table.</exception>
    public static int GetBasePosition(int slot) => s_bases[slot];

    internal static ReadOnlySpan<byte> FooterBits => s_footerBits;

    internal static ReadOnlySpan<int> BasePositions => s_bases;

    private static byte[] BuildFooterBits()
    {
        var bits = new byte[50];
        for (int s = 4; s < bits.Length; s++)
        {
            bits[s] = (byte)Math.Min(MaxFooterBits, (s >> 1) - 1);
        }

        return bits;
    }

    private static int[] BuildBases()
    {
        var bases = new int[50];
        for (int s = 1; s < bases.Length; s++)
        {
            bases[s] = bases[s - 1] + (1 << s_footerBits[s - 1]);
        }

        return bases;
    }
}
