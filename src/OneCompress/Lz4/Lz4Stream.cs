using System.Buffers;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// A stream that decompresses LZ4 data from another stream (one or many frames in a row), or compresses what is written to it
/// into another stream as one LZ4 frame.
/// </summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer.
/// The stream reports the end of the data only when the inner stream ends at a frame boundary; an inner stream that
/// ends inside a frame, or that is empty, raises <see cref="CompressionFormatException"/> with
/// <see cref="CompressionFormatException.IsTruncated"/> set.</para>
/// <para>In compression mode (the constructor taking <see cref="Lz4CompressionOptions"/>) the stream is write-only. Data is
/// collected into blocks by a <see cref="Lz4Encoder"/> and written to the inner stream as blocks fill; <see cref="Flush()"/>
/// ends the current block and flushes the inner stream, so everything written so far can be read back; disposing the stream
/// ends the frame (the end mark and the checksum) and, unless <c>leaveOpen</c> is set, disposes the inner stream. A stream that
/// is never disposed leaves an unfinished frame.</para>
/// </remarks>
public sealed class Lz4Stream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly Lz4Decoder? _decoder;
    private readonly Lz4Encoder? _encoder;
    private byte[]? _output;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _innerEnded;
    private bool _sawFrame;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of LZ4 data.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public Lz4Stream(Stream stream, Lz4DecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _decoder = new Lz4Decoder(options);
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>The size of the buffer that holds compressed bytes before they are written to the inner stream, in compression mode.</summary>
    public const int OutputBufferSize = 64 * 1024;

    /// <summary>Creates a stream that compresses what is written to it into <paramref name="stream"/> as one LZ4 frame.</summary>
    /// <param name="stream">A writable stream that receives the frame.</param>
    /// <param name="options">The level, block layout, checksums and dictionary.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public Lz4Stream(Stream stream, Lz4CompressionOptions options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _inner = stream;
        _leaveOpen = leaveOpen;
        _encoder = new Lz4Encoder(options);
        _output = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    /// <inheritdoc />
    public override bool CanRead => _decoder is not null && _input is not null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _encoder is not null && _output is not null;

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
        if (_encoder is null || _output is null)
        {
            return;
        }

        while (true)
        {
            OperationStatus status = _encoder.Flush(_output, out int w);
            _inner.Write(_output, 0, w);
            if (status == OperationStatus.Done)
            {
                break;
            }
        }

        _inner.Flush();
    }

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_encoder is null || _output is null)
        {
            return;
        }

        while (true)
        {
            OperationStatus status = _encoder.Flush(_output, out int w);
            await _inner.WriteAsync(_output.AsMemory(0, w), cancellationToken).ConfigureAwait(false);
            if (status == OperationStatus.Done)
            {
                break;
            }
        }

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
    /// <exception cref="NotSupportedException">The stream is in decompression mode.</exception>
    /// <exception cref="InvalidOperationException">More data was written than <see cref="Lz4CompressionOptions.ContentSize"/> declared.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Lz4Encoder encoder = GetEncoder(out byte[] output);
        while (true)
        {
            OperationStatus status = encoder.Compress(buffer, output, out int c, out int w, isFinalBlock: false);
            buffer = buffer.Slice(c);
            if (w > 0)
            {
                _inner.Write(output, 0, w);
            }

            if (status == OperationStatus.Done)
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
    /// <exception cref="NotSupportedException">The stream is in decompression mode.</exception>
    /// <exception cref="InvalidOperationException">More data was written than <see cref="Lz4CompressionOptions.ContentSize"/> declared.</exception>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Lz4Encoder encoder = GetEncoder(out byte[] output);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationStatus status = encoder.Compress(buffer.Span, output, out int c, out int w, isFinalBlock: false);
            buffer = buffer.Slice(c);
            if (w > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, w), cancellationToken).ConfigureAwait(false);
            }

            if (status == OperationStatus.Done)
            {
                return;
            }
        }
    }

    private Lz4Encoder GetEncoder(out byte[] output)
    {
        if (_encoder is null)
        {
            throw new NotSupportedException("The stream decompresses; it cannot be written to.");
        }

        ObjectDisposedException.ThrowIf(_output is null, this);
        output = _output;
        return _encoder;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="Lz4Decoder.Decompress"/>).</exception>
    public override int Read(Span<byte> buffer)
    {
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
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="Lz4Decoder.Decompress"/>).</exception>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
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
        Lz4Decoder decoder = _decoder!;
        written = 0;
        while (true)
        {
            if (_innerEnded && _inputStart == _inputEnd && decoder.IsAtFrameBoundary)
            {
                if (!_sawFrame && decoder.TotalConsumed == 0)
                {
                    Lz4Format.ThrowTruncated(decoder.TotalConsumed, "an LZ4 frame");
                }

                return true; // the end of the data
            }

            OperationStatus status = decoder.Decompress(input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer[written..], out int consumed, out int w);
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
                        decoder.ThrowIfIncomplete();
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

    private byte[] GetInput()
    {
        if (_decoder is null)
        {
            throw new NotSupportedException("The stream compresses; it cannot be read from.");
        }

        ObjectDisposedException.ThrowIf(_input is null, this);
        return _input;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_encoder is not null && _output is not null)
            {
                FinishFrame();
            }

            if (ReleaseBuffers() && !_leaveOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_encoder is not null && _output is not null)
        {
            await FinishFrameAsync().ConfigureAwait(false);
        }

        if (ReleaseBuffers() && !_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // Writes the end of the frame.
    private void FinishFrame()
    {
        Lz4Encoder encoder = _encoder!;
        byte[] output = _output!;
        OperationStatus status;
        do
        {
            status = encoder.Compress(default, output, out _, out int w, isFinalBlock: true);
            _inner.Write(output, 0, w);
        }
        while (status != OperationStatus.Done);
    }

    private async ValueTask FinishFrameAsync()
    {
        Lz4Encoder encoder = _encoder!;
        byte[] output = _output!;
        OperationStatus status;
        do
        {
            status = encoder.Compress(default, output, out _, out int w, isFinalBlock: true);
            await _inner.WriteAsync(output.AsMemory(0, w)).ConfigureAwait(false);
        }
        while (status != OperationStatus.Done);
    }

    // Returns the buffers to the pool; true only the first time.
    private bool ReleaseBuffers()
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
