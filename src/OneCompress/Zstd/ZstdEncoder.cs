using System.Buffers;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Compresses data to Zstandard frames. The encoder owns its buffers and tables and reuses them from frame to frame, so a
/// steady state (one encoder compressing many frames, with or without a dictionary) allocates nothing.
/// </summary>
/// <remarks>
/// <para>Two ways to use it. <see cref="CompressFrame"/> turns a whole buffer into one frame. <see cref="Compress"/> and
/// <see cref="Flush"/> take the data in pieces: blocks are cut every 128 KiB of the input, so the output does not depend on how
/// the input was split across calls (unless <see cref="Flush"/> is called, which ends a block early).</para>
/// <para>An encoder is not thread-safe; a dictionary can be shared between encoders.</para>
/// </remarks>
public sealed partial class ZstdEncoder
{
    private const int BlockSize = ZstdFormat.BlockSizeMax;
    private const int OutputSlack = ZstdBlockWriter.DestinationSlack;

    private readonly ZstdCompressionOptions _options;
    private readonly ZstdFastMatchFinder _fastFinder = new();
    private ZstdLazyMatchFinder? _lazyFinder;
    private ZstdOptimalMatchFinder? _optimalFinder;
    private ZstdBlockSplitter? _splitter;
    private ZstdSequenceStore? _subStore;
    private bool _split;
    private ZstdMatchFinder _finder;
    private readonly ZstdBlockWriter _writer = new();
    private readonly ZstdSequenceStore _store = new();

    private ZstdCompressionParameters _parameters;
    private byte[] _window = [];
    private int _windowFill;
    private int _blockStart;
    private long _windowStartPosition;
    private long _totalIn;
    private XxHash64 _hash;
    private bool _started;
    private bool _finished;
    private bool _lastBlockWritten;

    // Compressed bytes (header, a block, the checksum) that the caller's buffer had no room for yet.
    private byte[] _pending = [];
    private int _pendingStart;
    private int _pendingEnd;

    // Used when a one-shot caller's buffer is too small to receive a block in place.
    private byte[] _blockScratch = [];

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The options; <see langword="null"/> for <see cref="ZstdCompressionOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is not supported.</exception>
    public ZstdEncoder(ZstdCompressionOptions? options = null)
    {
        _options = options ?? ZstdCompressionOptions.Default;
        _finder = _fastFinder;
        _ = ZstdCompressionParameters.Resolve(_options.Level, -1, 0);
        if (_options.WindowLog != 0 && (_options.WindowLog < ZstdCompressionParameters.MinWindowLog || _options.WindowLog > 31))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "WindowLog must be 0 or from 10 to 31.");
        }

        ValidateParallelOptions();
    }

    /// <summary>
    /// Gets the most bytes that compressing <paramref name="sourceLength"/> bytes in one frame can produce, including the header,
    /// block headers, checksum and the slack the encoder needs to write in place.
    /// </summary>
    /// <param name="sourceLength">The number of bytes to compress.</param>
    /// <returns>The size of a destination buffer that always suffices for <see cref="CompressFrame"/>.</returns>
    public static int GetMaxCompressedLength(int sourceLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        long blocks = Math.Max(1, ((long)sourceLength + BlockSize - 1) / BlockSize);
        long bound = ZstdFrameWriter.MaxHeaderSize + sourceLength + (3 * blocks * ZstdBlockSplitter.MaxSegments) + 4;
        return (int)Math.Min(bound, int.MaxValue - 64);
    }

    /// <summary>Drops any frame in progress; the next call starts a new frame.</summary>
    public void Reset()
    {
        _started = false;
        _finished = false;
        _lastBlockWritten = false;
        _windowFill = 0;
        _blockStart = 0;
        _windowStartPosition = 0;
        _totalIn = 0;
        _pendingStart = 0;
        _pendingEnd = 0;
        ResetParallel();
    }

    // ---- One-shot ----

    /// <summary>Compresses <paramref name="source"/> into one frame.</summary>
    /// <param name="source">The data; may be empty.</param>
    /// <param name="destination">Receives the frame; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small for the frame.</exception>
    public int CompressFrame(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (!TryCompressFrame(source, destination, out int written))
        {
            throw new ArgumentException("The destination is too small for the compressed frame.", nameof(destination));
        }

        return written;
    }

    /// <summary>Compresses <paramref name="source"/> into one frame if <paramref name="destination"/> is large enough.</summary>
    /// <param name="source">The data; may be empty.</param>
    /// <param name="destination">Receives the frame.</param>
    /// <param name="written">The number of bytes written, 0 when the destination was too small.</param>
    /// <returns>True on success; false when the frame does not fit.</returns>
    public bool TryCompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int written) =>
        TryCompressFrame(source, source.Length, destination, out written);

    // declaredSize is what the parameters and header are derived from: the source length, or -1 for a frame whose size
    // the caller did not give (the streaming call, so that its bytes do not depend on how the input arrives).
    private bool TryCompressFrame(ReadOnlySpan<byte> source, long declaredSize, Span<byte> destination, out int written)
    {
        Reset();
        written = 0;
        ZstdCompressionParameters parameters = ResolveParameters(declaredSize);
        ZstdCompressionDictionary? dictionary = _options.Dictionary;
        SelectFinder(parameters.Strategy);
        _finder.BeginFrame(parameters, dictionary);
        _writer.BeginFrame(dictionary, parameters.CompressLiterals);
        BeginLongDistance(declaredSize);
        int p;
        Span<byte> header = stackalloc byte[ZstdFrameWriter.MaxHeaderSize];
        int headerSize = ZstdFrameWriter.WriteHeader(header, _headerWindowLog, _options.WriteContentSize ? declaredSize : -1, DictionaryIdToWrite(), _options.Checksum);
        if (destination.Length < headerSize)
        {
            return false;
        }

        header[..headerSize].CopyTo(destination);
        p = headerSize;
        int jobSize = ResolveJobSize(parameters);
        if (jobSize > 0 && source.Length > jobSize)
        {
            if (!TryCompressJobs(source, parameters, jobSize, destination, ref p))
            {
                return false;
            }
        }
        else
        {
            int position = 0;
            do
            {
                int end = Math.Min(position + BlockSize, source.Length);
                bool last = end == source.Length;
                int written2 = CompressBlock(source, position, end, 0, last, destination[p..], out bool fits);
                if (!fits)
                {
                    return false;
                }

                p += written2;
                position = end;
            }
            while (position < source.Length);
        }

        if (_options.Checksum)
        {
            if (destination.Length - p < 4)
            {
                return false;
            }

            p += ZstdFrameWriter.WriteChecksum(destination[p..], XxHash64.Compute(source));
        }

        written = p;
        return true;
    }

    private uint DictionaryIdToWrite() => _options.WriteDictionaryId && _options.Dictionary is { } d ? d.Id : 0;

    private ZstdCompressionParameters ResolveParameters(long contentSize)
    {
        ZstdCompressionParameters p = ZstdCompressionParameters.Resolve(_options.Level, contentSize, _options.Dictionary?.Content.Length ?? 0);
        int windowLog = _options.WindowLog;
        if (windowLog == 0 && _options.LongDistanceMatching)
        {
            windowLog = DefaultLongWindowLog(contentSize, _options.Dictionary?.Content.Length ?? 0);
        }

        if (windowLog != 0)
        {
            p = new ZstdCompressionParameters
            {
                WindowLog = windowLog,
                ChainLog = Math.Min(p.ChainLog, windowLog),
                HashLog = Math.Min(p.HashLog, windowLog + 1),
                DictionaryHashLog = p.DictionaryHashLog,
                DictionaryChainLog = p.DictionaryChainLog,
                SearchLog = p.SearchLog,
                MinMatch = p.MinMatch,
                TargetLength = p.TargetLength,
                Strategy = p.Strategy,
                CompressLiterals = p.CompressLiterals,
            };
        }

        // The header states the window asked for; matching never reaches past 2^30 bytes, the most a window buffer can hold.
        // Blocks are cut every 128 KiB, and a block may not exceed the window (RFC 8878 3.1.1.2.3), so a window log below 17 asked for
        // explicitly is declared as 17 in the header while matching keeps to the smaller window. Windows chosen by the level tables are
        // only that small for sources that fit them whole, where the frame is single-segment.
        _headerWindowLog = _options.WindowLog != 0 ? Math.Max(p.WindowLog, 17) : p.WindowLog;
        if (p.WindowLog > MaxMatchWindowLog)
        {
            p = new ZstdCompressionParameters
            {
                WindowLog = MaxMatchWindowLog,
                ChainLog = p.ChainLog,
                HashLog = p.HashLog,
                DictionaryHashLog = p.DictionaryHashLog,
                DictionaryChainLog = p.DictionaryChainLog,
                SearchLog = p.SearchLog,
                MinMatch = p.MinMatch,
                TargetLength = p.TargetLength,
                Strategy = p.Strategy,
                CompressLiterals = p.CompressLiterals,
            };
        }

        _parameters = p;
        return p;
    }

    // Parses and writes one block of window[start..end]. The block goes straight to destination when it has room for the block and
    // the writer's slack; otherwise it is built in a scratch buffer and copied if it fits.
    private int CompressBlock(ReadOnlySpan<byte> window, int start, int end, long windowStartPosition, bool last, Span<byte> destination, out bool fits)
    {
        int n = end - start;
        ReadOnlySpan<byte> block = window[start..end];
        int needed = n + 3 + OutputSlack + (_split ? 3 * (ZstdBlockSplitter.MaxSegments - 1) : 0);
        Span<byte> target = destination;
        bool scratch = destination.Length < needed;
        if (scratch)
        {
            if (_blockScratch.Length < BlockSize + 3 + OutputSlack + (3 * ZstdBlockSplitter.MaxSegments))
            {
                _blockScratch = new byte[BlockSize + 3 + OutputSlack + (3 * ZstdBlockSplitter.MaxSegments)];
            }

            target = _blockScratch;
        }

        // The long-distance matcher reads every block, whatever kind of block it becomes, so it never misses data.
        ReadOnlySpan<ZstdLongMatch> longMatches = default;
        if (_ldm is not null)
        {
            _ldmMatches.Clear();
            _ldm.FindMatches(window, start, end, _ldmMatches);
            longMatches = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ldmMatches);
        }

        int size;
        if (n > 1 && block.IndexOfAnyExcept(block[0]) < 0)
        {
            size = ZstdBlockWriter.WriteRleBlock(block[0], n, last, target);
        }
        else
        {
            _store.Clear();
            ZstdLongSequencer.Fill(_finder, window, start, end, windowStartPosition, _writer.RepeatOffsets, longMatches, _store);
            size = _split ? WriteSplit(block, last, target) : _writer.WriteBlock(block, _store, last, target);
        }

        fits = true;
        if (scratch)
        {
            if (size > destination.Length)
            {
                fits = false;
                return 0;
            }

            target[..size].CopyTo(destination);
        }

        return size;
    }

    // Writes the parsed block (in _store) as one block or, when the sequences' statistics change enough to pay for another set of
    // headers and tables, as several consecutive blocks.
    private int WriteSplit(ReadOnlySpan<byte> block, bool last, Span<byte> target)
    {
        ZstdBlockSplitter splitter = _splitter ??= new ZstdBlockSplitter();
        int segments = splitter.Plan(_store, _writer.RepeatOffsets);
        if (segments < 2)
        {
            return _writer.WriteBlock(block, _store, last, target);
        }

        _subStore ??= new ZstdSequenceStore();
        ReadOnlySpan<ZstdSequence> sequences = _store.Sequences;
        ReadOnlySpan<byte> literals = _store.Literals;
        int written = 0;
        for (int s = 0; s < segments; s++)
        {
            _subStore.Clear();
            _subStore.BeginTracking(splitter.Reps[s]);
            int literalPosition = splitter.LiteralStart[s];
            for (int i = splitter.SequenceStart[s]; i < splitter.SequenceStart[s + 1]; i++)
            {
                ZstdSequence q = sequences[i];
                _subStore.AppendCopied(literals.Slice(literalPosition, (int)q.LiteralLength), q.LiteralLength, q.MatchLength, q.Offset, splitter.OffsetValue(i));
                literalPosition += (int)q.LiteralLength;
            }

            if (s == segments - 1)
            {
                _subStore.AppendLiterals(literals[literalPosition..]);
            }

            ZstdRepeatOffsets end = splitter.Reps[s + 1];
            _subStore.SetTrackedEnd(end.Offset1, end.Offset2, end.Offset3);
            int byteEnd = s == segments - 1 ? block.Length : splitter.ByteStart[s + 1];
            written += _writer.WriteBlock(block[splitter.ByteStart[s]..byteEnd], _subStore, last && s == segments - 1, target[written..]);
        }

        return written;
    }

    // ---- Streaming ----

    /// <summary>
    /// Compresses a piece of the input.
    /// </summary>
    /// <param name="source">The next input bytes.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken.</param>
    /// <param name="written">The number of compressed bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> ends the input of the frame.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when all of <paramref name="source"/> was taken and, with <paramref name="isFinalBlock"/>, the
    /// frame is complete and fully written; <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting for room
    /// (call again with the input not yet consumed and an empty or fresh destination). It never returns <see cref="OperationStatus.InvalidData"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException"><see cref="ZstdCompressionOptions.ContentSize"/> was given and the frame did not receive that many bytes.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
    {
        consumed = 0;
        written = 0;
        if (_finished && _pendingStart == _pendingEnd)
        {
            if (source.IsEmpty)
            {
                return OperationStatus.Done;
            }

            Reset();
        }

        if (_options.JobSize != 0 && (_started || !isFinalBlock || (_options.ContentSize ?? source.Length) != source.Length || destination.Length < GetMaxCompressedLength(source.Length)))
        {
            return CompressJobs(source, destination, out consumed, out written, isFinalBlock);
        }

        // A whole frame in one call goes straight from the caller's buffer to the caller's buffer.
        if (!_started && isFinalBlock && (_options.ContentSize ?? source.Length) == source.Length && destination.Length >= GetMaxCompressedLength(source.Length))
        {
            TryCompressFrame(source, _options.ContentSize ?? -1, destination, out written);
            consumed = source.Length;
            _started = true;
            _finished = true;
            return OperationStatus.Done;
        }

        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_finished)
            {
                return OperationStatus.Done;
            }

            if (!_started)
            {
                StartFrame();
                continue;
            }

            int available = _windowFill - _blockStart;
            bool inputLeft = consumed < source.Length;
            if (available > BlockSize || (isFinalBlock && !inputLeft && available > 0))
            {
                bool last = isFinalBlock && !inputLeft && available <= BlockSize;
                EmitBlock(Math.Min(available, BlockSize), last);
                continue;
            }

            if (inputLeft)
            {
                int room = BlockSize + 1 - available;
                int take = Math.Min(room, source.Length - consumed);
                MakeRoom(take);
                source.Slice(consumed, take).CopyTo(_window.AsSpan(_windowFill));
                if (_options.Checksum)
                {
                    _hash.Append(source.Slice(consumed, take));
                }

                _windowFill += take;
                _totalIn += take;
                consumed += take;
                continue;
            }

            if (isFinalBlock && !_lastBlockWritten)
            {
                WriteEmptyLastBlock();
                continue;
            }

            return OperationStatus.Done;
        }
    }

    /// <summary>
    /// Ends the block in progress with the input given so far, so the compressed bytes written so far decode up to this point.
    /// The frame stays open. Flushing changes where blocks end, and so changes the compressed bytes.
    /// </summary>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.Done"/> or <see cref="OperationStatus.DestinationTooSmall"/> (call again with room).</returns>
    public OperationStatus Flush(Span<byte> destination, out int written)
    {
        written = 0;
        if (!_started)
        {
            return OperationStatus.Done;
        }

        if (_mtActive)
        {
            return FlushJobs(destination, out written);
        }

        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            int available = _windowFill - _blockStart;
            if (_finished || available == 0)
            {
                return OperationStatus.Done;
            }

            EmitBlock(Math.Min(available, BlockSize), false);
        }
    }

    private bool Drain(Span<byte> destination, ref int written)
    {
        int count = Math.Min(_pendingEnd - _pendingStart, destination.Length - written);
        if (count > 0)
        {
            _pending.AsSpan(_pendingStart, count).CopyTo(destination[written..]);
            _pendingStart += count;
            written += count;
        }

        if (_pendingStart == _pendingEnd)
        {
            _pendingStart = 0;
            _pendingEnd = 0;
            return true;
        }

        return false;
    }

    private void SelectFinder(ZstdStrategy strategy)
    {
        _split = strategy >= ZstdStrategy.BinaryTreeOptimal;
        if (strategy >= ZstdStrategy.BinaryTreeOptimal)
        {
            _finder = _optimalFinder ??= new ZstdOptimalMatchFinder();
            return;
        }

        if (strategy >= ZstdStrategy.Greedy)
        {
            _finder = _lazyFinder ??= new ZstdLazyMatchFinder();
            return;
        }

        _finder = _fastFinder;
    }

    private void StartFrame()
    {
        long pledged = _options.ContentSize ?? -1;
        ZstdCompressionParameters parameters = ResolveParameters(pledged);
        ZstdCompressionDictionary? dictionary = _options.Dictionary;
        SelectFinder(parameters.Strategy);
        _finder.BeginFrame(parameters, dictionary);
        _writer.BeginFrame(dictionary, parameters.CompressLiterals);
        BeginLongDistance(pledged);
        _hash = new XxHash64(0);
        _totalIn = 0;
        _windowFill = 0;
        _blockStart = 0;
        _windowStartPosition = 0;

        // The window holds the history the finder may reach (the window size and one more byte, so the oldest table entry
        // is always out of reach) plus the block being gathered.
        int windowSize = (int)Math.Min(parameters.WindowSize, 1 << 30);
        int capacity = windowSize + 1 + (2 * BlockSize) + 64;
        if (_window.Length < capacity)
        {
            _window = new byte[capacity];
        }

        if (_pending.Length < BlockSize + 3 + OutputSlack + 64)
        {
            _pending = new byte[BlockSize + 3 + OutputSlack + 64];
        }

        _pendingStart = 0;
        _pendingEnd = ZstdFrameWriter.WriteHeader(_pending, _headerWindowLog, _options.WriteContentSize ? pledged : -1, DictionaryIdToWrite(), _options.Checksum);
        _started = true;
    }

    private void MakeRoom(int incoming)
    {
        if (_windowFill + incoming <= _window.Length - 64)
        {
            return;
        }

        int keep = (int)Math.Min(_parameters.WindowSize + 1, _blockStart);
        int delta = _blockStart - keep;
        if (delta > 0)
        {
            Buffer.BlockCopy(_window, delta, _window, 0, _windowFill - delta);
            _windowFill -= delta;
            _blockStart -= delta;
            _windowStartPosition += delta;
            _finder.Slide(delta);
            _ldm?.Slide(delta);
        }
    }

    private void EmitBlock(int length, bool last)
    {
        int size = CompressBlock(_window, _blockStart, _blockStart + length, _windowStartPosition, last, _pending.AsSpan(_pendingEnd), out _);
        _pendingEnd += size;
        _blockStart += length;
        if (last)
        {
            _lastBlockWritten = true;
            CompleteFrame();
        }
    }

    // The input ended without a block that ends the frame (no input at all, or everything went out in flushed blocks).
    private void WriteEmptyLastBlock()
    {
        _pendingEnd += ZstdBlockWriter.WriteRawBlock(ReadOnlySpan<byte>.Empty, true, _pending.AsSpan(_pendingEnd));
        _lastBlockWritten = true;
        CompleteFrame();
    }

    private void CompleteFrame()
    {
        if (_options.ContentSize is { } pledged && pledged != _totalIn)
        {
            throw new InvalidOperationException($"The frame was started for {pledged} bytes of content but received {_totalIn}.");
        }

        if (_options.Checksum)
        {
            _pendingEnd += ZstdFrameWriter.WriteChecksum(_pending.AsSpan(_pendingEnd), _hash.GetCurrentValue());
        }

        _finished = true;
    }
}
