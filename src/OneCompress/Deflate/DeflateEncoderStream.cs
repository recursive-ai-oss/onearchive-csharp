using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A write-only stream that compresses what is written to it as raw Deflate, zlib or gzip and passes the result to another stream.
/// Disposing the stream finishes the compressed stream (final block and trailer).
/// </summary>
/// <remarks>
/// <para>Compressed bytes reach the inner stream when the encoder has a block ready, on <see cref="Flush()"/> and on disposal.
/// <see cref="Flush()"/> is a sync flush (see <see cref="DeflateFlush.Sync"/>), like the BCL's streams, so calling it often costs ratio;
/// <see cref="Flush(DeflateFlush)"/> chooses the mode.</para>
/// <para>The output is the same as <see cref="DeflateEncoder"/> gives for the same writes and flushes.</para>
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateEncoderStream : Stream
{
    private const int OutputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly DeflateEncoder _encoder;
    private byte[]? _output;
    private bool _finished;

    /// <summary>Creates a stream that compresses into <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed bytes.</param>
    /// <param name="format">The wrapper to write.</param>
    /// <param name="options">The level, strategy, dictionary and gzip header fields; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written, or an option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> or an option is out of range.</exception>
    public DeflateEncoderStream(Stream stream, DeflateFormat format, DeflateOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _encoder = new DeflateEncoder(format, options);
        _inner = stream;
        _leaveOpen = leaveOpen;
        _output = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _output is not null;

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

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] output = GetOutput();
        while (true)
        {
            OperationStatus status = _encoder.Compress(buffer, output, out int consumed, out int written, DeflateFlush.None);
            if (written > 0)
            {
                _inner.Write(output, 0, written);
            }

            buffer = buffer.Slice(consumed);
            if (buffer.IsEmpty && status != OperationStatus.DestinationTooSmall)
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
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = GetOutput();
        while (true)
        {
            OperationStatus status = _encoder.Compress(buffer.Span, output, out int consumed, out int written, DeflateFlush.None);
            if (written > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            buffer = buffer.Slice(consumed);
            if (buffer.IsEmpty && status != OperationStatus.DestinationTooSmall)
            {
                return;
            }
        }
    }

    /// <summary>Writes out everything given so far and flushes the inner stream, as <see cref="Flush(DeflateFlush)"/> with <see cref="DeflateFlush.Sync"/>.</summary>
    public override void Flush() => Flush(DeflateFlush.Sync);

    /// <summary>Forces out what was written so far.</summary>
    /// <param name="mode">How much to force; <see cref="DeflateFlush.None"/> only flushes the inner stream, and <see cref="DeflateFlush.Finish"/> ends the compressed stream (the stream then takes no more writes).</param>
    /// <exception cref="ObjectDisposedException">The stream is disposed.</exception>
    public void Flush(DeflateFlush mode)
    {
        if (_output is null)
        {
            return;
        }

        if (mode != DeflateFlush.None && !_finished)
        {
            while (true)
            {
                OperationStatus status = _encoder.Compress([], _output, out _, out int written, mode);
                if (written > 0)
                {
                    _inner.Write(_output, 0, written);
                }

                if (status == OperationStatus.Done)
                {
                    break;
                }
            }

            _finished = mode == DeflateFlush.Finish;
        }

        _inner.Flush();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => FlushAsync(DeflateFlush.Sync, cancellationToken);

    /// <summary>Forces out what was written so far (see <see cref="Flush(DeflateFlush)"/>).</summary>
    /// <param name="mode">How much to force.</param>
    /// <param name="cancellationToken">A token that cancels the write to the inner stream.</param>
    /// <returns>A task that completes when the data reached the inner stream.</returns>
    public async Task FlushAsync(DeflateFlush mode, CancellationToken cancellationToken)
    {
        if (_output is null)
        {
            return;
        }

        if (mode != DeflateFlush.None && !_finished)
        {
            while (true)
            {
                OperationStatus status = _encoder.Compress([], _output, out _, out int written, mode);
                if (written > 0)
                {
                    await _inner.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
                }

                if (status == OperationStatus.Done)
                {
                    break;
                }
            }

            _finished = mode == DeflateFlush.Finish;
        }

        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && _output is not null)
        {
            try
            {
                Flush(DeflateFlush.Finish);
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
        if (_output is not null)
        {
            try
            {
                await FlushAsync(DeflateFlush.Finish, CancellationToken.None).ConfigureAwait(false);
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

    private byte[] GetOutput()
    {
        ObjectDisposedException.ThrowIf(_output is null, this);
        if (_finished)
        {
            throw new InvalidOperationException("The compressed stream was finished by Flush(DeflateFlush.Finish).");
        }

        return _output;
    }

    private void Release()
    {
        byte[]? output = _output;
        _output = null;
        if (output is not null)
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}
