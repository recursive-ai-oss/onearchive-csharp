using System.Buffers;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A read-only stream that decompresses Zstandard data from another stream, one or many frames in a row.
/// </summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer.
/// The stream reports the end of the data only when the inner stream ends at a frame boundary; an inner stream that
/// ends inside a frame, or that is empty, raises <see cref="CompressionFormatException"/> with
/// <see cref="CompressionFormatException.IsTruncated"/> set.</para>
/// <para>Only decompression is supported so far, so <see cref="CanWrite"/> is false.</para>
/// </remarks>
public sealed class ZstdStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly ZstdDecoder _decoder;
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

    /// <inheritdoc />
    public override bool CanRead => _input is not null;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

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
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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

    private byte[] GetInput()
    {
        ObjectDisposedException.ThrowIf(_input is null, this);
        return _input;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && ReleaseBuffer() && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (ReleaseBuffer() && !_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    // Returns the input buffer to the pool; true only the first time.
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
