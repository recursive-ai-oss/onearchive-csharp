using System.Buffers.Binary;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Reads ranges of a seekable Zstandard file: it finds the frames covering a requested range in the seek table and
/// decompresses only those. One decoded frame is cached, so reading a file sequentially in small pieces decodes each
/// frame once.
/// </summary>
/// <remarks>
/// The reader owns one decoder and reuses its buffers, so steady-state reads allocate nothing. A read that covers a
/// whole frame decodes straight into the caller's buffer. Not thread-safe. Opening validates the seek table against the
/// file's length; <see cref="Verify"/> checks every frame.
/// </remarks>
public sealed class ZstdSeekableReader : IDisposable
{
    private readonly Stream? _stream;
    private readonly ReadOnlyMemory<byte> _memory;
    private readonly bool _leaveOpen;
    private readonly ZstdSeekableReaderOptions _options;
    private readonly ZstdDecoder _decoder;
    private byte[] _compressed = [];
    private byte[] _cache = [];
    private int _cacheFrame = -1;
    private bool _disposed;

    /// <summary>Opens a seekable file on a stream.</summary>
    /// <param name="source">A readable, seekable stream holding the whole file. The reader moves its position.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the reader is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot read or seek. A non-seekable stream can be decoded sequentially with <see cref="ZstdStream"/>, which skips the seek table.</exception>
    /// <exception cref="CompressionFormatException">The file has no valid seek table, or the table does not fit the file.</exception>
    /// <exception cref="CompressionLimitException">The table has more entries than <see cref="ZstdSeekableReaderOptions.MaxFrames"/>, or a frame declares more content than <see cref="ZstdSeekableReaderOptions.MaxFrameSize"/>.</exception>
    public ZstdSeekableReader(Stream source, ZstdSeekableReaderOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        _options = options ?? ZstdSeekableReaderOptions.Default;
        Table = ZstdSeekTable.Read(source, _options.MaxFrames);
        _stream = source;
        _leaveOpen = leaveOpen;
        _decoder = new ZstdDecoder(_options.Decompression);
        CheckFrameSizes();
    }

    /// <summary>Opens a seekable file held in memory; frames are decoded straight from it.</summary>
    /// <param name="source">The whole file.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <exception cref="CompressionFormatException">The file has no valid seek table, or the table does not fit the file.</exception>
    /// <exception cref="CompressionLimitException">The table has more entries than <see cref="ZstdSeekableReaderOptions.MaxFrames"/>, or a frame declares more content than <see cref="ZstdSeekableReaderOptions.MaxFrameSize"/>.</exception>
    public ZstdSeekableReader(ReadOnlyMemory<byte> source, ZstdSeekableReaderOptions? options = null)
    {
        _options = options ?? ZstdSeekableReaderOptions.Default;
        Table = ZstdSeekTable.Read(source.Span, _options.MaxFrames);
        _memory = source;
        _decoder = new ZstdDecoder(_options.Decompression);
        CheckFrameSizes();
    }

    /// <summary>The file's seek table: the frame index.</summary>
    public ZstdSeekTable Table { get; }

    /// <summary>The size of the decompressed content.</summary>
    public long Length => Table.DecompressedLength;

    /// <summary>How many frames have been decompressed since the reader was opened: the cost of the reads so far, in frames.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>How many compressed bytes have been decompressed since the reader was opened.</summary>
    public long CompressedBytesDecoded { get; private set; }

    private void CheckFrameSizes()
    {
        // The table gives every frame's size, so the largest one is known before any buffer exists.
        long limit = Math.Min(_options.MaxFrameSize, Array.MaxLength);
        for (int i = 0; i < Table.Count; i++)
        {
            long size = Table.GetFrame(i).DecompressedSize;
            if (size > limit)
            {
                throw new CompressionLimitException(ZstdSeekTable.FormatName, "MaxFrameSize", limit, size);
            }
        }
    }

    /// <summary>Reads content at an offset, without moving any position.</summary>
    /// <param name="offset">Offset into the decompressed content.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The number of bytes read: <c>destination.Length</c> unless the content ends first; 0 at or past the end.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">A frame is corrupt, does not decode to the size or checksum the table lists, or lies outside the file.</exception>
    /// <exception cref="CompressionLimitException">A frame exceeds a decoding limit.</exception>
    public int Read(long offset, Span<byte> destination)
    {
        int count = Prepare(offset, destination.Length);
        int total = 0;
        while (total < count)
        {
            long position = offset + total;
            int index = Table.FindFrame(position);
            ZstdSeekFrame frame = Table.GetFrame(index);
            long within = position - frame.DecompressedOffset;
            int take = (int)Math.Min(count - total, frame.DecompressedSize - within);
            if (within == 0 && take == frame.DecompressedSize && _cacheFrame != index)
            {
                // The read covers the whole frame: decode into the caller's buffer and skip the cache.
                Decode(index, frame, LoadCompressed(frame), destination.Slice(total, take));
            }
            else
            {
                if (_cacheFrame != index)
                {
                    FillCache(index, frame, LoadCompressed(frame));
                }

                _cache.AsSpan((int)within, take).CopyTo(destination[total..]);
            }

            total += take;
        }

        return total;
    }

    /// <summary>Reads content at an offset, without moving any position.</summary>
    /// <param name="offset">Offset into the decompressed content.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="cancellationToken">Cancels the reads from the stream.</param>
    /// <returns>The number of bytes read: <c>destination.Length</c> unless the content ends first; 0 at or past the end.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">A frame is corrupt, does not decode to the size or checksum the table lists, or lies outside the file.</exception>
    /// <exception cref="CompressionLimitException">A frame exceeds a decoding limit.</exception>
    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        int count = Prepare(offset, destination.Length);
        int total = 0;
        while (total < count)
        {
            long position = offset + total;
            int index = Table.FindFrame(position);
            ZstdSeekFrame frame = Table.GetFrame(index);
            long within = position - frame.DecompressedOffset;
            int take = (int)Math.Min(count - total, frame.DecompressedSize - within);
            if (within == 0 && take == frame.DecompressedSize && _cacheFrame != index)
            {
                ReadOnlyMemory<byte> compressed = await LoadCompressedAsync(frame, cancellationToken).ConfigureAwait(false);
                Decode(index, frame, compressed, destination.Span.Slice(total, take));
            }
            else
            {
                if (_cacheFrame != index)
                {
                    ReadOnlyMemory<byte> compressed = await LoadCompressedAsync(frame, cancellationToken).ConfigureAwait(false);
                    FillCache(index, frame, compressed);
                }

                _cache.AsSpan((int)within, take).CopyTo(destination.Span[total..]);
            }

            total += take;
        }

        return total;
    }

    /// <summary>
    /// Decompresses every frame and checks it against the seek table: size, checksum if the table has them, and that frames
    /// with no content are skippable frames or decode to nothing. Throws on the first problem.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check between frames.</param>
    /// <exception cref="CompressionFormatException">A frame does not match the table.</exception>
    public void Verify(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < Table.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZstdSeekFrame frame = Table.GetFrame(i);
            ReadOnlyMemory<byte> compressed = LoadCompressed(frame);
            if (frame.DecompressedSize == 0)
            {
                ReadOnlySpan<byte> bytes = compressed.Span;
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != bytes.Length - 8)
                    {
                        throw new CompressionFormatException(ZstdSeekTable.FormatName, frame.CompressedOffset + 4, $"a skippable frame of {bytes.Length - 8} bytes as the table lists for frame {i}");
                    }

                    continue;
                }
            }

            byte[] scratch = frame.DecompressedSize == 0 ? [] : GrowCache((int)frame.DecompressedSize);
            _cacheFrame = -1;
            Decode(i, frame, compressed, scratch.AsSpan(0, (int)frame.DecompressedSize));
        }
    }

    /// <summary>Closes the stream unless it was left open.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_leaveOpen)
        {
            _stream?.Dispose();
        }
    }

    private int Prepare(long offset, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= Length)
        {
            return 0;
        }

        return (int)Math.Min(length, Length - offset);
    }

    private byte[] GrowCache(int size)
    {
        if (_cache.Length < size)
        {
            // Frames are usually all one size; round up to avoid reallocating for every slightly larger frame.
            _cache = new byte[Math.Max(size, (int)Math.Min(int.MaxValue / 2, (long)_cache.Length * 2))];
        }

        return _cache;
    }

    private void FillCache(int index, ZstdSeekFrame frame, ReadOnlyMemory<byte> compressed)
    {
        GrowCache((int)frame.DecompressedSize);
        _cacheFrame = -1;
        Decode(index, frame, compressed, _cache.AsSpan(0, (int)frame.DecompressedSize));
        _cacheFrame = index;
    }

    private ReadOnlyMemory<byte> LoadCompressed(ZstdSeekFrame frame)
    {
        if (_stream is null)
        {
            return _memory.Slice((int)frame.CompressedOffset, (int)frame.CompressedSize);
        }

        int size = ReserveCompressed(frame);
        _stream.Seek(frame.CompressedOffset, SeekOrigin.Begin);
        int got = _stream.ReadAtLeast(_compressed.AsSpan(0, size), size, throwOnEndOfStream: false);
        if (got < size)
        {
            throw new CompressionFormatException(ZstdSeekTable.FormatName, frame.CompressedOffset + got, "the frame's bytes the seek table lists", isTruncated: true);
        }

        return _compressed.AsMemory(0, size);
    }

    private async ValueTask<ReadOnlyMemory<byte>> LoadCompressedAsync(ZstdSeekFrame frame, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return _memory.Slice((int)frame.CompressedOffset, (int)frame.CompressedSize);
        }

        int size = ReserveCompressed(frame);
        _stream.Seek(frame.CompressedOffset, SeekOrigin.Begin);
        int got = 0;
        while (got < size)
        {
            int n = await _stream.ReadAsync(_compressed.AsMemory(got, size - got), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new CompressionFormatException(ZstdSeekTable.FormatName, frame.CompressedOffset + got, "the frame's bytes the seek table lists", isTruncated: true);
            }

            got += n;
        }

        return _compressed.AsMemory(0, size);
    }

    private int ReserveCompressed(ZstdSeekFrame frame)
    {
        // The table's sizes add up to the file's length, so a frame is never larger than the file.
        int size = checked((int)frame.CompressedSize);
        if (_compressed.Length < size)
        {
            _compressed = new byte[size];
        }

        return size;
    }

    private void Decode(int index, ZstdSeekFrame frame, ReadOnlyMemory<byte> compressed, Span<byte> destination)
    {
        bool fits;
        int written;
        try
        {
            fits = Zstd.TryDecompress(_decoder, compressed.Span, destination, out written);
        }
        catch (CompressionFormatException ex)
        {
            throw new CompressionFormatException(ex.Format, frame.CompressedOffset + ex.Offset, $"frame {index}: {ex.Expected}", ex.IsTruncated);
        }

        if (!fits || written != destination.Length)
        {
            throw new CompressionFormatException(ZstdSeekTable.FormatName, frame.CompressedOffset, $"frame {index} to decompress to the {frame.DecompressedSize} bytes the seek table lists");
        }

        FramesDecoded++;
        CompressedBytesDecoded += frame.CompressedSize;
        if (frame.HasChecksum && _options.VerifyChecksums && (uint)XxHash64.Compute(destination) != frame.Checksum)
        {
            throw new CompressionFormatException(ZstdSeekTable.FormatName, frame.CompressedOffset, $"the content of frame {index} to match the checksum in the seek table");
        }
    }
}
