using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// One match command of a block (RFC 8878 §3.1.1.3.2): copy <see cref="LiteralLength"/> literals, then
/// <see cref="MatchLength"/> bytes from <see cref="Offset"/> bytes back.
/// </summary>
/// <remarks>
/// <see cref="Offset"/> is the real distance (at least 1), never a repeat code: the block writer decides which matches can be
/// written as repeat offsets, so a match finder is free to use the repeat offsets it is given without encoding them itself.
/// </remarks>
public struct ZstdSequence
{
    /// <summary>The number of literal bytes before the match; they are taken in order from the store's literals.</summary>
    public uint LiteralLength { get; set; }

    /// <summary>The match length, at least 3 (RFC 8878 §3.1.1.3.2.1.1).</summary>
    public uint MatchLength { get; set; }

    /// <summary>The distance back to the match source, at least 1.</summary>
    public uint Offset { get; set; }
}

/// <summary>
/// What a match finder produces for one block: the literals in order, and the sequences that interleave them with matches.
/// Literals after the last sequence are the literals the block ends with. The store is reused from block to block.
/// </summary>
public sealed class ZstdSequenceStore
{
    private byte[] _literals;
    private ZstdSequence[] _sequences;
    private uint[] _offsetValues;
    private bool _tracked;
    private ZstdRepeatOffsets _trackedStart;
    private ZstdRepeatOffsets _trackedEnd;
    private int _literalCount;
    private int _sequenceCount;

    /// <summary>Creates a store sized for blocks of up to <paramref name="blockSize"/> bytes.</summary>
    /// <param name="blockSize">The largest block, at most 128 KiB.</param>
    public ZstdSequenceStore(int blockSize = ZstdFormat.BlockSizeMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blockSize, ZstdFormat.BlockSizeMax);
        BlockSize = blockSize;

        // Literals get a margin so finders can copy them a word at a time past the end. Sequences grow on demand.
        _literals = new byte[blockSize + 64];
        _sequences = new ZstdSequence[Math.Min((blockSize / 8) + 16, 4096)];
        _offsetValues = new uint[_sequences.Length];
    }

    /// <summary>Gets the largest block this store was sized for.</summary>
    public int BlockSize { get; }

    /// <summary>Gets the number of literal bytes stored, counting those after the last sequence.</summary>
    public int LiteralCount => _literalCount;

    /// <summary>Gets the number of sequences stored.</summary>
    public int SequenceCount => _sequenceCount;

    /// <summary>Gets the stored literals.</summary>
    public ReadOnlySpan<byte> Literals => _literals.AsSpan(0, _literalCount);

    /// <summary>Gets the stored sequences.</summary>
    public ReadOnlySpan<ZstdSequence> Sequences => _sequences.AsSpan(0, _sequenceCount);

    /// <summary>Empties the store for the next block.</summary>
    public void Clear()
    {
        _literalCount = 0;
        _sequenceCount = 0;
        _tracked = false;
    }

    /// <summary>Appends literals that are not followed by a match yet (or that end the block).</summary>
    /// <param name="literals">The bytes to append; the block's total must stay within the block size.</param>
    public void AppendLiterals(ReadOnlySpan<byte> literals)
    {
        literals.CopyTo(_literals.AsSpan(_literalCount));
        _literalCount += literals.Length;
    }

    /// <summary>Appends a sequence whose literals are the next <paramref name="literalLength"/> bytes of <paramref name="source"/>, then the match.</summary>
    /// <param name="source">The bytes to take the literals from.</param>
    /// <param name="literalLength">The number of literals before the match.</param>
    /// <param name="matchLength">The match length, at least 3.</param>
    /// <param name="offset">The match distance, at least 1.</param>
    public void Append(ReadOnlySpan<byte> source, uint literalLength, uint matchLength, uint offset)
    {
        AppendLiterals(source[..(int)literalLength]);
        AddSequence(literalLength, matchLength, offset);
    }

    // Appends a sequence whose literals are window[anchor..anchor + literalLength], then the match. A finder that resolves repeat
    // offsets itself (it must, to find repeat matches) passes the offset value it coded: 1 to 3 for a repeat, otherwise
    // offset + 3. The block writer then skips resolving them again, provided the store's chain started from the offsets the
    // writer holds (see BeginTracking and TryGetTracked).
    internal void AppendTracked(ReadOnlySpan<byte> window, int anchor, uint literalLength, uint matchLength, uint offset, uint offsetValue)
    {
        int ll = (int)literalLength;
        byte[] literals = _literals;
        if (ll <= 16 && anchor + 16 <= window.Length)
        {
            // The literal buffer has a 64-byte margin, so a 16-byte copy past the true length is harmless: the next append overwrites it.
            Raw.Copy16(literals, _literalCount, window, anchor);
        }
        else
        {
            window.Slice(anchor, ll).CopyTo(literals.AsSpan(_literalCount));
        }

        _literalCount += ll;
        if (_sequenceCount == _sequences.Length)
        {
            Grow();
        }

        ref ZstdSequence seq = ref _sequences[_sequenceCount];
        seq.LiteralLength = literalLength;
        seq.MatchLength = matchLength;
        seq.Offset = offset;
        _offsetValues[_sequenceCount++] = offsetValue;
    }

    /// <summary>Records the offsets in force after the block's last sequence.</summary>
    internal void SetTrackedEnd(uint offset1, uint offset2, uint offset3) =>
        _trackedEnd = new ZstdRepeatOffsets { Offset1 = offset1, Offset2 = offset2, Offset3 = offset3 };

    private void Grow()
    {
        Array.Resize(ref _sequences, _sequences.Length * 2);
        Array.Resize(ref _offsetValues, _sequences.Length);
    }

    /// <summary>Starts offset tracking for a block: the offsets in force before its first sequence. Only an empty store can be tracked.</summary>
    internal void BeginTracking(in ZstdRepeatOffsets start)
    {
        _tracked = _sequenceCount == 0;
        _trackedStart = start;
        _trackedEnd = start;
    }

    /// <summary>
    /// The resolved offset values and the offsets in force after the last sequence, if every sequence was added with them
    /// and the chain began at <paramref name="expectedStart"/>.
    /// </summary>
    internal bool TryGetTracked(in ZstdRepeatOffsets expectedStart, out ReadOnlySpan<uint> offsetValues, out ZstdRepeatOffsets end)
    {
        offsetValues = _offsetValues.AsSpan(0, _sequenceCount);
        end = _trackedEnd;
        return _tracked
            && _trackedStart.Offset1 == expectedStart.Offset1
            && _trackedStart.Offset2 == expectedStart.Offset2
            && _trackedStart.Offset3 == expectedStart.Offset3;
    }

    /// <summary>Records a sequence whose literals were already appended with <see cref="AppendLiterals"/> or written through <see cref="LiteralBuffer"/>.</summary>
    internal void AddSequence(uint literalLength, uint matchLength, uint offset)
    {
        if (_sequenceCount == _sequences.Length)
        {
            Grow();
        }

        _tracked = false;
        ref ZstdSequence s = ref _sequences[_sequenceCount++];
        s.LiteralLength = literalLength;
        s.MatchLength = matchLength;
        s.Offset = offset;
    }

    /// <summary>
    /// The literal buffer for finders that copy literals themselves (they may write up to 64 bytes past the count they
    /// then commit with <see cref="CommitLiterals"/>).
    /// </summary>
    internal byte[] LiteralBuffer => _literals;

    internal void CommitLiterals(int count) => _literalCount += count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetLiteralCount(int count) => _literalCount = count;
}
