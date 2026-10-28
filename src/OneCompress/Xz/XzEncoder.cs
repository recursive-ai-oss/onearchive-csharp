using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Xz;

/// <summary>
/// Compresses data to the xz format: a stream header, one or more blocks (each a block header, an LZMA2 stream with optional
/// branch and delta filters in front, padding and an integrity check), an index of the blocks and a stream footer.
/// </summary>
/// <remarks>
/// <para>The xz file format specification, sections 2 to 4. Shaped like <c>BrotliEncoder</c>: call <see cref="Compress"/> until it returns
/// <see cref="OperationStatus.Done"/>, passing <c>isFinalBlock</c> with the last input. Empty input gives a stream with no blocks.</para>
/// <para>Block headers leave the optional compressed and uncompressed sizes out (the index has them), as the reference tool does when it
/// streams. Output depends only on the input and the options, including <see cref="XzCompressionOptions.BlockSize"/>, never on how the
/// input was split into calls. An instance is not thread-safe.</para>
/// </remarks>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class XzEncoder : IQueuedEncoder
{
    private const int StageSize = 1 << 16;
    private const int FilterIdLzma2 = 0x21;

    private readonly OutputQueue _queue = new();
    private readonly CountingWriter _counter;
    private readonly Lzma2Chunker _chunker;
    private readonly XzCheck _check;
    private readonly long _blockSize;
    private readonly XzFilter[] _filters;
    private readonly XzFilter.IFilterStage[] _stages;
    private readonly byte[][] _stageBuffers;
    private readonly int[] _stageFill;
    private readonly byte[] _pending;
    private readonly List<(long Unpadded, long Uncompressed)> _records = [];
    private readonly IncrementalHash? _sha256;
    private Crc32 _crc32;
    private Crc64 _crc64;
    private int _pendingOffset;
    private int _pendingCount;
    private bool _headerWritten;
    private bool _inBlock;
    private bool _chunkerDirty;
    private bool _finished;
    private long _blockRaw;
    private int _blockHeaderSize;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The options; <see langword="null"/> for the defaults.</param>
    /// <param name="inputLength">The total input length when known, or -1. It only sizes memory; a wrong value can make matches shorter than announced dictionary allows, but the stream stays valid.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="ArgumentException">There are too many filters.</exception>
    public XzEncoder(XzCompressionOptions? options = null, long inputLength = -1)
    {
        options ??= XzCompressionOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(inputLength, -1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BlockSize);
        if (options.Check is not (XzCheck.None or XzCheck.Crc32 or XzCheck.Crc64 or XzCheck.Sha256))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The check must be None, Crc32, Crc64 or Sha256.");
        }

        _filters = options.Filters is null ? [] : [.. options.Filters];
        if (_filters.Length > XzCompressionOptions.MaxFilters - 1)
        {
            throw new ArgumentException($"At most {XzCompressionOptions.MaxFilters - 1} filters can precede LZMA2.", nameof(options));
        }

        _check = options.Check;
        _blockSize = options.BlockSize;
        long hint = inputLength;
        if (_blockSize > 0)
        {
            hint = hint < 0 ? _blockSize : Math.Min(hint, _blockSize);
        }

        _chunker = new Lzma2Chunker(options.Lzma.Resolve(), hint, _blockSize);
        _counter = new CountingWriter(_queue);
        _stages = new XzFilter.IFilterStage[_filters.Length];
        _stageBuffers = new byte[_filters.Length][];
        _stageFill = new int[_filters.Length];
        for (int i = 0; i < _filters.Length; i++)
        {
            _stages[i] = _filters[i].CreateStage();
            _stageBuffers[i] = new byte[(2 * StageSize) + 256];
        }

        _pending = new byte[(2 * StageSize) + 1024];
        if (_check == XzCheck.Sha256)
        {
            _sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }
    }

    /// <summary>Gets a value indicating whether the final block was given and all output has been produced.</summary>
    public bool IsFinished => _finished;

    OutputQueue IQueuedEncoder.Queue => _queue;

    bool IQueuedEncoder.IsDone => _finished;

    /// <summary>Compresses input.</summary>
    /// <param name="source">The next input.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken. Input not taken must be passed again.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last input.</param>
    /// <returns><see cref="OperationStatus.Done"/> when the stream is complete; <see cref="OperationStatus.DestinationTooSmall"/> when more output is waiting; <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not final.</returns>
    /// <exception cref="InvalidOperationException">Input was passed after the final block.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock) =>
        EncodeLoop.Compress(this, source, destination, out consumed, out written, isFinalBlock);

    /// <summary>Starts a new stream, keeping the allocated window and tables.</summary>
    public void Reset()
    {
        _chunker.Reset();
        _queue.Clear();
        _records.Clear();
        _pendingCount = 0;
        _pendingOffset = 0;
        _headerWritten = false;
        _inBlock = false;
        _chunkerDirty = false;
        _finished = false;
        _blockRaw = 0;
        ResetFilters();
    }

    int IQueuedEncoder.Accept(ReadOnlySpan<byte> source)
    {
        EnsureStreamHeader();
        if (_pendingCount > 0 || source.Length == 0)
        {
            return 0;
        }

        if (!_inBlock)
        {
            StartBlock();
        }

        long room = _blockSize > 0 ? _blockSize - _blockRaw : long.MaxValue;
        if (room <= 0)
        {
            return 0; // the block is full; the next Pump closes it
        }

        int take = (int)Math.Min(Math.Min(source.Length, room), StageSize);
        ReadOnlySpan<byte> part = source[..take];
        UpdateCheck(part);
        _blockRaw += take;
        Feed(0, part);
        return take;
    }

    bool IQueuedEncoder.Pump(bool drain)
    {
        EnsureStreamHeader();
        bool progressed = DrainPending();
        if (!_inBlock)
        {
            return progressed;
        }

        if (drain || (_blockSize > 0 && _blockRaw >= _blockSize))
        {
            CloseBlock();
            return true;
        }

        return _chunker.WriteChunks(_counter, false, maxChunks: 8) > 0 || progressed;
    }

    void IQueuedEncoder.Finish()
    {
        EnsureStreamHeader();
        WriteIndexAndFooter();
        _finished = true;
    }

    // ---- Stream structure ----

    private static int CheckSize(XzCheck check) => check switch
    {
        XzCheck.None => 0,
        XzCheck.Crc32 => 4,
        XzCheck.Crc64 => 8,
        _ => 32,
    };

    private void EnsureStreamHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;
        Span<byte> h = _queue.GetSpan(12);
        h[0] = 0xFD;
        h[1] = 0x37;
        h[2] = 0x7A;
        h[3] = 0x58;
        h[4] = 0x5A;
        h[5] = 0x00;
        h[6] = 0x00;
        h[7] = (byte)_check;
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], Crc32.Compute(h.Slice(6, 2)));
        _queue.Advance(12);
    }

    private static int WriteVarint(Span<byte> destination, ulong value)
    {
        int n = 0;
        while (value >= 0x80)
        {
            destination[n++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[n++] = (byte)value;
        return n;
    }

    // xz file format specification, section 3.1: size byte, flags, filter flags, padding to a multiple of four, CRC32.
    private void StartBlock()
    {
        if (_chunkerDirty)
        {
            _chunker.Reset();
        }

        _chunkerDirty = true;
        ResetFilters();
        _blockRaw = 0;
        _counter.Count = 0;
        _crc32 = default;
        _crc64 = default;
        _sha256?.GetHashAndReset();
        _inBlock = true;

        Span<byte> header = stackalloc byte[64];
        header.Clear();
        Span<byte> props = stackalloc byte[4];
        int at = 2; // the size byte and the flags come last
        header[1] = (byte)_filters.Length; // number of filters minus one, counting LZMA2; no size fields
        foreach (XzFilter filter in _filters)
        {
            at += WriteVarint(header[at..], (ulong)filter.Id);
            int propSize = filter.WriteProperties(props);
            at += WriteVarint(header[at..], (ulong)propSize);
            props[..propSize].CopyTo(header[at..]);
            at += propSize;
        }

        at += WriteVarint(header[at..], FilterIdLzma2);
        at += WriteVarint(header[at..], 1);
        header[at++] = _chunker.DictionaryProperty;
        int total = (at + 4 + 3) & ~3;
        header = header[..total];
        header[0] = (byte)((total / 4) - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(total - 4)..], Crc32.Compute(header[..(total - 4)]));
        _queue.Write(header);
        _blockHeaderSize = total;
    }

    private void ResetFilters()
    {
        for (int i = 0; i < _stages.Length; i++)
        {
            _stages[i].Reset();
            _stageFill[i] = 0;
        }
    }

    private void UpdateCheck(ReadOnlySpan<byte> data)
    {
        switch (_check)
        {
            case XzCheck.Crc32:
                _crc32.Append(data);
                break;
            case XzCheck.Crc64:
                _crc64.Append(data);
                break;
            case XzCheck.Sha256:
                _sha256!.AppendData(data);
                break;
        }
    }

    // ---- Filters and window ----

    // Passes data through filter `index` and on; a filter may hold back a few trailing bytes until the bytes that follow arrive.
    private void Feed(int index, ReadOnlySpan<byte> data)
    {
        if (index == _stages.Length)
        {
            Push(data);
            return;
        }

        byte[] buffer = _stageBuffers[index];
        int fill = _stageFill[index];
        data.CopyTo(buffer.AsSpan(fill));
        fill += data.Length;
        int done = _stages[index].Convert(buffer.AsSpan(0, fill));
        Feed(index + 1, buffer.AsSpan(0, done));
        int rest = fill - done;
        Buffer.BlockCopy(buffer, done, buffer, 0, rest);
        _stageFill[index] = rest;
    }

    private void Push(ReadOnlySpan<byte> data)
    {
        if (_pendingCount == 0)
        {
            int taken = _chunker.Append(data);
            data = data[taken..];
        }

        if (data.Length > 0)
        {
            if (_pendingOffset + _pendingCount + data.Length > _pending.Length)
            {
                Buffer.BlockCopy(_pending, _pendingOffset, _pending, 0, _pendingCount);
                _pendingOffset = 0;
            }

            data.CopyTo(_pending.AsSpan(_pendingOffset + _pendingCount));
            _pendingCount += data.Length;
        }
    }

    private bool DrainPending()
    {
        if (_pendingCount == 0)
        {
            return false;
        }

        int taken = _chunker.Append(_pending.AsSpan(_pendingOffset, _pendingCount));
        _pendingOffset += taken;
        _pendingCount -= taken;
        if (_pendingCount == 0)
        {
            _pendingOffset = 0;
        }

        return taken > 0;
    }

    // xz file format specification, section 3: LZMA2 data, block padding, check.
    private void CloseBlock()
    {
        // Bytes the filters held back go through unconverted, from the first filter to the last.
        for (int i = 0; i < _stages.Length; i++)
        {
            int fill = _stageFill[i];
            _stageFill[i] = 0;
            Feed(i + 1, _stageBuffers[i].AsSpan(0, fill));
        }

        while (_pendingCount > 0)
        {
            if (!DrainPending() && _chunker.WriteChunks(_counter, false, maxChunks: 8) == 0)
            {
                throw new InvalidOperationException("The encoder window is full but nothing can be encoded.");
            }
        }

        _chunker.WriteChunks(_counter, true);
        _chunker.WriteEnd(_counter);
        long compressed = _counter.Count;
        int padding = (int)((4 - (compressed & 3)) & 3);
        if (padding > 0)
        {
            _queue.GetSpan(padding)[..padding].Clear();
            _queue.Advance(padding);
        }

        int checkSize = CheckSize(_check);
        if (checkSize > 0)
        {
            Span<byte> c = _queue.GetSpan(checkSize);
            switch (_check)
            {
                case XzCheck.Crc32:
                    BinaryPrimitives.WriteUInt32LittleEndian(c, _crc32.GetCurrentValue());
                    break;
                case XzCheck.Crc64:
                    BinaryPrimitives.WriteUInt64LittleEndian(c, _crc64.GetCurrentValue());
                    break;
                default:
                    _sha256!.GetHashAndReset(c);
                    break;
            }

            _queue.Advance(checkSize);
        }

        _records.Add((_blockHeaderSize + compressed + checkSize, _blockRaw));
        _inBlock = false;
    }

    // xz file format specification, sections 4 (index) and 2.1.2 (footer).
    private void WriteIndexAndFooter()
    {
        var index = new List<byte>(16 + (_records.Count * 10)) { 0x00 };
        Span<byte> v = stackalloc byte[10];
        index.AddRange(v[..WriteVarint(v, (ulong)_records.Count)].ToArray());
        foreach ((long unpadded, long uncompressed) in _records)
        {
            index.AddRange(v[..WriteVarint(v, (ulong)unpadded)].ToArray());
            index.AddRange(v[..WriteVarint(v, (ulong)uncompressed)].ToArray());
        }

        while ((index.Count & 3) != 0)
        {
            index.Add(0);
        }

        byte[] crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(index)));
        index.AddRange(crc);
        _queue.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(index));

        Span<byte> f = _queue.GetSpan(12);
        BinaryPrimitives.WriteUInt32LittleEndian(f[4..], (uint)((index.Count / 4) - 1));
        f[8] = 0x00;
        f[9] = (byte)_check;
        BinaryPrimitives.WriteUInt32LittleEndian(f, Crc32.Compute(f.Slice(4, 6)));
        f[10] = (byte)'Y';
        f[11] = (byte)'Z';
        _queue.Advance(12);
    }

    // Counts the bytes an LZMA2 stream writes so the block's size is known without buffering it.
    private sealed class CountingWriter(OutputQueue inner) : IBufferWriter<byte>
    {
        public long Count { get; set; }

        public void Advance(int count)
        {
            inner.Advance(count);
            Count += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }
}
