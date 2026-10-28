using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Writes LZ4 frames (LZ4 Frame Format) from data given in pieces. The encoder collects input into blocks of
/// <see cref="Lz4CompressionOptions.BlockSize"/>, compresses each with the compressor for the level, and writes the frame
/// header, the blocks, the end mark and the optional checksums. Several frames can be written one after another with the same
/// instance, which keeps its tables and buffers: after the first frame, encoding allocates nothing.
/// </summary>
/// <remarks>
/// <para>The output depends on the options and the data, and on where <see cref="Flush"/> is called, but not on how the data is
/// split between calls to <see cref="Compress"/> or on the size of the destination buffers.</para>
/// <para>A block that would not shrink is written uncompressed, as the format allows.</para>
/// <para>An instance is not thread-safe. A dictionary (<see cref="Lz4CompressionOptions.Dictionary"/>) is prepared once and may be
/// shared by encoders on several threads.</para>
/// </remarks>
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lz4Encoder
{
    private const int MaxHeader = Lz4Format.FrameHeaderMax;
    private const int History = Lz4Format.MaxOffset + 1;

    private readonly Lz4CompressionOptions _options;
    private readonly int _blockBytes;
    private readonly bool _linked;
    private readonly Lz4BlockCompressor _compressor;
    private readonly byte[] _stage;
    private readonly byte[] _window;
    private int _stageStart;
    private int _stageEnd;
    private int _historyLength;
    private int _pending;
    private bool _started;
    private bool _finished;
    private long _totalIn;
    private long _frameSize;
    private XxHash32 _contentHash;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The level, block layout, checksums and dictionary; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public Lz4Encoder(Lz4CompressionOptions? options = null)
    {
        _options = options ?? Lz4CompressionOptions.Default;
        _options.Validate();
        _blockBytes = _options.BlockBytes;
        _linked = !_options.BlocksIndependent;
        _compressor = Lz4BlockCompressor.Create(_options.Level);
        _compressor.SetDictionary(_options.Dictionary);

        // The stage holds a frame header, or one block with its size field and checksum, or the end of the frame; never two of them.
        _stage = new byte[Math.Max(MaxHeader, 4 + Lz4BlockWriter.GetMaxCompressedLength(_blockBytes) + 4)];
        _window = new byte[_linked ? History + _blockBytes : _blockBytes];
        _contentHash = new XxHash32();
        _frameSize = _options.ContentSize ?? -1;
    }

    /// <summary>
    /// Gets the most bytes a frame holding <paramref name="inputLength"/> bytes can take when it is written without
    /// <see cref="Flush"/>: the header, a size field (and checksum) per block, the data, the end mark and the content checksum.
    /// </summary>
    /// <param name="inputLength">The number of uncompressed bytes in the frame.</param>
    /// <param name="options">The options the frame is written with; <see langword="null"/> for the defaults.</param>
    /// <returns>An upper bound on the frame's size, saturating at <see cref="long.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative, or an option is out of range.</exception>
    public static long GetMaxCompressedLength(long inputLength, Lz4CompressionOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputLength);
        options ??= Lz4CompressionOptions.Default;
        options.Validate();
        long blockBytes = options.BlockBytes;
        long blocks = (inputLength + blockBytes - 1) / blockBytes;
        long perBlock = 4 + (options.BlockChecksum ? 4 : 0);
        long overhead = MaxHeader + (blocks * perBlock) + 4 + (options.ContentChecksum ? 4 : 0);
        return inputLength > long.MaxValue - overhead ? long.MaxValue : inputLength + overhead;
    }

    /// <summary>Gets the number of uncompressed bytes accepted in the current frame.</summary>
    public long TotalIn => _totalIn;

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramref name="destination"/>. Input is accepted into the current block, and
    /// output appears as blocks fill. When the frame is complete (<paramref name="isFinalBlock"/> true and everything written)
    /// the next call starts a new frame.
    /// </summary>
    /// <param name="source">The next bytes of the frame; may be empty.</param>
    /// <param name="destination">Receives frame bytes.</param>
    /// <param name="consumed">The number of bytes taken from <paramref name="source"/>; always set.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>; always set.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> ends the frame: the last block, the end mark and the checksum are written.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when all of <paramref name="source"/> was taken and all output due was written (with
    /// <paramref name="isFinalBlock"/>, the frame is complete); <see cref="OperationStatus.DestinationTooSmall"/> when output is
    /// waiting: call again with more room and the rest of the source. A destination of 4 KiB or more is enough to make progress.
    /// </returns>
    /// <exception cref="InvalidOperationException">More bytes arrived than <see cref="Lz4CompressionOptions.ContentSize"/> declared, or fewer when the frame ended.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
    {
        consumed = 0;
        written = 0;
        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_finished)
            {
                if (consumed == source.Length)
                {
                    return OperationStatus.Done;
                }

                StartNewFrame();
            }

            ReadOnlySpan<byte> rest = source.Slice(consumed);
            if (rest.IsEmpty && !isFinalBlock)
            {
                return OperationStatus.Done;
            }

            if (!_started)
            {
                WriteHeader();
                continue;
            }

            if (!rest.IsEmpty)
            {
                CheckContentSize(rest.Length);
                if (_pending == 0 && CanCompressInPlace && (rest.Length >= _blockBytes || isFinalBlock))
                {
                    // Whole blocks come straight from the caller's memory.
                    ReadOnlySpan<byte> block = rest.Slice(0, Math.Min(rest.Length, _blockBytes));
                    if (_options.ContentChecksum)
                    {
                        _contentHash.Append(block);
                    }

                    _totalIn += block.Length;
                    consumed += block.Length;
                    CompressBlock(block, 0, block);
                    continue;
                }

                int take = Math.Min(rest.Length, _blockBytes - _pending);
                rest.Slice(0, take).CopyTo(_window.AsSpan(_historyLength + _pending));
                if (_options.ContentChecksum)
                {
                    _contentHash.Append(rest.Slice(0, take));
                }

                _pending += take;
                _totalIn += take;
                consumed += take;
                if (_pending == _blockBytes)
                {
                    CompressPending();
                }

                continue;
            }

            // Nothing left to take and this is the end of the frame.
            if (_pending > 0)
            {
                CompressPending();
                continue;
            }

            WriteEnd();
        }
    }

    /// <summary>
    /// Ends the current block early, so that everything accepted so far can be decoded from what has been written. This costs
    /// ratio (a short block, and with independent blocks no history); the frame stays open.
    /// </summary>
    /// <param name="destination">Receives frame bytes.</param>
    /// <param name="written">The number of bytes written; always set.</param>
    /// <returns><see cref="OperationStatus.Done"/> when everything is written; <see cref="OperationStatus.DestinationTooSmall"/> when more room is needed: call again.</returns>
    public OperationStatus Flush(Span<byte> destination, out int written)
    {
        written = 0;
        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_pending == 0 || _finished)
            {
                return OperationStatus.Done;
            }

            CompressPending();
        }
    }

    /// <summary>
    /// Writes <paramref name="source"/> as one complete frame, abandoning any frame in progress. Unlike <see cref="Compress"/>,
    /// it knows the size, so it writes the content size in the header when <see cref="Lz4CompressionOptions.IncludeContentSize"/>
    /// is true and <see cref="Lz4CompressionOptions.ContentSize"/> is not set. Use it to encode many small payloads with one
    /// encoder: after the first call it allocates nothing.
    /// </summary>
    /// <param name="source">The frame's data; may be empty.</param>
    /// <param name="destination">Receives the frame; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="written">The number of bytes written when the method returns true.</param>
    /// <returns>True when the frame was written; false when <paramref name="destination"/> is too small (the encoder is then reset).</returns>
    /// <exception cref="InvalidOperationException"><see cref="Lz4CompressionOptions.ContentSize"/> is set and differs from the length of <paramref name="source"/>.</exception>
    public bool TryCompressFrame(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        Reset();
        if (_options.ContentSize is null && _options.IncludeContentSize)
        {
            _frameSize = source.Length;
        }

        OperationStatus status = Compress(source, destination, out _, out written, isFinalBlock: true);
        if (status != OperationStatus.Done)
        {
            Reset();
            return false;
        }

        return true;
    }

    /// <summary>Abandons the current frame, if any, and returns the encoder to its initial state. The next call starts a new frame.</summary>
    public void Reset()
    {
        StartNewFrame();
        _stageStart = 0;
        _stageEnd = 0;
    }

    private bool CanCompressInPlace => !_linked || _historyLength == 0;

    private void StartNewFrame()
    {
        _started = false;
        _finished = false;
        _historyLength = 0;
        _pending = 0;
        _totalIn = 0;
        _contentHash = new XxHash32();
        _frameSize = _options.ContentSize ?? -1;
        _compressor.Reset();
    }

    private bool Drain(Span<byte> destination, ref int written)
    {
        int available = _stageEnd - _stageStart;
        if (available > 0)
        {
            int room = destination.Length - written;
            int n = Math.Min(available, room);
            _stage.AsSpan(_stageStart, n).CopyTo(destination.Slice(written));
            written += n;
            _stageStart += n;
            if (n < available)
            {
                return false;
            }
        }

        _stageStart = 0;
        _stageEnd = 0;
        return true;
    }

    private void CheckContentSize(int arriving)
    {
        if (_frameSize >= 0 && _totalIn + arriving > _frameSize)
        {
            throw new InvalidOperationException($"More data arrived than the declared content size of {_frameSize} bytes.");
        }
    }

    private void WriteHeader()
    {
        Span<byte> h = _stage;
        BinaryPrimitives.WriteUInt32LittleEndian(h, Lz4Format.FrameMagic);
        Lz4Dictionary? dictionary = _options.Dictionary;
        bool hasDictionaryId = dictionary is not null && dictionary.Id != 0;
        int flg = 0x40;
        if (!_linked)
        {
            flg |= 0x20;
        }

        if (_options.BlockChecksum)
        {
            flg |= 0x10;
        }

        if (_frameSize >= 0)
        {
            flg |= 0x08;
        }

        if (_options.ContentChecksum)
        {
            flg |= 0x04;
        }

        if (hasDictionaryId)
        {
            flg |= 0x01;
        }

        h[4] = (byte)flg;
        h[5] = (byte)((int)_options.BlockSize << 4);
        int at = 6;
        if (_frameSize >= 0)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(h.Slice(at), (ulong)_frameSize);
            at += 8;
        }

        if (hasDictionaryId)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.Slice(at), dictionary!.Id);
            at += 4;
        }

        // Frame format, "Header Checksum": the second byte of xxh32 of the descriptor.
        h[at] = (byte)(XxHash32.Compute(h.Slice(4, at - 4)) >> 8);
        _stageStart = 0;
        _stageEnd = at + 1;
        _started = true;
    }

    private void CompressPending()
    {
        int total = _historyLength + _pending;
        CompressBlock(_window.AsSpan(0, total), _historyLength, default);
        _pending = 0;
        if (_linked)
        {
            // Keep the last 64 KiB as history for the next block, moved to the front of the window.
            int keep = Math.Min(total, History);
            int removed = total - keep;
            if (removed > 0)
            {
                Buffer.BlockCopy(_window, removed, _window, 0, keep);
                _compressor.Rebase(removed);
            }

            _historyLength = keep;
        }
        else
        {
            _historyLength = 0;
        }
    }

    // Compresses window[history..] into the stage as one block. `inPlace` is non-empty when the block came straight from the
    // caller's memory rather than from the window; a linked frame then needs its tail copied into the window as history.
    private void CompressBlock(ReadOnlySpan<byte> window, int history, ReadOnlySpan<byte> inPlace)
    {
        int n = window.Length - history;
        Span<byte> stage = _stage;
        int size = _compressor.Compress(window, history, stage.Slice(4));
        ReadOnlySpan<byte> data;
        if (size >= n)
        {
            // Frame format, "Data Blocks": the high bit of the size marks a block stored as it is.
            BinaryPrimitives.WriteUInt32LittleEndian(stage, (uint)n | 0x80000000u);
            window.Slice(history).CopyTo(stage.Slice(4));
            size = n;
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(stage, (uint)size);
        }

        data = stage.Slice(4, size);
        int end = 4 + size;
        if (_options.BlockChecksum)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(stage.Slice(end), XxHash32.Compute(data));
            end += 4;
        }

        _stageStart = 0;
        _stageEnd = end;

        if (!inPlace.IsEmpty && _linked)
        {
            int keep = Math.Min(n, History);
            inPlace.Slice(n - keep).CopyTo(_window);
            _compressor.Rebase(n - keep);
            _historyLength = keep;
        }
    }

    private void WriteEnd()
    {
        if (_frameSize >= 0 && _totalIn != _frameSize)
        {
            throw new InvalidOperationException($"The frame ended after {_totalIn} bytes, but its header declares {_frameSize}.");
        }

        Span<byte> stage = _stage;
        BinaryPrimitives.WriteUInt32LittleEndian(stage, 0);
        int end = 4;
        if (_options.ContentChecksum)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(stage.Slice(end), _contentHash.GetCurrentValue());
            end += 4;
        }

        _stageStart = 0;
        _stageEnd = end;
        _finished = true;
    }
}
