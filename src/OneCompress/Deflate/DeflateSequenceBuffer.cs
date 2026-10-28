using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// The parsed form of one Deflate block: literals and matches in order, with the symbol counts a Huffman code is built from.
/// A parser fills it, <see cref="DeflateBlockWriter"/> turns it into bits.
/// </summary>
/// <remarks>
/// Each entry is one 32-bit word: bits 0-8 hold the match length (0 for a literal) and bits 9 and up hold the literal byte, or the
/// match distance minus one. Counts for the end-of-block symbol (256) are included, as a block always ends with one.
/// </remarks>
public sealed class DeflateSequenceBuffer
{
    /// <summary>The number of literal/length symbols a block can use (RFC 1951 section 3.2.5: 257..285 plus the 256 literals and end of block).</summary>
    public const int LiteralLengthSymbolCount = 286;

    /// <summary>The number of distance symbols a block can use.</summary>
    public const int DistanceSymbolCount = 30;

    private readonly uint[] _sequences;
    private readonly uint[] _literalLengthFrequencies = new uint[LiteralLengthSymbolCount + 2];
    private readonly uint[] _distanceFrequencies = new uint[32];
    private int _count;
    private int _inputLength;

    /// <summary>Creates a buffer that holds up to <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">The most literals and matches one block holds; at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
    public DeflateSequenceBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _sequences = new uint[capacity];
        Clear();
    }

    /// <summary>Gets the number of entries.</summary>
    public int Count => _count;

    /// <summary>Gets the most entries the buffer holds.</summary>
    public int Capacity => _sequences.Length;

    /// <summary>Gets the number of input bytes the entries cover.</summary>
    public int InputLength => _inputLength;

    /// <summary>Gets the entries.</summary>
    public ReadOnlySpan<uint> Sequences => _sequences.AsSpan(0, _count);

    /// <summary>Gets the counts per literal/length symbol (0..285), including one for the end-of-block symbol.</summary>
    public ReadOnlySpan<uint> LiteralLengthFrequencies => _literalLengthFrequencies.AsSpan(0, LiteralLengthSymbolCount);

    /// <summary>Gets the counts per distance symbol (0..29).</summary>
    public ReadOnlySpan<uint> DistanceFrequencies => _distanceFrequencies.AsSpan(0, DistanceSymbolCount);

    internal uint[] LiteralLengthCounts => _literalLengthFrequencies;

    internal uint[] DistanceCounts => _distanceFrequencies;

    /// <summary>Removes every entry and counts only the end-of-block symbol.</summary>
    public void Clear()
    {
        _count = 0;
        _inputLength = 0;
        Array.Clear(_literalLengthFrequencies);
        Array.Clear(_distanceFrequencies);
        _literalLengthFrequencies[DeflateTables.EndOfBlockSymbol] = 1;
    }

    /// <summary>Appends a literal byte.</summary>
    /// <param name="value">The byte.</param>
    /// <exception cref="InvalidOperationException">The buffer is full.</exception>
    public void AddLiteral(byte value)
    {
        if (_count >= _sequences.Length)
        {
            throw new InvalidOperationException("The sequence buffer is full.");
        }

        AddLiteralUnchecked(value);
    }

    /// <summary>Appends a match.</summary>
    /// <param name="length">The match length, 3 to 258.</param>
    /// <param name="distance">The distance back, 1 to 32768.</param>
    /// <exception cref="ArgumentOutOfRangeException">The length or distance is outside its range.</exception>
    /// <exception cref="InvalidOperationException">The buffer is full.</exception>
    public void AddMatch(int length, int distance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, DeflateTables.MinMatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, DeflateTables.MaxMatchLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, DeflateTables.WindowSize);
        if (_count >= _sequences.Length)
        {
            throw new InvalidOperationException("The sequence buffer is full.");
        }

        AddMatchUnchecked(length, distance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddLiteralUnchecked(byte value)
    {
        _sequences[_count++] = (uint)value << 9;
        _literalLengthFrequencies[value]++;
        _inputLength++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddMatchUnchecked(int length, int distance)
    {
        _sequences[_count++] = (uint)length | ((uint)(distance - 1) << 9);
        _literalLengthFrequencies[257 + DeflateSymbols.LengthSlot[length]]++;
        _distanceFrequencies[DeflateSymbols.DistanceSlot(distance)]++;
        _inputLength += length;
    }
}

/// <summary>
/// Decides where a Deflate block should end by watching how the mix of literals and match lengths drifts. It compares each batch of
/// entries with everything seen since the block began and asks for a split when the mix differs enough that a new Huffman code
/// would pay for its header.
/// </summary>
/// <remarks>The measure is integer arithmetic on counts, so the decision depends only on the entries given.</remarks>
public sealed class DeflateBlockSplitter
{
    private const int ClassCount = 11;

    private readonly uint[] _block = new uint[ClassCount];
    private readonly uint[] _batch = new uint[ClassCount];
    private int _blockTotal;

    /// <summary>Creates a splitter.</summary>
    /// <param name="minBlockEntries">No split is requested before the block holds this many entries. The default is 4096.</param>
    /// <param name="thresholdPerMille">The difference between the batch's mix and the block's mix (the sum of absolute differences of class shares, 0 to 2000) above which a split is requested. The default is 450.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public DeflateBlockSplitter(int minBlockEntries = 4096, int thresholdPerMille = 450)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minBlockEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdPerMille, 1);
        MinBlockEntries = minBlockEntries;
        ThresholdPerMille = thresholdPerMille;
    }

    /// <summary>Gets the entries a block must hold before a split is considered.</summary>
    public int MinBlockEntries { get; }

    /// <summary>Gets the split threshold in thousandths.</summary>
    public int ThresholdPerMille { get; }

    /// <summary>Forgets the current block; call when a new block begins.</summary>
    public void Reset()
    {
        Array.Clear(_block);
        _blockTotal = 0;
    }

    /// <summary>Takes the next batch of entries of the current block.</summary>
    /// <param name="batch">Entries in the form <see cref="DeflateSequenceBuffer.Sequences"/> holds them; at least one.</param>
    /// <returns>True when the block should end after this batch (and the splitter is reset); false when the batch joined the block.</returns>
    public bool Observe(ReadOnlySpan<uint> batch)
    {
        if (batch.IsEmpty)
        {
            return false;
        }

        Array.Clear(_batch);
        foreach (uint word in batch)
        {
            uint length = word & 0x1FF;
            int cls;
            if (length == 0)
            {
                cls = (int)((word >> 9) & 0xFF) >> 5;
            }
            else
            {
                cls = length <= 5 ? 8 : length <= 9 ? 9 : 10;
            }

            _batch[cls]++;
        }

        if (_blockTotal >= MinBlockEntries)
        {
            ulong difference = 0;
            ulong batchTotal = (ulong)batch.Length;
            for (int i = 0; i < ClassCount; i++)
            {
                ulong a = (ulong)_block[i] * batchTotal;
                ulong b = (ulong)_batch[i] * (ulong)_blockTotal;
                difference += a > b ? a - b : b - a;
            }

            if (difference * 1000 > (ulong)ThresholdPerMille * batchTotal * (ulong)_blockTotal)
            {
                Reset();
                return true;
            }
        }

        for (int i = 0; i < ClassCount; i++)
        {
            _block[i] += _batch[i];
        }

        _blockTotal += batch.Length;
        return false;
    }
}
