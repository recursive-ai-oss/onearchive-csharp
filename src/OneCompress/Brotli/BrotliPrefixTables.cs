using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Builds the lookup tables that decode brotli's canonical prefix codes (RFC 7932 section 3.2).
/// </summary>
/// <remarks>
/// <para>A table has 256 root entries indexed by the next eight bits of the stream; a code longer than eight bits continues in a
/// second-level table that the root entry points to. An entry is <c>symbol &lt;&lt; 8 | length</c> for a symbol (the length counts the
/// bits left to consume at this level), or <c>offset &lt;&lt; 8 | 0x80 | bits</c> for a link, where <c>offset</c> is relative to the start
/// of the table and <c>bits</c> is the index width of the second level. A code with one symbol has length zero.</para>
/// </remarks>
internal static class BrotliPrefixTables
{
    internal const int RootBits = 9;
    internal const uint RootMask = (1u << RootBits) - 1;
    internal const uint LinkFlag = 0x80;
    internal const int MaxCodeLength = 15;

    /// <summary>The largest alphabet in the format: the insert-and-copy length codes (RFC 7932 section 3.3).</summary>
    internal const int MaxAlphabetSize = 704;

    private static readonly byte[] s_reverse8 = CreateReverse8();

    private static byte[] CreateReverse8()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int r = 0;
            for (int b = 0; b < 8; b++)
            {
                r |= ((i >> b) & 1) << (7 - b);
            }

            table[i] = (byte)r;
        }

        return table;
    }

    // The code of the given length with its bits in reading order: the most significant bit of the canonical code is read first,
    // so it becomes the least significant bit of the table index.
    private static int ReverseBits(int code, int length) => ((s_reverse8[code & 0xFF] << 8) | s_reverse8[code >> 8]) >> (16 - length);

    /// <summary>The entry count of the table for a code with a single symbol.</summary>
    internal const int SingleSymbolTableSize = 1 << RootBits;

    /// <summary>Fills the table at <paramref name="offset"/> for a code whose one symbol takes no bits.</summary>
    internal static int BuildSingle(int symbol, ref uint[] table, int offset)
    {
        Ensure(ref table, offset + SingleSymbolTableSize);
        table.AsSpan(offset, SingleSymbolTableSize).Fill((uint)symbol << 8);
        return SingleSymbolTableSize;
    }

    /// <summary>
    /// Builds the decoding table for the code with the given code lengths (zero for an unused symbol) at <paramref name="offset"/>,
    /// growing <paramref name="table"/> if needed.
    /// </summary>
    /// <returns>The number of entries used, or -1 if the lengths do not describe a complete prefix code (every bit pattern must start exactly one code).</returns>
    internal static int Build(ReadOnlySpan<byte> lengths, ref uint[] table, int offset)
    {
        if (lengths.Length > MaxAlphabetSize)
        {
            return -1;
        }

        Span<int> count = stackalloc int[MaxCodeLength + 1];
        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len > MaxCodeLength)
            {
                return -1;
            }

            count[len]++;
        }

        count[0] = 0;
        int space = 1 << MaxCodeLength;
        for (int len = 1; len <= MaxCodeLength; len++)
        {
            space -= count[len] << (MaxCodeLength - len);
        }

        if (space != 0)
        {
            return -1;
        }

        // RFC 7932 section 3.2: the first code of each length, then consecutive codes in symbol order.
        Span<int> nextCode = stackalloc int[MaxCodeLength + 2];
        Span<int> startIndex = stackalloc int[MaxCodeLength + 2];
        int code = 0;
        int index = 0;
        for (int len = 1; len <= MaxCodeLength; len++)
        {
            code = (code + count[len - 1]) << 1;
            nextCode[len] = code;
            startIndex[len] = index;
            index += count[len];
        }

        // Symbols in code order: by length, then by value.
        Span<ushort> sorted = stackalloc ushort[MaxAlphabetSize];
        for (int sym = 0; sym < lengths.Length; sym++)
        {
            int len = lengths[sym];
            if (len != 0)
            {
                sorted[startIndex[len]++] = (ushort)sym;
            }
        }

        Ensure(ref table, offset + (1 << RootBits));
        int size = 1 << RootBits;
        int currentRoot = -1;
        int subStart = 0;
        int subBits = 0;
        int total = index;
        for (int i = 0; i < total; i++)
        {
            int sym = sorted[i];
            int len = lengths[sym];
            int c = nextCode[len]++;
            int reversed = ReverseBits(c, len);
            if (len <= RootBits)
            {
                uint entry = ((uint)sym << 8) | (uint)len;
                for (int k = reversed; k < (1 << RootBits); k += 1 << len)
                {
                    table[offset + k] = entry;
                }

                count[len]--;
                continue;
            }

            int root = reversed & (int)RootMask;
            int low = len - RootBits;
            if (root != currentRoot)
            {
                // A new group of codes shares these eight bits. Size its second-level table for the longest code among them: the
                // codes still to come that are longer than this one fill the room that the current length leaves free.
                currentRoot = root;
                int bits = low;
                int left = 1 << bits;
                while (bits + RootBits < MaxCodeLength)
                {
                    left -= count[bits + RootBits];
                    if (left <= 0)
                    {
                        break;
                    }

                    bits++;
                    left <<= 1;
                }

                Ensure(ref table, offset + size + (1 << bits));
                table[offset + root] = ((uint)size << 8) | LinkFlag | (uint)bits;
                subStart = size;
                subBits = bits;
                size += 1 << bits;
            }

            uint subEntry = ((uint)sym << 8) | (uint)low;
            for (int k = reversed >> RootBits; k < (1 << subBits); k += 1 << low)
            {
                table[offset + subStart + k] = subEntry;
            }

            count[len]--;
        }

        return size;
    }

    private static void Ensure(ref uint[] table, int needed)
    {
        if (table.Length < needed)
        {
            Array.Resize(ref table, Math.Max(needed, table.Length * 2));
        }
    }

    /// <summary>The number of bits that a symbol of an alphabet of the given size takes in a simple prefix code (RFC 7932 section 3.4).</summary>
    internal static int AlphabetBits(int alphabetSize) => 32 - BitOperations.LeadingZeroCount((uint)(alphabetSize - 1));
}
