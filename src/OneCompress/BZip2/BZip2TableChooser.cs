
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Chooses the Huffman tables of a block and the table for each group of 50 symbols (derivation note, facts F4 and F5). The
/// number of tables depends on the number of symbols; the groups start dealt out to the tables in runs of consecutive groups (text changes
/// character along a block, so neighbours resemble each other) and are then refined: each group goes to the table that codes it in the fewest
/// bits, and each table's lengths are rebuilt from the groups it received, for a fixed number of passes. All arithmetic is integer and ties go to the lower table, so the result depends
/// only on the symbols.
/// </summary>
public sealed class BZip2TableChooser
{
    /// <summary>The number of symbols in a group that shares a table.</summary>
    public const int GroupSize = 50;

    /// <summary>The most tables a block may have.</summary>
    public const int MaxTables = 6;

    /// <summary>The number of refinement passes.</summary>
    public const int Passes = 6;

    private readonly BZip2HuffmanBuilder _builder = new();
    private readonly byte[] _lengths = new byte[MaxTables * BZip2HuffmanTable.MaxAlphabetSize];
    private readonly int[] _freq = new int[MaxTables * BZip2HuffmanTable.MaxAlphabetSize];
    private byte[] _selectors = new byte[1024];
    private int _tableCount;
    private int _selectorCount;
    private int _alphabetSize;

    /// <summary>Gets the number of tables chosen, 2 to 6.</summary>
    public int TableCount => _tableCount;

    /// <summary>Gets the table number for each group of <see cref="GroupSize"/> symbols.</summary>
    public ReadOnlySpan<byte> Selectors => _selectors.AsSpan(0, _selectorCount);

    /// <summary>Gets the code lengths of a table, one per symbol of the alphabet.</summary>
    /// <param name="table">The table, 0 to <see cref="TableCount"/> - 1.</param>
    /// <returns>The lengths, each 1 to the maximum length.</returns>
    public ReadOnlySpan<byte> GetLengths(int table)
    {
        if ((uint)table >= (uint)_tableCount)
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        return _lengths.AsSpan(table * BZip2HuffmanTable.MaxAlphabetSize, _alphabetSize);
    }

    /// <summary>Gets the table count used for a block of <paramref name="symbolCount"/> symbols.</summary>
    /// <param name="symbolCount">The number of symbols, end of block included.</param>
    /// <returns>2 to 6.</returns>
    public static int GetTableCount(int symbolCount) => symbolCount switch
    {
        < 200 => 2,
        < 600 => 3,
        < 1200 => 4,
        < 2400 => 5,
        _ => MaxTables,
    };

    /// <summary>Chooses tables for a block's symbols.</summary>
    /// <param name="symbols">The symbols, ending with the end-of-block symbol (the last symbol of the alphabet).</param>
    /// <param name="alphabetSize">The alphabet size, 3 to 258.</param>
    /// <param name="maxLength">The longest code length allowed, 9 to 20.</param>
    /// <returns>The number of tables.</returns>
    /// <exception cref="ArgumentException">There are no symbols, one is outside the alphabet, or the alphabet size is invalid.</exception>
    public int Choose(ReadOnlySpan<ushort> symbols, int alphabetSize, int maxLength = BZip2HuffmanBuilder.DefaultMaxLength)
    {
        if (symbols.IsEmpty)
        {
            throw new ArgumentException("A block has at least the end-of-block symbol.", nameof(symbols));
        }

        if (alphabetSize is < BZip2HuffmanTable.MinAlphabetSize or > BZip2HuffmanTable.MaxAlphabetSize)
        {
            throw new ArgumentOutOfRangeException(nameof(alphabetSize));
        }

        int stride = BZip2HuffmanTable.MaxAlphabetSize;
        _alphabetSize = alphabetSize;
        int groups = (symbols.Length + GroupSize - 1) / GroupSize;
        if (_selectors.Length < groups)
        {
            _selectors = new byte[groups];
        }

        foreach (ushort s in symbols)
        {
            if (s >= alphabetSize)
            {
                throw new ArgumentException("A symbol is outside the alphabet.", nameof(symbols));
            }
        }

        int k = GetTableCount(symbols.Length);

        // Start: the groups are dealt out to the tables in k runs of consecutive groups, and each table's lengths come from its run.
        Array.Clear(_freq);
        for (int g = 0; g < groups; g++)
        {
            int t = (int)((long)g * k / groups);
            _selectors[g] = (byte)t;
            int from = g * GroupSize;
            foreach (ushort s in symbols.Slice(from, Math.Min(GroupSize, symbols.Length - from)))
            {
                _freq[(t * stride) + s]++;
            }
        }

        for (int t = 0; t < k; t++)
        {
            _builder.Build(_freq.AsSpan(t * stride, alphabetSize), _lengths.AsSpan(t * stride, alphabetSize), maxLength);
        }

        // A group's cost under every table is added up in one go: each symbol's code lengths, one per table, sit in one 64-bit word as ten-bit
        // lanes (a group's cost is at most 50 x 20 = 1000, which fits a lane, so no lane carries into the next).
        Span<ulong> packed = stackalloc ulong[BZip2HuffmanTable.MaxAlphabetSize];
        const ulong LaneMask = (1UL << 10) - 1;
        Span<bool> changed = stackalloc bool[MaxTables];
        for (int pass = 0; pass < Passes; pass++)
        {
            for (int s = 0; s < alphabetSize; s++)
            {
                ulong word = 0;
                for (int t = 0; t < k; t++)
                {
                    word |= (ulong)_lengths[(t * stride) + s] << (10 * t);
                }

                packed[s] = word;
            }

            // The symbol counts of each table follow the groups it holds: a group that moves is taken out of one table's counts and put into
            // the other's, which gives the counts a rebuild from scratch would.
            bool moved = false;
            changed.Clear();
            for (int g = 0; g < groups; g++)
            {
                ReadOnlySpan<ushort> group = symbols.Slice(g * GroupSize, Math.Min(GroupSize, symbols.Length - (g * GroupSize)));
                ulong cost = 0;
                foreach (ushort s in group)
                {
                    cost += packed[s];
                }

                int best = 0;
                ulong bestCost = cost & LaneMask;
                for (int t = 1; t < k; t++)
                {
                    ulong c = (cost >> (10 * t)) & LaneMask;
                    if (c < bestCost)
                    {
                        best = t;
                        bestCost = c;
                    }
                }

                int previous = _selectors[g];
                if (best != previous)
                {
                    moved = true;
                    changed[previous] = true;
                    changed[best] = true;
                    _selectors[g] = (byte)best;
                    foreach (ushort s in group)
                    {
                        _freq[(previous * stride) + s]--;
                        _freq[(best * stride) + s]++;
                    }
                }
            }

            if (!moved)
            {
                // Nothing changed, so the tables would be rebuilt from the same counts and every later pass would choose the same again.
                break;
            }

            // A table whose counts did not change gets the lengths it already has, so only the others are rebuilt.
            for (int t = 0; t < k; t++)
            {
                if (changed[t])
                {
                    _builder.Build(_freq.AsSpan(t * stride, alphabetSize), _lengths.AsSpan(t * stride, alphabetSize), maxLength);
                }
            }
        }

        // Drop tables no group uses (their lengths would only cost header bits); the format needs at least two.
        Span<int> remap = stackalloc int[MaxTables];
        remap.Fill(-1);
        int kept = 0;
        for (int g = 0; g < groups; g++)
        {
            if (remap[_selectors[g]] < 0)
            {
                remap[_selectors[g]] = 0;
            }
        }

        for (int t = 0; t < k; t++)
        {
            if (remap[t] >= 0)
            {
                remap[t] = kept;
                if (kept != t)
                {
                    _lengths.AsSpan(t * stride, alphabetSize).CopyTo(_lengths.AsSpan(kept * stride, alphabetSize));
                }

                kept++;
            }
        }

        for (int g = 0; g < groups; g++)
        {
            _selectors[g] = (byte)remap[_selectors[g]];
        }

        if (kept < 2)
        {
            _lengths.AsSpan(0, alphabetSize).CopyTo(_lengths.AsSpan(stride, alphabetSize));
            kept = 2;
        }

        _tableCount = kept;
        _selectorCount = groups;
        return kept;
    }
}
