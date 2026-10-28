using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Xz;

/// <summary>
/// A write-only stream that compresses what is written to it as the xz format and passes the result to another stream. Disposing the stream
/// finishes the compressed stream.
/// </summary>
/// <remarks>
/// Compressed bytes reach the inner stream whenever the encoder has a complete chunk ready and on disposal. <see cref="Flush"/> only flushes
/// the inner stream: it does not end a chunk or block, because that would cost ratio. Output is the same as XzEncoder gives for the same
/// input, however the writes are cut.
/// </remarks>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class XzEncoderStream : Stream
{
    private readonly EncoderPump<XzEncoder> _pump;

    /// <summary>Creates a stream that compresses into <paramref name="stream"/>.</summary>
    /// <param name="stream">A writable stream that receives the compressed bytes.</param>
    /// <param name="options">The LZMA2 options, check, block size and filters; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be written.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public XzEncoderStream(Stream stream, XzCompressionOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _pump = new EncoderPump<XzEncoder>(stream, new XzEncoder(options), leaveOpen);
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => _pump.IsOpen;

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
        _pump.Write(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer) => _pump.Write(buffer);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return _pump.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _pump.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override void Flush() => _pump.FlushInner();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _pump.FlushInnerAsync(cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pump.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        await _pump.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
