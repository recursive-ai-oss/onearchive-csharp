using System.Diagnostics.CodeAnalysis;

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
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateSequenceBuffer
{
    /// <summary>The number of literal/length symbols a block can use (RFC 1951 section 3.2.5: 257..285 plus the 256 literals and end of block).</summary>
    public const int LiteralLengthSymbolCount = 286;

    /// <summary>The number of distance symbols a block can use.</summary>
    public const int DistanceSymbolCount = 30;

    private readonly uint[] _sequences;

    // The encoder's parsers use the buffer in run mode: only matches are stored (in _sequences, in the same layout), each with the
    // window position it starts at in _starts, and the literals between matches are not stored at all because the writer reads
    // them back from the input. _count still counts literals and matches together, so block sizes mean the same in both modes.
    private readonly uint[]? _starts;
    private int _matchCount;
    private readonly uint[] _literalLengthFrequencies = new uint[LiteralLengthSymbolCount + 2];
    private readonly uint[] _distanceFrequencies = new uint[32];
    private int _count;

    /// <summary>Creates a buffer that holds up to <paramref name="capacity"/> entries.</summary>
    /// <param name="capacity">The most literals and matches one block holds; at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below 1.</exception>
    public DeflateSequenceBuffer(int capacity)
        : this(capacity, runMode: false)
    {
    }

    internal DeflateSequenceBuffer(int capacity, bool runMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _sequences = new uint[capacity];
        if (runMode)
        {
            _starts = new uint[capacity];
        }

        Clear();
    }

    /// <summary>Gets the number of entries.</summary>
    public int Count => _count;

    /// <summary>Gets the most entries the buffer holds.</summary>
    public int Capacity => _sequences.Length;

    /// <summary>Gets the number of input bytes the entries cover (found by adding up the entries, so it costs time proportional to <see cref="Count"/>).</summary>
    public int InputLength
    {
        get
        {
            long total = 0;
            for (int i = 0; i < _count; i++)
            {
                uint length = _sequences[i] & 0x1FF;
                total += length == 0 ? 1 : length;
            }

            return (int)total;
        }
    }

    /// <summary>Gets the entries.</summary>
    public ReadOnlySpan<uint> Sequences => _sequences.AsSpan(0, _count);

    /// <summary>Gets the counts per literal/length symbol (0..285), including one for the end-of-block symbol.</summary>
    public ReadOnlySpan<uint> LiteralLengthFrequencies => _literalLengthFrequencies.AsSpan(0, LiteralLengthSymbolCount);

    /// <summary>Gets the counts per distance symbol (0..29).</summary>
    public ReadOnlySpan<uint> DistanceFrequencies => _distanceFrequencies.AsSpan(0, DistanceSymbolCount);

    // The parsers keep the entry count in a local while they run, and store it back through these before they call anything else.
    internal uint[] Words => _sequences;

    internal bool IsRunMode => _starts is not null;

    // Run mode only: the window position each match starts at, and how many matches there are.
    internal uint[] Starts => _starts!;

    internal int MatchCountValue
    {
        get => _matchCount;
        set => _matchCount = value;
    }

    // Run mode only: the window index of the first byte of the block, which the writer subtracts from the match positions.
    internal int RunBase { get; set; }

    internal int CountValue
    {
        get => _count;
        set => _count = value;
    }

    internal uint[] LiteralLengthCounts => _literalLengthFrequencies;

    internal uint[] DistanceCounts => _distanceFrequencies;

    /// <summary>Removes every entry and counts only the end-of-block symbol.</summary>
    public void Clear()
    {
        _count = 0;
        _matchCount = 0;
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddMatchUnchecked(int length, int distance)
    {
        _sequences[_count++] = (uint)length | ((uint)(distance - 1) << 9);
        _literalLengthFrequencies[257 + DeflateSymbols.LengthSlot[length]]++;
        _distanceFrequencies[DeflateSymbols.DistanceSlot(distance)]++;
    }
}

/// <summary>
/// Decides where a Deflate block should end by watching how the mix of literals and match lengths drifts. At each call it
/// compares the entries added since the last call with everything seen since the block began, and asks for a split when the mix
/// differs enough that a new Huffman code would pay for its header.
/// </summary>
/// <remarks>The measure is integer arithmetic on counts, so the decision depends only on the counts given.</remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateBlockSplitter
{
    private const int ClassCount = 11;

    private readonly ulong[] _previous = new ulong[ClassCount];
    private readonly ulong[] _current = new ulong[ClassCount];
    private ulong _previousTotal;

    /// <summary>Creates a splitter.</summary>
    /// <param name="minBlockEntries">No split is requested before the block holds this many entries. The default is 4096.</param>
    /// <param name="thresholdPerMille">The difference between the new entries' mix and the block's mix (the sum of absolute differences of class shares, 0 to 2000) above which a split is requested. The default is 450.</param>
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

    /// <summary>Forgets the current block; call when a new block begins (the counts then start again from zero).</summary>
    public void Reset()
    {
        Array.Clear(_previous);
        _previousTotal = 0;
    }

    /// <summary>Takes the counts of the current block so far.</summary>
    /// <param name="literalLengthFrequencies">The block's counts per literal/length symbol so far, as <see cref="DeflateSequenceBuffer.LiteralLengthFrequencies"/> holds them (at least 286 values).</param>
    /// <returns>True when the block should end now (and the splitter is reset); false when it should go on.</returns>
    /// <exception cref="ArgumentException">Fewer than 286 counts were given.</exception>
    public bool Observe(ReadOnlySpan<uint> literalLengthFrequencies)
    {
        if (literalLengthFrequencies.Length < DeflateSequenceBuffer.LiteralLengthSymbolCount)
        {
            throw new ArgumentException("Counts for 286 symbols are needed.", nameof(literalLengthFrequencies));
        }

        // Classes: the literals in eight groups by value, then matches of length up to 5, 6 to 9, and longer.
        Array.Clear(_current);
        ulong total = 0;
        for (int group = 0; group < 8; group++)
        {
            ulong sum = 0;
            foreach (uint f in literalLengthFrequencies.Slice(group * 32, 32))
            {
                sum += f;
            }

            _current[group] = sum;
            total += sum;
        }

        for (int slot = 0; slot < 29; slot++)
        {
            ulong f = literalLengthFrequencies[257 + slot];
            _current[slot < 3 ? 8 : slot < 7 ? 9 : 10] += f;
            total += f;
        }

        ulong batchTotal = total - _previousTotal;
        if (batchTotal == 0)
        {
            return false;
        }

        if (_previousTotal >= (ulong)MinBlockEntries)
        {
            ulong difference = 0;
            for (int i = 0; i < ClassCount; i++)
            {
                ulong batch = _current[i] - _previous[i];
                ulong a = _previous[i] * batchTotal;
                ulong b = batch * _previousTotal;
                difference += a > b ? a - b : b - a;
            }

            if (difference * 1000 > (ulong)ThresholdPerMille * batchTotal * _previousTotal)
            {
                Reset();
                return true;
            }
        }

        Array.Copy(_current, _previous, ClassCount);
        _previousTotal = total;
        return false;
    }
}
