using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>One row of an FSE decoding table (RFC 8878 §4.1).</summary>
public struct FseEntry
{
    /// <summary>The value added to the bits read to form the next state.</summary>
    public ushort Baseline { get; internal set; }

    /// <summary>The number of bits to read to form the next state.</summary>
    public byte NumBits { get; internal set; }

    /// <summary>The symbol this state decodes to.</summary>
    public byte Symbol { get; internal set; }
}

/// <summary>
/// An FSE (finite state entropy) decoding table: what a normalized probability distribution turns into
/// (RFC 8878 §4.1). An instance can be rebuilt many times, so a decoder keeps one per symbol type and
/// allocates nothing per block.
/// </summary>
public sealed class FseDecodingTable
{
    /// <summary>The largest accuracy log any Zstandard table uses (RFC 8878 §3.1.1.3.2.1).</summary>
    public const int MaxAccuracyLog = 9;

    private readonly FseEntry[] _entries = new FseEntry[1 << MaxAccuracyLog];
    private readonly ushort[] _symbolNext = new ushort[256];
    private int _accuracyLog;

    /// <summary>Gets the accuracy log: the table has <c>1 &lt;&lt; AccuracyLog</c> rows.</summary>
    public int AccuracyLog => _accuracyLog;

    /// <summary>Gets the rows of the table, indexed by state.</summary>
    public ReadOnlySpan<FseEntry> Entries => _entries.AsSpan(0, 1 << _accuracyLog);

    /// <summary>
    /// Reads a table description (RFC 8878 §4.1.1) from the start of <paramref name="source"/> and builds the table.
    /// </summary>
    /// <param name="source">The bytes that start with the description; it may be followed by other data.</param>
    /// <param name="maxSymbol">The largest symbol value the table may describe.</param>
    /// <param name="maxAccuracyLog">The largest accuracy log the caller accepts (at most <see cref="MaxAccuracyLog"/>).</param>
    /// <param name="streamOffset">Offset of <paramref name="source"/> in the compressed input, for error messages.</param>
    /// <returns>The number of bytes the description occupies.</returns>
    /// <exception cref="CompressionFormatException">The description is truncated or violates the format.</exception>
    public int ReadDescription(ReadOnlySpan<byte> source, int maxSymbol, int maxAccuracyLog, long streamOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAccuracyLog, MaxAccuracyLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSymbol, 255);
        if (source.Length == 0)
        {
            ZstdFormat.ThrowTruncated(streamOffset, "an FSE table description");
        }

        int accuracyLog = (source[0] & 15) + 5;
        if (accuracyLog > maxAccuracyLog)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, $"an FSE accuracy log of at most {maxAccuracyLog}, found {accuracyLog}");
        }

        Span<short> counts = stackalloc short[256];
        var reader = new ZstdForwardBitReader(source, 4);
        int remaining = (1 << accuracyLog) + 1; // RFC 8878 §4.1.1: probabilities are sent as value + 1, so the budget is one larger
        int threshold = 1 << accuracyLog;
        int nbBits = accuracyLog + 1;
        int symbol = 0;
        bool previousWasZero = false;
        while (remaining > 1 && symbol <= maxSymbol)
        {
            if (previousWasZero)
            {
                // A zero probability is followed by 2-bit repeat flags; a flag of 3 is followed by another flag.
                uint flag;
                do
                {
                    flag = reader.Read(2);
                    if (symbol + (int)flag > maxSymbol + 1)
                    {
                        ZstdFormat.ThrowCorrupt(streamOffset, $"FSE symbols not above {maxSymbol}");
                    }

                    counts.Slice(symbol, (int)flag).Clear();
                    symbol += (int)flag;
                }
                while (flag == 3);
                previousWasZero = false;
                if (reader.IsOverflowed)
                {
                    ZstdFormat.ThrowTruncated(streamOffset + source.Length, "the rest of an FSE table description");
                }

                if (symbol > maxSymbol)
                {
                    break;
                }
            }

            // RFC 8878 §4.1.1: values below `max` use one bit fewer than the field width.
            int max = ((2 * threshold) - 1) - remaining;
            uint bits = reader.Peek(nbBits);
            int count;
            if ((int)(bits & (uint)(threshold - 1)) < max)
            {
                count = (int)(bits & (uint)(threshold - 1));
                reader.Skip(nbBits - 1);
            }
            else
            {
                count = (int)(bits & (uint)((2 * threshold) - 1));
                if (count >= threshold)
                {
                    count -= max;
                }

                reader.Skip(nbBits);
            }

            count--; // value 0 is the "less than one" probability, -1
            remaining -= count < 0 ? -count : count;
            if (remaining < 1)
            {
                ZstdFormat.ThrowCorrupt(streamOffset, "FSE probabilities that sum to the table size");
            }

            counts[symbol++] = (short)count;
            previousWasZero = count == 0;
            while (remaining < threshold)
            {
                nbBits--;
                threshold >>= 1;
            }
        }

        if (reader.IsOverflowed)
        {
            ZstdFormat.ThrowTruncated(streamOffset + source.Length, "the rest of an FSE table description");
        }

        if (remaining != 1)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, $"FSE probabilities that sum to {1 << accuracyLog} using symbols up to {maxSymbol}");
        }

        Build(counts[..symbol], accuracyLog);
        return reader.BytesConsumed;
    }

    /// <summary>
    /// Builds the table from a normalized distribution (RFC 8878 §4.1.1, "From normalized distribution to decoding tables").
    /// </summary>
    /// <param name="normalizedCounts">The probability of each symbol from 0; <c>-1</c> marks a "less than one" probability.</param>
    /// <param name="accuracyLog">The accuracy log, 5 to <see cref="MaxAccuracyLog"/> (or fewer for a table of a single symbol).</param>
    /// <exception cref="ArgumentException">The counts do not sum to <c>1 &lt;&lt; accuracyLog</c>.</exception>
    public void Build(ReadOnlySpan<short> normalizedCounts, int accuracyLog)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accuracyLog, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accuracyLog, MaxAccuracyLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalizedCounts.Length, 256);

        int tableSize = 1 << accuracyLog;
        int tableMask = tableSize - 1;
        int total = 0;
        foreach (short c in normalizedCounts)
        {
            if (c < -1)
            {
                throw new ArgumentException("A probability cannot be below -1.", nameof(normalizedCounts));
            }

            total += c < 0 ? 1 : c;
        }

        if (total != tableSize)
        {
            throw new ArgumentException("The probabilities must sum to the table size.", nameof(normalizedCounts));
        }

        FseEntry[] entries = _entries;
        ushort[] symbolNext = _symbolNext;

        // Symbols with a "less than one" probability take one row each from the end of the table.
        int highThreshold = tableSize - 1;
        for (int s = 0; s < normalizedCounts.Length; s++)
        {
            if (normalizedCounts[s] == -1)
            {
                entries[highThreshold--].Symbol = (byte)s;
                symbolNext[s] = 1;
            }
            else
            {
                symbolNext[s] = (ushort)normalizedCounts[s];
            }
        }

        // The other symbols are spread over the remaining rows with a fixed stride.
        int step = (tableSize >> 1) + (tableSize >> 3) + 3;
        int position = 0;
        for (int s = 0; s < normalizedCounts.Length; s++)
        {
            for (int i = 0; i < normalizedCounts[s]; i++)
            {
                entries[position].Symbol = (byte)s;
                do
                {
                    position = (position + step) & tableMask;
                }
                while (position > highThreshold);
            }
        }

        // Each symbol's rows, in state order, get the next-state ranges: the lower rows use more bits.
        for (int state = 0; state < tableSize; state++)
        {
            byte symbol = entries[state].Symbol;
            int nextState = symbolNext[symbol]++;
            int bits = accuracyLog - BitOperations.Log2((uint)nextState);
            entries[state].NumBits = (byte)bits;
            entries[state].Baseline = (ushort)((nextState << bits) - tableSize);
        }

        _accuracyLog = accuracyLog;
    }

    /// <summary>Makes a table of a single symbol that costs no bits (the RLE mode, RFC 8878 §3.1.1.3.2.1).</summary>
    /// <param name="symbol">The only symbol.</param>
    public void BuildSingleSymbol(byte symbol)
    {
        _entries[0] = new FseEntry { Symbol = symbol, NumBits = 0, Baseline = 0 };
        _accuracyLog = 0;
    }
}
