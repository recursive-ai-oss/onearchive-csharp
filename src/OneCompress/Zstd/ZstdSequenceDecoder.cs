using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Decodes the sequences of a compressed block (RFC 8878 §3.1.1.3.2): three interleaved FSE states read
/// from one backward bitstream, giving for each sequence a literals length, a match length and an offset
/// value. Call <see cref="Next"/> once per sequence, passing <c>false</c> for <c>updateStates</c> on the
/// last one, and check <see cref="IsFinished"/> afterwards.
/// </summary>
public ref struct ZstdSequenceDecoder
{
    private ZstdReverseBitReader _bits;
    private readonly ReadOnlySpan<FseEntry> _literalLengths;
    private readonly ReadOnlySpan<FseEntry> _offsets;
    private readonly ReadOnlySpan<FseEntry> _matchLengths;
    private uint _literalLengthState;
    private uint _offsetState;
    private uint _matchLengthState;

    /// <summary>Starts decoding: reads the three initial states.</summary>
    /// <param name="bitstream">The sequence bitstream that follows the tables, ending with the marker byte.</param>
    /// <param name="literalLengths">The literals length table.</param>
    /// <param name="offsets">The offset table.</param>
    /// <param name="matchLengths">The match length table.</param>
    /// <param name="streamOffset">Offset of <paramref name="bitstream"/> in the compressed input, for error messages.</param>
    /// <exception cref="CompressionFormatException">The bitstream is empty, has no marker bit, or is too short for the states.</exception>
    public ZstdSequenceDecoder(
        ReadOnlySpan<byte> bitstream,
        FseDecodingTable literalLengths,
        FseDecodingTable offsets,
        FseDecodingTable matchLengths,
        long streamOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(literalLengths);
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(matchLengths);
        _bits = new ZstdReverseBitReader(bitstream, streamOffset);
        _literalLengths = literalLengths.Entries;
        _offsets = offsets.Entries;
        _matchLengths = matchLengths.Entries;

        // RFC 8878 §3.1.1.3.2.1.4: the initial states come in the order literals length, offset, match length.
        _bits.Reload();
        _literalLengthState = _bits.ReadBitsNoReload(literalLengths.AccuracyLog);
        _offsetState = _bits.ReadBitsNoReload(offsets.AccuracyLog);
        _matchLengthState = _bits.ReadBitsNoReload(matchLengths.AccuracyLog);
        if (_bits.IsOverflowed)
        {
            ZstdFormat.ThrowTruncated(streamOffset, "three initial FSE states");
        }
    }

    /// <summary>Gets a value indicating whether the whole bitstream has been used. It must be true after the last sequence.</summary>
    public readonly bool IsFinished => _bits.IsFinished;

    /// <summary>Gets a value indicating whether decoding read beyond the start of the bitstream, which means the data is corrupt.</summary>
    public readonly bool IsOverflowed => _bits.IsOverflowed;

    /// <summary>Decodes the next sequence.</summary>
    /// <param name="literalLength">The number of literals to copy before the match.</param>
    /// <param name="matchLength">The match length, at least 3.</param>
    /// <param name="offsetValue">
    /// The offset value of RFC 8878 §3.1.1.3.2.1.1: 1 to 3 select a repeat offset, larger values mean an offset of
    /// the value minus 3. Use <see cref="ZstdRepeatOffsets.Resolve"/> to turn it into an offset.
    /// </param>
    /// <param name="updateStates">False for the last sequence of the block, which has no state update bits.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Next(out int literalLength, out int matchLength, out uint offsetValue, bool updateStates)
    {
        FseEntry ll = _literalLengths[(int)_literalLengthState];
        FseEntry of = _offsets[(int)_offsetState];
        FseEntry ml = _matchLengths[(int)_matchLengthState];

        // RFC 8878 §3.1.1.3.2.1.5: the offset's bits come first, then the match length's, then the literals length's.
        // Symbols are range-checked when a table is built (offset codes up to 31, ml up to 52, ll up to 35).
        _bits.Reload();
        int offsetCode = of.Symbol;
        offsetValue = (1u << offsetCode) + _bits.ReadBitsNoReload(offsetCode);
        _bits.Reload();
        matchLength = (int)(ZstdFormat.MatchLengthBase[ml.Symbol] + _bits.ReadBitsNoReload(ZstdFormat.MatchLengthBits[ml.Symbol]));
        literalLength = (int)(ZstdFormat.LiteralLengthBase[ll.Symbol] + _bits.ReadBitsNoReload(ZstdFormat.LiteralLengthBits[ll.Symbol]));

        if (updateStates)
        {
            // Update order: literals length, match length, offset.
            _bits.Reload();
            _literalLengthState = ll.Baseline + _bits.ReadBitsNoReload(ll.NumBits);
            _matchLengthState = ml.Baseline + _bits.ReadBitsNoReload(ml.NumBits);
            _offsetState = of.Baseline + _bits.ReadBitsNoReload(of.NumBits);
        }
    }
}

/// <summary>
/// The three most recent offsets of a frame (RFC 8878 §3.1.1.5), with the rules for resolving a sequence's
/// offset value against them.
/// </summary>
public struct ZstdRepeatOffsets
{
    /// <summary>The most recently used offset.</summary>
    public uint Offset1 { get; set; }

    /// <summary>The second most recently used offset.</summary>
    public uint Offset2 { get; set; }

    /// <summary>The third most recently used offset.</summary>
    public uint Offset3 { get; set; }

    /// <summary>Gets the offsets a frame starts with when no dictionary supplies others: 1, 4 and 8.</summary>
    public static ZstdRepeatOffsets Initial => new() { Offset1 = 1, Offset2 = 4, Offset3 = 8 };

    /// <summary>
    /// Turns an offset value into an offset and updates the history.
    /// </summary>
    /// <param name="offsetValue">The value from <see cref="ZstdSequenceDecoder.Next"/>.</param>
    /// <param name="literalLength">The sequence's literals length; zero shifts the meaning of the repeat codes.</param>
    /// <returns>The offset, or 0 if the data is corrupt (a repeat code that works out to zero).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Resolve(uint offsetValue, int literalLength)
    {
        uint offset;
        if (offsetValue > 3)
        {
            offset = offsetValue - 3;
            Offset3 = Offset2;
            Offset2 = Offset1;
            Offset1 = offset;
            return offset;
        }

        // With no literals, repeat codes 1, 2 and 3 mean the second offset, the third, and the first minus one.
        uint index = offsetValue - 1 + (literalLength == 0 ? 1u : 0u);
        if (index == 0)
        {
            return Offset1;
        }

        if (index == 1)
        {
            offset = Offset2;
            Offset2 = Offset1;
            Offset1 = offset;
            return offset;
        }

        if (index == 2)
        {
            offset = Offset3;
        }
        else
        {
            offset = Offset1 - 1;
            if (offset == 0)
            {
                return 0;
            }
        }

        Offset3 = Offset2;
        Offset2 = Offset1;
        Offset1 = offset;
        return offset;
    }
}
