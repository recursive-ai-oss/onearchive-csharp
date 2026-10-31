using System.Buffers;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A read-only stream that decompresses raw Deflate, zlib or gzip data from another stream. A gzip stream decodes all of its
/// members in a row, as the gzip tool does.
/// </summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer. The stream reports
/// the end of the data only when the stream is complete: an inner stream that ends inside a Deflate stream, a zlib stream or a gzip
/// member, or that is empty, raises <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set.
/// Bytes after the end of a raw Deflate or zlib stream raise <see cref="CompressionFormatException"/> unless
/// <see cref="DeflateDecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>Writing is not supported by this version: <see cref="CanWrite"/> is false.</para>
/// </remarks>
public sealed class DeflateCodecStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly DeflateDecoder _decoder;
    private readonly bool _allowTrailing;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _innerEnded;
    private bool _sawUnit;
    private bool _finished;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="format">The wrapper to expect.</param>
    /// <param name="options">Limits and the preset dictionary; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined value.</exception>
    public DeflateCodecStream(Stream stream, DeflateFormat format, DeflateDecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _decoder = new DeflateDecoder(format, options);
        _inner = stream;
        _leaveOpen = leaveOpen;
        _allowTrailing = (options ?? DeflateDecompressionOptions.Default).AllowTrailingData;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>Gets the header of the gzip member being read (see <see cref="DeflateDecoder.CurrentGZipHeader"/>).</summary>
    public GZipHeader CurrentGZipHeader => _decoder.CurrentGZipHeader;

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
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="DeflateDecoder.Decompress"/>).</exception>
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
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="DeflateDecoder.Decompress"/>).</exception>
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

    // Decodes what the buffered input allows. Returns false when more input is needed from the inner stream; true with
    // written == 0 means the end of the data.
    private bool TryDecode(byte[] input, Span<byte> buffer, out int written)
    {
        written = 0;
        while (true)
        {
            if (_finished)
            {
                return true;
            }

            if (_innerEnded && _inputStart == _inputEnd && _decoder.IsAtFrameBoundary)
            {
                if (!_sawUnit)
                {
                    _decoder.ThrowIfIncomplete();
                    throw new CompressionFormatException(FormatName, 0, "a " + FormatName + " stream", isTruncated: true);
                }

                return true;
            }

            OperationStatus status = _decoder.Decompress(input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer[written..], out int consumed, out int w);
            _inputStart += consumed;
            written += w;
            if (status == OperationStatus.Done)
            {
                _sawUnit = true;
                if (_inputStart < _inputEnd && EndsHere(input.AsSpan(_inputStart, _inputEnd - _inputStart)))
                {
                    _finished = true;
                    return true;
                }
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

                        continue;
                    }

                    return written > 0;
                case OperationStatus.DestinationTooSmall:
                    return true;
                default: // Done: the next member or stream, or the end
                    if (written > 0 && _inputStart == _inputEnd)
                    {
                        return true;
                    }

                    break;
            }
        }
    }

    private string FormatName => _decoder.Format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zlib", _ => "gzip" };

    // Called after a unit ended with input left: true when the rest is not part of the data (trailing bytes that are allowed).
    // Raw Deflate and zlib end after one stream; trailing bytes are an error unless allowed. gzip continues with the next member.
    private bool EndsHere(ReadOnlySpan<byte> rest)
    {
        if (_decoder.Format == DeflateFormat.GZip)
        {
            return _allowTrailing && (rest[0] != 0x1F || (rest.Length >= 2 && rest[1] != 0x8B));
        }

        if (_allowTrailing)
        {
            return true;
        }

        throw new CompressionFormatException(FormatName, _decoder.TotalConsumed, "the input to end with the stream");
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
