using System.Buffers.Binary;

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
        for (int i = 0; i < sequenceCount; i++)
        {
            sequences.Next(out int literalLength, out int matchLength, out uint offsetValue, i != sequenceCount - 1);

            if (literalLength > literals.Length - literalPosition)
            {
                ZstdFormat.ThrowCorrupt(blockOffset + p, "a literals length within the block's literals");
            }

            if (literalLength > end - current)
            {
                ZstdFormat.ThrowCorrupt(blockOffset + p, "block output within the block size limit and the frame content size");
            }

            if (literalLength <= 16 && buffer.Length - current >= 16 && literals.Length - literalPosition >= 16)
            {
                // A fixed 16-byte copy beats a variable-length one; the bytes past the literals are overwritten
                // by what follows, and both 16-byte ranges are inside their buffers (checked just above).
                Raw.Copy16(buffer, current, literals, literalPosition);
            }
            else
            {
                literals.Slice(literalPosition, literalLength).CopyTo(buffer[current..]);
            }

            literalPosition += literalLength;
            current += literalLength;

            uint offset = reps.Resolve(offsetValue, literalLength);
            long produced = frameOutput + (current - position);
            // RFC 8878 §3.1.1.4 and §5: a match reaches at most a window back; while the output so far is within
            // the window, it may reach on into the dictionary, even past the window's size.
            bool inRange = offset <= produced
                ? offset <= windowSize
                : produced <= windowSize && offset - produced <= dictionary.Length;
            if (offset == 0 || !inRange)
            {
                ZstdFormat.ThrowCorrupt(blockOffset + p, "a match offset from 1 to the window size and the history available");
            }

            if (matchLength > end - current)
            {
                ZstdFormat.ThrowCorrupt(blockOffset + p, "block output within the block size limit and the frame content size");
            }

            CopyMatch(buffer, current, (int)offset, matchLength, dictionary);
            current += matchLength;
        }

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
