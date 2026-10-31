using System.Buffers;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// A streaming bzip2 decompressor over spans. It reads one stream (the <c>BZh</c> header, blocks, and the end-of-stream marker with
/// the combined CRC) per unit and reports <see cref="OperationStatus.Done"/> after each, so concatenated streams, as the
/// <c>cat</c> command or a parallel compressor produces, are decoded by calling it again with the rest of the input.
/// </summary>
/// <remarks>
/// <para>The format has no official specification; this decoder follows the public descriptions of the format and the
/// behaviour of the bzip2 tool. What it checks: the signatures, the block size level (1 to 9), each block's original row,
/// the symbol map, 2 to 6 tables, 1 to 32767 selectors (those past 18002 are read and ignored, as bzip2 1.0.8 does) each below the table count, code lengths from 1 to 20 that do not
/// oversubscribe the code space and change by at most 40 steps per symbol, that symbols never overflow the level's block size,
/// that runs of zeros fit the block, the block CRC, and the combined CRC of the stream.</para>
/// <para>Memory: one transformation vector of 4 bytes per byte of the block size level (at most 3.6 MB), a 64 KiB staging
/// buffer for block headers, and a few small tables. They are allocated when first needed and reused for every block and stream.</para>
/// </remarks>
public sealed partial class BZip2Decoder
{
    private const string Name = "bzip2";
    private const long BlockMagic = 0x314159265359;
    private const long EndMagic = 0x177245385090;
    private const int MaxSelectors = 18002;
    private const int GroupSize = 50;
    private const int MaxLengthChanges = 40;
    private const int StageCapacity = 64 * 1024;
    private const int ChunkSize = 4096;
    private const int MaxTables = 6;

    private enum Phase
    {
        StreamHeader,
        BlockStart,
        Symbols,
        Output,
    }

    private enum Parse
    {
        NeedMore,
        Block,
        End,
        StreamHeader,
    }

    private readonly long _maxOutput;
    private readonly bool _verify;

    // Header bytes that are waiting for the rest of their header (and the leftover bits of the data before it).
    private byte[]? _stage;
    private int _stageLen;
    private int _stageStartBit;
    private long _stageOrigin;

    private Phase _phase;
    private bool _faulted;
    private long _totalIn;
    private long _totalOut;

    // Per stream.
    private int _level;
    private int _blockMax;
    private uint _combinedCrc;

    // Per block header.
    private long _blockOffset;
    private uint _blockCrc;
    private bool _randomized;
    private int _origPtr;
    private int _selectorCount;
    private int _tableCount;
    private int _alphabetSize;
    private uint _storedStreamCrc;
    private readonly byte[] _symbolMap = new byte[256];
    private int _usedCount;
    private byte[]? _selectors;
    private byte[]? _lengths;
    private BZip2HuffmanTable[]? _tables;
    private BZip2MoveToFront? _symbolMtf;
    private BZip2MoveToFront? _selectorMtf;

    // Symbol decoding.
    private ulong _acc;
    private int _nbits;
    private uint[]? _tt;
    private int _nblock;
    private int _runLength;
    private int _runShift;
    private int _selectorIndex;
    private int _groupLeft;
    private BZip2HuffmanTable? _table;

    // Output.
    private int _origin;
    private int _left;
    private int _blockPos;
    private int _nextFlip;
    private int _flipIndex;
    private uint _crc;
    private byte[]? _chunk;
    private int _chunkPos;
    private int _chunkLen;
    private BZip2RunLengthDecoder _rle;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Limits and checks; <see langword="null"/> for <see cref="BZip2DecompressionOptions.Default"/>.</param>
    public BZip2Decoder(BZip2DecompressionOptions? options = null)
    {
        options ??= BZip2DecompressionOptions.Default;
        _maxOutput = options.MaxOutputSize;
        _verify = options.VerifyChecksum;
    }

    /// <summary>Gets a value indicating whether the decoder is between streams: nothing of a stream is partly read. True for a new or reset decoder and after a call returned <see cref="OperationStatus.Done"/>.</summary>
    public bool IsAtFrameBoundary => _phase == Phase.StreamHeader && _stageLen == 0 && !_faulted;

    /// <summary>Gets the number of input bytes consumed since construction or the last <see cref="Reset"/>.</summary>
    public long TotalConsumed => _totalIn;

    /// <summary>Gets the number of bytes written since construction or the last <see cref="Reset"/>.</summary>
    public long TotalWritten => _totalOut;

    /// <summary>Forgets everything about the stream being read, and clears a failure. Buffers are kept.</summary>
    public void Reset()
    {
        _phase = Phase.StreamHeader;
        _faulted = false;
        _stageLen = 0;
        _stageStartBit = 0;
        _nbits = 0;
        _acc = 0;
        _totalIn = 0;
        _totalOut = 0;
        _combinedCrc = 0;
        _left = 0;
        _chunkPos = 0;
        _chunkLen = 0;
        _rle.Reset();
    }

    /// <summary>Raises the truncated-input error when the decoder is inside a stream. Call it when no more input will come.</summary>
    /// <exception cref="CompressionFormatException">The input ended inside a stream.</exception>
    public void ThrowIfIncomplete()
    {
        if (!IsAtFrameBoundary)
        {
            if (_faulted)
            {
                throw new InvalidOperationException("The decoder failed earlier; call Reset() before using it again.");
            }

            throw new CompressionFormatException(Name, _totalIn, "the end-of-stream marker", isTruncated: true);
        }
    }

    /// <summary>Decompresses bzip2 data.</summary>
    /// <param name="source">Input not yet consumed by earlier calls, plus any new bytes.</param>
    /// <param name="destination">Receives output.</param>
    /// <param name="consumed">The number of source bytes used; always set. All of the source is used unless a stream ended inside it.</param>
    /// <param name="written">The number of bytes written; always set.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when a stream is complete (its end-of-stream marker and combined CRC were read and all its
    /// output is written; bytes after it are not consumed), <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting,
    /// <see cref="OperationStatus.NeedMoreData"/> when all input is used and the stream is incomplete.
    /// </returns>
    /// <exception cref="CompressionFormatException">The data is malformed or a CRC does not match. After this the decoder refuses calls until <see cref="Reset"/>.</exception>
    /// <exception cref="CompressionLimitException">The output would pass <see cref="BZip2DecompressionOptions.MaxOutputSize"/>.</exception>
    /// <exception cref="InvalidOperationException">An earlier call threw and <see cref="Reset"/> was not called.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        if (_faulted)
        {
            throw new InvalidOperationException("The decoder failed earlier; call Reset() before using it again.");
        }

        consumed = 0;
        written = 0;
        long baseOffset = _totalIn;
        int pos = 0;
        int w = 0;
        OperationStatus status;
        try
        {
            status = Run(source, destination, baseOffset, ref pos, ref w);
        }
        catch
        {
            _faulted = true;
            consumed = pos;
            written = w;
            _totalIn = baseOffset + pos;
            throw;
        }

        consumed = pos;
        written = w;
        _totalIn = baseOffset + pos;
        return status;
    }

    private OperationStatus Run(ReadOnlySpan<byte> src, Span<byte> dst, long baseOffset, ref int pos, ref int w)
    {
        while (true)
        {
            switch (_phase)
            {
                case Phase.StreamHeader:
                {
                    if (ParseStaged(src, baseOffset, ref pos, Parse.StreamHeader) == Parse.NeedMore)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    _phase = Phase.BlockStart;
                    _combinedCrc = 0;
                    break;
                }

                case Phase.BlockStart:
                {
                    Parse r = ParseStaged(src, baseOffset, ref pos, Parse.Block);
                    if (r == Parse.NeedMore)
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    if (r == Parse.End)
                    {
                        _phase = Phase.StreamHeader;
                        return OperationStatus.Done;
                    }

                    StartSymbols();
                    _phase = Phase.Symbols;
                    break;
                }

                case Phase.Symbols:
                {
                    if (!DecodeSymbols(src, baseOffset, ref pos))
                    {
                        return OperationStatus.NeedMoreData;
                    }

                    StartOutput(baseOffset + pos);
                    _phase = Phase.Output;
                    break;
                }

                default:
                {
                    OperationStatus s = ProduceOutput(dst, baseOffset + pos, ref w);
                    if (s != OperationStatus.Done)
                    {
                        return s;
                    }

                    _phase = Phase.BlockStart;
                    break;
                }
            }
        }
    }

    private static CompressionFormatException Bad(long offset, string expected) => new(Name, offset, expected);

    // ---- Header parsing over the staging buffer ----

    // Adds the source bytes the parse may need to the stage, parses, and gives back the bytes it did not need. When the parse
    // stalls, the whole source stays in the stage (consumed): staged bytes always belong to the unfinished header.
    private Parse ParseStaged(ReadOnlySpan<byte> src, long baseOffset, ref int pos, Parse what)
    {
        byte[] stage = _stage ??= new byte[StageCapacity];
        if (_stageLen == 0)
        {
            _stageStartBit = 0;
            _stageOrigin = baseOffset + pos;
        }

        int before = _stageLen;
        int take = Math.Min(src.Length - pos, StageCapacity - _stageLen);
        src.Slice(pos, take).CopyTo(stage.AsSpan(_stageLen));
        _stageLen += take;

        long bitEnd;
        Parse result = what == Parse.StreamHeader ? ParseStreamHeader(out bitEnd) : ParseBlockHeader(out bitEnd);
        if (result == Parse.NeedMore)
        {
            if (_stageLen == StageCapacity)
            {
                throw Bad(_stageOrigin, "a block header that fits in 65536 bytes");
            }

            pos += take;
            return Parse.NeedMore;
        }

        int used = (int)((bitEnd + 7) >> 3);
        int fromSource = used - before;
        pos += fromSource;
        int leftoverBits = (used << 3) - (int)bitEnd;
        if (result == Parse.Block && leftoverBits > 0)
        {
            _acc = (ulong)(stage[used - 1] & ((1 << leftoverBits) - 1)) << (64 - leftoverBits);
        }
        else
        {
            _acc = 0;
        }

        _nbits = result == Parse.Block ? leftoverBits : 0;
        _stageLen = 0;
        _stageStartBit = 0;
        return result;
    }

    private Parse ParseStreamHeader(out long bitEnd)
    {
        // "BZh" and the block size level as the ASCII digit '1' to '9'. Wrong bytes are reported as soon as they are seen.
        bitEnd = 0;
        ReadOnlySpan<byte> s = _stage.AsSpan(0, _stageLen);
        ReadOnlySpan<byte> magic = "BZh"u8;
        for (int i = 0; i < 3 && i < s.Length; i++)
        {
            if (s[i] != magic[i])
            {
                throw Bad(_stageOrigin + i, "the stream signature \"BZh\"");
            }
        }

        if (s.Length < 4)
        {
            return Parse.NeedMore;
        }

        if (s[3] is < (byte)'1' or > (byte)'9')
        {
            throw Bad(_stageOrigin + 3, "a block size level, the digit 1 to 9");
        }

        _level = s[3] - '0';
        _blockMax = _level * 100_000;
        int need = _blockMax;
        if (_tt is null || _tt.Length < need)
        {
            _tt = new uint[need];
        }

        bitEnd = 32;
        return Parse.StreamHeader;
    }

    private Parse ParseBlockHeader(out long bitEnd)
    {
        var c = new BitCursor(_stage.AsSpan(0, _stageLen), _stageStartBit);
        bitEnd = 0;
        long headerOffset = _stageOrigin + c.StartByte;
        long magic = ((long)c.Read(24) << 24) | c.Read(24);
        if (c.Overrun)
        {
            return Parse.NeedMore;
        }

        if (magic == EndMagic)
        {
            uint crc = c.Read(32);
            if (c.Overrun)
            {
                return Parse.NeedMore;
            }

            _storedStreamCrc = crc;
            bitEnd = c.BitPosition;
            if (_verify && crc != _combinedCrc)
            {
                throw Bad(_stageOrigin + ((c.BitPosition - 32) >> 3), $"the combined CRC 0x{_combinedCrc:X8} but found 0x{crc:X8}");
            }

            return Parse.End;
        }

        if (magic != BlockMagic)
        {
            throw Bad(headerOffset, "the block signature 0x314159265359 or the end-of-stream signature 0x177245385090");
        }

        uint blockCrc = c.Read(32);
        bool randomized = c.Read(1) != 0;
        int origPtr = (int)c.Read(24);

        // The symbol map: 16 flags for 16 groups of 16 byte values, then 16 flags for each group present.
        uint groups = c.Read(16);
        int used = 0;
        for (int g = 0; g < 16; g++)
        {
            if ((groups & (0x8000u >> g)) != 0)
            {
                uint bits = c.Read(16);
                for (int k = 0; k < 16; k++)
                {
                    if ((bits & (0x8000u >> k)) != 0)
                    {
                        _symbolMap[used++] = (byte)((g << 4) | k);
                    }
                }
            }
        }

        if (c.Overrun)
        {
            return Parse.NeedMore;
        }

        if (used == 0)
        {
            throw Bad(_stageOrigin + (c.BitPosition >> 3), "at least one byte value in use in the symbol map");
        }

        int tableCount = (int)c.Read(3);
        int selectorCount = (int)c.Read(15);
        if (c.Overrun)
        {
            return Parse.NeedMore;
        }

        if (tableCount is < 2 or > MaxTables)
        {
            throw Bad(_stageOrigin + (c.BitPosition >> 3), "2 to 6 Huffman tables");
        }

        if (selectorCount < 1)
        {
            throw Bad(_stageOrigin + (c.BitPosition >> 3), "at least 1 table selector");
        }

        // Selectors: each a unary count of 1 bits (ended by a 0 bit), then undone by move-to-front.
        byte[] selectors = _selectors ??= new byte[MaxSelectors];
        BZip2MoveToFront selMtf = _selectorMtf ??= new BZip2MoveToFront();
        selMtf.Reset(tableCount);
        for (int i = 0; i < selectorCount; i++)
        {
            int j = 0;
            while (c.Read(1) != 0)
            {
                if (++j >= tableCount)
                {
                    if (c.Overrun)
                    {
                        return Parse.NeedMore;
                    }

                    throw Bad(_stageOrigin + ((c.BitPosition - 1) >> 3), "a table selector below the table count");
                }
            }

            if (c.Overrun)
            {
                return Parse.NeedMore;
            }

            // Like bzip2 1.0.8, read selectors past the 18002 a block can use (to stay aligned) and ignore them.
            byte selector = selMtf.Decode(j);
            if (i < MaxSelectors)
            {
                selectors[i] = selector;
            }
        }

        // Code lengths: a 5-bit start, then per symbol a walk of +1 / -1 steps (bits 10 / 11), ended by a 0 bit.
        int alphabetSize = used + 2;
        byte[] lengths = _lengths ??= new byte[MaxTables * BZip2HuffmanTable.MaxAlphabetSize];
        for (int t = 0; t < tableCount; t++)
        {
            int cur = (int)c.Read(5);
            for (int s = 0; s < alphabetSize; s++)
            {
                int steps = 0;
                while (true)
                {
                    if (c.Overrun)
                    {
                        return Parse.NeedMore;
                    }

                    if (cur is < 1 or > BZip2HuffmanTable.MaxCodeLength)
                    {
                        throw Bad(_stageOrigin + (c.BitPosition >> 3), "a code length from 1 to 20");
                    }

                    if (c.Read(1) == 0)
                    {
                        break;
                    }

                    if (++steps > MaxLengthChanges)
                    {
                        throw Bad(_stageOrigin + (c.BitPosition >> 3), "a code length reached in at most 40 steps");
                    }

                    cur += c.Read(1) == 0 ? 1 : -1;
                }

                lengths[(t * BZip2HuffmanTable.MaxAlphabetSize) + s] = (byte)cur;
            }
        }

        if (c.Overrun)
        {
            return Parse.NeedMore;
        }

        BZip2HuffmanTable[] tables = _tables ??= CreateTables();
        for (int t = 0; t < tableCount; t++)
        {
            if (!tables[t].TryBuild(lengths.AsSpan(t * BZip2HuffmanTable.MaxAlphabetSize, alphabetSize)))
            {
                throw Bad(_stageOrigin + (c.BitPosition >> 3), "code lengths that do not oversubscribe the code space");
            }
        }

        _blockOffset = headerOffset;
        _blockCrc = blockCrc;
        _randomized = randomized;
        _origPtr = origPtr;
        _usedCount = used;
        _tableCount = tableCount;
        _selectorCount = Math.Min(selectorCount, MaxSelectors);
        _alphabetSize = alphabetSize;
        bitEnd = c.BitPosition;
        return Parse.Block;
    }

    private static BZip2HuffmanTable[] CreateTables()
    {
        var tables = new BZip2HuffmanTable[MaxTables];
        for (int i = 0; i < tables.Length; i++)
        {
            tables[i] = new BZip2HuffmanTable();
        }

        return tables;
    }

    // Reads big-endian bit fields from the staged header. Reading past the end sets Overrun and yields zeros; callers check
    // Overrun before they act on what they read.
    private ref struct BitCursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _bit;
        private readonly long _end;

        public BitCursor(ReadOnlySpan<byte> data, int startBit)
        {
            _data = data;
            _bit = startBit;
            _end = (long)data.Length * 8;
            Overrun = false;
        }

        public bool Overrun { get; private set; }

        public readonly long BitPosition => _bit;

        public readonly int StartByte => (int)(_bit >> 3);

        public uint Read(int n)
        {
            if (Overrun || _bit + n > _end)
            {
                Overrun = true;
                return 0;
            }

            int first = (int)(_bit >> 3);
            int offset = (int)(_bit & 7);
            int bytes = (offset + n + 7) >> 3;
            ulong v = 0;
            for (int i = 0; i < bytes; i++)
            {
                v = (v << 8) | _data[first + i];
            }

            v >>= (bytes * 8) - offset - n;
            _bit += n;
            return (uint)(v & ((1UL << n) - 1));
        }
    }
}
