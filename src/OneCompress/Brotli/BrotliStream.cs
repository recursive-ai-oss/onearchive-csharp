using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>A read-only stream that decompresses brotli data (RFC 7932) from another stream.</summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer. The stream reports the
/// end of the data only after a complete brotli stream: an inner stream that ends inside the stream, or that is empty, raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set. Bytes after the stream raise
/// <see cref="CompressionFormatException"/> unless <see cref="BrotliDecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>This type has the name of <c>System.IO.Compression.BrotliStream</c>, which is a different type; qualify or alias the name where both namespaces are imported.
/// Writing (compression) is not supported by this version: <see cref="CanWrite"/> is false.</para>
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BrotliStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly BrotliDecoder _decoder;
    private readonly BrotliDecompressionOptions _options;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private long _consumedTotal;
    private bool _innerEnded;
    private bool _finished;
    private bool _probeTrailing;

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
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="BrotliDecoder.Decompress"/>).</exception>
    public override int Read(Span<byte> buffer)
    {
        byte[] input = GetInput();
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
