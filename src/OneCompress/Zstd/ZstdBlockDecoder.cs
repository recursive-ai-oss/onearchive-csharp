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
            ref sequences, literalLengths, offsets, matchLengths, block[p..], sequenceCount, literals, buffer, position, end, ref reps, windowSize, frameOutput,
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
            Array.Copy(literalLengths.GetSequenceEntries(0), 0, _rows, 0, 1 << literalLengths.AccuracyLog);
            _rowsLiteralLengths = literalLengths;
            _rowsLiteralLengthsVersion = literalLengths.Version;
        }

        if (!ReferenceEquals(_rowsOffsets, offsets) || _rowsOffsetsVersion != offsets.Version)
        {
            Array.Copy(offsets.GetSequenceEntries(1), 0, _rows, 1 << FseDecodingTable.MaxAccuracyLog, 1 << offsets.AccuracyLog);
            _rowsOffsets = offsets;
            _rowsOffsetsVersion = offsets.Version;
        }

        if (!ReferenceEquals(_rowsMatchLengths, matchLengths) || _rowsMatchLengthsVersion != matchLengths.Version)
        {
            Array.Copy(matchLengths.GetSequenceEntries(2), 0, _rows, 2 << FseDecodingTable.MaxAccuracyLog, 1 << matchLengths.AccuracyLog);
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
        if (sequenceCount < SmallBlockSequences)
        {
            // Expanding the tables costs more than a handful of sequences take to decode the plain way.
            return ExecuteFewSequences(
                ref sequences, sequenceCount, literals, buffer, position, end, ref reps, windowSize, frameOutput, dictionary,
                streamOffset, out literalPosition);
        }

        SequenceEntry[] rows = CombinedRows(literalLengthTable, offsetTable, matchLengthTable);
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
            RunFast(ref st, rows, data, sequenceCount - 1, literals, buffer, limit, window);
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

    private const int SmallBlockSequences = 32;

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
    // 16-byte-piece kind. Never handles the last sequence (index `last`), which has no state update bits. Contains no
    // calls: that, and the few variables, keep the loop's state in registers.
    private static void RunFast(
        ref SequenceState st,
        SequenceEntry[] rows,
        ReadOnlySpan<byte> data,
        int last,
        ReadOnlySpan<byte> literals,
        Span<byte> buffer,
        int limit,
        int window)
    {
        uint llState = st.LiteralLengthState;
        uint ofState = st.OffsetState;
        uint mlState = st.MatchLengthState;
        int current = st.Current;
        int litPos = st.LiteralPosition;
        int i = st.Index;
        uint r1 = st.Reps.Offset1;
        uint r2 = st.Reps.Offset2;
        uint r3 = st.Reps.Offset3;
        int litSafe = literals.Length - 32;

        // The unread bits of the stream: 8 * Pointer + 64 - Consumed. A refill moves Pointer down by the whole bytes
        // consumed, so with U unread bits the register starts at byte (U - 57) >> 3 and its first unread bit is
        // U - 8 * that bit-index of the register. Fewer than 121 unread bits means Pointer < 8: leave it to the general code.
        int unread = (st.Pointer << 3) + 64 - st.Consumed;
        int startUnread = unread;

        while (i < last && unread >= 121)
        {
            // Each state is below its table's size (see ZstdSequenceDecoder.Next); the rows are read field by field from the table.
            ref readonly SequenceEntry ll = ref Raw.Ref(rows, (int)llState);
            ref readonly SequenceEntry of = ref Raw.Ref(rows, (int)ofState);
            ref readonly SequenceEntry ml = ref Raw.Ref(rows, (int)mlState);
            int ofBits = of.Extra;
            int mlBits = ml.Extra;
            int llBits = ll.Extra;
            int total = ofBits + mlBits + llBits + ll.NumBits + ml.NumBits + of.NumBits;
            if (total > 56)
            {
                break;
            }

            // One refill covers the whole step: the register starts at the byte p = (unread - 57) >> 3 (at least 8 here, so the
            // load cannot reach past the start of the stream) and holds at least 57 unread bits, the step needs at most 56.
            // Each field ends at a position known from the table rows alone, so the six reads are independent of each other.
            int p = (unread - 57) >> 3;
            ulong c = Raw.ReadUInt64(data, p);
            int sh = unread - (p << 3) - ofBits;
            uint offsetValue = of.Base + Field(c, sh, ofBits);
            sh -= mlBits;
            int matchLength = (int)(ml.Base + Field(c, sh, mlBits));
            sh -= llBits;
            int literalLength = (int)(ll.Base + Field(c, sh, llBits));
            sh -= ll.NumBits;
            llState = ll.Next + Field(c, sh, ll.NumBits);
            sh -= ml.NumBits;
            mlState = ml.Next + Field(c, sh, ml.NumBits);
            sh -= of.NumBits;
            ofState = of.Next + Field(c, sh, of.NumBits);
            unread -= total;
            i++;

            // Literals: whole 16-byte pieces, at least two, which may run up to 31 bytes past the literals; both sides stay
            // inside their buffers by the two limits.
            if (litPos + literalLength > litSafe || current + literalLength > limit)
            {
                st.Pending = 1;
                st.OffsetValue = offsetValue;
                st.MatchLength = matchLength;
                st.LiteralLength = literalLength;
                break;
            }

            Raw.Copy16(buffer, current, literals, litPos);
            Raw.Copy16(buffer, current + 16, literals, litPos + 16);
            if (literalLength > 32)
            {
                for (int k = 32; k < literalLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, literals, litPos + k);
                }
            }

            litPos += literalLength;
            current += literalLength;

            // RFC 8878 §3.1.1.5: resolve the offset value against the repeat offsets.
            var repeats = new ZstdRepeatOffsets { Offset1 = r1, Offset2 = r2, Offset3 = r3 };
            uint offset = repeats.Resolve(offsetValue, literalLength);
            r1 = repeats.Offset1;
            r2 = repeats.Offset2;
            r3 = repeats.Offset3;

            // RFC 8878 §5: a match reaches back at most a window and no further than the start of the buffer (which holds
            // at least the window's worth of history). An offset that fails these, or is below 16, or ends past the limit,
            // goes to the general code, which also tells a dictionary match from corrupt data.
            if (offset < 16 || offset > (uint)Math.Min(current, window) || current + matchLength > limit)
            {
                st.Pending = 2;
                st.OffsetValue = offset;
                st.MatchLength = matchLength;
                break;
            }

            // Pieces of 16 bytes that read only bytes written before the piece starts (the offset is at least 16) and
            // overshoot by less than 32 bytes, inside the buffer and the block (limit).
            int from = current - (int)offset;
            Raw.Copy16(buffer, current, buffer, from);
            Raw.Copy16(buffer, current + 16, buffer, from + 16);
            if (matchLength > 32)
            {
                for (int k = 32; k < matchLength; k += 16)
                {
                    Raw.Copy16(buffer, current + k, buffer, from + k);
                }
            }

            current += matchLength;
        }

        if (unread != startUnread)
        {
            int p = (unread - 57) >> 3;
            st.Pointer = p;
            st.Consumed = 64 + (p << 3) - unread;
        }

        st.LiteralLengthState = llState;
        st.OffsetState = ofState;
        st.MatchLengthState = mlState;
        st.Current = current;
        st.LiteralPosition = litPos;
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
                offsetValue = of.Base + ReadBits(st.Container, ref st.Consumed, of.Extra);
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
                return block.Slice(headerSize, regenerated);

            case 1: // RLE_Literals_Block
                if (block.Length - headerSize < 1)
                {
                    ZstdFormat.ThrowCorrupt(blockOffset + headerSize, "the byte of an RLE literals block");
                }

                EnsureLiterals(regenerated);
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
        Span<byte> output = _literals.AsSpan(0, regenerated);
        if (sizeFormat == 0)
        {
            table.DecodeStream(data, output, dataOffset);
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

    private void EnsureLiterals(int size)
    {
        if (_literals.Length < size)
        {
            _literals = new byte[Math.Min(Math.Max(size, _literals.Length * 2), ZstdFormat.BlockSizeMax)];
        }
    }
}
