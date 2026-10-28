using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A Huffman code for Zstandard literals (RFC 8878 §4.2): a canonical, length-limited prefix code over bytes, with the
/// tree description writer (direct 4-bit weights or FSE-compressed weights) and the one- and four-stream literal encoders.
/// </summary>
/// <remarks>
/// Codes are assigned the way the format's decoder expects: by weight (rarer symbols, the longer codes, come first) and within
/// a weight by symbol value. The table is not thread-safe and is reused by calling a build method again.
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
// Building a tree and describing it use a few KiB of stack scratch, and zeroing it cost more than the work on a small block. Every
// stackalloc below is written before it is read, or cleared explicitly where the code counts into it.
[System.Runtime.CompilerServices.SkipLocalsInit]
public sealed class HuffmanEncodingTable
{
    /// <summary>The longest code the format allows (RFC 8878 §4.2.1).</summary>
    public const int MaxBits = ZstdFormat.HuffmanMaxBits;

    private readonly byte[] _length = new byte[256];
    private readonly ushort[] _code = new ushort[256];
    private int _symbolCount;
    private int _maxBits;
    private FseEncodingTable? _weightTable;

    /// <summary>Gets one more than the largest symbol with a code, or 0 if the table is empty.</summary>
    public int SymbolCount => _symbolCount;

    /// <summary>Gets the length in bits of the longest code.</summary>
    public int MaxCodeLength => _maxBits;

    /// <summary>Gets the code length in bits of each symbol, 0 for symbols without a code.</summary>
    public ReadOnlySpan<byte> CodeLengths => _length;

    // Counts up to this are sorted by counting rather than by comparison: small blocks, where the sort would be most of the work.
    private const int SmallCountLimit = 1024;

    // Orders keys (count << 8 | symbol) ascending, as Sort would, by bucketing on the count: symbols come in ascending order,
    // so equal counts stay ordered by symbol.
    private static void SortByCounting(Span<ulong> keys, int largest)
    {
        Span<ushort> start = stackalloc ushort[SmallCountLimit + 1];
        start[..(largest + 2)].Clear();
        for (int i = 0; i < keys.Length; i++)
        {
            start[(int)(keys[i] >> 8) + 1]++;
        }

        for (int c = 1; c <= largest + 1; c++)
        {
            start[c] += start[c - 1];
        }

        Span<ulong> sorted = stackalloc ulong[256];
        for (int i = 0; i < keys.Length; i++)
        {
            sorted[start[(int)(keys[i] >> 8)]++] = keys[i];
        }

        sorted[..keys.Length].CopyTo(keys);
    }

    /// <summary>
    /// Builds the code for a histogram of bytes: a Huffman tree whose depth is limited to <paramref name="maxBits"/> by
    /// lengthening the cheapest codes until the lengths satisfy the Kraft equality.
    /// </summary>
    /// <param name="histogram">How often each byte value occurs (at least <c>maxSymbol + 1</c> entries).</param>
    /// <param name="maxSymbol">The largest byte value that occurs.</param>
    /// <param name="maxBits">The longest code to allow, 1 to 11 (and enough for the number of symbols in use).</param>
    /// <returns>False if fewer than two byte values occur (a Huffman code needs two); true otherwise.</returns>
    public bool Build(ReadOnlySpan<uint> histogram, int maxSymbol, int maxBits = MaxBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBits, MaxBits);
        Span<byte> symbols = stackalloc byte[257];
        int n = ListPresent(histogram, maxSymbol, symbols);
        if (n < 2 || n > (1 << maxBits))
        {
            return false;
        }

        // The work from here on is proportional to the symbols that occur, not to the 256 possible ones.
        Span<ulong> keys = stackalloc ulong[256];
        uint largest = 0;
        for (int i = 0; i < n; i++)
        {
            uint count = histogram[symbols[i]];
            keys[i] = ((ulong)count << 8) | symbols[i];
            largest = Math.Max(largest, count);
        }

        if (largest < SmallCountLimit)
        {
            SortByCounting(keys[..n], (int)largest);
        }
        else
        {
            keys[..n].Sort();
        }

        Span<uint> weight = stackalloc uint[512];
        Span<ushort> parent = stackalloc ushort[512];
        Span<byte> depth = stackalloc byte[512];
        for (int i = 0; i < n; i++)
        {
            weight[i] = (uint)(keys[i] >> 8);
        }

        int leaf = 0;
        int internalNode = n;
        for (int k = 0; k < n - 1; k++)
        {
            int a = PickSmallest(weight, ref leaf, ref internalNode, n, n + k);
            int b = PickSmallest(weight, ref leaf, ref internalNode, n, n + k);
            weight[n + k] = weight[a] + weight[b];
            parent[a] = (ushort)(n + k);
            parent[b] = (ushort)(n + k);
        }

        int root = (2 * n) - 2;
        depth[root] = 0;
        int longest = 0;
        for (int i = root - 1; i >= 0; i--)
        {
            depth[i] = (byte)(depth[parent[i]] + 1);
        }

        for (int i = 0; i < n; i++)
        {
            longest = Math.Max(longest, depth[i]);
        }

        // Code lengths by symbol; only the entries of symbols that occur are written, and only those are read.
        Span<byte> lengths = stackalloc byte[256];
        for (int i = 0; i < n; i++)
        {
            lengths[(int)(keys[i] & 255)] = depth[i];
        }

        if (longest > maxBits)
        {
            // The rare case: LimitLengths walks all symbols, so it gets zeros for the ones that do not occur.
            Span<byte> full = stackalloc byte[256];
            full[..(maxSymbol + 1)].Clear();
            for (int i = 0; i < n; i++)
            {
                full[symbols[i]] = lengths[symbols[i]];
            }

            LimitLengths(full, histogram, maxSymbol, maxBits);
            for (int i = 0; i < n; i++)
            {
                lengths[symbols[i]] = full[symbols[i]];
            }
        }

        AssignCodes(symbols[..n], lengths);
        return true;
    }

    // Lists, in ascending order, the symbols up to maxSymbol whose counter is not zero; returns how many. Four counters are tested
    // at a time with vectors where the hardware has them; the list is the same either way.
    private static int ListPresent(ReadOnlySpan<uint> histogram, int maxSymbol, Span<byte> symbols)
    {
        int n = 0;
        int s = 0;
        int end = maxSymbol + 1;
        if (System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated)
        {
            for (; s + 4 <= end; s += 4)
            {
                var v = System.Runtime.Intrinsics.Vector128.Create(histogram.Slice(s, 4));
                uint zero = System.Runtime.Intrinsics.Vector128.ExtractMostSignificantBits(System.Runtime.Intrinsics.Vector128.Equals(v, System.Runtime.Intrinsics.Vector128<uint>.Zero));
                uint present = ~zero & 0xF;
                while (present != 0)
                {
                    symbols[n++] = (byte)(s + BitOperations.TrailingZeroCount(present));
                    present &= present - 1;
                }
            }
        }

        for (; s < end; s++)
        {
            symbols[n] = (byte)s;
            n += histogram[s] != 0 ? 1 : 0;
        }

        return n;
    }

    // Hands out codes to the symbols that occur (ascending), whose code lengths are lengths[symbol]. Same assignment as the general
    // routine below: rows of the decoder's table go from the lowest weight up, and equal weights in symbol order.
    private void AssignCodes(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> lengths)
    {
        int maxBits = 0;
        for (int i = 0; i < symbols.Length; i++)
        {
            maxBits = Math.Max(maxBits, lengths[symbols[i]]);
        }

        Span<int> rankCount = stackalloc int[MaxBits + 2];
        rankCount.Clear();
        for (int i = 0; i < symbols.Length; i++)
        {
            rankCount[maxBits + 1 - lengths[symbols[i]]]++;
        }

        Span<int> rankStart = stackalloc int[MaxBits + 2];
        int next = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            rankStart[w] = next;
            next += rankCount[w] << (w - 1);
        }

        Array.Clear(_length);
        for (int i = 0; i < symbols.Length; i++)
        {
            int symbol = symbols[i];
            int len = lengths[symbol];
            int w = maxBits + 1 - len;
            _code[symbol] = (ushort)(rankStart[w] >> (w - 1));
            rankStart[w] += 1 << (w - 1);
            _length[symbol] = (byte)len;
        }

        _symbolCount = symbols[^1] + 1;
        _maxBits = maxBits;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int PickSmallest(Span<uint> weight, ref int leaf, ref int internalNode, int leafCount, int internalEnd)
    {
        bool haveLeaf = leaf < leafCount;
        bool haveInternal = internalNode < internalEnd;
        if (haveLeaf && (!haveInternal || weight[leaf] <= weight[internalNode]))
        {
            return leaf++;
        }

        return internalNode++;
    }

    // Lengths above the limit are clamped, which over-subscribes the code space; the cheapest codes are then lengthened
    // until the space fits, and any space left over is given back to the most frequent symbols.
    private static void LimitLengths(Span<byte> lengths, ReadOnlySpan<uint> histogram, int maxSymbol, int maxBits)
    {
        long full = 1L << maxBits;
        long kraft = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (lengths[s] != 0)
            {
                if (lengths[s] > maxBits)
                {
                    lengths[s] = (byte)maxBits;
                }

                kraft += 1L << (maxBits - lengths[s]);
            }
        }

        while (kraft > full)
        {
            // Lengthen the rarest symbol among those with the longest code that can still grow.
            int best = -1;
            for (int s = 0; s <= maxSymbol; s++)
            {
                byte len = lengths[s];
                if (len == 0 || len >= maxBits)
                {
                    continue;
                }

                if (best < 0 || len > lengths[best] || (len == lengths[best] && histogram[s] < histogram[best]))
                {
                    best = s;
                }
            }

            kraft -= 1L << (maxBits - lengths[best] - 1);
            lengths[best]++;
        }

        while (kraft < full)
        {
            long gap = full - kraft;
            int best = -1;
            for (int s = 0; s <= maxSymbol; s++)
            {
                byte len = lengths[s];
                if (len <= 1 || (1L << (maxBits - len)) > gap)
                {
                    continue;
                }

                if (best < 0 || histogram[s] > histogram[best] || (histogram[s] == histogram[best] && len > lengths[best]))
                {
                    best = s;
                }
            }

            kraft += 1L << (maxBits - lengths[best]);
            lengths[best]--;
        }
    }

    /// <summary>
    /// Builds the code from the explicit weights of a described tree, as <see cref="HuffmanDecodingTable.BuildFromWeights"/> does
    /// (the last symbol's weight is implied). Used for the tree carried by a dictionary.
    /// </summary>
    /// <param name="weights">The weights of symbols 0 up to the last symbol, exclusive.</param>
    /// <exception cref="CompressionFormatException">The weights are not a valid tree.</exception>
    internal void BuildFromWeights(ReadOnlySpan<byte> weights)
    {
        uint total = 0;
        foreach (byte w in weights)
        {
            if (w > MaxBits)
            {
                ZstdFormat.ThrowCorrupt(0, "Huffman weights of at most 11");
            }

            if (w != 0)
            {
                total += 1u << (w - 1);
            }
        }

        if (total == 0)
        {
            ZstdFormat.ThrowCorrupt(0, "a nonzero Huffman weight");
        }

        int maxBits = BitOperations.Log2(total) + 1;
        uint rest = (1u << maxBits) - total;
        if (maxBits > MaxBits || !BitOperations.IsPow2(rest))
        {
            ZstdFormat.ThrowCorrupt(0, "Huffman weights that complete to a power of two");
        }

        Span<byte> lengths = stackalloc byte[256];
        for (int s = 0; s < weights.Length; s++)
        {
            lengths[s] = weights[s] == 0 ? (byte)0 : (byte)(maxBits + 1 - weights[s]);
        }

        lengths[weights.Length] = (byte)(maxBits + 1 - (BitOperations.Log2(rest) + 1));
        AssignCodes(lengths, weights.Length + 1);
    }

    private void AssignCodes(ReadOnlySpan<byte> lengths, int symbolCount)
    {
        int maxBits = 0;
        int last = 0;
        Span<int> rankCount = stackalloc int[MaxBits + 2];
        rankCount.Clear();
        for (int s = 0; s < symbolCount; s++)
        {
            if (lengths[s] != 0)
            {
                maxBits = Math.Max(maxBits, lengths[s]);
                last = s;
            }
        }

        for (int s = 0; s < symbolCount; s++)
        {
            if (lengths[s] != 0)
            {
                rankCount[maxBits + 1 - lengths[s]]++;
            }
        }

        // Rows of the decoder's table are handed out from the lowest weight up; a symbol of weight w owns 2^(w-1) rows,
        // and its code is its first row shifted down to the code's length.
        Span<int> rankStart = stackalloc int[MaxBits + 2];
        int next = 0;
        for (int w = 1; w <= maxBits; w++)
        {
            rankStart[w] = next;
            next += rankCount[w] << (w - 1);
        }

        Array.Clear(_length);
        for (int s = 0; s < symbolCount; s++)
        {
            int len = lengths[s];
            if (len == 0)
            {
                continue;
            }

            int w = maxBits + 1 - len;
            _code[s] = (ushort)(rankStart[w] >> (w - 1));
            rankStart[w] += 1 << (w - 1);
            _length[s] = (byte)len;
        }

        _symbolCount = last + 1;
        _maxBits = maxBits;
    }

    /// <summary>Whether every byte value that occurs has a code in this table (so the table can be reused for them).</summary>
    public bool CanEncode(ReadOnlySpan<uint> histogram, int maxSymbol)
    {
        if (maxSymbol >= _symbolCount && maxSymbol > 0)
        {
            for (int s = _symbolCount; s <= maxSymbol; s++)
            {
                if (histogram[s] != 0)
                {
                    return false;
                }
            }
        }

        int limit = Math.Min(maxSymbol + 1, _symbolCount);
        for (int s = 0; s < limit; s++)
        {
            if (histogram[s] != 0 && _length[s] == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Adds up the bits of the literals of <paramref name="histogram"/> under this tree, in one pass, if every symbol that occurs has a code.</summary>
    /// <param name="histogram">How often each symbol occurs.</param>
    /// <param name="maxSymbol">The largest symbol that occurs.</param>
    /// <param name="bits">The size of the coded symbols in bits, when the tree can code them.</param>
    /// <returns>True if every symbol that occurs has a code.</returns>
    internal bool TryEncodedBits(ReadOnlySpan<uint> histogram, int maxSymbol, out long bits)
    {
        bits = 0;
        if (maxSymbol >= _symbolCount)
        {
            for (int s = _symbolCount; s <= maxSymbol; s++)
            {
                if (histogram[s] != 0)
                {
                    return false;
                }
            }

            maxSymbol = _symbolCount - 1;
        }

        long total = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            uint count = histogram[s];
            if (count != 0)
            {
                int length = _length[s];
                if (length == 0)
                {
                    return false;
                }

                total += (long)count * length;
            }
        }

        bits = total;
        return true;
    }

    /// <summary>The same as the overload over all counters, for a caller that already lists the symbols that occur (each has a nonzero count).</summary>
    internal bool TryEncodedBits(ReadOnlySpan<uint> histogram, ReadOnlySpan<byte> symbols, out long bits)
    {
        bits = 0;
        long total = 0;
        foreach (byte s in symbols)
        {
            int length = s < _symbolCount ? _length[s] : 0;
            if (length == 0)
            {
                return false;
            }

            total += (long)histogram[s] * length;
        }

        bits = total;
        return true;
    }

    /// <summary>Gets the size in bits of the literals coded with this table, in one stream or in four (add up to 7 bits of padding per stream).</summary>
    public long EncodedBits(ReadOnlySpan<uint> histogram, int maxSymbol)
    {
        long bits = 0;
        int limit = Math.Min(maxSymbol + 1, _symbolCount);
        for (int s = 0; s < limit; s++)
        {
            bits += (long)histogram[s] * _length[s];
        }

        return bits;
    }

    /// <summary>
    /// Writes the tree description (RFC 8878 §4.2.1): the weights of symbols 0 up to the last symbol, exclusive, either as 4-bit
    /// values or compressed with FSE, whichever is smaller. A compressed form is decoded again to make sure it reads back.
    /// </summary>
    /// <param name="destination">Receives the description, at most 130 bytes.</param>
    /// <returns>The number of bytes written, or -1 if the tree cannot be described (a very wide alphabet whose weights do not compress).</returns>
    public int WriteDescription(Span<byte> destination)
    {
        int n = _symbolCount - 1;
        Span<byte> weights = stackalloc byte[256];
        Span<uint> weightHistogram = stackalloc uint[MaxBits + 1];
        weightHistogram.Clear();
        int maxWeight = 0;
        for (int s = 0; s < n; s++)
        {
            int w = _length[s] == 0 ? 0 : _maxBits + 1 - _length[s];
            weights[s] = (byte)w;
            weightHistogram[w]++;
            maxWeight = Math.Max(maxWeight, w);
        }

        int directSize = n <= 128 ? 1 + ((n + 1) / 2) : int.MaxValue;
        int distinct = 0;
        foreach (uint c in weightHistogram)
        {
            if (c != 0)
            {
                distinct++;
            }
        }

        if (n >= 2 && distinct >= 2)
        {
            Span<byte> scratch = stackalloc byte[160];
            int size = TryCompressWeights(scratch, weights[..n], weightHistogram, maxWeight);
            if (size > 0 && size < directSize)
            {
                scratch[..size].CopyTo(destination);
                return size;
            }
        }

        if (directSize == int.MaxValue)
        {
            return -1;
        }

        destination[0] = (byte)(127 + n);
        destination[1..directSize].Clear();
        for (int i = 0; i < n; i += 2)
        {
            int packed = weights[i] << 4;
            if (i + 1 < n)
            {
                packed |= weights[i + 1];
            }

            destination[1 + (i / 2)] = (byte)packed;
        }

        return directSize;
    }

    // RFC 8878 §4.2.1.2: the weights are coded with FSE using two interleaved states, even-numbered weights on the first state.
    private int TryCompressWeights(Span<byte> destination, ReadOnlySpan<byte> weights, ReadOnlySpan<uint> histogram, int maxWeight)
    {
        int n = weights.Length;
        FseEncodingTable table = _weightTable ??= new FseEncodingTable();
        Span<short> normalized = stackalloc short[MaxBits + 1];
        normalized.Clear();
        int accuracyLog = FseEncodingTable.OptimalAccuracyLog(ZstdFormat.HuffmanWeightMaxAccuracyLog, n, maxWeight);
        int distinct = 0;
        foreach (uint c in histogram)
        {
            if (c != 0)
            {
                distinct++;
            }
        }

        if (distinct > (1 << accuracyLog))
        {
            return 0;
        }

        FseEncodingTable.Normalize(normalized, accuracyLog, histogram, (uint)n, maxWeight);
        table.Build(normalized[..(maxWeight + 1)], accuracyLog);
        Span<byte> body = stackalloc byte[160];
        int descriptionSize = FseEncodingTable.WriteDescription(body, normalized[..(maxWeight + 1)], accuracyLog);
        var writer = new ZstdBitWriter(body[descriptionSize..]);

        int state1;
        int state2;
        if ((n & 1) == 1)
        {
            state1 = table.InitialState(weights[n - 1]);
            state2 = table.InitialState(weights[n - 2]);
        }
        else
        {
            state2 = table.InitialState(weights[n - 1]);
            state1 = table.InitialState(weights[n - 2]);
        }

        for (int i = n - 3; i >= 0; i--)
        {
            if ((i & 1) == 0)
            {
                state1 = table.Encode(state1, weights[i], out uint bits, out int count);
                writer.Add(bits, count);
            }
            else
            {
                state2 = table.Encode(state2, weights[i], out uint bits, out int count);
                writer.Add(bits, count);
            }

            // A weight takes at most 6 bits, so four of them and the seven leftover bits fit the container between flushes.
            if ((i & 3) == 0)
            {
                writer.Flush();
            }
        }

        int tableSize = 1 << accuracyLog;
        writer.Add((uint)(state2 - tableSize), accuracyLog);
        writer.Flush();
        writer.Add((uint)(state1 - tableSize), accuracyLog);
        int streamSize = writer.CloseBackward();
        int total = descriptionSize + streamSize;
        if (total >= 128)
        {
            return 0;
        }

        destination[0] = (byte)total;
        body[..total].CopyTo(destination[1..]);

        // RFC 8878 4.2.1.2: the decoder finds the end of the weights by an update that needs more bits than remain. The last two
        // symbols are coded as initial states, which emit nothing, so the update after the second to last symbol always overruns: a
        // state that starts a symbol with fewer than all of the table's states (two or more distinct weights guarantee that) reads at
        // least one bit. The description therefore decodes to exactly n weights; a test decodes thousands of random trees to confirm.
        return 1 + total;
    }

    /// <summary>
    /// Encodes literals as one backward bitstream (RFC 8878 §4.2.2).
    /// </summary>
    /// <param name="source">The bytes to code; every one must have a code.</param>
    /// <param name="destination">Receives the stream; it needs <c>source.Length * MaxCodeLength / 8 + 16</c> bytes.</param>
    /// <returns>The stream's length in bytes.</returns>
    public int Compress1(ReadOnlySpan<byte> source, Span<byte> destination) => EncodeStream(source, destination);

    /// <summary>
    /// Encodes literals as the four streams of RFC 8878 §4.2.2: a 6-byte table of the first three streams' sizes, then the
    /// streams. The input is cut into four parts of <c>(length + 3) / 4</c> bytes, the last taking what is left.
    /// </summary>
    /// <param name="source">The bytes to code; at least 4 of them, every one with a code.</param>
    /// <param name="destination">Receives the result; it needs <c>source.Length * MaxCodeLength / 8 + 40</c> bytes.</param>
    /// <returns>The length in bytes, or -1 if a stream is too large for its 16-bit size.</returns>
    public int Compress4(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int segment = (source.Length + 3) / 4;
        int position = 6;
        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<byte> part = i < 3 ? source.Slice(i * segment, segment) : source[(3 * segment)..];
            int size = EncodeStream(part, destination[position..]);
            if (i < 3)
            {
                if (size > ushort.MaxValue)
                {
                    return -1;
                }

                BinaryPrimitives.WriteUInt16LittleEndian(destination[(2 * i)..], (ushort)size);
            }

            position += size;
        }

        return position;
    }

    // One backward stream (RFC 8878 §4.2.2), the symbols last to first, then the end marker. The bit container lives in locals so
    // that the four adds of a round stay in registers.
    private int EncodeStream(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        ushort[] code = _code;
        byte[] length = _length;
        ulong container = 0;
        int bitCount = 0;
        int position = 0;
        int i = source.Length;
        while (i >= 4)
        {
            // Bounds: 4 <= i <= source.Length, so the four reads are inside the source; a symbol is a byte and the tables have 256 entries.
            int a = Raw.Get(source, i - 1);
            int b = Raw.Get(source, i - 2);
            int c = Raw.Get(source, i - 3);
            int d = Raw.Get(source, i - 4);
            container |= (ulong)Raw.Get(code, a) << bitCount;
            bitCount += Raw.Get(length, a);
            container |= (ulong)Raw.Get(code, b) << bitCount;
            bitCount += Raw.Get(length, b);
            container |= (ulong)Raw.Get(code, c) << bitCount;
            bitCount += Raw.Get(length, c);
            container |= (ulong)Raw.Get(code, d) << bitCount;
            bitCount += Raw.Get(length, d);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[position..], container);
            int bytes = bitCount >> 3;
            position += bytes;
            container >>= bytes << 3;
            bitCount &= 7;
            i -= 4;
        }

        while (i > 0)
        {
            int a = source[--i];
            container |= (ulong)code[a] << bitCount;
            bitCount += length[a];
        }

        // The end marker, then the last whole bytes and the padding.
        container |= 1UL << bitCount;
        bitCount++;
        BinaryPrimitives.WriteUInt64LittleEndian(destination[position..], container);
        position += bitCount >> 3;
        if ((bitCount & 7) != 0)
        {
            position++;
        }

        return position;
    }
}
