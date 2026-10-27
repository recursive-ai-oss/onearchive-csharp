using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Decodes the compressed blocks of a frame (RFC 8878 §3.1.1.3) and carries what one block leaves for the
/// next: the repeat offsets, the Huffman table and the three sequence tables. One instance serves any
/// number of frames; <see cref="BeginFrame"/> resets it between them.
/// </summary>
internal sealed class ZstdBlockDecoder
{
    private readonly FseDecodingTable _literalLengthScratch = new();
    private readonly FseDecodingTable _offsetScratch = new();
    private readonly FseDecodingTable _matchLengthScratch = new();
    private readonly HuffmanDecodingTable _huffmanScratch = new();

    // The tables a Repeat_Mode block would reuse; they point either at a scratch table, a shared predefined
    // table or a dictionary's table, none of which is modified while it is referenced here.
    private FseDecodingTable? _literalLengths;
    private FseDecodingTable? _offsets;
    private FseDecodingTable? _matchLengths;
    private HuffmanDecodingTable? _huffman;
    private ZstdRepeatOffsets _repeatOffsets;
    private byte[] _literals = [];

    // The sequence loop copies literals in 16-byte pieces and may read up to 31 bytes past the last one it needs. Literals that
    // live in _literals have this much readable room after them; the others (a long raw section read in place) have none.
    private const int LiteralSlack = 32;
    private bool _literalsHaveSlack;

    /// <summary>Resets the carried state for a new frame, starting from a dictionary's tables when it has them.</summary>
    public void BeginFrame(ZstdDictionary? dictionary)
    {
        if (dictionary is not null && dictionary.HasEntropyTables)
        {
            _literalLengths = dictionary.LiteralLengths;
            _offsets = dictionary.Offsets;
            _matchLengths = dictionary.MatchLengths;
            _huffman = dictionary.Huffman;
            uint[] reps = dictionary.RepeatOffsets;
            _repeatOffsets = new ZstdRepeatOffsets { Offset1 = reps[0], Offset2 = reps[1], Offset3 = reps[2] };
        }
        else
        {
            _literalLengths = null;
            _offsets = null;
            _matchLengths = null;
            _huffman = null;
            _repeatOffsets = ZstdRepeatOffsets.Initial;
        }
    }

    /// <summary>
    /// Decodes one compressed block, appending its output to <paramref name="buffer"/> at <paramref name="position"/>.
    /// </summary>
    /// <param name="block">The block's content.</param>
    /// <param name="buffer">History followed by room for the output. History before <paramref name="position"/> is what matches copy from.</param>
    /// <param name="position">The index in <paramref name="buffer"/> where the output starts.</param>
    /// <param name="maxOutput">The most this block may produce (the block size limit, and the rest of the frame's declared size).</param>
    /// <param name="windowSize">The frame's window size: no offset may exceed it.</param>
    /// <param name="frameOutput">The number of bytes the frame produced before this block.</param>
    /// <param name="dictionary">The dictionary content, which acts as history before the frame's first byte.</param>
    /// <param name="blockOffset">Offset of the block in the compressed input, for error messages.</param>
    /// <returns>The number of bytes appended.</returns>
    public int Decode(
        ReadOnlySpan<byte> block,
        Span<byte> buffer,
        int position,
        int maxOutput,
        long windowSize,
        long frameOutput,
        ReadOnlySpan<byte> dictionary,
        long blockOffset)
    {
        // RFC 8878 §3.1.1.3.1: the literals section comes first.
        ReadOnlySpan<byte> literals = DecodeLiterals(block, out int p, blockOffset);
        ReadOnlySpan<byte> literalsWide = _literalsHaveSlack ? _literals.AsSpan(0, literals.Length + LiteralSlack) : literals;

        // RFC 8878 §3.1.1.3.2.1: Number_of_Sequences, one to three bytes.
        if (p >= block.Length)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + block.Length, "a sequences section after the literals section");
        }

        int sequenceCount = block[p++];
        if (sequenceCount >= 128)
        {
            if (sequenceCount == 255)
            {
                if (block.Length - p < 2)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + p, "a three-byte sequence count");
                }

                sequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(block[p..]) + 0x7F00;
                p += 2;
            }
            else
            {
                if (block.Length - p < 1)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + p, "a two-byte sequence count");
                }

                sequenceCount = ((sequenceCount - 0x80) << 8) + block[p++];
            }
        }

        int end = position + maxOutput;
        int current = position;

        if (sequenceCount == 0)
        {
            // No sequences: the block is its literals, and the section ends right after the count.
            if (p != block.Length)
            {
                ZstdFormat.ThrowCorrupt(blockOffset + p, "no data after a sequence count of zero");
            }

            if (literals.Length > maxOutput)
            {
                ZstdFormat.ThrowCorrupt(blockOffset, "block output within the block size limit and the frame content size");
            }

            literals.CopyTo(buffer[current..]);
            return literals.Length;
        }

        if (p >= block.Length)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + p, "a symbol compression modes byte");
        }

        // RFC 8878 §3.1.1.3.2.1.1: the modes of literals lengths, offsets and match lengths; the low two bits are reserved.
        int modes = block[p++];
        if ((modes & 3) != 0)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + p - 1, "zero reserved bits in the symbol compression modes");
        }

        FseDecodingTable literalLengths = SelectTable(
            modes >> 6, _literalLengths, _literalLengthScratch, ZstdFormat.DefaultLiteralLengths,
            ZstdFormat.LiteralLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog, block, ref p, blockOffset);
        FseDecodingTable offsets = SelectTable(
            (modes >> 4) & 3, _offsets, _offsetScratch, ZstdFormat.DefaultOffsets,
            ZstdFormat.OffsetMaxSymbol, ZstdFormat.OffsetMaxAccuracyLog, block, ref p, blockOffset);
        FseDecodingTable matchLengths = SelectTable(
            (modes >> 2) & 3, _matchLengths, _matchLengthScratch, ZstdFormat.DefaultMatchLengths,
            ZstdFormat.MatchLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog, block, ref p, blockOffset);
        _literalLengths = literalLengths;
        _offsets = offsets;
        _matchLengths = matchLengths;

        var sequences = new ZstdSequenceDecoder(block[p..], literalLengths, offsets, matchLengths, blockOffset + p);
        ZstdRepeatOffsets reps = _repeatOffsets;
        int literalPosition = 0;

        // RFC 8878 §3.1.1.4: each sequence copies literals, then a match from earlier output.
        current = ExecuteSequences(
            ref sequences, literalLengths, offsets, matchLengths, block[p..], sequenceCount, literals, literalsWide, buffer, position, end, ref reps, windowSize, frameOutput,
            dictionary, blockOffset + p, out literalPosition);

        if (sequences.IsOverflowed || !sequences.IsFinished)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + p, "a sequence bitstream that is consumed exactly by its sequences");
        }

        _repeatOffsets = reps;

        // RFC 8878 §3.1.1.4: literals not used by a sequence follow the last match.
        int remaining = literals.Length - literalPosition;
        if (remaining > end - current)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + p, "block output within the block size limit and the frame content size");
        }

        literals[literalPosition..].CopyTo(buffer[current..]);
        current += remaining;
        return current - position;
    }

    // The state of the sequence loop between its fast and general parts.
    private struct SequenceState
    {
        public ulong Container;
        public int Consumed;
        public int Pointer;
        public uint LiteralLengthState;
        public uint OffsetState;
        public uint MatchLengthState;
        public int Current;
        public int LiteralPosition;
        public int Index;
        public ZstdRepeatOffsets Reps;

        // A sequence the fast loop decoded but left to the general code. 0: none; 1: nothing applied yet (offset value,
        // match length and literals length are in the three fields below); 2: literals applied and the offset resolved
        // (Offset and MatchLength are set).
        public int Pending;
        public uint OffsetValue;
        public int MatchLength;
        public int LiteralLength;
    }

    private readonly SequenceEntry[] _rows = new SequenceEntry[3 << FseDecodingTable.MaxAccuracyLog];
    private FseDecodingTable? _rowsLiteralLengths;
    private FseDecodingTable? _rowsOffsets;
    private FseDecodingTable? _rowsMatchLengths;
    private int _rowsLiteralLengthsVersion;
    private int _rowsOffsetsVersion;
    private int _rowsMatchLengthsVersion;

    // The three tables' rows in one array: literals lengths at 0, offsets at 512, match lengths at 1024. Copied only when a
    // table changed since the last block (repeat mode and the predefined tables keep the rows).
    private SequenceEntry[] CombinedRows(FseDecodingTable literalLengths, FseDecodingTable offsets, FseDecodingTable matchLengths)
    {
        if (!ReferenceEquals(_rowsLiteralLengths, literalLengths) || _rowsLiteralLengthsVersion != literalLengths.Version)
        {
            literalLengths.FillSequenceEntries(0, _rows.AsSpan(0));
            _rowsLiteralLengths = literalLengths;
            _rowsLiteralLengthsVersion = literalLengths.Version;
        }

        if (!ReferenceEquals(_rowsOffsets, offsets) || _rowsOffsetsVersion != offsets.Version)
        {
            offsets.FillSequenceEntries(1, _rows.AsSpan(1 << FseDecodingTable.MaxAccuracyLog));
            _rowsOffsets = offsets;
            _rowsOffsetsVersion = offsets.Version;
        }

        if (!ReferenceEquals(_rowsMatchLengths, matchLengths) || _rowsMatchLengthsVersion != matchLengths.Version)
        {
            matchLengths.FillSequenceEntries(2, _rows.AsSpan(2 << FseDecodingTable.MaxAccuracyLog));
            _rowsMatchLengths = matchLengths;
            _rowsMatchLengthsVersion = matchLengths.Version;
        }

        return _rows;
    }

    // The sequence loop of a block (RFC 8878 §3.1.1.4): decode each sequence's three codes, copy its literals, then
    // its match. The bulk of the sequences go through RunFast, a loop with no calls and a small set of variables, which
    // hands anything unusual (the last sequence, the stream's start, a copy near the buffer's end or from the
    // dictionary, an offset below 16, corrupt data) to RunGeneral. The FSE states are indexes into the combined rows.
    private int ExecuteSequences(
        ref ZstdSequenceDecoder sequences,
        FseDecodingTable literalLengthTable,
        FseDecodingTable offsetTable,
        FseDecodingTable matchLengthTable,
        ReadOnlySpan<byte> data,
        int sequenceCount,
        ReadOnlySpan<byte> literals,
        ReadOnlySpan<byte> literalsWide,
        Span<byte> buffer,
        int position,
        int end,
        ref ZstdRepeatOffsets reps,
        long windowSize,
        long frameOutput,
        ReadOnlySpan<byte> dictionary,
        long streamOffset,
        out int literalPosition)
    {
        if (sequenceCount < SmallBlockSequences || data.Length < 8)
        {
            return ExecuteFewSequences(
                ref sequences, sequenceCount, literals, buffer, position, end, ref reps, windowSize, frameOutput, dictionary,
                streamOffset, out literalPosition);
        }

        SequenceEntry[] rows = CombinedRows(literalLengthTable, offsetTable, matchLengthTable);
        if (sequenceCount < SmallLoopSequences)
        {
            return RunSmall(
                ref sequences, rows, data, sequenceCount, literals, literalsWide, buffer, position, end, ref reps, windowSize, frameOutput,
                dictionary, streamOffset, out literalPosition);
        }

        var st = new SequenceState { Current = position, Reps = reps };
        sequences.Export(out st.Container, out st.Consumed, out st.Pointer, out st.LiteralLengthState, out st.OffsetState, out st.MatchLengthState);
        st.OffsetState += 1 << FseDecodingTable.MaxAccuracyLog;
        st.MatchLengthState += 2 << FseDecodingTable.MaxAccuracyLog;
        long producedBase = frameOutput - position;

        // A copy in 16-byte pieces writes at least two pieces and may write up to 31 bytes past its end; `limit` keeps it
        // inside the buffer and the block. Offsets beyond the window are rejected by comparing with its size, clamped to int.
        int limit = Math.Min(end, buffer.Length - 32);
        int window = (int)Math.Min(windowSize, int.MaxValue);

        while (st.Index < sequenceCount)
        {
            RunFast<MainLoop>(ref st, rows, data, sequenceCount - 1, literalsWide, buffer, limit, window, dictionary, producedBase);
            if (st.Index < sequenceCount && st.Pending == 0)
            {
                RunFast<TailLoop>(ref st, rows, data, sequenceCount - 1, literalsWide, buffer, limit, window, dictionary, producedBase);
            }

            if (st.Index < sequenceCount)
            {
                RunGeneral(ref st, rows, data, sequenceCount, literals, buffer, end, producedBase, windowSize, dictionary, streamOffset);
            }
        }

        sequences.Import(st.Container, st.Consumed, st.Pointer, st.LiteralLengthState, st.OffsetState, st.MatchLengthState);
        reps = st.Reps;
        literalPosition = st.LiteralPosition;
        return st.Current;
    }

    // Blocks with fewer sequences than this run in RunSmall, which does everything in one pass; the others use the fast loops
    // with their set-up and hand-offs, which pay off over many sequences.
    private const int SmallLoopSequences = 64;

    // A whole block's sequences in one function, for blocks with few of them (game-sized payloads): no hand-offs between loops,
    // the last sequence (which has no state update bits) and the stream's start are handled in line, and every check is made
    // for each sequence. The table rows and the bit arithmetic are those of RunFast.
    private static int RunSmall(
        ref ZstdSequenceDecoder sequences,
        SequenceEntry[] rows,
        ReadOnlySpan<byte> data,
        int sequenceCount,
        ReadOnlySpan<byte> literals,
        ReadOnlySpan<byte> literalsWide,
        Span<byte> buffer,
        int position,
        int end,
        ref ZstdRepeatOffsets reps,
        long windowSize,
        long frameOutput,
        ReadOnlySpan<byte> dictionary,
        long streamOffset,
        out int literalPosition)
    {
        sequences.Export(out _, out int consumed, out int pointer, out uint llStart, out uint ofStart, out uint mlStart);
        nuint llState = llStart;
        nuint ofState = ofStart + (1 << FseDecodingTable.MaxAccuracyLog);
        nuint mlState = mlStart + (2 << FseDecodingTable.MaxAccuracyLog);
        nint unread = ((nint)pointer << 3) + 64 - consumed; // the stream is at least eight bytes
        nint current = position;
        nint litPos = 0;
        uint r1 = reps.Offset1;
        uint r2 = reps.Offset2;
        uint r3 = reps.Offset3;
        long producedBase = frameOutput - position;
        nint literalCount = literals.Length;

        for (int i = 0; i < sequenceCount; i++)
        {
            ulong ll = Raw.Row(rows, llState);
            ulong of = Raw.Row(rows, ofState);
            ulong ml = Raw.Row(rows, mlState);
            bool final = i == sequenceCount - 1;

            // RFC 8878 §3.1.1.3.2.1.5: the last sequence has no state update bits.
            int total = (byte)((ll + ml + of) >> 16);
            if (final)
            {
                total -= (byte)(ll >> 24) + (byte)(ml >> 24) + (byte)(of >> 24);
            }

            nint literalLength;
            nint matchLength;
            uint offsetValue;
            if (total <= 56)
            {
                // One 64-bit read covers the sequence, as in RunFast; near the start of the stream the read is clamped to the
                // first eight bytes and what the sequence needs must be among the unread bits.
                nint p = (unread - 57) >> 3;
                if (p < 0)
                {
                    p = 0;
                }

                nint shift = unread - (p << 3) - total;
                if (shift < 0)
                {
                    ThrowStreamEnd(streamOffset);
                }

                ulong c = Raw.ReadUInt64(data, p) >> (int)shift;
                uint n;
                if (!final)
                {
                    n = (uint)(of >> 24);
                    ofState = (ushort)of + Bits(c, n);
                    c >>= (int)n;
                    n = (uint)(ml >> 24);
                    mlState = (ushort)ml + Bits(c, n);
                    c >>= (int)n;
                    n = (uint)(ll >> 24);
                    llState = (ushort)ll + Bits(c, n);
                    c >>= (int)n;
                }

                n = (uint)(ll >> 32);
                literalLength = (nint)((uint)(ll >> 40) + Bits(c, n));
                c >>= (int)n;
                n = (uint)(ml >> 32);
                matchLength = (nint)((uint)(ml >> 40) + Bits(c, n));
                c >>= (int)n;
                n = (uint)(of >> 32);
                offsetValue = (1u << (int)n) + Bits(c, n);
                unread -= total;
            }
            else
            {
                ReadSequenceSlowly(data, ref unread, ll, ml, of, final, out literalLength, out matchLength, out offsetValue, out uint llNext, out uint mlNext, out uint ofNext, streamOffset);
                if (!final)
                {
                    llState = (ushort)ll + llNext;
                    mlState = (ushort)ml + mlNext;
                    ofState = (ushort)of + ofNext;
                }
            }

            // RFC 8878 §3.1.1.4: copy the literals, then the match.
            if (literalLength > literalCount - litPos || literalLength > end - current)
            {
                ThrowSequenceLimit(literalLength > literalCount - litPos, streamOffset);
            }

            if (litPos + literalLength + 32 <= literalsWide.Length && current + literalLength + 32 <= buffer.Length)
            {
                // Whole 16-byte pieces, at least two; they stay inside both buffers by the test above.
                Raw.Copy16(buffer, current, literalsWide, litPos);
                Raw.Copy16(buffer, current + 16, literalsWide, litPos + 16);
                for (nint k = 32; k < literalLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, literalsWide, litPos + k);
                }
            }
            else
            {
                literals.Slice((int)litPos, (int)literalLength).CopyTo(buffer[(int)current..]);
            }

            litPos += literalLength;
            current += literalLength;

            // RFC 8878 §3.1.1.5: resolve the offset value against the repeat offsets.
            uint offset;
            if (offsetValue > 3)
            {
                offset = offsetValue - 3;
                r3 = r2;
                r2 = r1;
                r1 = offset;
            }
            else
            {
                uint index = offsetValue - 1 + (literalLength == 0 ? 1u : 0u);
                if (index == 0)
                {
                    offset = r1;
                }
                else
                {
                    offset = index == 1 ? r2 : (index == 2 ? r3 : r1 - 1);
                    r3 = index == 1 ? r3 : r2;
                    r2 = r1;
                    r1 = offset;
                }
            }

            long produced = producedBase + current;
            if ((ulong)(offset - 1) >= (ulong)(produced < windowSize ? produced : windowSize))
            {
                CheckOffsetIntoDictionary(offset, produced, windowSize, dictionary.Length, streamOffset);
            }

            if (matchLength > end - current)
            {
                ThrowSequenceLimit(false, streamOffset);
            }

            if (offset >= 16 && offset <= (nuint)current && current + matchLength + 32 <= buffer.Length)
            {
                nint from = current - (nint)offset;
                Raw.Copy16(buffer, current, buffer, from);
                Raw.Copy16(buffer, current + 16, buffer, from + 16);
                for (nint k = 32; k < matchLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, buffer, from + k);
                }
            }
            else if (offset > (nuint)current && produced - offset >= -dictionary.Length && produced - offset <= -(matchLength + 32) && current + matchLength + 32 <= buffer.Length)
            {
                // The match lies in the dictionary, `offset - produced` bytes before its end, with room for whole pieces after it.
                nint source = dictionary.Length - (nint)(offset - produced);
                Raw.Copy16(buffer, current, dictionary, source);
                Raw.Copy16(buffer, current + 16, dictionary, source + 16);
                for (nint k = 32; k < matchLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, dictionary, source + k);
                }
            }
            else
            {
                CopyMatch(buffer, (int)current, (int)offset, (int)matchLength, dictionary);
            }

            current += matchLength;
        }

        // Hand the position in the stream back so that the caller's check that it was consumed exactly applies.
        nint rest = (unread - 57) >> 3;
        if (rest < 0)
        {
            rest = 0;
        }

        sequences.Import(Raw.ReadUInt64(data, rest), 64 + ((int)rest << 3) - (int)unread, (int)rest, 0, 0, 0);
        reps = new ZstdRepeatOffsets { Offset1 = r1, Offset2 = r2, Offset3 = r3 };
        literalPosition = (int)litPos;
        return (int)current;
    }

    // One sequence's fields read one after the other, for the rare sequence whose fields need more than 56 bits in all.
    private static void ReadSequenceSlowly(
        ReadOnlySpan<byte> data,
        ref nint unread,
        ulong ll,
        ulong ml,
        ulong of,
        bool final,
        out nint literalLength,
        out nint matchLength,
        out uint offsetValue,
        out uint llNext,
        out uint mlNext,
        out uint ofNext,
        long streamOffset)
    {
        nint u = unread;
        uint n = (uint)(of >> 32) & 0xFF;
        offsetValue = (1u << (int)n) + ReadBitsAt(data, ref u, n, streamOffset);
        n = (uint)(ml >> 32) & 0xFF;
        matchLength = (nint)((uint)(ml >> 40) + ReadBitsAt(data, ref u, n, streamOffset));
        n = (uint)(ll >> 32) & 0xFF;
        literalLength = (nint)((uint)(ll >> 40) + ReadBitsAt(data, ref u, n, streamOffset));
        llNext = mlNext = ofNext = 0;
        if (!final)
        {
            llNext = ReadBitsAt(data, ref u, (uint)(ll >> 24) & 0xFF, streamOffset);
            mlNext = ReadBitsAt(data, ref u, (uint)(ml >> 24) & 0xFF, streamOffset);
            ofNext = ReadBitsAt(data, ref u, (uint)(of >> 24) & 0xFF, streamOffset);
        }

        unread = u;
    }

    // The next n (at most 56) unread bits, taking them off `unread`.
    private static uint ReadBitsAt(ReadOnlySpan<byte> data, ref nint unread, uint n, long streamOffset)
    {
        nint p = (unread - 57) >> 3;
        if (p < 0)
        {
            p = 0;
        }

        nint shift = unread - (p << 3) - (nint)n;
        if (shift < 0)
        {
            ThrowStreamEnd(streamOffset);
        }

        unread -= (nint)n;
        return Bits(Raw.ReadUInt64(data, p) >> (int)shift, n);
    }

    private static void ThrowStreamEnd(long streamOffset) =>
        ZstdFormat.ThrowCorrupt(streamOffset, "a sequence bitstream that is consumed exactly by its sequences");

    private const int SmallBlockSequences = 4;

    // The two flavours of the fast loop, as type arguments so that each compiles to its own code without a flag test.
    private interface ILoopKind
    {
        // True when the stream may be nearer than 121 bits to its start, where a refill is clamped to the first eight bytes.
        static abstract bool NearStart { get; }
    }

    private struct MainLoop : ILoopKind
    {
        public static bool NearStart => false;
    }

    private struct TailLoop : ILoopKind
    {
        public static bool NearStart => true;
    }

    // The straightforward sequence loop on the tier-3 sequence decoder, for blocks with few sequences.
    private static int ExecuteFewSequences(
        ref ZstdSequenceDecoder sequences,
        int sequenceCount,
        ReadOnlySpan<byte> literals,
        Span<byte> buffer,
        int position,
        int end,
        ref ZstdRepeatOffsets reps,
        long windowSize,
        long frameOutput,
        ReadOnlySpan<byte> dictionary,
        long streamOffset,
        out int literalPosition)
    {
        ZstdRepeatOffsets r = reps;
        int current = position;
        int litPos = 0;
        for (int i = 0; i < sequenceCount; i++)
        {
            sequences.Next(out int literalLength, out int matchLength, out uint offsetValue, i != sequenceCount - 1);
            if (literalLength > literals.Length - litPos || literalLength > end - current)
            {
                ThrowSequenceLimit(literalLength > literals.Length - litPos, streamOffset);
            }

            literals.Slice(litPos, literalLength).CopyTo(buffer[current..]);
            litPos += literalLength;
            current += literalLength;

            uint offset = r.Resolve(offsetValue, literalLength);
            long produced = frameOutput + (current - position);
            if ((ulong)(offset - 1) >= (ulong)(produced < windowSize ? produced : windowSize))
            {
                CheckOffsetIntoDictionary(offset, produced, windowSize, dictionary.Length, streamOffset);
            }

            if (matchLength > end - current)
            {
                ThrowSequenceLimit(false, streamOffset);
            }

            CopyMatch(buffer, current, (int)offset, matchLength, dictionary);
            current += matchLength;
        }

        reps = r;
        literalPosition = litPos;
        return current;
    }

    // Runs sequences while each needs only one refill, the stream is not near its start and every copy is the plain
    // piecewise kind. Never handles the last sequence (index `last`), which has no state update bits. Contains no
    // calls, uses native-width indices and reads each table row as one 64-bit value (see SequenceEntry): that keeps the
    // loop's variables in registers, which matters more here than any instruction count.
    private static void RunFast<TLoop>(
        ref SequenceState st,
        SequenceEntry[] rows,
        ReadOnlySpan<byte> data,
        int last,
        ReadOnlySpan<byte> literals,
        Span<byte> buffer,
        int limitInt,
        int windowInt,
        ReadOnlySpan<byte> dictionary,
        long producedBase)
        where TLoop : struct, ILoopKind
    {
        nuint llState = st.LiteralLengthState;
        nuint ofState = st.OffsetState;
        nuint mlState = st.MatchLengthState;
        nint current = st.Current;
        nint litPos = st.LiteralPosition;
        int i = st.Index;
        uint r1 = st.Reps.Offset1;
        uint r2 = st.Reps.Offset2;
        uint r3 = st.Reps.Offset3;
        nint litSafe = literals.Length - 32;
        nint limit = limitInt;
        nint window = windowInt;

        // The unread bits of the stream: 8 * Pointer + 64 - Consumed. A refill moves Pointer down by the whole bytes
        // consumed, so with U unread bits the register starts at byte (U - 57) >> 3 and its first unread bit is
        // U - 8 * that bit-index of the register. Fewer than 121 unread bits means Pointer < 8: leave it to the general code.
        nint unread = (st.Pointer << 3) + 64 - st.Consumed;
        nint startUnread = unread;

        while (i < last && (TLoop.NearStart || unread >= 121))
        {
            // Each state is below its table's size (see ZstdSequenceDecoder.Next).
            ulong ll = Raw.Row(rows, llState);
            ulong of = Raw.Row(rows, ofState);
            ulong ml = Raw.Row(rows, mlState);

            // Adding the rows adds their Total fields (bits 16 to 23) without carries from or into them.
            int total = (byte)((ll + ml + of) >> 16);
            if (total > 56)
            {
                break;
            }

            // One refill covers the whole step: the register starts at the byte p = (unread - 57) >> 3 (at least 8 here, so the
            // load cannot reach past the start of the stream) and holds at least 57 unread bits, the step needs at most 56.
            // Shifting it right by the unread bits below the step leaves the step's bits at the bottom: from the top the
            // offset, match length and literals length extra bits, then the three state updates, the offset's last. Each
            // field is cut from the bottom up. A row shifted right by 24 or 32 has the bit count in its low byte (the bits
            // above are other fields, which the count's use ignores).
            nint p = (unread - 57) >> 3;
            nint shift;
            if (TLoop.NearStart)
            {
                // Close to the start of the stream the register is the first eight bytes and the step must fit in what is unread.
                if (p < 0)
                {
                    p = 0;
                }

                shift = unread - (p << 3) - total;
                if (shift < 0)
                {
                    break;
                }
            }
            else
            {
                shift = ((unread - 57) & 7) + 57 - total;
            }

            ulong c = Raw.ReadUInt64(data, p) >> (int)shift;
            uint n = (uint)(of >> 24);
            ofState = (ushort)of + Bits(c, n);
            c >>= (int)n;
            n = (uint)(ml >> 24);
            mlState = (ushort)ml + Bits(c, n);
            c >>= (int)n;
            n = (uint)(ll >> 24);
            llState = (ushort)ll + Bits(c, n);
            c >>= (int)n;
            n = (uint)(ll >> 32);
            nint literalLength = (nint)((uint)(ll >> 40) + Bits(c, n));
            c >>= (int)n;
            n = (uint)(ml >> 32);
            nint matchLength = (nint)((uint)(ml >> 40) + Bits(c, n));
            c >>= (int)n;
            n = (uint)(of >> 32);
            uint offsetValue = (1u << (int)n) + Bits(c, n);
            unread -= total;
            i++;

            // Literals: whole 16-byte pieces, at least two, which may run up to 31 bytes past the literals; both sides stay
            // inside their buffers by the two limits.
            if (litPos + literalLength > litSafe || current + literalLength > limit)
            {
                st.Pending = 1;
                st.OffsetValue = offsetValue;
                st.MatchLength = (int)matchLength;
                st.LiteralLength = (int)literalLength;
                break;
            }

            Raw.Copy16(buffer, current, literals, litPos);
            Raw.Copy16(buffer, current + 16, literals, litPos + 16);
            if (literalLength > 32)
            {
                for (nint k = 32; k < literalLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, literals, litPos + k);
                }
            }

            litPos += literalLength;
            current += literalLength;

            // RFC 8878 §3.1.1.5: resolve the offset value against the repeat offsets: values above 3 are new offsets.
            uint offset;
            if (offsetValue > 3)
            {
                offset = offsetValue - 3;
                r3 = r2;
                r2 = r1;
                r1 = offset;
            }
            else
            {
                // With no literals, repeat codes 1, 2 and 3 mean the second offset, the third, and the first minus one.
                uint index = offsetValue - 1 + (literalLength == 0 ? 1u : 0u);
                if (index == 0)
                {
                    offset = r1;
                }
                else
                {
                    offset = index == 1 ? r2 : (index == 2 ? r3 : r1 - 1);
                    r3 = index == 1 ? r3 : r2;
                    r2 = r1;
                    r1 = offset;
                }
            }

            // RFC 8878 §5: a match reaches back at most a window, except that while the frame's output is still within the window
            // the dictionary's content counts as history before it. What the cases below do not take (an offset below 8, a copy
            // that would end past the limit, a match that runs on from the dictionary into the output or ends near its end, an
            // offset that is not valid) goes to the general code, which also tells corrupt data from the rare valid case.
            bool general = offset < 8 || current + matchLength > limit;
            if (!general)
            {
                if (offset <= (nuint)current)
                {
                    general = offset > (nuint)window;
                    if (!general)
                    {
                        // Pieces of 16 (or 8) bytes that read only bytes written before the piece starts (the offset is at least
                        // the piece size) and overshoot by less than 32 bytes, inside the buffer and the block (limit).
                        nint from = current - (nint)offset;
                        if (offset >= 16)
                        {
                            Raw.Copy16(buffer, current, buffer, from);
                            Raw.Copy16(buffer, current + 16, buffer, from + 16);
                            if (matchLength > 32)
                            {
                                for (nint k = 32; k < matchLength; k += 16)
                                {
                                    Raw.Copy16(buffer, current + k, buffer, from + k);
                                }
                            }
                        }
                        else
                        {
                            for (nint k = 0; k < matchLength; k += 8)
                            {
                                Raw.Copy8(buffer, current + k, buffer, from + k);
                            }
                        }
                    }
                }
                else
                {
                    // The match starts in the dictionary, `back` bytes before its end. Pieces are copied while a whole piece
                    // after the match's end is still inside the dictionary.
                    long produced = producedBase + current;
                    long back = offset - produced;
                    general = back < 1 || back > dictionary.Length || back < matchLength + 32 || produced > window;
                    if (!general)
                    {
                        nint source = dictionary.Length - (nint)back;
                        Raw.Copy16(buffer, current, dictionary, source);
                        Raw.Copy16(buffer, current + 16, dictionary, source + 16);
                        for (nint k = 32; k < matchLength; k += 16)
                        {
                            Raw.Copy16(buffer, current + k, dictionary, source + k);
                        }
                    }
                }
            }

            if (general)
            {
                st.Pending = 2;
                st.OffsetValue = offset;
                st.MatchLength = (int)matchLength;
                break;
            }

            current += matchLength;
        }

        if (unread != startUnread)
        {
            nint p = (unread - 57) >> 3;
            if (TLoop.NearStart && p < 0)
            {
                p = 0;
            }

            st.Pointer = (int)p;
            st.Consumed = 64 + ((int)p << 3) - (int)unread;
            st.Container = Raw.ReadUInt64(data, p);
        }

        st.LiteralLengthState = (uint)llState;
        st.OffsetState = (uint)ofState;
        st.MatchLengthState = (uint)mlState;
        st.Current = (int)current;
        st.LiteralPosition = (int)litPos;
        st.Index = i;
        st.Reps = new ZstdRepeatOffsets { Offset1 = r1, Offset2 = r2, Offset3 = r3 };
    }

    // One sequence the careful way: refills as often as the bits require, works near the start of the stream, and
    // finishes a sequence the fast loop set aside. Index counts the sequence as done only when its match is copied; the fast
    // loop counts a sequence it set aside as started, so Pending says where to resume.
    private static void RunGeneral(
        ref SequenceState st,
        SequenceEntry[] rows,
        ReadOnlySpan<byte> data,
        int sequenceCount,
        ReadOnlySpan<byte> literals,
        Span<byte> buffer,
        int end,
        long producedBase,
        long windowSize,
        ReadOnlySpan<byte> dictionary,
        long streamOffset)
    {
        uint offset;
        int matchLength;
        int pending = st.Pending;
        st.Pending = 0;
        if (pending == 2)
        {
            offset = st.OffsetValue;
            matchLength = st.MatchLength;
        }
        else
        {
            uint offsetValue;
            int literalLength;
            if (pending == 1)
            {
                offsetValue = st.OffsetValue;
                matchLength = st.MatchLength;
                literalLength = st.LiteralLength;
            }
            else
            {
                SequenceEntry ll = Raw.Get(rows, (int)st.LiteralLengthState);
                SequenceEntry of = Raw.Get(rows, (int)st.OffsetState);
                SequenceEntry ml = Raw.Get(rows, (int)st.MatchLengthState);
                ReloadBits(data, ref st.Container, ref st.Consumed, ref st.Pointer);
                offsetValue = (1u << of.Extra) + ReadBits(st.Container, ref st.Consumed, of.Extra);
                if (of.Extra > 20)
                {
                    ReloadBits(data, ref st.Container, ref st.Consumed, ref st.Pointer);
                }

                matchLength = (int)(ml.Base + ReadBits(st.Container, ref st.Consumed, ml.Extra));
                literalLength = (int)(ll.Base + ReadBits(st.Container, ref st.Consumed, ll.Extra));
                if (st.Index != sequenceCount - 1)
                {
                    ReloadBits(data, ref st.Container, ref st.Consumed, ref st.Pointer);
                    st.LiteralLengthState = ll.Next + ReadBits(st.Container, ref st.Consumed, ll.NumBits);
                    st.MatchLengthState = ml.Next + ReadBits(st.Container, ref st.Consumed, ml.NumBits);
                    st.OffsetState = of.Next + ReadBits(st.Container, ref st.Consumed, of.NumBits);
                }
            }

            if (literalLength > literals.Length - st.LiteralPosition || literalLength > end - st.Current)
            {
                ThrowSequenceLimit(literalLength > literals.Length - st.LiteralPosition, streamOffset);
            }

            literals.Slice(st.LiteralPosition, literalLength).CopyTo(buffer[st.Current..]);
            st.LiteralPosition += literalLength;
            st.Current += literalLength;
            offset = st.Reps.Resolve(offsetValue, literalLength);
        }

        long produced = producedBase + st.Current;
        if ((ulong)(offset - 1) >= (ulong)(produced < windowSize ? produced : windowSize))
        {
            CheckOffsetIntoDictionary(offset, produced, windowSize, dictionary.Length, streamOffset);
        }

        if (matchLength > end - st.Current)
        {
            ThrowSequenceLimit(false, streamOffset);
        }

        CopyMatch(buffer, st.Current, (int)offset, matchLength, dictionary);
        st.Current += matchLength;
        if (pending == 0)
        {
            st.Index++;
        }
    }

    // Called when an offset is not within min(output so far, window): valid only if it reaches into the dictionary while the
    // output so far is within the window (RFC 8878 §5).
    private static void CheckOffsetIntoDictionary(uint offset, long produced, long windowSize, int dictionaryLength, long streamOffset)
    {
        bool inRange = offset != 0 && offset <= int.MaxValue && offset > produced && produced <= windowSize && offset - produced <= dictionaryLength;
        if (!inRange)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "a match offset from 1 to the window size and the history available");
        }
    }

    private static void ThrowSequenceLimit(bool literals, long streamOffset)
    {
        if (literals)
        {
            ZstdFormat.ThrowCorrupt(streamOffset, "a literals length within the block's literals");
        }

        ZstdFormat.ThrowCorrupt(streamOffset, "block output within the block size limit and the frame content size");
    }

    // Refills the register so at least 56 bits can be read; the same rule as ZstdReverseBitReader.Reload.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReloadBits(ReadOnlySpan<byte> data, ref ulong container, ref int consumed, ref int ptr)
    {
        if (ptr >= 8)
        {
            // At least eight bytes remain below the register, so the refill never reaches the start.
            ptr -= consumed >> 3;
            consumed &= 7;
            container = Raw.ReadUInt64(data, ptr);
        }
        else if (ptr != 0)
        {
            int bytes = consumed >> 3;
            if (ptr >= bytes)
            {
                ptr -= bytes;
                consumed &= 7;
            }
            else
            {
                consumed -= ptr << 3;
                ptr = 0;
            }

            container = Raw.ReadUInt64(data, ptr);
        }
    }

    // The low n bits of c, where n is the low byte of `n` (0 to 56); the bits above it are ignored: BZHI reads only the low byte and
    // a C# shift count is taken modulo 64.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Bits(ulong c, uint n) =>
        (uint)(Bmi2.X64.IsSupported ? Bmi2.X64.ZeroHighBits(c, n) : c & ~(~0UL << (int)n));

    // The n bits (0 to 56) of c that end sh bits above its least significant bit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Field(ulong c, int sh, int n)
    {
        ulong v = c >> sh;
        return (uint)(Bmi2.X64.IsSupported ? Bmi2.X64.ZeroHighBits(v, (uint)n) : v & ((1UL << n) - 1));
    }

    // The top n bits of x (0 to 56) as an integer; two shifts so that n == 0 gives 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Top(ulong x, int n) => (uint)((x >> 1) >> (63 - n));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ReadBits(ulong container, ref int consumed, int count)
    {
        uint value = (uint)(((container << (consumed & 63)) >> 1) >> (63 - count));
        consumed += count;
        return value;
    }

    private static void CopyMatch(Span<byte> buffer, int current, int offset, int length, ReadOnlySpan<byte> dictionary)
    {
        if (offset <= current)
        {
            int source = current - offset;
            if (buffer.Length - current - length >= 16 && offset >= 8)
            {
                // Copy in 16- (or 8-) byte pieces. A piece reads only bytes written before the piece starts because the
                // offset is at least the piece size, and the last piece writes less than 16 bytes past the match, inside
                // the buffer (checked above); those bytes are overwritten by the output that follows.
                if (offset >= 16)
                {
                    for (int i = 0; i < length; i += 16)
                    {
                        Raw.Copy16(buffer, current + i, buffer, source + i);
                    }
                }
                else
                {
                    for (int i = 0; i < length; i += 8)
                    {
                        Raw.Copy8(buffer, current + i, buffer, source + i);
                    }
                }
            }
            else if (offset >= length)
            {
                buffer.Slice(source, length).CopyTo(buffer[current..]);
            }
            else if (offset == 1)
            {
                buffer.Slice(current, length).Fill(buffer[source]);
            }
            else
            {
                // The match overlaps its own output: it repeats the last `offset` bytes.
                for (int k = 0; k < length; k++)
                {
                    buffer[current + k] = buffer[source + k];
                }
            }

            return;
        }

        // The match starts in the dictionary and may run on into the output (RFC 8878 §5).
        int back = offset - current;
        if (back > dictionary.Length)
        {
            ZstdFormat.ThrowCorrupt(0, "a match offset inside the dictionary");
        }

        int fromDictionary = Math.Min(length, back);
        dictionary.Slice(dictionary.Length - back, fromDictionary).CopyTo(buffer[current..]);
        for (int k = 0; k < length - fromDictionary; k++)
        {
            buffer[current + fromDictionary + k] = buffer[k];
        }
    }

    private static FseDecodingTable SelectTable(
        int mode,
        FseDecodingTable? previous,
        FseDecodingTable scratch,
        FseDecodingTable predefined,
        int maxSymbol,
        int maxAccuracyLog,
        ReadOnlySpan<byte> block,
        ref int p,
        long blockOffset)
    {
        switch (mode)
        {
            case 0:
                return predefined;
            case 1:
                if (p >= block.Length)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + p, "a one-byte RLE symbol");
                }

                if (block[p] > maxSymbol)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + p, $"an RLE symbol of at most {maxSymbol}, found {block[p]}");
                }

                scratch.BuildSingleSymbol(block[p++]);
                return scratch;
            case 2:
                p += scratch.ReadDescription(block[p..], maxSymbol, maxAccuracyLog, blockOffset + p);
                return scratch;
            default:
                if (previous is null)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + p, "a table from an earlier block or the dictionary to repeat");
                }

                return previous;
        }
    }

    // RFC 8878 §3.1.1.3.1: Literals_Section_Header and the literals it describes. `used` receives the section size.
    private ReadOnlySpan<byte> DecodeLiterals(ReadOnlySpan<byte> block, out int used, long blockOffset)
    {
        if (block.Length == 0)
        {
            ZstdFormat.ThrowCorrupt(blockOffset, "a literals section");
        }

        int b0 = block[0];
        int type = b0 & 3;
        int sizeFormat = (b0 >> 2) & 3;
        int regenerated;
        int compressed = 0;
        int headerSize;

        if (type < 2)
        {
            switch (sizeFormat)
            {
                case 1:
                    headerSize = 2;
                    RequireHeader(block, headerSize, blockOffset);
                    regenerated = (b0 >> 4) + (block[1] << 4);
                    break;
                case 3:
                    headerSize = 3;
                    RequireHeader(block, headerSize, blockOffset);
                    regenerated = (b0 >> 4) + (block[1] << 4) + (block[2] << 12);
                    break;
                default:
                    headerSize = 1;
                    regenerated = b0 >> 3;
                    break;
            }
        }
        else
        {
            switch (sizeFormat)
            {
                case 0:
                case 1:
                    headerSize = 3;
                    RequireHeader(block, headerSize, blockOffset);
                    uint v3 = (uint)(b0 | (block[1] << 8) | (block[2] << 16));
                    regenerated = (int)((v3 >> 4) & 0x3FF);
                    compressed = (int)((v3 >> 14) & 0x3FF);
                    break;
                case 2:
                    headerSize = 4;
                    RequireHeader(block, headerSize, blockOffset);
                    uint v4 = BinaryPrimitives.ReadUInt32LittleEndian(block);
                    regenerated = (int)((v4 >> 4) & 0x3FFF);
                    compressed = (int)(v4 >> 18);
                    break;
                default:
                    headerSize = 5;
                    RequireHeader(block, headerSize, blockOffset);
                    ulong v5 = BinaryPrimitives.ReadUInt32LittleEndian(block) | ((ulong)block[4] << 32);
                    regenerated = (int)((v5 >> 4) & 0x3FFFF);
                    compressed = (int)(v5 >> 22);
                    break;
            }
        }

        if (regenerated > ZstdFormat.BlockSizeMax)
        {
            ZstdFormat.ThrowCorrupt(blockOffset, "at most 128 KiB of literals");
        }

        switch (type)
        {
            case 0: // Raw_Literals_Block
                if (block.Length - headerSize < regenerated)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + headerSize, $"{regenerated} bytes of raw literals");
                }

                used = headerSize + regenerated;
                if (regenerated <= RawLiteralsCopyMax)
                {
                    // Small sections are copied so that the sequence loop's wide copies have room behind them.
                    EnsureLiterals(regenerated);
                    block.Slice(headerSize, regenerated).CopyTo(_literals);
                    _literalsHaveSlack = true;
                    return _literals.AsSpan(0, regenerated);
                }

                _literalsHaveSlack = false;
                return block.Slice(headerSize, regenerated);

            case 1: // RLE_Literals_Block
                if (block.Length - headerSize < 1)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + headerSize, "the byte of an RLE literals block");
                }

                EnsureLiterals(regenerated);
                _literalsHaveSlack = true;
                _literals.AsSpan(0, regenerated).Fill(block[headerSize]);
                used = headerSize + 1;
                return _literals.AsSpan(0, regenerated);
        }

        // Compressed_Literals_Block (2) and Treeless_Literals_Block (3).
        if (compressed == 0 || block.Length - headerSize < compressed)
        {
            ZstdFormat.ThrowCorrupt(blockOffset + headerSize, $"{compressed} bytes of Huffman-coded literals within the block");
        }

        ReadOnlySpan<byte> data = block.Slice(headerSize, compressed);
        long dataOffset = blockOffset + headerSize;
        HuffmanDecodingTable table;
        if (type == 2)
        {
            int treeSize = _huffmanScratch.ReadDescription(data, dataOffset);
            data = data[treeSize..];
            dataOffset += treeSize;
            _huffman = table = _huffmanScratch;
        }
        else
        {
            if (_huffman is null)
            {
                ZstdFormat.ThrowCorrupt(blockOffset, "a Huffman table from an earlier block or the dictionary for a treeless literals block");
            }

            table = _huffman;
        }

        EnsureLiterals(regenerated);
        _literalsHaveSlack = true;
        Span<byte> output = _literals.AsSpan(0, regenerated);
        if (sizeFormat == 0)
        {
            // A dictionary's table is never rebuilt, so its pair table is built once and shared; a table read from a block
            // is used once or twice, which does not pay for building one.
            table.DecodeStream(data, output, dataOffset, !ReferenceEquals(table, _huffmanScratch) && regenerated >= 32 ? table.GetPairs() : null);
        }
        else
        {
            table.DecodeFourStreams(data, output, dataOffset);
        }

        used = headerSize + compressed;
        return output;
    }

    private static void RequireHeader(ReadOnlySpan<byte> block, int size, long blockOffset)
    {
        if (block.Length < size)
        {
            ZstdFormat.ThrowCorrupt(blockOffset, $"a {size}-byte literals section header");
        }
    }

    private const int RawLiteralsCopyMax = 4096;

    private void EnsureLiterals(int size)
    {
        if (_literals.Length < size + LiteralSlack)
        {
            _literals = new byte[Math.Min(Math.Max(size, _literals.Length * 2), ZstdFormat.BlockSizeMax) + LiteralSlack];
        }
    }
}

