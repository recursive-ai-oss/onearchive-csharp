using System.Buffers;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A streaming compressor for raw Deflate (RFC 1951), zlib (RFC 1950) or gzip (RFC 1952). Input goes in with
/// <see cref="Compress(ReadOnlySpan{byte}, Span{byte}, out int, out int, DeflateFlush)"/> in any number of pieces; the output is
/// the same however the input is split, unless a flush is requested between the pieces.
/// </summary>
/// <remarks>
/// <para>The encoder keeps a window of recent input, parses it into literals and matches, and writes blocks through a
/// <see cref="DeflateBlockWriter"/>. It owns every buffer it needs (about 2 MiB) and reuses them across <see cref="Reset"/>,
/// so steady-state calls allocate nothing. An instance is not thread-safe.</para>
/// <para>Output depends only on the bytes given, where flushes fall, and the <see cref="DeflateOptions"/>.</para>
/// </remarks>
public sealed partial class DeflateEncoder
{
    /// <summary>The size of the buffer the encoder fills before it parses: the 32 KiB window plus room for new input.</summary>
    internal const int WindowCapacity = 1 << 18;

    // Bytes kept unparsed at the end of a full window, so every match search sees the longest possible match ahead.
    private const int Lookahead = DeflateTables.MaxMatchLength + 8;
    private const int SequenceCapacity = 1 << 16;
    private const int CheckInterval = 512;

    private readonly DeflateFormat _format;
    private readonly DeflateOptions _options;
    private readonly LevelParameters _parameters;
    private byte[] _window;
    private readonly DeflateSequenceBuffer _sequences;
    private readonly DeflateBlockSplitter _splitter = new();
    private readonly DeflateBlockWriter _writer = new(WindowCapacity + 2048);
    private readonly DeflateHashChainMatchFinder? _chain;
    private readonly ReadOnlyMemory<byte> _dictionary;

    private int _position;
    private int _fill;
    private int _blockStart;
    private int _oldest;
    private int _checkAt = CheckInterval;
    private Checksums.Crc32 _crc;
    private Checksums.Adler32 _adler;
    private long _totalIn;
    private bool _headerWritten;
    private bool _dirty = true;
    private int _flushIssued = -1;
    private bool _finished;

    /// <summary>Creates an encoder.</summary>
    /// <param name="format">The wrapper to write.</param>
    /// <param name="options">The level, strategy, dictionary and gzip header fields; <see langword="null"/> for <see cref="DeflateOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined value, or an option is out of range.</exception>
    /// <exception cref="ArgumentException">An option is invalid, or a dictionary was given for gzip.</exception>
    public DeflateEncoder(DeflateFormat format, DeflateOptions? options = null)
        : this(format, options, long.MaxValue)
    {
    }

    // sizeHint is the exact input length when the caller knows it (the one-call methods); the buffers are then allocated no
    // larger than the input needs. A block is closed when the sequence buffer fills, so the buffer is only shrunk when the
    // input is too short to fill it, and the output is the same as with the full size.
    internal DeflateEncoder(DeflateFormat format, DeflateOptions? options, long sizeHint)
    {
        if (!Enum.IsDefined(format))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        options ??= DeflateOptions.Default;
        options.Validate();
        if (format == DeflateFormat.GZip && !options.Dictionary.IsEmpty)
        {
            throw new ArgumentException("gzip streams cannot use a preset dictionary.", nameof(options));
        }

        _format = format;
        _options = options;
        int dictionaryLength = Math.Min(options.Dictionary.Length, DeflateTables.WindowSize);
        int windowSize = (int)Math.Min(WindowCapacity, Math.Min(Math.Max(sizeHint, 0), WindowCapacity) + dictionaryLength);
        _window = new byte[windowSize + 16];
        _parameters = LevelParameters.For(options.Level);
        // The greedy and lazy parsers (and the two strategies that bypass the match finder) store matches only; the optimal parser stores every entry.
        bool runMode = _parameters.Mode is ParseMode.Greedy or ParseMode.Lazy
            || options.Strategy is DeflateStrategy.HuffmanOnly or DeflateStrategy.RunLength;
        _sequences = new DeflateSequenceBuffer((int)Math.Min(SequenceCapacity, Math.Min(Math.Max(sizeHint, 0), SequenceCapacity) + 1), runMode);
        _dictionary = options.Dictionary.Length > DeflateTables.WindowSize
            ? options.Dictionary.Slice(options.Dictionary.Length - DeflateTables.WindowSize)
            : options.Dictionary;
        if (_parameters.Mode is ParseMode.Greedy or ParseMode.Lazy)
        {
            _chain = new DeflateHashChainMatchFinder(_parameters.HashBits, _parameters.HashBytes);
        }

        if (_parameters.Mode == ParseMode.Optimal)
        {
            _optimal = new OptimalState();
        }

        Reset();
    }

    /// <summary>Gets the wrapper this encoder writes.</summary>
    public DeflateFormat Format => _format;

    /// <summary>Gets the number of input bytes accepted since the last reset.</summary>
    public long TotalIn => _totalIn;

    /// <summary>Gets a value indicating whether <see cref="DeflateFlush.Finish"/> completed and all output was delivered.</summary>
    public bool IsFinished => _finished;

    /// <summary>
    /// The most bytes the encoder can produce for input of <paramref name="inputLength"/> bytes in one stream with no flushes,
    /// wrapper included. Output buffers of this size never report <see cref="OperationStatus.DestinationTooSmall"/>.
    /// </summary>
    /// <param name="format">The wrapper.</param>
    /// <param name="inputLength">The input size, 0 or more.</param>
    /// <param name="options">The options (the gzip header fields change the bound); <see langword="null"/> for the defaults.</param>
    /// <returns>The bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative or too large.</exception>
    public static long GetMaxCompressedLength(DeflateFormat format, long inputLength, DeflateOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(inputLength, long.MaxValue / 2);
        options ??= DeflateOptions.Default;

        // Every block is at most as big as storing it (the writer picks the smallest), a stored piece costs 5 bytes per 65535,
        // and a block ends after at least 4096 input bytes except at window boundaries; see docs/deflate-encoder.md.
        long bound = inputLength + (inputLength >> 9) + 64;
        switch (format)
        {
            case DeflateFormat.ZLib:
                bound += 6;
                break;
            case DeflateFormat.GZip:
                bound += 10 + 8 + options.GZipExtra.Length + (options.GZipExtra.IsEmpty ? 0 : 2)
                    + (options.GZipFileName is null ? 0 : options.GZipFileName.Length + 1)
                    + (options.GZipComment is null ? 0 : options.GZipComment.Length + 1)
                    + (options.GZipHeaderCrc ? 2 : 0);
                break;
        }

        return bound;
    }

    /// <summary>Starts a new stream, keeping the buffers. Everything pending is dropped.</summary>
    public void Reset()
    {
        _writer.Reset();
        _sequences.Clear();
        _splitter.Reset();
        _chain?.Reset();
        _optimal?.Reset();
        _position = 0;
        _fill = 0;
        _blockStart = 0;
        _oldest = 0;
        _checkAt = CheckInterval;
        _crc = default;
        _adler = default;
        _adler.Reset();
        _totalIn = 0;
        _headerWritten = false;
        _dirty = true;
        _flushIssued = -1;
        _finished = false;
        _finishedPending = false;
        if (!_dictionary.IsEmpty)
        {
            // The dictionary counts as output before the stream: matches may point into it (RFC 1950 section 2.2).
            _dictionary.Span.CopyTo(_window);
            _fill = _dictionary.Length;
            _position = _fill;
            _blockStart = _fill;
            for (int p = 0; p + DeflateHashChainMatchFinder.RequiredLookahead <= _fill; p++)
            {
                _chain?.InsertUnchecked(_window, p);
                _optimal?.Finder.Search(_window, p, Math.Min(DeflateTables.MaxMatchLength, _fill - p), _parameters.Chain, _parameters.Nice, 0, null, null);
            }
        }
    }

    /// <summary>Compresses input, optionally flushing or finishing the stream.</summary>
    /// <param name="source">New input. Pass the bytes <paramref name="consumed"/> left over by an earlier call again.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken. Less than <paramref name="source"/>'s length only when <see cref="OperationStatus.DestinationTooSmall"/> is returned.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>.</param>
    /// <param name="flush">What to force out after the input; see <see cref="DeflateFlush"/>.</param>
    /// <returns>
    /// <see cref="OperationStatus.NeedMoreData"/> when all input was taken and no flush was requested;
    /// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting (call again with more room and the unconsumed input);
    /// <see cref="OperationStatus.Done"/> when the requested flush is complete and fully written. It never returns InvalidData.
    /// </returns>
    /// <exception cref="InvalidOperationException">Input was given after the stream finished; call <see cref="Reset"/> first.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="flush"/> is not a defined value.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, DeflateFlush flush = DeflateFlush.None)
    {
        consumed = 0;
        written = 0;
        if (!Enum.IsDefined(flush))
        {
            throw new ArgumentOutOfRangeException(nameof(flush));
        }

        if (_finished)
        {
            if (!source.IsEmpty)
            {
                throw new InvalidOperationException("The stream is finished; call Reset to start another.");
            }

            return Drain(destination, ref written) ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
        }

        if (!_headerWritten)
        {
            WriteHeader();
            _headerWritten = true;
        }

        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (consumed == source.Length)
            {
                break;
            }

            _flushIssued = -1;
            _dirty = true;
            int count = Math.Min(WindowCapacity - _fill, source.Length - consumed);
            if (_fill + count + 16 > _window.Length)
            {
                Array.Resize(ref _window, (int)Math.Min(WindowCapacity, Math.Max(_fill + count, (long)_window.Length * 2)) + 16);
            }

            ReadOnlySpan<byte> piece = source.Slice(consumed, count);
            piece.CopyTo(_window.AsSpan(_fill));
            UpdateChecksum(piece);
            _fill += count;
            consumed += count;
            _totalIn += count;
            if (_fill == WindowCapacity)
            {
                ProcessFullWindow();
            }
        }

        if (flush == DeflateFlush.None)
        {
            return OperationStatus.NeedMoreData;
        }

        if ((int)flush > _flushIssued)
        {
            PerformFlush(flush);
            _flushIssued = (int)flush;
            if (flush == DeflateFlush.Finish)
            {
                _finishedPending = true;
            }
        }

        if (!Drain(destination, ref written))
        {
            return OperationStatus.DestinationTooSmall;
        }

        if (_finishedPending)
        {
            _finished = true;
        }

        return OperationStatus.Done;
    }

    /// <summary>Compresses input; <paramref name="isFinalBlock"/> true is the same as <see cref="DeflateFlush.Finish"/> and false the same as <see cref="DeflateFlush.None"/>.</summary>
    /// <param name="source">New input.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last of the stream.</param>
    /// <returns>As <see cref="Compress(ReadOnlySpan{byte}, Span{byte}, out int, out int, DeflateFlush)"/>.</returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock) =>
        Compress(source, destination, out consumed, out written, isFinalBlock ? DeflateFlush.Finish : DeflateFlush.None);

    /// <summary>Forces out everything given so far without more input (<see cref="Compress(ReadOnlySpan{byte}, Span{byte}, out int, out int, DeflateFlush)"/> with an empty source).</summary>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <param name="flush">The flush mode.</param>
    /// <returns><see cref="OperationStatus.Done"/> or <see cref="OperationStatus.DestinationTooSmall"/>.</returns>
    public OperationStatus Flush(Span<byte> destination, out int written, DeflateFlush flush = DeflateFlush.Sync) =>
        Compress(ReadOnlySpan<byte>.Empty, destination, out _, out written, flush);

    private bool _finishedPending;

    private bool Drain(Span<byte> destination, ref int written)
    {
        written += _writer.CopyPending(destination.Slice(written));
        return _writer.PendingBytes == 0;
    }

    private void UpdateChecksum(ReadOnlySpan<byte> piece)
    {
        if (_format == DeflateFormat.GZip)
        {
            _crc.Append(piece);
        }
        else if (_format == DeflateFormat.ZLib)
        {
            _adler.Append(piece);
        }
    }

    private void WriteHeader()
    {
        switch (_format)
        {
            case DeflateFormat.ZLib:
                {
                    // RFC 1950 section 2.2: CMF = deflate with a 32 KiB window; FLG = level hint, dictionary flag, check bits.
                    int level = _options.Strategy is DeflateStrategy.HuffmanOnly or DeflateStrategy.RunLength || _options.Level < 2 ? 0
                        : _options.Level < 6 ? 1 : _options.Level == 6 ? 2 : 3;
                    int flags = (level << 6) | (_dictionary.IsEmpty ? 0 : 0x20);
                    int header = (0x78 << 8) | flags;
                    header += 31 - (header % 31);
                    Span<byte> bytes = stackalloc byte[6];
                    bytes[0] = (byte)(header >> 8);
                    bytes[1] = (byte)header;
                    int length = 2;
                    if (!_dictionary.IsEmpty)
                    {
                        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.Slice(2), Checksums.Adler32.Compute(_options.Dictionary.Span));
                        length = 6;
                    }

                    _writer.WriteBytes(bytes.Slice(0, length));
                    break;
                }

            case DeflateFormat.GZip:
                WriteGZipHeader();
                break;
        }
    }

    // RFC 1952 section 2.3.
    private void WriteGZipHeader()
    {
        DeflateOptions o = _options;
        int size = 10 + (o.GZipExtra.IsEmpty ? 0 : 2 + o.GZipExtra.Length)
            + (o.GZipFileName is null ? 0 : o.GZipFileName.Length + 1)
            + (o.GZipComment is null ? 0 : o.GZipComment.Length + 1)
            + (o.GZipHeaderCrc ? 2 : 0);
        // Usually small; only a long name, comment or extra field needs a heap buffer, so a reused encoder does not allocate.
        Span<byte> header = size <= 256 ? stackalloc byte[256].Slice(0, size) : new byte[size];
        header[0] = 0x1F;
        header[1] = 0x8B;
        header[2] = 8;
        header[3] = (byte)((o.GZipIsText ? 1 : 0) | (o.GZipHeaderCrc ? 2 : 0) | (o.GZipExtra.IsEmpty ? 0 : 4)
            | (o.GZipFileName is null ? 0 : 8) | (o.GZipComment is null ? 0 : 16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4), o.GZipModificationTime);
        header[8] = (byte)(o.Level >= 9 ? 2 : o.Level == 1 ? 4 : 0);
        header[9] = o.GZipOperatingSystem;
        int p = 10;
        if (!o.GZipExtra.IsEmpty)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(p), (ushort)o.GZipExtra.Length);
            o.GZipExtra.Span.CopyTo(header.Slice(p + 2));
            p += 2 + o.GZipExtra.Length;
        }

        if (o.GZipFileName is not null)
        {
            p += System.Text.Encoding.Latin1.GetBytes(o.GZipFileName, header.Slice(p)) + 1;
        }

        if (o.GZipComment is not null)
        {
            p += System.Text.Encoding.Latin1.GetBytes(o.GZipComment, header.Slice(p)) + 1;
        }

        if (o.GZipHeaderCrc)
        {
            uint crc = Checksums.Crc32.Compute(header.Slice(0, p));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(p), (ushort)crc);
        }

        _writer.WriteBytes(header);
    }

    private void WriteTrailer()
    {
        Span<byte> trailer = stackalloc byte[8];
        if (_format == DeflateFormat.ZLib)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(trailer, _adler.GetCurrentValue());
            _writer.WriteBytes(trailer.Slice(0, 4));
        }
        else if (_format == DeflateFormat.GZip)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer, _crc.GetCurrentValue());
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(trailer.Slice(4), (uint)_totalIn);
            _writer.WriteBytes(trailer);
        }
    }

    private void PerformFlush(DeflateFlush flush)
    {
        if (flush == DeflateFlush.Finish)
        {
            ParseTo(_fill);
            CloseBlock(isFinal: true);
            _writer.AlignToByte();
            WriteTrailer();
            return;
        }

        if (_dirty)
        {
            ParseTo(_fill);
            if (_position > _blockStart || _sequences.Count > 0)
            {
                CloseBlock(isFinal: false);
            }

            _writer.WriteEmptyStoredBlock();
            _dirty = false;
        }

        if (flush == DeflateFlush.Full)
        {
            // Later matches may not reach before this point: the stream can be decoded from here without what came before.
            _oldest = _position;
        }
    }

    // The window is full: parse all but the lookahead, write the block that is open, and slide the window down so that only
    // the last 32 KiB of history and the unparsed tail remain. Blocks never span a slide, because the stored-block fallback
    // needs the block's raw bytes.
    private void ProcessFullWindow()
    {
        ParseTo(_fill - Lookahead);
        if (_position > _blockStart)
        {
            CloseBlock(isFinal: false);
        }

        // The match finders index their link tables by position modulo the window size, so the window may only slide by a
        // multiple of that size: every stored position then keeps its slot.
        int delta = (_position - DeflateTables.WindowSize) & ~(DeflateTables.WindowSize - 1);
        if (delta <= 0)
        {
            return;
        }

        Buffer.BlockCopy(_window, delta, _window, 0, _fill - delta);
        _fill -= delta;
        _position -= delta;
        _blockStart = _position;
        _oldest = Math.Max(0, _oldest - delta);
        _chain?.Slide(delta);
        _optimal?.Finder.Slide(delta);
    }

    private void CloseBlock(bool isFinal)
    {
        DeflateBlockKind kind = _options.Strategy == DeflateStrategy.FixedCodes ? DeflateBlockKind.FixedOrStored : DeflateBlockKind.Auto;
        if (_parameters.Mode == ParseMode.Stored)
        {
            _writer.WriteStoredBlocks(_window.AsSpan(_blockStart, _position - _blockStart), isFinal);
        }
        else
        {
            _sequences.RunBase = _blockStart;
            _writer.WriteBlock(_sequences, _window.AsSpan(_blockStart, _position - _blockStart), haveInput: true, isFinal, kind);
        }

        _sequences.Clear();
        _splitter.Reset();
        _blockStart = _position;
        _checkAt = CheckInterval;
    }

    // Called after an entry is added, once the entry count passes _checkAt: end the block when it is full or its mix has drifted.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CheckBlock()
    {
        if (_sequences.Count >= _sequences.Capacity)
        {
            CloseBlock(isFinal: false);
            return;
        }

        if (_splitter.Observe(_sequences.LiteralLengthFrequencies))
        {
            CloseBlock(isFinal: false);
            return;
        }

        _checkAt = Math.Min(_sequences.Count + CheckInterval, _sequences.Capacity);
    }

    private void ParseTo(int limit)
    {
        if (_position >= limit)
        {
            return;
        }

        switch (_parameters.Mode)
        {
            case ParseMode.Stored:
                _position = limit;
                return;
            case ParseMode.Greedy:
                if (_options.Strategy == DeflateStrategy.HuffmanOnly)
                {
                    ParseLiterals(limit);
                }
                else if (_options.Strategy == DeflateStrategy.RunLength)
                {
                    ParseRuns(limit);
                }
                else if (_parameters.Chain == 1)
                {
                    ParseFastest(limit);
                }
                else
                {
                    ParseGreedy(limit);
                }

                return;
            default:
                if (_options.Strategy == DeflateStrategy.HuffmanOnly)
                {
                    ParseLiterals(limit);
                }
                else if (_options.Strategy == DeflateStrategy.RunLength)
                {
                    ParseRuns(limit);
                }
                else if (_parameters.Mode == ParseMode.Lazy)
                {
                    ParseLazy(limit);
                }
                else
                {
                    ParseOptimal(limit);
                }

                return;
        }
    }
}
