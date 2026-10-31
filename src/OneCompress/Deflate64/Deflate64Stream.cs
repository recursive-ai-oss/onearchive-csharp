using System.Buffers;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>
/// A stream that compresses to, or decompresses from, one raw Deflate64 stream (zip method 9) on another stream. Shaped like
/// <see cref="System.IO.Compression.BrotliStream"/>: the constructor you call picks the direction.
/// </summary>
/// <remarks>
/// <para>Decompressing: input is read from the inner stream in chunks of <see cref="BufferSize"/> bytes into a pooled buffer. The
/// end of the data is reported only when the Deflate64 stream is complete; an inner stream that ends earlier, or is empty, raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set. Bytes after the end of
/// the stream raise <see cref="CompressionFormatException"/> unless <see cref="Deflate64DecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>Compressing: <see cref="Flush()"/> ends the data written so far on a byte boundary (a sync flush) and costs a little ratio;
/// disposing the stream finishes the Deflate64 stream and flushes the inner stream's buffer through to it. Output depends only on the
/// bytes written, the level and where flushes fall.</para>
/// </remarks>
public sealed class Deflate64Stream : Stream
{
    /// <summary>The size of the buffers between this stream and the inner stream.</summary>
    public const int BufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly bool _compress;
    private readonly Deflate64Decoder? _decoder;
    private readonly Deflate64Encoder? _encoder;
    private readonly bool _allowTrailing;
    private byte[]? _buffer;
    private int _start;
    private int _end;
    private bool _innerEnded;
    private bool _finished;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="options">Limits; <see langword="null"/> for <see cref="Deflate64DecompressionOptions.Default"/>.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public Deflate64Stream(Stream stream, Deflate64DecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _decoder = new Deflate64Decoder(options);
        _allowTrailing = (options ?? Deflate64DecompressionOptions.Default).AllowTrailingData;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <summary>Creates a stream that compresses to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed data.</param>
    /// <param name="options">The level; <see langword="null"/> for <see cref="Deflate64Options.Default"/>.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 9.</exception>
    public Deflate64Stream(Stream stream, Deflate64Options? options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _compress = true;
        _encoder = new Deflate64Encoder(options);
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <inheritdoc />
    public override bool CanRead => !_compress && _buffer is not null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _compress && _buffer is not null;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    private byte[] GetBuffer()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);
        return _buffer;
    }

    // ---- reading ----

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit.</exception>
    /// <exception cref="NotSupportedException">The stream compresses.</exception>
    public override int Read(Span<byte> buffer)
    {
        byte[] input = GetBuffer();
        if (_compress)
        {
            throw new NotSupportedException();
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (TryDecode(input, buffer, out int written))
            {
                return written;
            }

            _end = _inner.Read(input, 0, input.Length);
            _start = 0;
            _innerEnded = _end == 0;
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit.</exception>
    /// <exception cref="NotSupportedException">The stream compresses.</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] input = GetBuffer();
        if (_compress)
        {
            throw new NotSupportedException();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (TryDecode(input, buffer.Span, out int written))
            {
                return written;
            }

            _end = await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
            _start = 0;
            _innerEnded = _end == 0;
        }
    }

    // Decodes what the buffered input allows. False: more input is needed from the inner stream. True with written == 0: the end.
    private bool TryDecode(byte[] input, Span<byte> buffer, out int written)
    {
        written = 0;
        Deflate64Decoder decoder = _decoder!;
        while (true)
        {
            if (_finished)
            {
                return true;
            }

            OperationStatus status = decoder.Decompress(input.AsSpan(_start, _end - _start), buffer[written..], out int consumed, out int w);
            _start += consumed;
            written += w;
            switch (status)
            {
                case OperationStatus.Done:
                    if (_start < _end && !_allowTrailing)
                    {
                        throw new CompressionFormatException("deflate64", decoder.TotalConsumed, "the input to end with the stream");
                    }

                    _finished = true;
                    return true;
                case OperationStatus.DestinationTooSmall:
                    return true;
                default: // NeedMoreData
                    if (_innerEnded)
                    {
                        decoder.ThrowIfIncomplete();
                        return true;
                    }

                    return written > 0;
            }
        }
    }

    // ---- writing ----

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The stream decompresses.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] output = GetBuffer();
        if (!_compress)
        {
            throw new NotSupportedException();
        }

        Deflate64Encoder encoder = _encoder!;
        while (true)
        {
            OperationStatus status = encoder.Compress(buffer, output, out int consumed, out int written);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                _inner.Write(output, 0, written);
            }

            if (status != OperationStatus.DestinationTooSmall && buffer.IsEmpty)
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The stream decompresses.</exception>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] output = GetBuffer();
        if (!_compress)
        {
            throw new NotSupportedException();
        }

        Deflate64Encoder encoder = _encoder!;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = encoder.Compress(buffer.Span, output, out int consumed, out int written);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            if (status != OperationStatus.DestinationTooSmall && buffer.IsEmpty)
            {
                return;
            }
        }
    }

    /// <summary>Ends the data written so far on a byte boundary and flushes it to the inner stream (a sync flush). Does nothing when decompressing.</summary>
    public override void Flush()
    {
        if (!_compress || _buffer is null || _finished)
        {
            return;
        }

        byte[] output = _buffer;
        OperationStatus status;
        do
        {
            status = _encoder!.Flush(output, out int written);
            _inner.Write(output, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
        _inner.Flush();
    }

    /// <inheritdoc cref="Flush()" />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_compress || _buffer is null || _finished)
        {
            return;
        }

        byte[] output = _buffer;
        OperationStatus status;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = _encoder!.Flush(output, out int written);
            await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- disposing ----

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is not null)
        {
            try
            {
                if (_compress && !_finished)
                {
                    _finished = true;
                    byte[] output = _buffer;
                    OperationStatus status;
                    do
                    {
                        status = _encoder!.Compress(default, output, out _, out int written, isFinalBlock: true);
                        _inner.Write(output, 0, written);
                    }
                    while (status == OperationStatus.DestinationTooSmall);
                }
            }
            finally
            {
                Release();
                if (!_leaveOpen)
                {
                    _inner.Dispose();
                }
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_buffer is not null)
        {
            try
            {
                if (_compress && !_finished)
                {
                    _finished = true;
                    byte[] output = _buffer;
                    OperationStatus status;
                    do
                    {
                        status = _encoder!.Compress(default, output, out _, out int written, isFinalBlock: true);
                        await _inner.WriteAsync(output.AsMemory(0, written)).ConfigureAwait(false);
                    }
                    while (status == OperationStatus.DestinationTooSmall);
                }
            }
            finally
            {
                Release();
                if (!_leaveOpen)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void Release()
    {
        byte[]? buffer = _buffer;
        _buffer = null;
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
