using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>
/// One-call PPMd I compression and decompression of raw coder output: the data of a zip method 98 entry after its two header bytes.
/// Everything here is built on <see cref="PpmdIEncoder"/> and <see cref="PpmdIDecoder"/>.
/// </summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class PpmdI
{
    /// <summary>Compresses a buffer.</summary>
    /// <param name="source">The data; may be empty.</param>
    /// <param name="options">The model parameters; <see langword="null"/> for <see cref="PpmdIOptions.Default"/>.</param>
    /// <returns>The raw coder output. The decoder needs the same order and memory size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, PpmdIOptions? options = null)
    {
        var encoder = new PpmdIEncoder(options);
        byte[] output = new byte[Math.Max((source.Length / 2) + 64, 64)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = encoder.Compress(source[consumed..], output.AsSpan(written), out int c, out int w, isFinalBlock: true);
            consumed += c;
            written += w;
            if (status == OperationStatus.Done)
            {
                return output.AsSpan(0, written).ToArray();
            }

            Array.Resize(ref output, checked(output.Length * 2));
        }
    }

    /// <summary>Compresses a buffer into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the coder output.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">The model parameters; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, PpmdIOptions? options = null)
    {
        var encoder = new PpmdIEncoder(options);
        OperationStatus status = encoder.Compress(source, destination, out _, out int written, isFinalBlock: true);
        bytesWritten = status == OperationStatus.Done ? written : 0;
        return status == OperationStatus.Done;
    }

    /// <summary>Decompresses a buffer of raw coder output.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="options">The model parameters the encoder used, the output size (or -1 to decode up to the end marker) and the limits.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is truncated or corrupt, or ended before <see cref="PpmdIDecompressionOptions.OutputSize"/> bytes.</exception>
    /// <exception cref="CompressionLimitException">The model memory or the output passes its limit; nothing is allocated past a limit.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, PpmdIDecompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var decoder = new PpmdIDecoder(options);
        long hint = options.OutputSize >= 0 ? options.OutputSize : Math.Min(Math.Min((long)source.Length * 4, options.MaxOutputSize), 1 << 20);
        if (options.OutputSize > options.MaxOutputSize)
        {
            throw new CompressionLimitException("ppmd8", nameof(PpmdIDecompressionOptions.MaxOutputSize), options.MaxOutputSize, options.OutputSize);
        }

        byte[] output = new byte[Math.Min(Math.Max(hint, 16), Array.MaxLength)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], output.AsSpan(written), out int c, out int w, isFinalBlock: true);
            consumed += c;
            written += w;
            if (status == OperationStatus.Done)
            {
                return written == output.Length ? output : output.AsSpan(0, written).ToArray();
            }

            if (output.Length >= Array.MaxLength || output.Length >= options.MaxOutputSize)
            {
                throw new CompressionLimitException("ppmd8", nameof(PpmdIDecompressionOptions.MaxOutputSize), options.MaxOutputSize, (long)written + 1);
            }

            Array.Resize(ref output, (int)Math.Min(Math.Min((long)output.Length * 2, Array.MaxLength), options.MaxOutputSize));
        }
    }
}

/// <summary>
/// Compresses to raw PPMd I coder output with the span-based shape of <c>BrotliEncoder</c>. The first call allocates the model
/// (<see cref="PpmdIOptions.MemorySize"/> bytes); one instance compresses one stream.
/// </summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdIEncoder
{
    private readonly PpmdIModel _model;
    private readonly PpmdEncoderCore _core;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The model parameters; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range.</exception>
    public PpmdIEncoder(PpmdIOptions? options = null)
    {
        options ??= PpmdIOptions.Default;
        _model = new PpmdIModel(options.Order, options.MemorySize, options.RestoreMethod);
        _core = new PpmdEncoderCore(_model, options.WriteEndMarker);
    }

    /// <summary>Gets the raw model, for callers that compose their own framing.</summary>
    public PpmdIModel Model => _model;

    internal PpmdEncoderCore Core => _core;

    /// <summary>Compresses a chunk.</summary>
    /// <param name="source">The input.</param>
    /// <param name="destination">Receives output. Output is produced a few bytes behind the input, so a chunk may yield nothing.</param>
    /// <param name="bytesConsumed">The number of input bytes used.</param>
    /// <param name="bytesWritten">The number of bytes written.</param>
    /// <param name="isFinalBlock">True for the last chunk: the range coder is flushed and the stream is complete once the status is <see cref="OperationStatus.Done"/>.</param>
    /// <returns><see cref="OperationStatus.Done"/> when all input is used (and the stream finished, for the final block), or <see cref="OperationStatus.DestinationTooSmall"/>: call again with the rest of the input and more room.</returns>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock) =>
        _core.Compress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);
}

/// <summary>
/// Decompresses raw PPMd I coder output with the span-based shape of <c>BrotliDecoder</c>. The constructor allocates the model.
/// </summary>
[Experimental("ONECOMPRESS010", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class PpmdIDecoder
{
    private readonly PpmdIModel _model;
    private readonly PpmdDecoderCore _core;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">The model parameters the encoder used, the output size and the limits.</param>
    /// <exception cref="CompressionFormatException">The order or memory size is outside what the format allows.</exception>
    /// <exception cref="CompressionLimitException">The memory size passes <see cref="PpmdIDecompressionOptions.MaxMemorySize"/>; nothing is allocated.</exception>
    public PpmdIDecoder(PpmdIDecompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Order is < PpmdIModel.MinOrder or > PpmdIModel.MaxOrder)
        {
            throw new CompressionFormatException("ppmd8", 0, $"a model order from {PpmdIModel.MinOrder} to {PpmdIModel.MaxOrder}, not {options.Order}");
        }

        if (options.MemorySize < PpmdIModel.MinMemorySize)
        {
            throw new CompressionFormatException("ppmd8", 0, $"a memory size of at least {PpmdIModel.MinMemorySize} bytes, not {options.MemorySize}");
        }

        if (options.MemorySize > options.MaxMemorySize)
        {
            throw new CompressionLimitException("ppmd8", nameof(PpmdIDecompressionOptions.MaxMemorySize), options.MaxMemorySize, options.MemorySize);
        }

        _model = new PpmdIModel(options.Order, options.MemorySize, options.RestoreMethod);
        _core = new PpmdDecoderCore(_model, "ppmd8", 4, options.OutputSize, options.MaxOutputSize);
    }

    /// <summary>Gets the raw model, for callers that compose their own framing.</summary>
    public PpmdIModel Model => _model;

    internal PpmdDecoderCore Core => _core;

    /// <summary>Gets the number of bytes decoded so far.</summary>
    public long TotalOutput => _core.TotalProduced;

    /// <summary>
    /// Reads the end marker that follows the data when <see cref="PpmdIDecompressionOptions.OutputSize"/> was set and the encoder still wrote one (zip method 98 entries from 7-Zip do).
    /// Call it after <see cref="Decompress"/> returned <see cref="OperationStatus.Done"/>.
    /// </summary>
    /// <param name="source">The input after the data.</param>
    /// <param name="bytesConsumed">The number of input bytes the marker used.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the marker was read; <see cref="OperationStatus.NeedMoreData"/>: call again with more bytes after the same ones;
    /// or <see cref="OperationStatus.InvalidData"/> when what follows is not the marker (the stream was written without one), in which case nothing is consumed.
    /// </returns>
    /// <exception cref="InvalidOperationException">The data has not been decoded to its end yet.</exception>
    public OperationStatus ReadEndMarker(ReadOnlySpan<byte> source, out int bytesConsumed) => _core.ReadEndMarker(source, out bytesConsumed);

    /// <summary>Decompresses a chunk.</summary>
    /// <param name="source">The input. Bytes the call did not use (see <paramref name="bytesConsumed"/>) must be presented again, with more after them.</param>
    /// <param name="destination">Receives output.</param>
    /// <param name="bytesConsumed">The number of input bytes used.</param>
    /// <param name="bytesWritten">The number of bytes written.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> ends the input; the decoder then does not wait for look-ahead bytes and reports a stream that ends early as truncated.</param>
    /// <returns><see cref="OperationStatus.Done"/> at the end of the data, <see cref="OperationStatus.DestinationTooSmall"/>, or <see cref="OperationStatus.NeedMoreData"/> (not for the final block).</returns>
    /// <exception cref="CompressionFormatException">The data is corrupt or truncated.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="PpmdIDecompressionOptions.MaxOutputSize"/>.</exception>
    public OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesConsumed, out int bytesWritten, bool isFinalBlock) =>
        _core.Decompress(source, destination, out bytesConsumed, out bytesWritten, isFinalBlock);
}
