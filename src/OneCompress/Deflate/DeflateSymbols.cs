using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>Lookup tables an encoder needs to turn lengths and distances into symbols (RFC 1951 section 3.2.5).</summary>
internal static class DeflateSymbols
{
    /// <summary>Maps a match length 3..258 to its length symbol index 0..28 (symbol 257 + index).</summary>
    public static readonly byte[] LengthSlot = BuildLengthSlots();

    /// <summary>
    /// Maps a distance to its slot 0..29. Distances up to 256 index directly with <c>distance - 1</c>; larger ones index at
    /// 256 + ((distance - 1) &gt;&gt; 7), because every slot above 15 covers whole multiples of 128 distances.
    /// </summary>
    public static readonly byte[] DistanceSlotTable = BuildDistanceSlots();

    /// <summary>The code lengths of the fixed literal/length code (RFC 1951 section 3.2.6).</summary>
    public static readonly byte[] FixedLiteralLengths = BuildFixedLiteralLengths();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DistanceSlot(int distance) =>
        distance <= 256 ? DistanceSlotTable[distance - 1] : DistanceSlotTable[256 + ((distance - 1) >> 7)];

    private static byte[] BuildLengthSlots()
    {
        var table = new byte[259];
        ReadOnlySpan<ushort> baseLength = DeflateTables.LengthBase;
        for (int slot = 0; slot < 29; slot++)
        {
            int start = baseLength[slot];
            int end = slot == 28 ? 259 : baseLength[slot + 1];
            for (int length = start; length < end; length++)
            {
                table[length] = (byte)slot;
            }
        }

        // Length 258 has its own symbol (285) with no extra bits, so slot 27 stops at 257.
        table[258] = 28;
        return table;
    }

    private static byte[] BuildDistanceSlots()
    {
        var table = new byte[512];
        ReadOnlySpan<ushort> baseDistance = DeflateTables.DistanceBase;
        for (int slot = 0; slot < 30; slot++)
        {
            int start = baseDistance[slot];
            int end = slot == 29 ? 32769 : baseDistance[slot + 1];
            for (int distance = start; distance < end; distance++)
            {
                if (distance <= 256)
                {
                    table[distance - 1] = (byte)slot;
                }
                else if (((distance - 1) & 127) == 0)
                {
                    table[256 + ((distance - 1) >> 7)] = (byte)slot;
                }
            }
        }

        return table;
    }

    private static byte[] BuildFixedLiteralLengths()
    {
        var lengths = new byte[288];
        DeflateTables.GetFixedLiteralLengthCodeLengths(lengths);
        return lengths;
    }
}
