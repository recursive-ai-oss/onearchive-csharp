using System.Buffers;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>
/// A stream that compresses to, or decompresses from, raw PPMd coder output (variant H for 7z, variant I for zip method 98).
/// </summary>
/// <remarks>
/// <para>Reading: the stream pulls compressed bytes from the inner stream in chunks of <see cref="BufferSize"/> and reports the end of the data
/// when the decoder reaches the output size set in the options (or the end marker, when the size is -1). An inner stream that ends first raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set.</para>
/// <para>Writing: bytes are modelled as they arrive; disposing flushes the range coder and finishes the stream. <see cref="Flush()"/> passes on the bytes
/// coded so far but cannot end the stream early, so the output depends only on the bytes written and the options.</para>
/// </remarks>
public sealed class PpmdStream : Stream
{
    /// <summary>The size of the buffers between the stream and the inner stream.</summary>
    public const int BufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly PpmdEncoderCore? _encoder;
    private readonly PpmdDecoderCore? _decoder;
    private byte[]? _buffer;
    private int _start;
    private int _end;
    private bool _innerEnded;
    private bool _finished;

    /// <summary>Creates a stream that decompresses variant H data from <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="options">The model parameters, output size and limits.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="CompressionFormatException">The order or memory size is invalid.</exception>
    /// <exception cref="CompressionLimitException">The memory size passes the limit.</exception>
    public PpmdStream(Stream stream, PpmdHDecompressionOptions options, bool leaveOpen = false)
        : this(stream, leaveOpen, decoder: new PpmdHDecoder(options).Core, encoder: null, write: false)
    {
    }

    /// <summary>Creates a stream that decompresses variant I data from <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="options">The model parameters, output size and limits.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="CompressionFormatException">The order or memory size is invalid.</exception>
    /// <exception cref="CompressionLimitException">The memory size passes the limit.</exception>
    public PpmdStream(Stream stream, PpmdIDecompressionOptions options, bool leaveOpen = false)
        : this(stream, leaveOpen, decoder: new PpmdIDecoder(options).Core, encoder: null, write: false)
    {
    }

    /// <summary>Creates a stream that compresses with variant H to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed data.</param>
    /// <param name="options">The model parameters; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public PpmdStream(Stream stream, PpmdHOptions? options, bool leaveOpen = false)
        : this(stream, leaveOpen, decoder: null, encoder: new PpmdHEncoder(options).Core, write: true)
    {
    }

    /// <summary>Creates a stream that compresses with variant I to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed data.</param>
    /// <param name="options">The model parameters; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public PpmdStream(Stream stream, PpmdIOptions? options, bool leaveOpen = false)
        : this(stream, leaveOpen, decoder: null, encoder: new PpmdIEncoder(options).Core, write: true)
    {
    }

    private PpmdStream(Stream stream, bool leaveOpen, PpmdDecoderCore? decoder, PpmdEncoderCore? encoder, bool write)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (write ? !stream.CanWrite : !stream.CanRead)
        {
            throw new ArgumentException(write ? "The stream must be writable." : "The stream must be readable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _decoder = decoder;
        _encoder = encoder;
        _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    }

    /// <inheritdoc />
    public override bool CanRead => _buffer is not null && _decoder is not null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _buffer is not null && _encoder is not null;

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

    // ---- writing ----

    /// <inheritdoc />
    public override void Flush()
    {
        if (_encoder is not null && !_finished)
        {
            _inner.Flush();
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _encoder is not null && !_finished ? _inner.FlushAsync(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => Write(new ReadOnlySpan<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] output = ThrowIfNotWritable();
        while (!buffer.IsEmpty)
        {
            OperationStatus status = _encoder!.Compress(buffer, output, out int consumed, out int written, isFinalBlock: false);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                _inner.Write(output, 0, written);
            }

            if (status == OperationStatus.Done && consumed == 0)
            {
                break;
            }
        }
    }

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] output = ThrowIfNotWritable();
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = _encoder!.Compress(buffer.Span, output, out int consumed, out int written, isFinalBlock: false);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            if (status == OperationStatus.Done && consumed == 0)
            {
                break;
            }
        }
    }

    private byte[] ThrowIfNotWritable()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        if (_encoder is null)
        {
            throw new NotSupportedException("This stream decompresses.");
        }

        return _buffer;
    }

    private void Finish()
    {
        if (_encoder is null || _buffer is null || _finished)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(default, _buffer, out _, out int written, isFinalBlock: true);
            _inner.Write(_buffer, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
        _inner.Flush();
    }

    private async ValueTask FinishAsync()
    {
        if (_encoder is null || _buffer is null || _finished)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(default, _buffer, out _, out int written, isFinalBlock: true);
            await _inner.WriteAsync(_buffer.AsMemory(0, written)).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall);
        await _inner.FlushAsync().ConfigureAwait(false);
    }

    // ---- reading ----

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

    /// <inheritdoc />
    public override int Read(Span<byte> buffer)
    {
        ThrowIfNotReadable();
        while (!buffer.IsEmpty && !_decoder!.IsDone)
        {
            int n = Step(buffer, out bool needInput);
            if (n > 0)
            {
                return n;
            }

            if (needInput)
            {
                Compact();
                int read = _inner.Read(_buffer!, _end, _buffer!.Length - _end);
                AfterFill(read);
            }
        }

        return 0;
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfNotReadable();
        while (!buffer.IsEmpty && !_decoder!.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = Step(buffer.Span, out bool needInput);
            if (n > 0)
            {
                return n;
            }

            if (needInput)
            {
                Compact();
                int read = await _inner.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
                AfterFill(read);
            }
        }

        return 0;
    }

    private void ThrowIfNotReadable()
    {
        ObjectDisposedException.ThrowIf(_buffer is null, this);

        if (_decoder is null)
        {
            throw new NotSupportedException("This stream compresses.");
        }
    }

    private int Step(Span<byte> destination, out bool needInput)
    {
        OperationStatus status = _decoder!.Decompress(_buffer.AsSpan(_start, _end - _start), destination, out int consumed, out int written, _innerEnded);
        _start += consumed;
        needInput = status == OperationStatus.NeedMoreData && written == 0;
        return written;
    }

    private void Compact()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer!, _start, _buffer!, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
    }

    private void AfterFill(int read)
    {
        if (read == 0)
        {
            _innerEnded = true;
        }
        else
        {
            _end += read;
        }
    }

    // ---- disposal ----

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _buffer is not null)
        {
            try
            {
                Finish();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
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
                await FinishAsync().ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null;
                if (!_leaveOpen)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
