using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.BZip2;

public sealed partial class BZip2Decoder
{
    // ---- Block data: the Huffman-coded symbols, undone back to the last column of the sorted block ----

    private void StartSymbols()
    {
        (_symbolMtf ??= new BZip2MoveToFront()).Reset(_symbolMap.AsSpan(0, _usedCount));
        _nblock = 0;
        _runLength = 0;
        _runShift = 0;
        _selectorIndex = 0;
        _groupLeft = 0;
        _table = null;
    }

    // Decodes symbols until the end-of-block symbol (true) or until the source runs out (false). Reads from the source
    // directly through a 64-bit accumulator that keeps the next bits at the top. A symbol is only decoded with 20 bits at hand;
    // that never stalls a valid stream, because at least the 80 bits of the next block header or the end marker follow every block.
    private bool DecodeSymbols(ReadOnlySpan<byte> src, long baseOffset, ref int posRef)
    {
        int p = posRef;
        int callStart = p;
        ulong acc = _acc;
        int nbits = _nbits;
        uint[] tt = _tt!;
        int nblock = _nblock;
        int blockMax = _blockMax;
        BZip2MoveToFront mtf = _symbolMtf!;
        byte[] selectors = _selectors!;
        BZip2HuffmanTable[] tables = _tables!;
        BZip2HuffmanTable? table = _table;
        int selectorCount = _selectorCount;
        int selectorIndex = _selectorIndex;
        int groupLeft = _groupLeft;
        int runLength = _runLength;
        int runShift = _runShift;
        int eob = _alphabetSize - 1;
        bool done = false;
        try
        {
            while (true)
            {
                if (nbits < 20)
                {
                    if (p + 8 <= src.Length)
                    {
                        acc |= BinaryPrimitives.ReadUInt64BigEndian(src[p..]) >> nbits;
                        int take = (63 - nbits) >> 3;
                        p += take;
                        nbits += take << 3;
                    }
                    else
                    {
                        while (nbits <= 56 && p < src.Length)
                        {
                            acc |= (ulong)src[p++] << (56 - nbits);
                            nbits += 8;
                        }

                        if (nbits < 20)
                        {
                            break;
                        }
                    }
                }

                if (groupLeft == 0)
                {
                    if (selectorIndex >= selectorCount)
                    {
                        throw Bad(baseOffset + p - (nbits >> 3), "no more symbols: the block ran past its last table selector");
                    }

                    table = tables[selectors[selectorIndex++]];
                    groupLeft = GroupSize;
                }

                groupLeft--;
                int sym = table!.Decode((uint)(acc >> 44), out int len);
                if (sym < 0)
                {
                    throw Bad(baseOffset + p - (nbits >> 3), "a code assigned in the Huffman table");
                }

                acc <<= len;
                nbits -= len;
                if (sym <= 1)
                {
                    // RUNA (0) and RUNB (1) are the digits 1 and 2 of a bijective base-2 number, least significant first.
                    if (runShift > 20)
                    {
                        throw Bad(baseOffset + p - (nbits >> 3), "a run of zeros that fits the block");
                    }

                    runLength += (sym + 1) << runShift;
                    runShift++;
                    if (runLength > blockMax - nblock)
                    {
                        throw Bad(baseOffset + p - (nbits >> 3), $"a run of zeros that fits the block size of {blockMax}");
                    }

                    continue;
                }

                if (runLength > 0)
                {
                    tt.AsSpan(nblock, runLength).Fill(mtf.Front);
                    nblock += runLength;
                    runLength = 0;
                    runShift = 0;
                }

                if (sym == eob)
                {
                    done = true;
                    break;
                }

                if (nblock >= blockMax)
                {
                    throw Bad(baseOffset + p - (nbits >> 3), $"at most {blockMax} symbols in the block (block size level {_level})");
                }

                tt[nblock++] = mtf.Decode(sym - 1);
            }
        }
        finally
        {
            // Also on failure: keep positions right for the error report.
            posRef = p;
        }

        if (done)
        {
            // The next thing is a block header or the end marker, parsed from staged bytes. Bytes this call took that the block
            // did not use go back to the caller; bits from earlier calls stay in the accumulator.
            int give = Math.Min(nbits >> 3, p - callStart);
            p -= give;
            nbits -= give << 3;
            posRef = p;
        }

        acc = nbits == 0 ? 0 : acc & (~0UL << (64 - nbits));
        _acc = acc;
        _nbits = nbits;
        _nblock = nblock;
        _table = table;
        _selectorIndex = selectorIndex;
        _groupLeft = groupLeft;
        _runLength = runLength;
        _runShift = runShift;
        return done;
    }

    // ---- Output: the inverse transform, then the first run-length stage, into the caller's buffer ----

    private void StartOutput(long absolutePosition)
    {
        if (_nblock == 0 || _origPtr >= _nblock)
        {
            throw Bad(_blockOffset, _nblock == 0 ? "at least one symbol in the block" : $"an original row number below the block length {_nblock}");
        }

        BZip2Bwt.BuildVector(_tt!, _nblock);
        _origin = (int)(_tt![_origPtr] >> 8);
        _left = _nblock;
        _blockPos = 0;
        _flipIndex = 0;
        _nextFlip = _randomized ? BZip2Randomization.FirstFlip : int.MaxValue;
        _crc = BZip2Crc.Initial;
        _rle.Reset();
        _chunk ??= new byte[ChunkSize];
        _chunkPos = 0;
        _chunkLen = 0;

        // The accumulator's bits belong to the next header: park them at the front of the stage.
        byte[] stage = _stage ??= new byte[StageCapacity];
        int bytes = (_nbits + 7) >> 3;
        if (bytes > 0)
        {
            ulong v = _acc >> (64 - _nbits);
            for (int i = 0; i < bytes; i++)
            {
                stage[i] = (byte)(v >> (8 * (bytes - 1 - i)));
            }
        }

        _stageLen = bytes;
        _stageStartBit = (bytes << 3) - _nbits;
        _stageOrigin = absolutePosition - bytes;
        _acc = 0;
        _nbits = 0;
    }

    private OperationStatus ProduceOutput(Span<byte> dst, long absolutePosition, ref int w)
    {
        int startW = w;
        long allowed = _maxOutput - _totalOut;
        bool limited = dst.Length - w >= allowed;
        int end = limited ? w + (int)Math.Max(0, allowed) : dst.Length;
        OperationStatus result = OperationStatus.Done;
        byte[] chunk = _chunk!;
        uint[] tt = _tt!;
        while (true)
        {
            OperationStatus s = _rle.Decode(chunk.AsSpan(_chunkPos, _chunkLen - _chunkPos), dst[w..end], out int c, out int n);
            _chunkPos += c;
            w += n;
            if (s == OperationStatus.DestinationTooSmall)
            {
                result = OperationStatus.DestinationTooSmall;
                break;
            }

            if (_left == 0)
            {
                if (_rle.IsAwaitingCount)
                {
                    throw Bad(_blockOffset, "a repeat count byte after four equal bytes at the end of the block");
                }

                break;
            }

            int count = Math.Min(ChunkSize, _left);
            uint row = (uint)_origin;
            for (int i = 0; i < count; i++)
            {
                uint e = tt[row];
                chunk[i] = (byte)e;
                row = e >> 8;
            }

            _origin = (int)row;
            if (_nextFlip < _blockPos + count)
            {
                // A randomised block (written only by very old versions): flip the scheduled bytes.
                do
                {
                    chunk[_nextFlip - _blockPos] ^= 1;
                    _nextFlip += BZip2Randomization.GapAfter(_flipIndex++);
                }
                while (_nextFlip < _blockPos + count);
            }

            _blockPos += count;
            _left -= count;
            _chunkPos = 0;
            _chunkLen = count;
        }

        if (w > startW)
        {
            if (_verify)
            {
                _crc = BZip2Crc.Update(_crc, dst[startW..w]);
            }

            _totalOut += w - startW;
        }

        if (result == OperationStatus.DestinationTooSmall)
        {
            if (limited)
            {
                throw new CompressionLimitException(Name, nameof(BZip2DecompressionOptions.MaxOutputSize), _maxOutput, _totalOut + 1);
            }

            return result;
        }

        if (_verify && ~_crc != _blockCrc)
        {
            throw Bad(_blockOffset, $"the block CRC 0x{_blockCrc:X8} but the data has 0x{~_crc:X8}");
        }

        _combinedCrc = BZip2Crc.Combine(_combinedCrc, _blockCrc);
        return OperationStatus.Done;
    }
}
