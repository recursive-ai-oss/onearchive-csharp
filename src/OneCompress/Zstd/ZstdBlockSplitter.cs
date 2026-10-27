using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Decides where a block of sequences should be cut into several blocks. Every block carries its own literal and sequence
/// tables, so cutting where the statistics of the data change pays for the extra headers with better-fitting codes.
/// </summary>
/// <remarks>
/// The sequences are grouped into chunks of about <see cref="ChunkBytes"/> input bytes. The cost of coding a run of chunks as one
/// block is estimated from the entropy of its literals, literal length codes, match length codes and offset codes, plus the
/// extra bits those codes carry, plus a fixed allowance for the headers and table descriptions that grows with the number of
/// distinct symbols. A shortest-path search over the chain boundaries then picks the cut points of least total cost. The estimate is integer arithmetic
/// (<see cref="ZstdCost"/>), so the cuts depend on the input and the parameters only.
/// </remarks>
internal sealed class ZstdBlockSplitter
{
    /// <summary>The most blocks one block of sequences is cut into.</summary>
    public const int MaxSegments = 32;

    private const int ChunkBytes = 4096;
    private const int MaxChunks = (ZstdFormat.BlockSizeMax / ChunkBytes) + 2;
    private const int Literals = 256;
    private const int LiteralLengths = 36;
    private const int MatchLengths = 53;
    private const int Offsets = 32;
    private const int Stride = Literals + LiteralLengths + MatchLengths + Offsets;

    // Header and table allowance for one block, in 1/256 bit: a fixed part plus a part per distinct symbol.
    private const int FixedBytes = 12;
    
    private readonly int[] _prefix = new int[(MaxChunks + 1) * Stride];
    private readonly long[] _extraBits = new long[MaxChunks + 1];
    private readonly int[] _chunkSequence = new int[MaxChunks + 1];
    private readonly int[] _chunkLiteral = new int[MaxChunks + 1];
    private readonly int[] _chunkByte = new int[MaxChunks + 1];
    private readonly uint[] _chunkRep1 = new uint[MaxChunks + 1];
    private readonly uint[] _chunkRep2 = new uint[MaxChunks + 1];
    private readonly uint[] _chunkRep3 = new uint[MaxChunks + 1];
    private readonly long[] _best = new long[MaxChunks + 1];
    private readonly int[] _from = new int[MaxChunks + 1];
    private readonly int[] _count = new int[MaxChunks + 1];
    private uint[] _offsetValues = new uint[256];

    /// <summary>The number of segments found by the last <see cref="Plan"/>.</summary>
    public int Segments { get; private set; }

    /// <summary>The index of the first sequence of each segment.</summary>
    public int[] SequenceStart { get; } = new int[MaxSegments + 1];

    /// <summary>The index of the first literal of each segment.</summary>
    public int[] LiteralStart { get; } = new int[MaxSegments + 1];

    /// <summary>The offset of the first input byte of each segment within the block.</summary>
    public int[] ByteStart { get; } = new int[MaxSegments + 1];

    /// <summary>The repeat offsets in force at the start of each segment.</summary>
    public ZstdRepeatOffsets[] Reps { get; } = new ZstdRepeatOffsets[MaxSegments + 1];

    /// <summary>The offset value (repeat code or offset + 3) the sequence <paramref name="index"/> is coded with.</summary>
    public uint OffsetValue(int index) => _offsetValues[index];

    /// <summary>Plans the cuts of the block held in <paramref name="store"/>.</summary>
    /// <param name="store">The block's literals and sequences.</param>
    /// <param name="start">The repeat offsets in force at the start of the block.</param>
    /// <returns>The number of segments; 1 means the block is written whole.</returns>
    public int Plan(ZstdSequenceStore store, ZstdRepeatOffsets start)
    {
        ReadOnlySpan<ZstdSequence> sequences = store.Sequences;
        ReadOnlySpan<byte> literals = store.Literals;
        Segments = 1;
        if (sequences.Length < 2)
        {
            return 1;
        }

        if (_offsetValues.Length < sequences.Length)
        {
            _offsetValues = new uint[Math.Max(sequences.Length, _offsetValues.Length * 2)];
        }

        // Chunk boundaries and the histograms at each one, as running totals.
        ZstdRepeatOffsets reps = start;
        int chunks = 0;
        int litPos = 0;
        int bytePos = 0;
        int chunkStartByte = 0;
        Array.Clear(_prefix, 0, Stride);
        _extraBits[0] = 0;
        _chunkSequence[0] = 0;
        _chunkLiteral[0] = 0;
        _chunkByte[0] = 0;
        _chunkRep1[0] = reps.Offset1;
        _chunkRep2[0] = reps.Offset2;
        _chunkRep3[0] = reps.Offset3;
        int current = 0;
        for (int i = 0; i < sequences.Length; i++)
        {
            ZstdSequence s = sequences[i];
            uint value = reps.Apply(s.Offset, s.LiteralLength);
            _offsetValues[i] = value;
            int ll = (int)s.LiteralLength;
            int llCode = ZstdBlockWriter.LiteralLengthCode(s.LiteralLength);
            int mlCode = ZstdBlockWriter.MatchLengthCode(s.MatchLength);
            int ofCode = BitOperations.Log2(value);
            int[] prefix = _prefix;
            int row = current * Stride;
            // The running totals of the open chunk are kept in row `current + 1`, copied forward from the row before.
            if (i == _chunkSequence[current])
            {
                Array.Copy(prefix, row, prefix, row + Stride, Stride);
                _extraBits[current + 1] = _extraBits[current];
            }

            int open = row + Stride;
            for (int k = 0; k < ll; k++)
            {
                prefix[open + literals[litPos + k]]++;
            }

            prefix[open + Literals + llCode]++;
            prefix[open + Literals + LiteralLengths + mlCode]++;
            prefix[open + Literals + LiteralLengths + MatchLengths + ofCode]++;
            _extraBits[current + 1] += ZstdFormat.LiteralLengthBits[llCode] + ZstdFormat.MatchLengthBits[mlCode] + ofCode;
            litPos += ll;
            bytePos += ll + (int)s.MatchLength;
            if (bytePos - chunkStartByte >= ChunkBytes && i + 1 < sequences.Length)
            {
                // Close the chunk after this sequence.
                current++;
                chunks = current;
                _chunkSequence[current] = i + 1;
                _chunkLiteral[current] = litPos;
                _chunkByte[current] = bytePos;
                _chunkRep1[current] = reps.Offset1;
                _chunkRep2[current] = reps.Offset2;
                _chunkRep3[current] = reps.Offset3;
                chunkStartByte = bytePos;
            }
        }

        // The last chunk also takes the literals after the final sequence.
        int total = current + 1;
        {
            int open = (current + 1) * Stride;
            for (int k = litPos; k < literals.Length; k++)
            {
                _prefix[open + literals[k]]++;
            }
        }

        _ = chunks;
        if (total < 2)
        {
            return 1;
        }

        // Shortest path over chunk boundaries.
        _best[0] = 0;
        for (int j = 1; j <= total; j++)
        {
            long best = long.MaxValue;
            int bestFrom = 0;
            int bestCount = 1;
            for (int i = j - 1; i >= 0; i--)
            {
                int count = i == 0 ? 1 : _count[i] + 1;
                if (count > MaxSegments)
                {
                    continue;
                }

                long c = _best[i] + SegmentCost(i, j);
                if (c < best)
                {
                    best = c;
                    bestFrom = i;
                    bestCount = count;
                }
            }

            _best[j] = best;
            _from[j] = bestFrom;
            _count[j] = bestCount;
        }

        int segments = _count[total];
        if (segments < 2)
        {
            return 1;
        }

        // Read the cuts back from the end.
        int pos = total;
        for (int s = segments - 1; s >= 0; s--)
        {
            int first = _from[pos];
            SequenceStart[s] = _chunkSequence[first];
            LiteralStart[s] = _chunkLiteral[first];
            ByteStart[s] = _chunkByte[first];
            Reps[s] = new ZstdRepeatOffsets { Offset1 = _chunkRep1[first], Offset2 = _chunkRep2[first], Offset3 = _chunkRep3[first] };
            pos = first;
        }

        Reps[segments] = reps;
        SequenceStart[segments] = sequences.Length;
        LiteralStart[segments] = literals.Length;
        Segments = segments;
        return segments;
    }

    // Estimated cost, in 1/256 bit, of coding chunks [i, j) as one block.
    private long SegmentCost(int i, int j)
    {
        ReadOnlySpan<int> a = _prefix.AsSpan(i * Stride, Stride);
        ReadOnlySpan<int> b = _prefix.AsSpan(j * Stride, Stride);
        long literalBits = Entropy(a, b, 0, Literals, out int distinctLiterals, out int literalCount);
        if (literalBits > (long)literalCount * 8 * ZstdCost.Bit)
        {
            literalBits = (long)literalCount * 8 * ZstdCost.Bit;
            distinctLiterals = 0;
        }

        long bits = literalBits;
        bits += Entropy(a, b, Literals, LiteralLengths, out int dLl, out _);
        bits += Entropy(a, b, Literals + LiteralLengths, MatchLengths, out int dMl, out _);
        bits += Entropy(a, b, Literals + LiteralLengths + MatchLengths, Offsets, out int dOf, out _);
        bits += (_extraBits[j] - _extraBits[i]) * ZstdCost.Bit;
        long overheadBytes = FixedBytes + (distinctLiterals / 2) + (((dLl + dMl + dOf) * 3) / 4);
        return bits + (overheadBytes * 8 * ZstdCost.Bit);
    }

    private static long Entropy(ReadOnlySpan<int> a, ReadOnlySpan<int> b, int first, int length, out int distinct, out int total)
    {
        total = 0;
        distinct = 0;
        for (int s = 0; s < length; s++)
        {
            total += b[first + s] - a[first + s];
        }

        if (total == 0)
        {
            return 0;
        }

        long logTotal = ZstdCost.Log2((uint)total);
        long sum = 0;
        for (int s = 0; s < length; s++)
        {
            int n = b[first + s] - a[first + s];
            if (n > 0)
            {
                distinct++;
                sum += n * (logTotal - ZstdCost.Log2((uint)n));
            }
        }

        return sum;
    }
}
