using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Lzip;

/// <summary>One-call decompression of lzip data. Everything here is built on <see cref="LzipDecoder"/>. A buffer may hold several members one after the other; their outputs are joined.</summary>
[Experimental("ONECOMPRESS007", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static partial class Lzip
{
    /// <summary>Decompresses a buffer holding one or more lzip members.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="options">Limits and checks; <see langword="null"/> for <see cref="LzipDecompressionOptions.Default"/>.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, the trailer does not match, or bytes that are not a member follow the last one (see <see cref="LzipDecompressionOptions.AllowTrailingData"/>).</exception>
    /// <exception cref="CompressionLimitException">A member declares a dictionary over <see cref="LzipDecompressionOptions.MaxDictionarySize"/> or the output passes <see cref="LzipDecompressionOptions.MaxOutputSize"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, LzipDecompressionOptions? options = null)
    {
        options ??= LzipDecompressionOptions.Default;
        return OneShot.Decompress(new LzipDecoder(options), source, "lzip", options.MaxOutputSize, failOnTrailing: false, multiStream: true);
    }

    /// <summary>Decompresses a buffer holding one or more lzip members into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Limits and checks; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small for the whole output.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or bytes that are not a member follow the last one.</exception>
    /// <exception cref="CompressionLimitException">A limit is exceeded.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, LzipDecompressionOptions? options = null)
    {
        options ??= LzipDecompressionOptions.Default;
        return OneShot.TryDecompress(new LzipDecoder(options), source, destination, out bytesWritten, "lzip", failOnTrailing: false, multiStream: true);
    }
}

/// <summary>A read-only stream that decompresses lzip data from another stream. Several members in a row are decoded in a row.</summary>
/// <remarks>
/// Input is read from the inner stream in chunks of <see cref="InputBufferSize"/> bytes into a pooled buffer. The stream reports the end
/// of the data only after a complete stream: an inner stream that ends inside one, or is empty, raises
/// <see cref="CompressionFormatException"/> with <see cref="CompressionFormatException.IsTruncated"/> set. Bytes after the last member that do not start another one raise it unless <see cref="LzipDecompressionOptions.AllowTrailingData"/> is set. Writing is not supported.
/// </remarks>
[Experimental("ONECOMPRESS007", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzipStream : Stream
{
    /// <summary>The size of the buffer that holds compressed bytes read from the inner stream.</summary>
    public const int InputBufferSize = DecoderPump<LzipDecoder>.InputBufferSize;

    private readonly DecoderPump<LzipDecoder> _pump;

    /// <summary>Creates a stream that decompresses <paramref name="stream"/>.</summary>
    /// <param name="stream">A readable stream of lzip data.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave <paramref name="stream"/> open when this stream is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="stream"/> cannot be read.</exception>
    public LzipStream(Stream stream, LzipDecompressionOptions? options = null, bool leaveOpen = false)
    {
        options ??= LzipDecompressionOptions.Default;
        _pump = new DecoderPump<LzipDecoder>(stream, new LzipDecoder(options), "lzip", singleStream: false, options.AllowTrailingData, leaveOpen);
    }

    /// <inheritdoc />
    public override bool CanRead => _pump.IsOpen;

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
        return _pump.Read(buffer.AsSpan(offset, count));
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="LzipDecoder.Decompress"/>).</exception>
    public override int Read(Span<byte> buffer) => _pump.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return _pump.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    /// <exception cref="CompressionException">The data is invalid or exceeds a limit (see <see cref="LzipDecoder.Decompress"/>).</exception>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _pump.ReadAsync(buffer, cancellationToken);

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
