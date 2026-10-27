namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A read-only, seekable view of the decompressed content of a seekable Zstandard file. Seeking is free; a read
/// decompresses only the frames it touches (see <see cref="ZstdSeekableReader"/>, which this wraps).
/// </summary>
/// <remarks>Not thread-safe. For a stream that cannot seek, use <see cref="ZstdStream"/>, which decodes the file sequentially and skips the seek table.</remarks>
public sealed class ZstdSeekableStream : Stream
{
    private readonly ZstdSeekableReader _reader;
    private long _position;

    /// <summary>Opens a seekable file on a stream.</summary>
    /// <param name="source">A readable, seekable stream holding the whole file.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="source"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot read or seek.</exception>
    /// <exception cref="CompressionFormatException">The file has no valid seek table.</exception>
    /// <exception cref="CompressionLimitException">The table exceeds a limit in <paramref name="options"/>.</exception>
    public ZstdSeekableStream(Stream source, ZstdSeekableReaderOptions? options = null, bool leaveOpen = false)
    {
        _reader = new ZstdSeekableReader(source, options, leaveOpen);
    }

    /// <summary>The reader behind the stream, with the frame index and the cost counters.</summary>
    public ZstdSeekableReader Reader => _reader;

    /// <summary>The seek table.</summary>
    public ZstdSeekTable Table => _reader.Table;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => true;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _reader.Length;

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        int n = _reader.Read(_position, buffer);
        _position += n;
        return n;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await _reader.ReadAsync(_position, buffer, cancellationToken).ConfigureAwait(false);
        _position += n;
        return n;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    /// <exception cref="IOException">The seek would land before the start.</exception>
    public override long Seek(long offset, SeekOrigin origin)
    {
        long basis = origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => _position,
            SeekOrigin.End => Length,
            _ => throw new ArgumentException("Invalid seek origin.", nameof(origin)),
        };
        long target;
        try
        {
            target = checked(basis + offset);
        }
        catch (OverflowException)
        {
            throw new IOException("The seek position overflows.");
        }

        if (target < 0)
        {
            throw new IOException("An attempt was made to move the position before the beginning of the stream.");
        }

        _position = target;
        return target;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the stream is read-only.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the stream is read-only.</exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
