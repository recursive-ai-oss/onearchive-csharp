using System.Diagnostics.CodeAnalysis;

using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>Which of the three Deflate block types (RFC 1951 section 3.2.3) <see cref="DeflateBlockWriter"/> writes.</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateBlockKind
{
    /// <summary>Whichever of the three is smallest for the block.</summary>
    Auto = 0,

    /// <summary>Stored: the input bytes copied as they are, in pieces of at most 65535 bytes.</summary>
    Stored = 1,

    /// <summary>Compressed with the fixed Huffman codes (RFC 1951 section 3.2.6).</summary>
    Fixed = 2,

    /// <summary>Compressed with Huffman codes built from the block's own counts (RFC 1951 section 3.2.7).</summary>
    Dynamic = 3,

    /// <summary>The fixed Huffman codes, or stored when that is smaller; never dynamic. A stream of such blocks never expands by more than stored blocks would.</summary>
    FixedOrStored = 4,
}

/// <summary>
/// Turns parsed blocks into Deflate bits: builds the Huffman codes, prices the three block types and writes the cheapest.
/// The bits collect in an internal buffer that <see cref="CopyPending"/> drains.
/// </summary>
/// <remarks>
/// The writer is the bit-level end of an encoder. It allocates while its buffer grows to fit the largest block it has seen, and
/// not afterwards. It is not thread-safe.
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateBlockWriter
{
    private const int Slack = 64;
    private const int MaxStoredChunk = 65535;

    private readonly DeflateHuffmanBuilder _builder = new();
    private readonly uint[] _literalFrequencies = new uint[DeflateSequenceBuffer.LiteralLengthSymbolCount];
    private readonly uint[] _distanceFrequencies = new uint[DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly uint[] _codeLengthFrequencies = new uint[DeflateTables.CodeLengthSymbolCount];
    private readonly byte[] _literalLengths = new byte[DeflateTables.LiteralLengthSymbolCount];
    private readonly byte[] _distanceLengths = new byte[DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly byte[] _codeLengthLengths = new byte[DeflateTables.CodeLengthSymbolCount];
    private readonly ushort[] _literalCodes = new ushort[DeflateTables.LiteralLengthSymbolCount];
    private readonly ushort[] _distanceCodes = new ushort[DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly ushort[] _codeLengthCodes = new ushort[DeflateTables.CodeLengthSymbolCount];
    private readonly byte[] _allLengths = new byte[DeflateSequenceBuffer.LiteralLengthSymbolCount + DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly byte[] _tokenSymbols = new byte[DeflateSequenceBuffer.LiteralLengthSymbolCount + DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly byte[] _tokenExtras = new byte[DeflateSequenceBuffer.LiteralLengthSymbolCount + DeflateSequenceBuffer.DistanceSymbolCount];
    private readonly uint[] _lengthComposite = new uint[DeflateTables.MaxMatchLength + 1];
    private readonly uint[] _literalInfo = new uint[256];
    private readonly uint[] _distanceInfo = new uint[DeflateSequenceBuffer.DistanceSymbolCount];
    private byte[] _buffer;
    private int _writePosition;
    private int _readPosition;
    private ulong _accumulator;
    private int _bitCount;

    /// <summary>Creates a writer.</summary>
    /// <param name="initialCapacity">The starting size of the output buffer in bytes; it grows when a block needs more. The default is 64 KiB.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is below 1.</exception>
    public DeflateBlockWriter(int initialCapacity = 1 << 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        _buffer = new byte[initialCapacity + Slack];
    }

    /// <summary>Gets the number of complete bytes written and not yet copied out.</summary>
    public int PendingBytes => _writePosition - _readPosition;

    /// <summary>Gets the number of bits (0 to 7) written after the last complete byte.</summary>
    public int PartialBitCount => _bitCount;

    /// <summary>Forgets all output and any partial byte.</summary>
    public void Reset()
    {
        _writePosition = 0;
        _readPosition = 0;
        _accumulator = 0;
        _bitCount = 0;
    }

    /// <summary>Copies complete bytes out of the buffer.</summary>
    /// <param name="destination">Receives up to its length in bytes.</param>
    /// <returns>The number of bytes copied.</returns>
    public int CopyPending(Span<byte> destination)
    {
        int count = Math.Min(destination.Length, _writePosition - _readPosition);
        _buffer.AsSpan(_readPosition, count).CopyTo(destination);
        _readPosition += count;
        if (_readPosition == _writePosition)
        {
            _readPosition = 0;
            _writePosition = 0;
        }

        return count;
    }

    /// <summary>Writes raw bits, least significant first, as Deflate packs every field that is not a Huffman code.</summary>
    /// <param name="value">The bits; only the low <paramref name="count"/> are used.</param>
    /// <param name="count">How many bits, 0 to 32.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 0..32.</exception>
    public void WriteBits(uint value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 32);
        Ensure(8);
        Put(count == 32 ? value : value & ((1u << count) - 1), count);
    }

    /// <summary>Pads with zero bits to the next byte boundary.</summary>
    public void AlignToByte()
    {
        if (_bitCount > 0)
        {
            Ensure(8);
            _buffer[_writePosition] = (byte)_accumulator;
            _writePosition++;
            _accumulator = 0;
            _bitCount = 0;
        }
    }

    /// <summary>Appends whole bytes. The writer must be on a byte boundary (the zlib and gzip wrappers sit outside the blocks).</summary>
    /// <param name="bytes">The bytes.</param>
    /// <exception cref="InvalidOperationException">There is a partial byte.</exception>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if (_bitCount != 0)
        {
            throw new InvalidOperationException("Bytes can only be written on a byte boundary.");
        }

        Ensure(bytes.Length + 8);
        bytes.CopyTo(_buffer.AsSpan(_writePosition));
        _writePosition += bytes.Length;
    }

    /// <summary>Writes an empty, non-final stored block and aligns to a byte: the marker (00 00 FF FF) a sync flush leaves so that a reader can resynchronise (zlib's Z_SYNC_FLUSH).</summary>
    public void WriteEmptyStoredBlock()
    {
        Ensure(16);
        Put(0, 3);
        AlignToByte();
        Ensure(8);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_writePosition), 0xFFFF0000u);
        _writePosition += 4;
    }

    /// <summary>Writes input as stored blocks (RFC 1951 section 3.2.4), in pieces of at most 65535 bytes.</summary>
    /// <param name="input">The bytes.</param>
    /// <param name="isFinal">True to mark the last piece as the final block.</param>
    public void WriteStoredBlocks(ReadOnlySpan<byte> input, bool isFinal)
    {
        int chunks = Math.Max(1, (input.Length + MaxStoredChunk - 1) / MaxStoredChunk);
        Ensure(input.Length + (chunks * 5) + 16);
        int offset = 0;
        for (int chunk = 0; chunk < chunks; chunk++)
        {
            int length = Math.Min(MaxStoredChunk, input.Length - offset);
            bool last = chunk == chunks - 1;
            Put(isFinal && last ? 1u : 0u, 3);
            AlignToByte();
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_writePosition), (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_writePosition + 2), (ushort)~length);
            _writePosition += 4;
            input.Slice(offset, length).CopyTo(_buffer.AsSpan(_writePosition));
            _writePosition += length;
            offset += length;
        }
    }

    /// <summary>Writes one block.</summary>
    /// <param name="sequences">The parsed block. An empty buffer writes an empty block.</param>
    /// <param name="input">The bytes the sequences stand for. Needed for <see cref="DeflateBlockKind.Stored"/>, and for <see cref="DeflateBlockKind.Auto"/> to consider stored; may be empty otherwise.</param>
    /// <param name="isFinal">True to set the block's BFINAL bit.</param>
    /// <param name="kind">The block type, or <see cref="DeflateBlockKind.Auto"/> for the cheapest.</param>
    /// <exception cref="ArgumentException">Stored was asked for and <paramref name="input"/> does not cover the sequences.</exception>
    public void WriteBlock(DeflateSequenceBuffer sequences, ReadOnlySpan<byte> input, bool isFinal, DeflateBlockKind kind = DeflateBlockKind.Auto)
    {
        ArgumentNullException.ThrowIfNull(sequences);
        WriteBlock(sequences, input, input.Length == sequences.InputLength, isFinal, kind);
    }

    // The encoder knows that its input covers the sequences exactly, so it skips the check that adds the entries up.
    internal void WriteBlock(DeflateSequenceBuffer sequences, ReadOnlySpan<byte> input, bool haveInput, bool isFinal, DeflateBlockKind kind)
    {
        if (kind == DeflateBlockKind.Stored && !haveInput)
        {
            throw new ArgumentException("Stored blocks need the input bytes the sequences stand for.", nameof(input));
        }

        if (kind == DeflateBlockKind.Stored)
        {
            WriteStoredBlocks(input, isFinal);
            return;
        }

        sequences.LiteralLengthFrequencies.CopyTo(_literalFrequencies);
        sequences.DistanceFrequencies.CopyTo(_distanceFrequencies);

        // Cost of the extra bits is the same for every code.
        long extraBits = 0;
        for (int slot = 0; slot < 29; slot++)
        {
            extraBits += (long)_literalFrequencies[257 + slot] * DeflateTables.LengthExtraBits[slot];
        }

        for (int slot = 0; slot < DeflateSequenceBuffer.DistanceSymbolCount; slot++)
        {
            extraBits += (long)_distanceFrequencies[slot] * DeflateTables.DistanceExtraBits[slot];
        }

        // Fixed block: 3 header bits, fixed code lengths.
        long fixedBits = 3 + extraBits;
        ReadOnlySpan<byte> fixedLiteral = DeflateSymbols.FixedLiteralLengths;
        for (int symbol = 0; symbol < DeflateSequenceBuffer.LiteralLengthSymbolCount; symbol++)
        {
            fixedBits += (long)_literalFrequencies[symbol] * fixedLiteral[symbol];
        }

        for (int slot = 0; slot < DeflateSequenceBuffer.DistanceSymbolCount; slot++)
        {
            fixedBits += (long)_distanceFrequencies[slot] * 5;
        }

        long bestBits = fixedBits;
        DeflateBlockKind best = DeflateBlockKind.Fixed;
        int literalCount = 0;
        int distanceCount = 0;
        int codeLengthCount = 0;
        if (kind is not (DeflateBlockKind.Fixed or DeflateBlockKind.FixedOrStored))
        {
            // Force two used symbols in each alphabet so that every code is complete (RFC 1951 section 3.2.7 allows a single
            // distance code, but some decoders reject it).
            EnsureTwoSymbols(_literalFrequencies);
            EnsureTwoSymbols(_distanceFrequencies);
            _builder.BuildLengths(_literalFrequencies, 15, _literalLengths);
            _literalLengths[286] = 0;
            _literalLengths[287] = 0;
            _builder.BuildLengths(_distanceFrequencies, 15, _distanceLengths);
            long dynamicBits = 3 + 14 + extraBits + BuildDynamicHeader(out literalCount, out distanceCount, out codeLengthCount);
            for (int symbol = 0; symbol < DeflateSequenceBuffer.LiteralLengthSymbolCount; symbol++)
            {
                dynamicBits += (long)_literalFrequencies[symbol] * _literalLengths[symbol];
            }

            for (int slot = 0; slot < DeflateSequenceBuffer.DistanceSymbolCount; slot++)
            {
                dynamicBits += (long)_distanceFrequencies[slot] * _distanceLengths[slot];
            }

            // The forced symbols above were counted once each; take their bits back out is not worth the cost of exactness:
            // they add at most a few bits to the estimate, and the estimate only picks between block types.
            if (kind == DeflateBlockKind.Dynamic || dynamicBits < bestBits)
            {
                bestBits = dynamicBits;
                best = DeflateBlockKind.Dynamic;
            }
        }

        if (kind is DeflateBlockKind.Auto or DeflateBlockKind.FixedOrStored && haveInput)
        {
            int chunks = Math.Max(1, (input.Length + MaxStoredChunk - 1) / MaxStoredChunk);
            long pad = (8 - ((_bitCount + 3) & 7)) & 7;
            long storedBits = 3 + pad + 32 + (8L * input.Length) + ((chunks - 1) * 40L);
            if (storedBits < bestBits)
            {
                WriteStoredBlocks(input, isFinal);
                return;
            }
        }

        Ensure((int)Math.Min(int.MaxValue / 2, (bestBits >> 3) + 32));
        Put(isFinal ? 1u : 0u, 1);
        if (best == DeflateBlockKind.Fixed)
        {
            Put(1, 2);
            DeflateTables.GetFixedLiteralLengthCodeLengths(_literalLengths);
            _distanceLengths.AsSpan().Fill(5);
            DeflateHuffmanTable.GetCanonicalCodes(_literalLengths, _literalCodes);
            DeflateHuffmanTable.GetCanonicalCodes(_distanceLengths, _distanceCodes);
        }
        else
        {
            Put(2, 2);
            WriteDynamicHeader(literalCount, distanceCount, codeLengthCount);
        }

        WriteSequences(sequences.Sequences);
    }

    // Writes the sequences and the end of block with the codes in _literalCodes/_literalLengths and the distance arrays.
    private void WriteSequences(ReadOnlySpan<uint> sequences)
    {
        // One table per kind of symbol, each entry holding what a single load needs: a literal's code (low 16 bits) and length;
        // a match length's code with its extra bits attached (low 24 bits) and the total bit count; a distance slot's code and length.
        ReadOnlySpan<ushort> lengthBase = DeflateTables.LengthBase;
        ReadOnlySpan<byte> lengthExtra = DeflateTables.LengthExtraBits;
        for (int length = DeflateTables.MinMatchLength; length <= DeflateTables.MaxMatchLength; length++)
        {
            int slot = DeflateSymbols.LengthSlot[length];
            int symbol = 257 + slot;
            int codeLength = _literalLengths[symbol];
            uint code = _literalCodes[symbol] | ((uint)(length - lengthBase[slot]) << codeLength);
            _lengthComposite[length] = code | ((uint)(codeLength + lengthExtra[slot]) << 24);
        }

        for (int symbol = 0; symbol < 256; symbol++)
        {
            _literalInfo[symbol] = _literalCodes[symbol] | ((uint)_literalLengths[symbol] << 16);
        }

        for (int slot = 0; slot < DeflateSequenceBuffer.DistanceSymbolCount; slot++)
        {
            _distanceInfo[slot] = _distanceCodes[slot] | ((uint)_distanceLengths[slot] << 16);
        }

        ReadOnlySpan<ushort> distanceBase = DeflateTables.DistanceBase;
        ReadOnlySpan<byte> distanceExtra = DeflateTables.DistanceExtraBits;
        byte[] buffer = _buffer;
        uint[] literalInfo = _literalInfo;
        uint[] lengthComposite = _lengthComposite;
        uint[] distanceInfo = _distanceInfo;
        int position = _writePosition;
        ulong accumulator = _accumulator;
        int bits = _bitCount;
        int count = sequences.Length;
        for (int i = 0; i < count; i++)
        {
            // Bounds: every index below is a literal byte, a length 3..258, or a distance slot 0..29, all inside the tables above;
            // the buffer was sized for this block with a margin of Slack bytes past the last byte written. Whole bytes are stored
            // once per group of symbols (an unconditional eight-byte store beats a branch on the bit count). Before a group the
            // accumulator holds at most 7 bits, and a group adds at most 3 literals of 15 bits, or one match of at most 20 bits for
            // the length with its extra bits and 28 for the distance with its extra bits, so 55 bits at most.
            uint word = sequences[i];
            uint length = word & 0x1FF;
            if (length == 0)
            {
                uint info = DeflateEncRaw.Get(literalInfo, (int)(word >> 9));
                accumulator |= (ulong)(info & 0xFFFF) << bits;
                bits += (int)(info >> 16);
                if (i + 1 < count && (sequences[i + 1] & 0x1FF) == 0)
                {
                    info = DeflateEncRaw.Get(literalInfo, (int)(sequences[++i] >> 9));
                    accumulator |= (ulong)(info & 0xFFFF) << bits;
                    bits += (int)(info >> 16);
                    if (i + 1 < count && (sequences[i + 1] & 0x1FF) == 0)
                    {
                        info = DeflateEncRaw.Get(literalInfo, (int)(sequences[++i] >> 9));
                        accumulator |= (ulong)(info & 0xFFFF) << bits;
                        bits += (int)(info >> 16);
                    }
                }
            }
            else
            {
                uint composite = DeflateEncRaw.Get(lengthComposite, (int)length);
                accumulator |= (ulong)(composite & 0xFFFFFF) << bits;
                bits += (int)(composite >> 24);

                int distance = (int)(word >> 9) + 1;
                int slot = DeflateSymbols.DistanceSlot(distance);
                uint info = DeflateEncRaw.Get(distanceInfo, slot);
                int codeLength = (int)(info >> 16);
                accumulator |= ((ulong)(info & 0xFFFF) | ((ulong)(uint)(distance - distanceBase[slot]) << codeLength)) << bits;
                bits += codeLength + distanceExtra[slot];
            }

            DeflateEncRaw.Write64(buffer, position, accumulator);
            position += bits >> 3;
            accumulator >>= bits & ~7;
            bits &= 7;
        }

        accumulator |= (ulong)_literalCodes[DeflateTables.EndOfBlockSymbol] << bits;
        bits += _literalLengths[DeflateTables.EndOfBlockSymbol];
        DeflateEncRaw.Write64(buffer, position, accumulator);
        position += bits >> 3;
        accumulator >>= bits & ~7;
        bits &= 7;
        _writePosition = position;
        _accumulator = accumulator;
        _bitCount = bits;
    }

    private static void EnsureTwoSymbols(uint[] frequencies)
    {
        int used = 0;
        foreach (uint f in frequencies)
        {
            if (f != 0 && ++used == 2)
            {
                return;
            }
        }

        for (int i = 0; used < 2; i++)
        {
            if (frequencies[i] == 0)
            {
                frequencies[i] = 1;
                used++;
            }
        }
    }

    // RFC 1951 section 3.2.7: the code lengths of both alphabets, run-length coded with symbols 16, 17 and 18 and then
    // Huffman coded themselves. Returns the header's size in bits (past the first 17 bits that BuildDynamic callers add).
    private int BuildDynamicHeader(out int literalCount, out int distanceCount, out int codeLengthCount)
    {
        literalCount = DeflateSequenceBuffer.LiteralLengthSymbolCount;
        while (literalCount > 257 && _literalLengths[literalCount - 1] == 0)
        {
            literalCount--;
        }

        distanceCount = DeflateSequenceBuffer.DistanceSymbolCount;
        while (distanceCount > 1 && _distanceLengths[distanceCount - 1] == 0)
        {
            distanceCount--;
        }

        _literalLengths.AsSpan(0, literalCount).CopyTo(_allLengths);
        _distanceLengths.AsSpan(0, distanceCount).CopyTo(_allLengths.AsSpan(literalCount));
        int total = literalCount + distanceCount;

        Array.Clear(_codeLengthFrequencies);
        int tokens = 0;
        int i = 0;
        while (i < total)
        {
            byte value = _allLengths[i];
            int run = 1;
            while (i + run < total && _allLengths[i + run] == value)
            {
                run++;
            }

            i += run;
            if (value == 0)
            {
                while (run >= 11)
                {
                    int take = Math.Min(run, 138);
                    _tokenSymbols[tokens] = 18;
                    _tokenExtras[tokens++] = (byte)(take - 11);
                    run -= take;
                }

                if (run >= 3)
                {
                    _tokenSymbols[tokens] = 17;
                    _tokenExtras[tokens++] = (byte)(run - 3);
                    run = 0;
                }
            }
            else
            {
                _tokenSymbols[tokens] = value;
                _tokenExtras[tokens++] = 0;
                run--;
                while (run >= 3)
                {
                    int take = Math.Min(run, 6);
                    _tokenSymbols[tokens] = 16;
                    _tokenExtras[tokens++] = (byte)(take - 3);
                    run -= take;
                }
            }

            while (run > 0)
            {
                _tokenSymbols[tokens] = value;
                _tokenExtras[tokens++] = 0;
                run--;
            }
        }

        _tokenCount = tokens;
        for (int t = 0; t < tokens; t++)
        {
            _codeLengthFrequencies[_tokenSymbols[t]]++;
        }

        EnsureTwoSymbols(_codeLengthFrequencies);
        _builder.BuildLengths(_codeLengthFrequencies, 7, _codeLengthLengths);
        ReadOnlySpan<byte> order = DeflateTables.CodeLengthOrder;
        codeLengthCount = DeflateTables.CodeLengthSymbolCount;
        while (codeLengthCount > 4 && _codeLengthLengths[order[codeLengthCount - 1]] == 0)
        {
            codeLengthCount--;
        }

        long bits = 3L * codeLengthCount;
        for (int t = 0; t < tokens; t++)
        {
            int symbol = _tokenSymbols[t];
            bits += _codeLengthLengths[symbol] + (symbol == 16 ? 2 : symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        }

        return (int)bits;
    }

    private int _tokenCount;

    private void WriteDynamicHeader(int literalCount, int distanceCount, int codeLengthCount)
    {
        Put((uint)(literalCount - 257), 5);
        Put((uint)(distanceCount - 1), 5);
        Put((uint)(codeLengthCount - 4), 4);
        ReadOnlySpan<byte> order = DeflateTables.CodeLengthOrder;
        for (int i = 0; i < codeLengthCount; i++)
        {
            Put(_codeLengthLengths[order[i]], 3);
        }

        DeflateHuffmanTable.GetCanonicalCodes(_codeLengthLengths, _codeLengthCodes);
        for (int t = 0; t < _tokenCount; t++)
        {
            int symbol = _tokenSymbols[t];
            Put(_codeLengthCodes[symbol], _codeLengthLengths[symbol]);
            switch (symbol)
            {
                case 16:
                    Put(_tokenExtras[t], 2);
                    break;
                case 17:
                    Put(_tokenExtras[t], 3);
                    break;
                case 18:
                    Put(_tokenExtras[t], 7);
                    break;
            }
        }

        DeflateHuffmanTable.GetCanonicalCodes(_literalLengths, _literalCodes);
        DeflateHuffmanTable.GetCanonicalCodes(_distanceLengths, _distanceCodes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(uint value, int count)
    {
        _accumulator |= (ulong)value << _bitCount;
        _bitCount += count;
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_writePosition), _accumulator);
        _writePosition += _bitCount >> 3;
        _accumulator >>= _bitCount & ~7;
        _bitCount &= 7;
    }

    private void Ensure(int extra)
    {
        if (_buffer.Length - _writePosition >= extra + Slack)
        {
            return;
        }

        if (_readPosition > 0)
        {
            // Compact: drop what has been copied out. The partial byte lives in the accumulator, so nothing else moves.
            Buffer.BlockCopy(_buffer, _readPosition, _buffer, 0, _writePosition - _readPosition);
            _writePosition -= _readPosition;
            _readPosition = 0;
            if (_buffer.Length - _writePosition >= extra + Slack)
            {
                return;
            }
        }

        Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _writePosition + extra + Slack));
    }
}
