using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// A read-only stream that decompresses bzip2 data from another stream. Streams that follow each other in the input (as
/// <c>cat a.bz2 b.bz2</c> or a parallel compressor writes them) are decoded in a row, as the bzip2 tool does.
/// </summary>
/// <remarks>
/// <para>Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer. The stream reports
/// the end of the data only after a complete stream: an inner stream that ends inside a stream, or that is empty, raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set. Bytes after the last stream that do
/// not start another one raise <see cref="CompressionFormatException"/> unless <see cref="BZip2DecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>Compressing (the constructor that takes <see cref="BZip2Options"/>): bytes written are collected into a block, which is sorted and written to the inner stream
/// when it is full or the stream is disposed, so output appears in steps of up to a block (about <c>level × 100 000</c> input bytes). <see cref="Flush()"/> passes on what is already coded
/// and does not end the block, because a bzip2 block cannot be ended early without changing the output; disposing finishes the stream. Output depends only on the bytes written and the level.</para>
/// </remarks>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2Stream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly BZip2Decoder _decoder;
    private readonly BZip2DecompressionOptions _options;
    private readonly BZip2Encoder? _encoder;
    private byte[]? _input;
    private int _inputStart;
    private int _inputEnd;
    private bool _innerEnded;
    private bool _sawStream;
    private bool _finished;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of compressed data.</param>
    /// <param name="options">Limits and checks; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public BZip2Stream(Stream stream, BZip2DecompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _options = options ?? BZip2DecompressionOptions.Default;
        _decoder = new BZip2Decoder(_options);
        _inner = stream;
        _leaveOpen = leaveOpen;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    /// <summary>Creates a stream that compresses to <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed data.</param>
    /// <param name="options">The level; <see langword="null"/> for <see cref="BZip2Options.Default"/>.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    public BZip2Stream(Stream stream, BZip2Options? options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _options = BZip2DecompressionOptions.Default;
        _decoder = new BZip2Decoder(_options);
        _encoder = new BZip2Encoder(options);
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
        if (_encoder is null || _input is null || _finished)
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
        if (_encoder is null || _input is null || _finished)
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
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="BZip2Decoder.Decompress"/>).</exception>
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
            if (TryDecode(input, buffer, out int written))
            {
                return written;
            }

            _inputEnd = _inner.Read(input, 0, input.Length);
            _inputStart = 0;
            if (_inputEnd == 0)
            {
                _innerEnded = true;
            }
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="BZip2Decoder.Decompress"/>).</exception>
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
            if (TryDecode(input, buffer.Span, out int written))
            {
                return written;
            }

            _inputEnd = await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false);
            _inputStart = 0;
            if (_inputEnd == 0)
            {
                _innerEnded = true;
            }
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
                if (!_sawStream)
                {
                    throw new CompressionFormatException("bzip2", 0, "a bzip2 stream", isTruncated: true);
                }

                return true;
            }

            OperationStatus status = _decoder.Decompress(input.AsSpan(_inputStart, _inputEnd - _inputStart), buffer[written..], out int consumed, out int w);
            _inputStart += consumed;
            written += w;
            if (status == OperationStatus.Done)
            {
                _sawStream = true;
                if (_inputStart < _inputEnd && !BZip2.MoreStreams(input.AsSpan(_inputStart, _inputEnd - _inputStart), _options))
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
                default: // Done: the next stream, or the end
                    if (written > 0 && _inputStart == _inputEnd)
                    {
                        return true;
                    }

                    break;
            }
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

    // Compressing: end the stream (the last block, the end marker) and pass it on.
    private void FinishEncoding()
    {
        if (_encoder is null || _input is null || _finished)
        {
            return;
        }

        _finished = true;
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
        if (_encoder is null || _input is null || _finished)
        {
            return;
        }

        _finished = true;
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
