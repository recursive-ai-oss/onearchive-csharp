using System.Buffers;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A stream that decompresses Zstandard data from another stream (read mode, one or many frames in a row) or compresses
/// what is written to it into another stream (write mode, one frame).
/// </summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer.
/// The stream reports the end of the data only when the inner stream ends at a frame boundary; an inner stream that
/// ends inside a frame, or that is empty, raises <see cref="CompressionFormatException"/> with
/// <see cref="CompressionFormatException.IsTruncated"/> set.</para>
/// <para>In write mode the stream owns a <see cref="ZstdEncoder"/>: writes are compressed as they come, <see cref="Flush()"/>
/// ends the current block so far-written data can be decoded, and disposing the stream ends the frame (an empty stream
/// writes an empty frame). Reading from a write-mode stream, and writing to a read-mode one, throw
/// <see cref="NotSupportedException"/>.</para>
/// </remarks>
public sealed class ZstdStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly ZstdDecoder _decoder;
    private readonly ZstdEncoder? _encoder;
    private byte[]? _output;
    private bool _finished;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _innerEnded;
    private bool _sawFrame;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of Zstandard data.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public ZstdStream(Stream stream, ZstdDecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _decoder = new ZstdDecoder(options);
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>Creates a stream that compresses what is written to it into <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives one Zstandard frame.</param>
    /// <param name="options">Level, checksum, dictionary and so on; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The level in <paramref name="options"/> is not supported.</exception>
    public ZstdStream(Stream stream, ZstdCompressionOptions? options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _encoder = new ZstdEncoder(options);
        _decoder = null!;
        _output = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    /// <summary>The size of the buffer that holds compressed bytes before they are written to the inner stream in write mode.</summary>
    public const int OutputBufferSize = 64 * 1024;

    /// <inheritdoc />
    public override bool CanRead => _input is not null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _output is not null && !_finished;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    /// <remarks>In write mode, ends the current block with the data written so far and flushes the inner stream.</remarks>
    public override void Flush()
    {
        if (_encoder is null || _output is null || _finished)
        {
            return;
        }

        OperationStatus status;
        do
        {
            status = _encoder.Flush(_output, out int written);
            _inner.Write(_output, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
        _inner.Flush();
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_encoder is null || _output is null || _finished)
        {
            return;
        }

        OperationStatus status;
        do
        {
            status = _encoder.Flush(_output, out int written);
            await _inner.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="NotSupportedException">The stream is in read mode.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] output = GetOutput();
        while (true)
        {
            OperationStatus status = _encoder!.Compress(buffer, output, out int consumed, out int written, false);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                _inner.Write(output, 0, written);
            }

            if (status == OperationStatus.Done && buffer.IsEmpty)
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
    /// <exception cref="NotSupportedException">The stream is in read mode.</exception>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] output = GetOutput();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = _encoder!.Compress(buffer.Span, output, out int consumed, out int written, false);
            buffer = buffer[consumed..];
            if (written > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            if (status == OperationStatus.Done && buffer.IsEmpty)
            {
                return;
            }
        }
    }

    private byte[] GetOutput()
    {
        if (_encoder is null)
        {
            throw new NotSupportedException("This stream decompresses; it cannot be written to.");
        }

        ObjectDisposedException.ThrowIf(_output is null || _finished, this);
        return _output;
    }

    // Ends the frame: the encoder is told the input is complete and everything it has left goes to the inner stream.
    private void FinishFrame()
    {
        if (_encoder is null || _output is null || _finished)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _output, out _, out int written, true);
            _inner.Write(_output, 0, written);
        }
        while (status == OperationStatus.DestinationTooSmall);
    }

    private async ValueTask FinishFrameAsync()
    {
        if (_encoder is null || _output is null || _finished)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = _encoder.Compress(ReadOnlySpan<byte>.Empty, _output, out _, out int written, true);
            await _inner.WriteAsync(_output.AsMemory(0, written)).ConfigureAwait(false);
        }
        while (status == OperationStatus.DestinationTooSmall);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="ZstdDecoder.Decompress"/>).</exception>
    public override int Read(Span<byte> buffer)
    {
        ThrowIfWriteMode();
        byte[] input = GetInput();
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

            _inputEnd = _inner.Read(input, 0, input.Length);
            _inputStart = 0;
            OnInputRead();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="ZstdDecoder.Decompress"/>).</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfWriteMode();
        byte[] input = GetInput();
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

            _inputEnd = await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
            _inputStart = 0;
            OnInputRead();
        }
    }

    // Decodes what the buffered input allows. Returns false when more input is needed from the inner stream
    // (or the end of the data has been reached with nothing to return, in which case written is 0 and true is returned).
    private bool TryDecode(byte[] input, Span<byte> buffer, out int written)
    {
        written = 0;
        while (true)
        {
            if (_innerEnded && _inputStart == _inputEnd && _decoder.IsAtFrameBoundary)
            {
                if (!_sawFrame)
                {
                    ZstdFormat.ThrowTruncated(_decoder.TotalConsumed, "a Zstandard frame");
                }

                return true; // the end of the data
            }

            OperationStatus status = _decoder.Decompress(input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer[written..], out int consumed, out int w);
            _inputStart += consumed;
            written += w;
            if (status == OperationStatus.Done)
            {
                _sawFrame = true;
            }

            if (written > 0 && (status == OperationStatus.DestinationTooSmall || written == buffer.Length))
            {
                return true;
            }

            switch (status)
            {
                case OperationStatus.NeedMoreData:
                    if (_innerEnded)
                    {
                        _decoder.ThrowIfIncomplete();
                        if (written > 0)
                        {
                            return true;
                        }

                        continue; // the boundary check above ends the stream
                    }

                    if (written > 0)
                    {
                        return true;
                    }

                    return false;
                case OperationStatus.DestinationTooSmall:
                    return true;
                default: // Done: continue with the next frame, or return what we have
                    if (written > 0 && _inputStart == _inputEnd)
                    {
                        return true;
                    }

                    break;
            }
        }
    }

    private void OnInputRead()
    {
        if (_inputEnd == 0)
        {
            _innerEnded = true;
        }
    }

    private void ThrowIfWriteMode()
    {
        if (_encoder is not null)
        {
            throw new NotSupportedException("This stream compresses; it cannot be read from.");
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
        if (disposing)
        {
            try
            {
                FinishFrame();
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
        try
        {
            await FinishFrameAsync().ConfigureAwait(false);
        }
        finally
        {
            if (ReleaseBuffer() && !_leaveOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // Returns the pooled buffers; true only the first time.
    private bool ReleaseBuffer()
    {
        byte[]? input = _input;
        byte[]? output = _output;
        _input = null;
        _output = null;
        if (input is null && output is null)
        {
            return false;
        }

        if (input is not null)
        {
            ArrayPool<byte>.Shared.Return(input);
        }

        if (output is not null)
        {
            ArrayPool<byte>.Shared.Return(output);
        }

        return true;
    }
}
