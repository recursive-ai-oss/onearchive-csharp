using System.Numerics;

namespace RecursiveAI.OneCompress.Brotli;

public sealed partial class BrotliDecoder
{
    private enum Phase : byte
    {
        StreamHeader,
        MetaStart,
        Skip,
        Raw,
        BlockInfo,
        Params,
        ContextMapLiteral,
        ContextMapDistance,
        Trees,
        Commands,
        Done,
    }

    // The three block categories of RFC 7932 section 6, as indexes into the per-category arrays.
    private const int Literals = 0;
    private const int InsertAndCopy = 1;
    private const int Distances = 2;

    private Phase _phase;
    private bool _isLast;

    // The stream's window (RFC 7932 section 9.1).
    private int _window;
    private int _maxCap;

    // Bytes the stream has produced, and the bytes still to come from the meta-block (or metadata) being read.
    private long _totalOut;
    private int _metaRemaining;

    // The sliding window: a ring buffer that holds the most recent output. Until the stream has produced as much as the buffer holds,
    // the buffer is a plain array that grows as output arrives. _flush is the start of the output not yet copied to the caller.
    private byte[] _ring = [];
    private int _cap;
    private int _pos;
    private int _flush;

    // RFC 7932 section 7.1: the last two bytes of output, which are the context of the next literal.
    private int _p1;
    private int _p2;

    // The four last distances (RFC 7932 section 4), most recent first.
    private int _d1;
    private int _d2;
    private int _d3;
    private int _d4;

    // The block categories: number of block types, current and previous type, elements left in the block, and the offsets of the
    // block type and block count codes in the arena.
    private readonly int[] _blockTypes = new int[3];
    private readonly int[] _blockType = new int[3];
    private readonly int[] _blockPrevType = new int[3];
    private readonly int[] _blockLeft = new int[3];
    private readonly int[] _blockTypeCodeOffset = new int[3];
    private readonly int[] _blockCountCodeOffset = new int[3];
    private int _blockCategory;

    private int _postfixBits;
    private int _directCodes;
    private int _distanceAlphabetSize;
    private readonly byte[] _contextModes = new byte[256];
    private readonly byte[] _literalContextMap = new byte[64 * 256];
    private readonly byte[] _distanceContextMap = new byte[4 * 256];
    private int _literalTrees;
    private int _distanceTrees;

    // All prefix code tables of the current meta-block, one after the other, and where each code starts.
    private uint[] _arena = new uint[4096];
    private int _arenaUsed;
    private readonly int[] _literalCodeOffset = new int[256];
    private readonly int[] _insertCodeOffset = new int[256];
    private readonly int[] _distanceCodeOffset = new int[256];
    private int _treeIndex;

    // Scratch for reading prefix codes.
    private readonly byte[] _lengths = new byte[BrotliPrefixTables.MaxAlphabetSize];
    private uint[] _lengthCodeTable = new uint[1 << BrotliPrefixTables.RootBits];

    private void ResetStream()
    {
        _phase = Phase.StreamHeader;
        _isLast = false;
        _totalOut = 0;
        _metaRemaining = 0;
        _cap = 0;
        _pos = 0;
        _flush = 0;
        _p1 = 0;
        _p2 = 0;
        _d1 = 4;
        _d2 = 11;
        _d3 = 15;
        _d4 = 16;
        _cmd = Cmd.Start;
        _insertLeft = 0;
        _copyLeft = 0;
        _wordLen = 0;
        _wordPos = 0;
    }

    private bool RunHeaderUnit(ref BrotliBitReader br)
    {
        switch (_phase)
        {
            case Phase.StreamHeader:
                return ReadStreamHeader(ref br);
            case Phase.MetaStart:
                return ReadMetaBlockStart(ref br);
            case Phase.BlockInfo:
                return ReadBlockInfo(ref br);
            case Phase.Params:
                return ReadParams(ref br);
            case Phase.ContextMapLiteral:
                return ReadContextMapUnit(ref br, Literals);
            case Phase.ContextMapDistance:
                return ReadContextMapUnit(ref br, Distances);
            case Phase.Trees:
                return ReadTree(ref br);
            default:
                throw new InvalidOperationException("Unreachable decoder phase " + _phase);
        }
    }

    // RFC 7932 section 9.1: WBITS.
    private bool ReadStreamHeader(ref BrotliBitReader br)
    {
        br.Refill();
        int wbits;
        if (br.Take(1) == 0)
        {
            wbits = 16;
        }
        else
        {
            int n = (int)br.Take(3);
            if (n != 0)
            {
                wbits = 17 + n;
            }
            else
            {
                int m = (int)br.Take(3);
                if (m == 1)
                {
                    // 0010001 is the one unused pattern (RFC 7932 section 9.1).
                    return Bad(br, "a window size code of 10 to 24 bits (the pattern 0010001 is not allowed)");
                }

                wbits = m == 0 ? 17 : 8 + m;
            }
        }

        if (br.Overrun)
        {
            return false;
        }

        long window = (1L << wbits) - 16;
        if (window > _maxWindowSize)
        {
            throw new CompressionLimitException(FormatName, nameof(BrotliDecompressionOptions.MaxWindowSize), _maxWindowSize, window);
        }

        _window = (int)window;
        _maxCap = 1 << wbits;
        _cap = Math.Min(_maxCap, 4096);
        if (_ring.Length < _cap + BrotliDecRaw.RingSlack)
        {
            _ring = new byte[_cap + BrotliDecRaw.RingSlack];
        }

        _phase = Phase.MetaStart;
        return true;
    }

    // RFC 7932 section 9.2: the start of the meta-block header, up to the choice between metadata, uncompressed and compressed.
    private bool ReadMetaBlockStart(ref BrotliBitReader br)
    {
        br.Refill();
        bool isLast = br.Take(1) != 0;
        if (isLast && br.Take(1) != 0)
        {
            // ISLASTEMPTY: the stream ends here, and the rest of the byte must be zero.
            if (br.Overrun)
            {
                return false;
            }

            if (!br.SkipToByteBoundary())
            {
                return Bad(br, "zero bits after the last meta-block");
            }

            br.ReturnWholeBytes();
            _phase = Phase.Done;
            return true;
        }

        int nibbleCode = (int)br.Take(2);
        if (nibbleCode == 3)
        {
            // MNIBBLES = 0: an empty meta-block that may carry metadata.
            if (br.Take(1) != 0)
            {
                return Bad(br, "a zero reserved bit in a metadata meta-block");
            }

            int skipBytes = (int)br.Take(2);
            int skipLength = 0;
            if (skipBytes > 0)
            {
                uint raw = br.Take(8 * skipBytes);
                if (skipBytes > 1 && (raw >> (8 * (skipBytes - 1))) == 0)
                {
                    return Bad(br, "a metadata length without a zero last byte");
                }

                skipLength = (int)raw + 1;
            }

            if (br.Overrun)
            {
                return false;
            }

            if (!br.SkipToByteBoundary())
            {
                return Bad(br, "zero bits before the metadata");
            }

            br.ReturnWholeBytes();
            _isLast = isLast;
            _metaRemaining = skipLength;
            _phase = Phase.Skip;
            return true;
        }

        int nibbles = 4 + nibbleCode;
        uint lengthMinusOne = br.Take(4 * nibbles);
        if (nibbles > 4 && (lengthMinusOne >> (4 * (nibbles - 1))) == 0)
        {
            return Bad(br, "a meta-block length without a zero last nibble");
        }

        bool uncompressed = false;
        if (!isLast)
        {
            uncompressed = br.Take(1) != 0;
        }

        if (br.Overrun)
        {
            return false;
        }

        long length = (long)lengthMinusOne + 1;
        if (_totalOut + length > _maxOutputSize)
        {
            throw new CompressionLimitException(FormatName, nameof(BrotliDecompressionOptions.MaxOutputSize), _maxOutputSize, _totalOut + length);
        }

        _isLast = isLast;
        _metaRemaining = (int)length;
        PrepareRing(length);
        if (uncompressed)
        {
            if (!br.SkipToByteBoundary())
            {
                return Bad(br, "zero bits before the uncompressed data");
            }

            br.ReturnWholeBytes();
            _phase = Phase.Raw;
            return true;
        }

        _blockCategory = 0;
        _arenaUsed = 0;
        _phase = Phase.BlockInfo;
        return true;
    }

    // RFC 7932 section 9.2: NBLTYPES and, for two or more, the block type and block count codes and the first block count.
    private bool ReadBlockInfo(ref BrotliBitReader br)
    {
        int c = _blockCategory;
        br.Refill();
        int types = ReadVariableCount(ref br);
        int typeCode = 0;
        int countCode = 0;
        int first = 1 << 24;
        if (types >= 2)
        {
            if (!ReadPrefixCode(ref br, types + 2, out typeCode) || !ReadPrefixCode(ref br, 26, out countCode))
            {
                return false;
            }

            br.Refill();
            int symbol = br.ReadSymbol(_arena, countCode);
            br.Refill();
            first = BrotliCodes.BlockBase[symbol] + (int)br.Take(BrotliCodes.BlockExtra[symbol]);
        }

        if (br.Overrun)
        {
            return false;
        }

        _blockTypes[c] = types;
        _blockTypeCodeOffset[c] = typeCode;
        _blockCountCodeOffset[c] = countCode;
        _blockLeft[c] = first;
        _blockType[c] = 0;
        _blockPrevType[c] = 1;
        if (++_blockCategory == 3)
        {
            _phase = Phase.Params;
        }

        return true;
    }

    // RFC 7932 section 9.2: NPOSTFIX, NDIRECT and the context mode of every literal block type.
    private bool ReadParams(ref BrotliBitReader br)
    {
        br.Refill();
        int postfix = (int)br.Take(2);
        int direct = (int)br.Take(4) << postfix;
        int types = _blockTypes[Literals];
        for (int i = 0; i < types; i++)
        {
            _contextModes[i] = (byte)br.TakeRefilled(2);
        }

        if (br.Overrun)
        {
            return false;
        }

        _postfixBits = postfix;
        _directCodes = direct;
        _distanceAlphabetSize = 16 + direct + (48 << postfix);
        _phase = Phase.ContextMapLiteral;
        return true;
    }

    // RFC 7932 section 9.2: NTREESL or NTREESD and the context map when there are two or more trees.
    private bool ReadContextMapUnit(ref BrotliBitReader br, int category)
    {
        br.Refill();
        int trees = ReadVariableCount(ref br);
        byte[] map = category == Literals ? _literalContextMap : _distanceContextMap;
        int size = (category == Literals ? 64 : 4) * _blockTypes[category];
        if (trees >= 2)
        {
            if (!ReadContextMap(ref br, size, trees, map))
            {
                return false;
            }
        }
        else
        {
            map.AsSpan(0, size).Clear();
        }

        if (br.Overrun)
        {
            return false;
        }

        if (category == Literals)
        {
            _literalTrees = trees;
            _phase = Phase.ContextMapDistance;
        }
        else
        {
            _distanceTrees = trees;
            _treeIndex = 0;
            _phase = Phase.Trees;
        }

        return true;
    }

    // RFC 7932 section 9.2: the prefix codes for literals, then insert-and-copy lengths, then distances.
    private bool ReadTree(ref BrotliBitReader br)
    {
        int i = _treeIndex;
        int literalTrees = _literalTrees;
        int insertTrees = _blockTypes[InsertAndCopy];
        int alphabet;
        if (i < literalTrees)
        {
            alphabet = 256;
        }
        else if (i < literalTrees + insertTrees)
        {
            alphabet = 704;
        }
        else
        {
            alphabet = _distanceAlphabetSize;
        }

        if (!ReadPrefixCode(ref br, alphabet, out int offset))
        {
            return false;
        }

        if (i < literalTrees)
        {
            _literalCodeOffset[i] = offset;
        }
        else if (i < literalTrees + insertTrees)
        {
            _insertCodeOffset[i - literalTrees] = offset;
        }
        else
        {
            _distanceCodeOffset[i - literalTrees - insertTrees] = offset;
        }

        _treeIndex = i + 1;
        if (_treeIndex == literalTrees + insertTrees + _distanceTrees)
        {
            BuildLiteralOffsets();
            _cmd = Cmd.Start;
            _phase = Phase.Commands;
        }

        return true;
    }

    // RFC 7932 section 9.2: the variable-length code for NBLTYPES and NTREES, 1 to 256.
    private static int ReadVariableCount(ref BrotliBitReader br)
    {
        if (br.Take(1) == 0)
        {
            return 1;
        }

        int n = (int)br.Take(3);
        if (n == 0)
        {
            return 2;
        }

        return (1 << n) + 1 + (int)br.Take(n);
    }

    // RFC 7932 section 3.4 and 3.5: reads one prefix code over an alphabet of the given size into the arena.
    private bool ReadPrefixCode(ref BrotliBitReader br, int alphabetSize, out int offset)
    {
        offset = _arenaUsed;
        br.Refill();
        int kind = (int)br.Take(2);
        if (kind == 1)
        {
            return ReadSimplePrefixCode(ref br, alphabetSize, offset);
        }

        return ReadComplexPrefixCode(ref br, alphabetSize, kind, offset);
    }

    private bool ReadSimplePrefixCode(ref BrotliBitReader br, int alphabetSize, int offset)
    {
        int count = (int)br.Take(2) + 1;
        int bits = BrotliPrefixTables.AlphabetBits(alphabetSize);
        Span<int> symbols = stackalloc int[4];
        for (int i = 0; i < count; i++)
        {
            symbols[i] = (int)br.Take(bits);
        }

        bool treeSelect = count == 4 && br.Take(1) != 0;
        if (br.Overrun)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (symbols[i] >= alphabetSize)
            {
                return Bad(br, "a symbol below the alphabet size in a simple prefix code");
            }

            for (int j = 0; j < i; j++)
            {
                if (symbols[j] == symbols[i])
                {
                    return Bad(br, "distinct symbols in a simple prefix code");
                }
            }
        }

        if (count == 1)
        {
            _arenaUsed += BrotliPrefixTables.BuildSingle(symbols[0], ref _arena, offset);
            return true;
        }

        // RFC 7932 section 3.4: the code lengths follow the order in which the symbols are given.
        Span<byte> lengths = _lengths.AsSpan(0, alphabetSize);
        lengths.Clear();
        switch (count)
        {
            case 2:
                lengths[symbols[0]] = 1;
                lengths[symbols[1]] = 1;
                break;
            case 3:
                lengths[symbols[0]] = 1;
                lengths[symbols[1]] = 2;
                lengths[symbols[2]] = 2;
                break;
            default:
                if (treeSelect)
                {
                    lengths[symbols[0]] = 1;
                    lengths[symbols[1]] = 2;
                    lengths[symbols[2]] = 3;
                    lengths[symbols[3]] = 3;
                }
                else
                {
                    lengths[symbols[0]] = 2;
                    lengths[symbols[1]] = 2;
                    lengths[symbols[2]] = 2;
                    lengths[symbols[3]] = 2;
                }

                break;
        }

        int size = BrotliPrefixTables.Build(lengths, ref _arena, offset);
        if (size < 0)
        {
            throw new InvalidOperationException("Unreachable: a simple prefix code is always complete.");
        }

        _arenaUsed += size;
        return true;
    }

    // RFC 7932 section 3.5. The order in which the code length code lengths are stored.
    private static ReadOnlySpan<byte> CodeLengthOrder => [1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // RFC 7932 section 3.5: the variable-length code for the code length code lengths, indexed by the next four bits of the stream (the
    // first bit read is the lowest bit). The code lengths 0..5 are the bit sequences 00, 1110, 110, 01, 10 and 1111 in reading order;
    // the RFC prints them with the first bit on the right: 00, 0111, 011, 10, 01, 1111. The low byte of an entry is the number of bits
    // the code takes and the high byte the length it stands for.
    private static ReadOnlySpan<ushort> CodeLengthLengthCode => [
        0x0002, 0x0402, 0x0302, 0x0203, // 0000 0001 0010 0011
        0x0002, 0x0402, 0x0302, 0x0104, // 0100 0101 0110 0111
        0x0002, 0x0402, 0x0302, 0x0203, // 1000 1001 1010 1011
        0x0002, 0x0402, 0x0302, 0x0504, // 1100 1101 1110 1111
    ];

    private bool ReadComplexPrefixCode(ref BrotliBitReader br, int alphabetSize, int skip, int offset)
    {
        // The code length codes (RFC 7932 section 3.5).
        Span<byte> codeLengthLengths = stackalloc byte[18];
        codeLengthLengths.Clear();
        int space = 32;
        int used = 0;
        ReadOnlySpan<byte> order = CodeLengthOrder;
        ReadOnlySpan<ushort> table = CodeLengthLengthCode;
        for (int i = skip; i < 18; i++)
        {
            br.Refill();
            ushort e = table[(int)br.Peek(4)];
            br.Drop(e & 0xFF);
            int length = e >> 8;
            codeLengthLengths[order[i]] = (byte)length;
            if (length != 0)
            {
                space -= 32 >> length;
                used++;
                if (space <= 0)
                {
                    break;
                }
            }
        }

        if (br.Overrun)
        {
            return false;
        }

        if (used == 0 || (used > 1 && space != 0))
        {
            return Bad(br, "code length code lengths that form a complete prefix code");
        }

        if (used == 1)
        {
            int only = codeLengthLengths.IndexOfAnyExcept((byte)0);
            BrotliPrefixTables.BuildSingle(only, ref _lengthCodeTable, 0);
        }
        else if (BrotliPrefixTables.Build(codeLengthLengths, ref _lengthCodeTable, 0) < 0)
        {
            return Bad(br, "code length code lengths that form a complete prefix code");
        }

        // The code lengths of the symbols, with the run-length codes 16 and 17.
        Span<byte> lengths = _lengths.AsSpan(0, alphabetSize);
        lengths.Clear();
        int symbol = 0;
        int remaining = 1 << 15;
        int previousLength = 8;
        int repeat = 0;
        int lastRepeatCode = 0;
        uint[] lengthTable = _lengthCodeTable;
        while (symbol < alphabetSize && remaining > 0)
        {
            br.Refill();
            int code = br.ReadSymbol(lengthTable, 0);
            if (code < 16)
            {
                lastRepeatCode = 0;
                lengths[symbol++] = (byte)code;
                if (code != 0)
                {
                    previousLength = code;
                    remaining -= 32768 >> code;
                }

                continue;
            }

            // 16 repeats the previous non-zero length 3 to 6 times, 17 repeats zero 3 to 10 times. A repeat code that follows
            // the same code extends the earlier repeat: the count so far is scaled by 4 (or 8) and the new extra bits are added.
            int extraBits = code == 16 ? 2 : 3;
            int newLength = code == 16 ? previousLength : 0;
            if (lastRepeatCode != code)
            {
                repeat = 0;
            }

            lastRepeatCode = code;
            int oldRepeat = repeat;
            if (repeat > 0)
            {
                repeat = (repeat - 2) << extraBits;
            }

            repeat += (int)br.Take(extraBits) + 3;
            int delta = repeat - oldRepeat;
            if (symbol + delta > alphabetSize)
            {
                return Bad(br, "code lengths that do not run past the end of the alphabet");
            }

            lengths.Slice(symbol, delta).Fill((byte)newLength);
            symbol += delta;
            if (newLength != 0)
            {
                remaining -= delta * (32768 >> newLength);
            }
        }

        if (br.Overrun)
        {
            return false;
        }

        if (remaining != 0)
        {
            return Bad(br, "code lengths that form a complete prefix code");
        }

        int size = BrotliPrefixTables.Build(lengths, ref _arena, offset);
        if (size < 0)
        {
            return Bad(br, "code lengths that form a complete prefix code");
        }

        _arenaUsed += size;
        return true;
    }

    // RFC 7932 section 7.3: the context map with its run-length coding of zeros and the optional inverse move-to-front transform.
    private bool ReadContextMap(ref BrotliBitReader br, int size, int trees, byte[] map)
    {
        br.Refill();
        int maxRun = 0;
        if (br.Take(1) != 0)
        {
            maxRun = (int)br.Take(4) + 1;
        }

        int arenaSave = _arenaUsed;
        if (!ReadPrefixCode(ref br, trees + maxRun, out int code))
        {
            return false;
        }

        int i = 0;
        while (i < size)
        {
            br.Refill();
            int symbol = br.ReadSymbol(_arena, code);
            if (symbol == 0)
            {
                map[i++] = 0;
            }
            else if (symbol <= maxRun)
            {
                int run = (1 << symbol) + (int)br.Take(symbol);
                if (i + run > size)
                {
                    return Bad(br, "a run of zeros that stays inside the context map");
                }

                map.AsSpan(i, run).Clear();
                i += run;
            }
            else
            {
                map[i++] = (byte)(symbol - maxRun);
            }
        }

        bool inverseMoveToFront = br.TakeRefilled(1) != 0;
        if (br.Overrun)
        {
            return false;
        }

        // The code that decoded the map is not needed any more.
        _arenaUsed = arenaSave;
        if (inverseMoveToFront)
        {
            Span<byte> mtf = stackalloc byte[256];
            for (int k = 0; k < 256; k++)
            {
                mtf[k] = (byte)k;
            }

            for (int k = 0; k < size; k++)
            {
                int index = map[k];
                byte value = mtf[index];
                map[k] = value;
                mtf.Slice(0, index).CopyTo(mtf.Slice(1));
                mtf[0] = value;
            }
        }

        return true;
    }

    // Makes room in the ring for a meta-block of the given length, before anything has wrapped.
    private void PrepareRing(long length)
    {
        if (_cap >= _maxCap)
        {
            return;
        }

        long want = Math.Min(_maxCap, (long)BitOperations.RoundUpToPowerOf2((uint)Math.Min(_totalOut + length, int.MaxValue / 2)));
        if (want > _cap)
        {
            GrowRingTo((int)want);
        }
    }

    private void GrowRingTo(int newCap)
    {
        if (_ring.Length < newCap + BrotliDecRaw.RingSlack)
        {
            Array.Resize(ref _ring, newCap + BrotliDecRaw.RingSlack);
        }

        _cap = newCap;
    }

    // Copies the output the ring holds for the caller into the destination.
    private void FlushOutput(Span<byte> dest)
    {
        int n = _pos - _flush;
        if (n > 0)
        {
            _ring.AsSpan(_flush, n).CopyTo(dest.Slice(_written));
            _written += n;
            _flush = _pos;
        }
    }

    // Returns how many bytes (at most want) can be produced into the ring now: as many as the destination has room for and the ring has
    // before its end. Zero means the destination is full.
    private int Claim(Span<byte> dest, int want)
    {
        if (_pos == _cap)
        {
            FlushOutput(dest);
            if (_cap < _maxCap)
            {
                GrowRingTo(Math.Min(_cap * 2, _maxCap));
            }
            else
            {
                _pos = 0;
                _flush = 0;
            }
        }

        int room = dest.Length - _written - (_pos - _flush);
        return Math.Min(Math.Min(want, room), _cap - _pos);
    }

    // Updates the literal context after n bytes were written to the ring ending at _pos.
    private void AfterWrite(int n)
    {
        if (n >= 2)
        {
            _p1 = _ring[_pos - 1];
            _p2 = _ring[_pos - 2];
        }
        else
        {
            _p2 = _p1;
            _p1 = _ring[_pos - 1];
        }
    }

    // RFC 7932 section 9.2: the bytes of an uncompressed meta-block, copied as they are.
    private Step CopyRaw(ref BrotliBitReader br, Span<byte> dest)
    {
        while (_metaRemaining > 0)
        {
            int available = br.Source.Length - br.Position;
            if (available <= 0)
            {
                return Step.Starved;
            }

            int n = Claim(dest, Math.Min(available, _metaRemaining));
            if (n == 0)
            {
                return Step.Full;
            }

            br.Source.Slice(br.Position, n).CopyTo(_ring.AsSpan(_pos, n));
            br.Advance(n);
            _pos += n;
            _metaRemaining -= n;
            _totalOut += n;
            AfterWrite(n);
        }

        _phase = Phase.MetaStart;
        return Step.Ok;
    }
}
