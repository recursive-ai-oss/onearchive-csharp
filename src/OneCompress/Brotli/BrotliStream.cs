using System.Buffers;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>A stream that decompresses brotli data (RFC 7932) from another stream, or compresses data written to it.</summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer. The stream reports the
/// end of the data only after a complete brotli stream: an inner stream that ends inside the stream, or that is empty, raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set. Bytes after the stream raise
/// <see cref="CompressionFormatException"/> unless <see cref="BrotliDecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>Compressing (the constructor that takes <see cref="BrotliOptions"/>): bytes written are collected into a block of the quality's size (128 KiB to 1 MiB, 4 MiB
/// for qualities 5 to 9), which is coded and written to the inner stream when it is full, on <see cref="Flush()"/>, or when the stream is disposed. <see cref="Flush()"/>
/// ends the block early and the stream at a byte boundary, so everything written so far can be decoded; flushing often costs ratio. Disposing finishes the stream. Output
/// depends only on the bytes written, the options and where flushes were made.</para>
/// <para>This type has the name of <c>System.IO.Compression.BrotliStream</c>, which is a different type; qualify or alias the name where both namespaces are imported.</para>
/// </remarks>
public sealed class BrotliStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly BrotliDecoder _decoder;
    private readonly BrotliDecompressionOptions _options;
    private readonly BrotliEncoder? _encoder;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private long _consumedTotal;
    private bool _innerEnded;
    private bool _finished;
    private bool _probeTrailing;
    private bool _encodingFinished;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="options">Limits and settings; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public BrotliStream(Stream stream, BrotliDecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _options = options ?? BrotliDecompressionOptions.Default;
        _decoder = new BrotliDecoder(_options);
        _inner = stream;
        _leaveOpen = leaveOpen;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>Creates a stream that compresses to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed data.</param>
    /// <param name="options">The quality and window; <see langword="null"/> for <see cref="BrotliOptions.Default"/>.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public BrotliStream(Stream stream, BrotliOptions? options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = BrotliDecompressionOptions.Default;
        _decoder = new BrotliDecoder(_options);
        _encoder = new BrotliEncoder(options);
        _inner = stream;
        _leaveOpen = leaveOpen;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <inheritdoc />
    public override bool CanRead => _input is not null && _encoder is null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _input is not null && _encoder is not null;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        if (_encoder is null || _input is null || _encodingFinished)
        {
            return;
        }

        OperationStatus status;
        do
        {
            status = _encoder.Flush(_input, out int written);
            _inner.Write(_input, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
        _inner.Flush();
    }

    /// <inheritdoc cref="Flush()" />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_encoder is null || _input is null || _encodingFinished)
        {
            return;
        }

        OperationStatus status;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            status = _encoder.Flush(_input, out int written);
            await _inner.WriteAsync(_input.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

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
        byte[] output = GetInput();
        if (_encoder is null)
        {
            throw new NotSupportedException();
        }

        while (true)
        {
            OperationStatus status = _encoder.Compress(buffer, output, out int consumed, out int written);
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
        byte[] output = GetInput();
        if (_encoder is null)
        {
            throw new NotSupportedException();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = _encoder.Compress(buffer.Span, output, out int consumed, out int written);
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

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="BrotliDecoder.Decompress"/>).</exception>
    public override int Read(Span<byte> buffer)
    {
        byte[] input = GetInput();
        if (_encoder is not null)
        {
            throw new NotSupportedException();
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (TryDecode(buffer, out int written))
            {
                if (_probeTrailing)
                {
                    _probeTrailing = false;
                    if (_inputStart == _inputEnd)
                    {
                        _inputEnd = _inner.Read(input, 0, input.Length);
                        _inputStart = 0;
                    }

                    CheckTrailing();
                }

                return written;
            }

            _inputEnd = _inner.Read(input, 0, input.Length);
            _inputStart = 0;
            _innerEnded = _inputEnd == 0;
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="BrotliDecoder.Decompress"/>).</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] input = GetInput();
        if (_encoder is not null)
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
            if (TryDecode(buffer.Span, out int written))
            {
                if (_probeTrailing)
                {
                    _probeTrailing = false;
                    if (_inputStart == _inputEnd)
                    {
                        _inputEnd = await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
                        _inputStart = 0;
                    }

                    CheckTrailing();
                }

                return written;
            }

            _inputEnd = await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
            _inputStart = 0;
            _innerEnded = _inputEnd == 0;
        }
    }

    // Decodes what the buffered input allows. Returns false when more input is needed from the inner stream; true with written == 0
    // means the end of the data.
    private bool TryDecode(Span<byte> buffer, out int written)
    {
        written = 0;
        if (_finished)
        {
            return true;
        }

        byte[] input = _input!;
        OperationStatus status = _decoder.Decompress(input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer, out int consumed, out int w);
        _inputStart += consumed;
        _consumedTotal += consumed;
        written = w;
        switch (status)
        {
            case OperationStatus.Done:
                _finished = true;
                _probeTrailing = !_options.AllowTrailingData;
                return true;
            case OperationStatus.DestinationTooSmall:
                return true;
            default:
                if (written > 0)
                {
                    return true;
                }

                if (_innerEnded)
                {
                    _decoder.ThrowIfIncomplete();
                }

                return false;
        }
    }

    private void CheckTrailing()
    {
        if (_inputStart < _inputEnd || _decoder.TrailingDataConsumed)
        {
            throw new CompressionFormatException(BrotliDecoder.FormatName, _consumedTotal, "the end of the input after the last meta-block");
        }
    }

    private byte[] GetInput()
    {
        ObjectDisposedException.ThrowIf(_input is null, this);
        return _input;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _input is not null)
        {
            try
            {
                FinishEncoding();
            }
            finally
            {
                if (ReleaseBuffer() && !_leaveOpen)
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
        if (_input is not null)
        {
            try
            {
                await FinishEncodingAsync().ConfigureAwait(false);
            }
            finally
            {
                if (ReleaseBuffer() && !_leaveOpen)
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // Compressing: end the stream (the last block) and pass it on.
    private void FinishEncoding()
    {
        if (_encoder is null || _input is null || _encodingFinished)
        {
            return;
        }

        _encodingFinished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(default, _input, out _, out int written, isFinalBlock: true);
            _inner.Write(_input, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
    }

    private async ValueTask FinishEncodingAsync()
    {
        if (_encoder is null || _input is null || _encodingFinished)
        {
            return;
        }

        _encodingFinished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(default, _input, out _, out int written, isFinalBlock: true);
            await _inner.WriteAsync(_input.AsMemory(0, written)).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall);
    }

    private bool ReleaseBuffer()
    {
        byte[]? input = _input;
        _input = null;
        if (input is null)
        {
            return false;
        }

        ArrayPool<byte>.Shared.Return(input);
        return true;
    }
}
