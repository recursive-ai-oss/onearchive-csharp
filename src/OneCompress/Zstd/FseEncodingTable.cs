using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// An FSE (tANS) encoding table (RFC 8878 §4.1): built from a normalized distribution, it turns symbols into state
/// transitions and bits that the matching <see cref="FseDecodingTable"/> reverses. Also holds the helpers to normalize a
/// histogram, to write the table description and to price a histogram, which is how the block writer picks table modes.
/// </summary>
/// <remarks>
/// A table is not thread-safe and is reused by calling <see cref="Build"/> again. Nothing here allocates after construction.
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class FseEncodingTable
{
    /// <summary>The largest accuracy log the format allows for any table (RFC 8878 §4.1.1 limits sequence tables further).</summary>
    public const int MaxAccuracyLog = 9;

    /// <summary>The smallest accuracy log of a described table (RFC 8878 §4.1.1: the stored value is the log minus 5).</summary>
    public const int MinAccuracyLog = 5;

    private readonly ushort[] _stateTable = new ushort[1 << MaxAccuracyLog];
    private readonly SymbolTransform[] _transform = new SymbolTransform[256];
    private readonly short[] _normalized = new short[256];
    private readonly ushort[] _cumulative = new ushort[257];
    private readonly byte[] _spread = new byte[1 << MaxAccuracyLog];
    private readonly ushort[] _fill = new ushort[256];
    private int _accuracyLog;
    private int _symbolCount;

    // Cost() prices a histogram once per block, and a table that serves many blocks (a dictionary's, a predefined one) is priced
    // against many histograms, so the price of one occurrence of each symbol is worked out once, on first use. -1 marks a symbol
    // with no state.
    private int[]? _symbolCosts;

    /// <summary>Gets the accuracy log: the table has <c>1 &lt;&lt; AccuracyLog</c> states.</summary>
    public int AccuracyLog => _accuracyLog;

    /// <summary>Gets one more than the largest symbol the table was built for.</summary>
    public int SymbolCount => _symbolCount;

    /// <summary>Gets the normalized distribution the table was built from (<c>-1</c> marks a probability below one state).</summary>
    public ReadOnlySpan<short> NormalizedCounts => _normalized.AsSpan(0, _symbolCount);

    /// <summary>
    /// Picks the accuracy log for a table that will code <paramref name="sampleCount"/> symbols drawn from symbols up to
    /// <paramref name="maxSymbol"/>: large enough to tell the symbols apart, small enough that the description does not
    /// outweigh the precision gained.
    /// </summary>
    /// <param name="maxAccuracyLog">The format's limit for this kind of table.</param>
    /// <param name="sampleCount">How many symbols will be coded.</param>
    /// <param name="maxSymbol">The largest symbol in use.</param>
    /// <returns>An accuracy log from 5 to <paramref name="maxAccuracyLog"/>.</returns>
    public static int OptimalAccuracyLog(int maxAccuracyLog, int sampleCount, int maxSymbol)
    {
        int sampleLog = BitOperations.Log2((uint)Math.Max(sampleCount - 1, 1));
        int maxBitsFromSamples = sampleLog - 2;
        int minBitsFromSamples = sampleLog + 1;
        int minBitsFromSymbols = BitOperations.Log2((uint)Math.Max(maxSymbol, 1)) + 2;
        int minBits = Math.Min(minBitsFromSamples, minBitsFromSymbols);
        int result = maxAccuracyLog;
        if (maxBitsFromSamples < result)
        {
            result = maxBitsFromSamples;
        }

        if (minBits > result)
        {
            result = minBits;
        }

        return Math.Clamp(result, MinAccuracyLog, maxAccuracyLog);
    }

    /// <summary>
    /// Scales a histogram to probabilities that sum to <c>1 &lt;&lt; accuracyLog</c> (RFC 8878 §4.1.1). Every symbol that occurs
    /// gets at least one state; symbols rarer than one state are marked <c>-1</c>. The remaining states go where they save the
    /// most bits, ties broken by the lower symbol, using integer arithmetic only.
    /// </summary>
    /// <param name="normalized">Receives one value per symbol up to the last that occurs; at least <c>maxSymbol + 1</c> long.</param>
    /// <param name="accuracyLog">The accuracy log, 5 to 9.</param>
    /// <param name="histogram">How often each symbol occurs.</param>
    /// <param name="total">The sum of the histogram, which must be nonzero and at least two symbols must occur.</param>
    /// <param name="maxSymbol">The largest symbol that occurs.</param>
    /// <returns>The number of values written to <paramref name="normalized"/>, which is <c>maxSymbol + 1</c>.</returns>
    /// <exception cref="ArgumentException">Fewer than two symbols occur, or the table is too small for the symbols in use.</exception>
    public static int Normalize(Span<short> normalized, int accuracyLog, ReadOnlySpan<uint> histogram, uint total, int maxSymbol)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accuracyLog, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accuracyLog, MaxAccuracyLog);
        int tableSize = 1 << accuracyLog;
        uint lowThreshold = total >> accuracyLog;
        int used = 0;
        int assigned = 0;
        normalized[..(maxSymbol + 1)].Clear();
        for (int s = 0; s <= maxSymbol; s++)
        {
            uint c = histogram[s];
            if (c == 0)
            {
                continue;
            }

            used++;
            if (c <= lowThreshold)
            {
                normalized[s] = -1;
                assigned++;
            }
            else
            {
                // Round to the nearest state count, at least 1.
                ulong scaled = (((ulong)c * (ulong)tableSize * 2) + total) / (2 * (ulong)total);
                int n = (int)Math.Max(scaled, 1);
                normalized[s] = (short)n;
                assigned += n;
            }
        }

        if (used < 2 || used > tableSize)
        {
            throw new ArgumentException("A table needs at least two symbols and no more than it has states.", nameof(histogram));
        }

        // Fix the sum: take states from the symbols that lose the least, or give them to those that gain the most.
        while (assigned > tableSize)
        {
            int best = -1;
            long bestLoss = long.MaxValue;
            for (int s = 0; s <= maxSymbol; s++)
            {
                int n = normalized[s];
                if (n < 2)
                {
                    continue;
                }

                long loss = (long)histogram[s] * (ZstdCost.Log2((uint)n) - ZstdCost.Log2((uint)(n - 1)));
                if (loss < bestLoss)
                {
                    bestLoss = loss;
                    best = s;
                }
            }

            if (best < 0)
            {
                throw new ArgumentException("The table is too small for the symbols in use.", nameof(histogram));
            }

            normalized[best]--;
            assigned--;
        }

        while (assigned < tableSize)
        {
            int best = -1;
            long bestGain = -1;
            for (int s = 0; s <= maxSymbol; s++)
            {
                int n = normalized[s];
                if (n == 0)
                {
                    continue;
                }

                int cur = n < 0 ? 1 : n;
                long gain = (long)histogram[s] * (ZstdCost.Log2((uint)(cur + 1)) - ZstdCost.Log2((uint)cur));

                // A symbol marked -1 that gets a state becomes an ordinary one with 2 states.
                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = s;
                }
            }

            if (normalized[best] < 0)
            {
                normalized[best] = 2;
                assigned++;
            }
            else
            {
                normalized[best]++;
                assigned++;
            }
        }

        return maxSymbol + 1;
    }

    /// <summary>Builds the encoding table (RFC 8878 §4.1.1 spreads the states exactly as the decoding table does).</summary>
    /// <param name="normalizedCounts">The distribution, from <see cref="Normalize"/> or a described table.</param>
    /// <param name="accuracyLog">The accuracy log it was normalized for.</param>
    /// <exception cref="ArgumentException">The counts do not sum to <c>1 &lt;&lt; accuracyLog</c>.</exception>
    public void Build(ReadOnlySpan<short> normalizedCounts, int accuracyLog)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accuracyLog, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accuracyLog, MaxAccuracyLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalizedCounts.Length, 256);
        int tableSize = 1 << accuracyLog;
        int tableMask = tableSize - 1;
        int sum = 0;
        foreach (short c in normalizedCounts)
        {
            sum += c < 0 ? 1 : c;
        }

        if (sum != tableSize)
        {
            throw new ArgumentException("The probabilities must sum to the table size.", nameof(normalizedCounts));
        }

        // Scratch lives in the table so that a build does not zero a few hundred bytes of stack each time; every entry read below is
        // written first (cumulative up to the symbol count, spread for each of the tableSize states) or cleared (fill).
        ushort[] cumulative = _cumulative;
        byte[] spread = _spread;
        int highThreshold = tableSize - 1;
        int running = 0;
        for (int s = 0; s < normalizedCounts.Length; s++)
        {
            cumulative[s] = (ushort)running;
            if (normalizedCounts[s] == -1)
            {
                running++;
                spread[highThreshold--] = (byte)s;
            }
            else
            {
                running += normalizedCounts[s];
            }
        }

        cumulative[normalizedCounts.Length] = (ushort)running;

        int step = (tableSize >> 1) + (tableSize >> 3) + 3;
        int position = 0;
        for (int s = 0; s < normalizedCounts.Length; s++)
        {
            for (int i = 0; i < normalizedCounts[s]; i++)
            {
                spread[position] = (byte)s;
                do
                {
                    position = (position + step) & tableMask;
                }
                while (position > highThreshold);
            }
        }

        // For each symbol the states that decode it, in increasing order, are listed; the k-th is where the encoder lands
        // when the previous state, shifted down, is count + k.
        ushort[] fill = _fill;
        Array.Clear(fill, 0, normalizedCounts.Length);
        for (int u = 0; u < tableSize; u++)
        {
            int s = spread[u];
            _stateTable[cumulative[s] + fill[s]++] = (ushort)(tableSize + u);
        }

        int total = 0;
        for (int s = 0; s < normalizedCounts.Length; s++)
        {
            int c = normalizedCounts[s];
            if (c == 0)
            {
                _transform[s].NbBits = (uint)(((accuracyLog + 1) << 16) - tableSize);
                _transform[s].FindState = 0;
            }
            else if (c == 1 || c == -1)
            {
                _transform[s].NbBits = (uint)((accuracyLog << 16) - tableSize);
                _transform[s].FindState = total - 1;
                total++;
            }
            else
            {
                int maxBitsOut = accuracyLog - BitOperations.Log2((uint)(c - 1));
                int minStatePlus = c << maxBitsOut;
                _transform[s].NbBits = (uint)((maxBitsOut << 16) - minStatePlus);
                _transform[s].FindState = total - c;
                total += c;
            }
        }

        normalizedCounts.CopyTo(_normalized);
        _symbolCount = normalizedCounts.Length;
        _accuracyLog = accuracyLog;
        _symbolCosts = null;
    }

    /// <summary>Per symbol: how many bits a transition emits (in the high half, with a correction) and where its states start.</summary>
    internal struct SymbolTransform
    {
        public uint NbBits;
        public int FindState;
    }

    /// <summary>The next-state table, shared with the sequence loop of the block writer.</summary>
    internal ushort[] StateTable => _stateTable;

    /// <summary>The per-symbol transform table (256 entries), shared with the sequence loop of the block writer.</summary>
    internal SymbolTransform[] Transforms => _transform;

    /// <summary>The state to start from when the first symbol to encode is <paramref name="symbol"/>; it costs no bits.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal int InitialState(int symbol)
    {
        int c = _normalized[symbol];
        return _stateTable[_transform[symbol].FindState + (c < 1 ? 1 : c)];
    }

    /// <summary>
    /// Encodes <paramref name="symbol"/> from <paramref name="state"/> (a value from <c>1 &lt;&lt; AccuracyLog</c> up to twice that,
    /// minus one): returns the bits to store and their count through the out parameters, and the next state.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal int Encode(int state, int symbol, out uint bits, out int bitCount)
    {
        // Bounds: symbols are at most 255 (the delta arrays have 256 entries); a state in [1 << AccuracyLog, 2 << AccuracyLog)
        // shifted down by nb lands in [1 << AccuracyLog - ... ] so that, plus the symbol's delta, the index stays inside the
        // table (this is the property that makes the table a valid tANS encoder).
        uint nb = ((uint)state + Raw.Get(_transform, symbol).NbBits) >> 16;
        bitCount = (int)nb;
        bits = (uint)state & ((1u << (int)nb) - 1);
        return Raw.Get(_stateTable, (state >> (int)nb) + Raw.Get(_transform, symbol).FindState);
    }

    private int[] BuildSymbolCosts()
    {
        var costs = new int[_symbolCount];
        int baseCost = _accuracyLog * ZstdCost.Bit;
        for (int s = 0; s < costs.Length; s++)
        {
            int c = _normalized[s];
            costs[s] = c == 0 ? -1 : baseCost - ZstdCost.Log2((uint)(c < 0 ? 1 : c));
        }

        return costs;
    }

    /// <summary>Whether the table can code <paramref name="symbol"/> (it has at least one state).</summary>
    internal bool Contains(int symbol) => symbol < _symbolCount && _normalized[symbol] != 0;

    /// <summary>
    /// The same price as <see cref="Cost(ReadOnlySpan{uint}, int)"/>, visiting only the symbols named in <paramref name="present"/> (bit s
    /// set when symbol s occurs) instead of every counter up to the largest symbol. Small blocks have few distinct symbols.
    /// </summary>
    internal int CostOfPresent(uint[] histogram, ulong present)
    {
        int[] costs = _symbolCosts ??= BuildSymbolCosts();
        long cost = 0;
        while (present != 0)
        {
            int s = System.Numerics.BitOperations.TrailingZeroCount(present);
            present &= present - 1;
            if ((uint)s >= (uint)costs.Length || costs[s] < 0)
            {
                return int.MaxValue;
            }

            cost += (long)histogram[s] * costs[s];
        }

        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    /// <summary>
    /// Prices a histogram under this table in 1/256 bit units (the cost of the symbols only, not of any extra bits).
    /// </summary>
    /// <param name="histogram">How often each symbol occurs.</param>
    /// <param name="maxSymbol">The largest symbol that occurs.</param>
    /// <returns>The cost, or <see cref="int.MaxValue"/> if a symbol that occurs has no state in this table.</returns>
    public int Cost(ReadOnlySpan<uint> histogram, int maxSymbol)
    {
        int[] costs = _symbolCosts ??= BuildSymbolCosts();
        long cost = 0;
        int known = Math.Min(maxSymbol + 1, _symbolCount);
        for (int s = 0; s < known; s++)
        {
            uint count = histogram[s];
            int perSymbol = costs[s];
            if (perSymbol < 0)
            {
                if (count != 0)
                {
                    return int.MaxValue;
                }

                continue;
            }

            cost += (long)count * perSymbol;
        }

        for (int s = known; s <= maxSymbol; s++)
        {
            if (histogram[s] != 0)
            {
                return int.MaxValue;
            }
        }

        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    /// <summary>
    /// Counts the bits the encoder spends coding <paramref name="codes"/> with this table, exactly: the state machine is run
    /// the way the sequence encoder runs it (last symbol first), so unlike <see cref="Cost(ReadOnlySpan{uint}, int)"/> there is
    /// no rounding of probabilities.
    /// </summary>
    /// <param name="codes">The symbols in stream order; every one must have a state in this table.</param>
    /// <returns>The number of bits for the symbols after the first one coded, plus the final state's bits.</returns>
    public int ExactBits(ReadOnlySpan<byte> codes)
    {
        if (codes.IsEmpty)
        {
            return 0;
        }

        int state = InitialState(codes[^1]);
        long bits = _accuracyLog;
        for (int i = codes.Length - 2; i >= 0; i--)
        {
            uint nb = ((uint)state + _transform[codes[i]].NbBits) >> 16;
            bits += nb;
            state = _stateTable[(state >> (int)nb) + _transform[codes[i]].FindState];
        }

        return (int)bits;
    }

    private static FseEncodingTable? s_every;

    /// <summary>A table that has a state for each of the 256 symbols; it stands in for the unused lanes of <see cref="ExactBits(ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, FseEncodingTable[], Span{int})"/>.</summary>
    internal static FseEncodingTable Every
    {
        get
        {
            if (s_every is null)
            {
                var counts = new short[256];
                Array.Fill(counts, (short)2);
                var table = new FseEncodingTable();
                table.Build(counts, MaxAccuracyLog);
                s_every = table;
            }

            return s_every;
        }
    }

    /// <summary>
    /// <see cref="ExactBits(ReadOnlySpan{byte})"/> for up to six tables at once: lanes 0 and 1 code <paramref name="codes0"/>, 2 and 3
    /// code <paramref name="codes1"/>, 4 and 5 code <paramref name="codes2"/>. The state machines are independent, so running them side
    /// by side hides the latency of each one's chain of table reads. Every table must have a state for each symbol its codes use
    /// (<see cref="Every"/> fills unused lanes); the three code spans have the same, nonzero, length.
    /// </summary>
    internal static void ExactBits(ReadOnlySpan<byte> codes0, ReadOnlySpan<byte> codes1, ReadOnlySpan<byte> codes2, FseEncodingTable[] tables, Span<int> bits)
    {
        int n = codes0.Length;
        if (n == 0 || codes1.Length != n || codes2.Length != n || tables.Length < 6 || bits.Length < 6)
        {
            throw new ArgumentException("The code spans must have the same nonzero length, and there must be six tables and six results.");
        }

        FseEncodingTable t0 = tables[0], t1 = tables[1], t2 = tables[2], t3 = tables[3], t4 = tables[4], t5 = tables[5];
        int q0 = t0.InitialState(codes0[n - 1]);
        int q1 = t1.InitialState(codes0[n - 1]);
        int q2 = t2.InitialState(codes1[n - 1]);
        int q3 = t3.InitialState(codes1[n - 1]);
        int q4 = t4.InitialState(codes2[n - 1]);
        int q5 = t5.InitialState(codes2[n - 1]);
        long b0 = t0._accuracyLog, b1 = t1._accuracyLog, b2 = t2._accuracyLog, b3 = t3._accuracyLog, b4 = t4._accuracyLog, b5 = t5._accuracyLog;
        SymbolTransform[] f0 = t0._transform, f1 = t1._transform, f2 = t2._transform, f3 = t3._transform, f4 = t4._transform, f5 = t5._transform;
        ushort[] a0 = t0._stateTable, a1 = t1._stateTable, a2 = t2._stateTable, a3 = t3._stateTable, a4 = t4._stateTable, a5 = t5._stateTable;
        // Bounds: the codes are at most 255, so every transform read is inside its 256 entries; each state stays in
        // [1 << log, 2 << log) for symbols the table has (the same property Encode relies on), so the next-state read is inside the table.
        for (int i = n - 2; i >= 0; i--)
        {
            int c0 = Raw.Get(codes0, i), c1 = Raw.Get(codes1, i), c2 = Raw.Get(codes2, i);
            ref readonly SymbolTransform x0 = ref Raw.Ref(f0, c0);
            ref readonly SymbolTransform x1 = ref Raw.Ref(f1, c0);
            ref readonly SymbolTransform x2 = ref Raw.Ref(f2, c1);
            ref readonly SymbolTransform x3 = ref Raw.Ref(f3, c1);
            ref readonly SymbolTransform x4 = ref Raw.Ref(f4, c2);
            ref readonly SymbolTransform x5 = ref Raw.Ref(f5, c2);
            uint n0 = ((uint)q0 + x0.NbBits) >> 16, n1 = ((uint)q1 + x1.NbBits) >> 16, n2 = ((uint)q2 + x2.NbBits) >> 16;
            uint n3 = ((uint)q3 + x3.NbBits) >> 16, n4 = ((uint)q4 + x4.NbBits) >> 16, n5 = ((uint)q5 + x5.NbBits) >> 16;
            b0 += n0; b1 += n1; b2 += n2; b3 += n3; b4 += n4; b5 += n5;
            q0 = Raw.Get(a0, (q0 >> (int)n0) + x0.FindState);
            q1 = Raw.Get(a1, (q1 >> (int)n1) + x1.FindState);
            q2 = Raw.Get(a2, (q2 >> (int)n2) + x2.FindState);
            q3 = Raw.Get(a3, (q3 >> (int)n3) + x3.FindState);
            q4 = Raw.Get(a4, (q4 >> (int)n4) + x4.FindState);
            q5 = Raw.Get(a5, (q5 >> (int)n5) + x5.FindState);
        }

        bits[0] = (int)b0; bits[1] = (int)b1; bits[2] = (int)b2; bits[3] = (int)b3; bits[4] = (int)b4; bits[5] = (int)b5;
    }

    /// <summary>
    /// <see cref="ExactBits(ReadOnlySpan{byte})"/> for three tables at once, each coding its own code sequence (all of the same,
    /// nonzero length); see the six-table overload for why.
    /// </summary>
    internal static void ExactBits(ReadOnlySpan<byte> codes0, ReadOnlySpan<byte> codes1, ReadOnlySpan<byte> codes2, FseEncodingTable t0, FseEncodingTable t1, FseEncodingTable t2, out int bits0, out int bits1, out int bits2)
    {
        int n = codes0.Length;
        if (n == 0 || codes1.Length != n || codes2.Length != n)
        {
            throw new ArgumentException("The code spans must have the same nonzero length.");
        }

        int q0 = t0.InitialState(codes0[n - 1]);
        int q1 = t1.InitialState(codes1[n - 1]);
        int q2 = t2.InitialState(codes2[n - 1]);
        long b0 = t0._accuracyLog, b1 = t1._accuracyLog, b2 = t2._accuracyLog;
        SymbolTransform[] f0 = t0._transform, f1 = t1._transform, f2 = t2._transform;
        ushort[] a0 = t0._stateTable, a1 = t1._stateTable, a2 = t2._stateTable;
        // Bounds: as in the six-table overload.
        for (int i = n - 2; i >= 0; i--)
        {
            ref readonly SymbolTransform x0 = ref Raw.Ref(f0, Raw.Get(codes0, i));
            ref readonly SymbolTransform x1 = ref Raw.Ref(f1, Raw.Get(codes1, i));
            ref readonly SymbolTransform x2 = ref Raw.Ref(f2, Raw.Get(codes2, i));
            uint n0 = ((uint)q0 + x0.NbBits) >> 16, n1 = ((uint)q1 + x1.NbBits) >> 16, n2 = ((uint)q2 + x2.NbBits) >> 16;
            b0 += n0; b1 += n1; b2 += n2;
            q0 = Raw.Get(a0, (q0 >> (int)n0) + x0.FindState);
            q1 = Raw.Get(a1, (q1 >> (int)n1) + x1.FindState);
            q2 = Raw.Get(a2, (q2 >> (int)n2) + x2.FindState);
        }

        bits0 = (int)b0;
        bits1 = (int)b1;
        bits2 = (int)b2;
    }

    /// <summary>Prices a histogram under a distribution that is not built as a table; as the overload that takes a built table.</summary>
    public static int Cost(ReadOnlySpan<short> normalizedCounts, int accuracyLog, ReadOnlySpan<uint> histogram, int maxSymbol)
    {
        long cost = 0;
        int baseCost = accuracyLog * ZstdCost.Bit;
        for (int s = 0; s <= maxSymbol; s++)
        {
            uint count = histogram[s];
            if (count == 0)
            {
                continue;
            }

            if (s >= normalizedCounts.Length || normalizedCounts[s] == 0)
            {
                return int.MaxValue;
            }

            int c = normalizedCounts[s] < 0 ? 1 : normalizedCounts[s];
            cost += (long)count * (baseCost - ZstdCost.Log2((uint)c));
        }

        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    /// <summary>
    /// Writes the table description of RFC 8878 §4.1.1: the accuracy log, then each probability in the fewest bits that
    /// can still tell the remaining possibilities apart, with runs of zero probabilities coded by repeat flags.
    /// </summary>
    /// <param name="destination">Receives the description; at least 40 bytes plus 1 per symbol is always enough.</param>
    /// <param name="normalizedCounts">The distribution; trailing zeros are not written.</param>
    /// <param name="accuracyLog">The accuracy log, 5 to 9.</param>
    /// <returns>The number of bytes written.</returns>
    public static int WriteDescription(Span<byte> destination, ReadOnlySpan<short> normalizedCounts, int accuracyLog)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(accuracyLog, MinAccuracyLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accuracyLog, MaxAccuracyLog);
        int symbolCount = normalizedCounts.Length;
        while (symbolCount > 0 && normalizedCounts[symbolCount - 1] == 0)
        {
            symbolCount--;
        }

        var writer = new ZstdBitWriter(destination);
        writer.Add((uint)(accuracyLog - MinAccuracyLog), 4);
        int remaining = (1 << accuracyLog) + 1;
        int threshold = 1 << accuracyLog;
        int nbBits = accuracyLog + 1;
        int symbol = 0;
        bool previousWasZero = false;
        while (remaining > 1 && symbol < symbolCount)
        {
            if (previousWasZero)
            {
                int end = symbol;
                while (end < symbolCount && normalizedCounts[end] == 0)
                {
                    end++;
                }

                int run = end - symbol;
                while (run >= 3)
                {
                    writer.Add(3, 2);
                    run -= 3;
                }

                writer.Add((uint)run, 2);
                writer.Flush();
                symbol = end;
                previousWasZero = false;
                if (symbol >= symbolCount)
                {
                    break;
                }
            }

            int count = normalizedCounts[symbol++];
            int max = ((2 * threshold) - 1) - remaining;
            remaining -= count < 0 ? 1 : count;
            int value = count + 1;
            if (value < max)
            {
                writer.Add((uint)value, nbBits - 1);
            }
            else
            {
                if (value >= threshold)
                {
                    value += max;
                }

                writer.Add((uint)value, nbBits);
            }

            writer.Flush();
            previousWasZero = count == 0;
            while (remaining < threshold)
            {
                nbBits--;
                threshold >>= 1;
            }
        }

        return writer.CloseForward();
    }
}
