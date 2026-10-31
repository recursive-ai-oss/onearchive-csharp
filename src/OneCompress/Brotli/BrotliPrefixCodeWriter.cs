using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Writes the description of a prefix code into a brotli stream (RFC 7932 sections 3.4 and 3.5): the simple form for up to four symbols
/// and the complex form, with run-length coded code lengths, otherwise. Part of the raw layer.
/// </summary>
/// <remarks>
/// <para>The writer picks the form from the lengths alone: a code whose lengths are one of the shapes the simple form can express
/// (1 symbol; 1,1; 1,2,2; 2,2,2,2; 1,2,3,3) uses it, anything else the complex form. In the complex form the lengths are cut after the last
/// symbol that has a code (the reader stops once the code is complete), a run of three or more zeros becomes code 17, and a run of three or
/// more copies of the previous non-zero length becomes code 16. The code that codes the lengths has at most 5 bits per symbol and is
/// always written with at least two symbols, so that the stream never depends on a reader's treatment of a one-symbol length code.</para>
/// </remarks>
public static class BrotliPrefixCodeWriter
{
    // RFC 7932 section 3.5: the order in which the code length code lengths are stored.
    private static ReadOnlySpan<byte> CodeLengthOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // RFC 7932 section 3.5: the fixed code for the code length code lengths 0..5, as (bits, bit count) in stream order.
    private static ReadOnlySpan<byte> LengthCodeBits => [0, 7, 3, 2, 1, 15];

    private static ReadOnlySpan<byte> LengthCodeBitCount => [2, 4, 3, 2, 2, 4];

    /// <summary>Writes a prefix code.</summary>
    /// <param name="writer">The destination.</param>
    /// <param name="lengths">The code length of each symbol of the alphabet (so its length is the alphabet size, at most 704); 0 for symbols without a code. The lengths must form a complete code, or have at most one nonzero length.</param>
    /// <param name="singleSymbol">For a code of one symbol, which takes no bits and so has length 0 in <paramref name="lengths"/>: the symbol. Ignored when the lengths name one or more symbols.</param>
    /// <exception cref="ArgumentException">The lengths are longer than 704, above 15, or not a complete code.</exception>
    public static void Write(BrotliBitWriter writer, ReadOnlySpan<byte> lengths, int singleSymbol = 0)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (lengths.Length is < 1 or > BrotliPrefixCode.MaxAlphabetSize)
        {
            throw new ArgumentException("A brotli alphabet has 1 to 704 symbols.", nameof(lengths));
        }

        int used = 0;
        long space = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] != 0)
            {
                if (lengths[i] > BrotliHuffmanCode.MaxCodeLength)
                {
                    throw new ArgumentException("A code length is above 15.", nameof(lengths));
                }

                used++;
                space += 32768 >> lengths[i];
            }
        }

        if ((uint)singleSymbol >= (uint)lengths.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(singleSymbol));
        }

        if (used > 1 && space != 32768)
        {
            throw new ArgumentException("The lengths are not a complete prefix code.", nameof(lengths));
        }

        if (used <= 1)
        {
            int only = singleSymbol;
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] != 0)
                {
                    only = i;
                }
            }

            WriteSimple(writer, lengths.Length, [only], 1, false);
            return;
        }

        if (used <= 4 && TryWriteSimple(writer, lengths, used))
        {
            return;
        }

        WriteComplex(writer, lengths);
    }

    private static bool TryWriteSimple(BrotliBitWriter writer, ReadOnlySpan<byte> lengths, int used)
    {
        Span<int> symbols = stackalloc int[4];
        int n = 0;
        // Ascending by length, then by symbol: the order the simple form assigns lengths in (RFC 7932 section 3.4).
        for (int len = 1; len <= 3 && n < used; len++)
        {
            for (int s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] == len)
                {
                    symbols[n++] = s;
                }
            }
        }

        int a = n > 0 ? lengths[symbols[0]] : 0;
        bool shape = used switch
        {
            2 => lengths[symbols[0]] == 1 && lengths[symbols[1]] == 1,
            3 => lengths[symbols[0]] == 1 && lengths[symbols[1]] == 2 && lengths[symbols[2]] == 2,
            _ => (a == 2 && lengths[symbols[3]] == 2) || (a == 1 && lengths[symbols[1]] == 2 && lengths[symbols[2]] == 3 && lengths[symbols[3]] == 3),
        };
        if (!shape || n != used)
        {
            return false;
        }

        WriteSimple(writer, lengths.Length, symbols.Slice(0, used), used, used == 4 && a == 1);
        return true;
    }

    private static void WriteSimple(BrotliBitWriter writer, int alphabetSize, ReadOnlySpan<int> symbols, int count, bool treeSelect)
    {
        int bits = 32 - BitOperations.LeadingZeroCount((uint)(alphabetSize - 1));
        writer.Write(1, 2);
        writer.Write((uint)(count - 1), 2);
        for (int i = 0; i < count; i++)
        {
            writer.Write((uint)symbols[i], bits);
        }

        if (count == 4)
        {
            writer.Write(treeSelect ? 1u : 0u, 1);
        }
    }

    private static void WriteComplex(BrotliBitWriter writer, ReadOnlySpan<byte> lengths)
    {
        int end = lengths.Length;
        while (lengths[end - 1] == 0)
        {
            end--;
        }

        // Run-length code the lengths: (code, extra bits value, extra bit count).
        var codes = new List<(byte Code, int Extra, int ExtraBits)>(end);
        int previousNonZero = 8;
        int pos = 0;
        while (pos < end)
        {
            byte value = lengths[pos];
            int run = 1;
            while (pos + run < end && lengths[pos + run] == value)
            {
                run++;
            }

            pos += run;
            if (value == 0)
            {
                if (run >= 3)
                {
                    EmitRepeat(codes, 17, 3, 7, run);
                }
                else
                {
                    for (int i = 0; i < run; i++)
                    {
                        codes.Add((0, 0, 0));
                    }
                }

                continue;
            }

            if (value != previousNonZero)
            {
                codes.Add((value, 0, 0));
                previousNonZero = value;
                run--;
            }

            if (run >= 3)
            {
                EmitRepeat(codes, 16, 2, 3, run);
            }
            else
            {
                for (int i = 0; i < run; i++)
                {
                    codes.Add((value, 0, 0));
                }
            }
        }

        Span<int> histogram = stackalloc int[18];
        histogram.Clear();
        foreach (var c in codes)
        {
            histogram[c.Code]++;
        }

        int distinct = 0;
        for (int i = 0; i < 18; i++)
        {
            if (histogram[i] > 0)
            {
                distinct++;
            }
        }

        if (distinct == 1)
        {
            histogram[histogram[0] > 0 ? 1 : 0] = 1;
        }

        Span<byte> clLengths = stackalloc byte[18];
        BrotliHuffmanCode.BuildLengths(histogram, 5, clLengths);
        Span<ushort> clCodes = stackalloc ushort[18];
        BrotliHuffmanCode.AssignCodes(clLengths, clCodes);

        int skip = 0;
        if (clLengths[CodeLengthOrder[0]] == 0 && clLengths[CodeLengthOrder[1]] == 0)
        {
            skip = clLengths[CodeLengthOrder[2]] == 0 ? 3 : 2;
        }

        writer.Write((uint)skip, 2);
        int space = 32;
        for (int i = skip; i < 18; i++)
        {
            int len = clLengths[CodeLengthOrder[i]];
            writer.Write(LengthCodeBits[len], LengthCodeBitCount[len]);
            if (len != 0)
            {
                space -= 32 >> len;
                if (space <= 0)
                {
                    break;
                }
            }
        }

        foreach (var c in codes)
        {
            writer.Write(clCodes[c.Code], clLengths[c.Code]);
            if (c.ExtraBits > 0)
            {
                writer.Write((uint)c.Extra, c.ExtraBits);
            }
        }
    }

    // RFC 7932 section 3.5: consecutive repeat codes of one kind multiply the count so far, so a long run is written as a base-2^extraBits
    // number. Solve for the digits from the least significant end: after the last code the count is (previous - 2) * radix + digit + 3.
    private static void EmitRepeat(List<(byte Code, int Extra, int ExtraBits)> codes, byte code, int extraBits, int digitMask, int run)
    {
        int max = (1 << extraBits) + 2; // the largest count a single code gives
        Span<int> digits = stackalloc int[16];
        int n = 0;
        int cur = run;
        while (cur > max)
        {
            digits[n++] = (cur - 3) & digitMask;
            cur = ((cur - 3) >> extraBits) + 2;
        }

        digits[n++] = cur - 3;
        for (int i = n - 1; i >= 0; i--)
        {
            codes.Add((code, digits[i], extraBits));
        }
    }
}
