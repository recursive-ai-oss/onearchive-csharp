using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>Limits and settings for brotli decompression.</summary>
/// <remarks>
/// Brotli streams carry no uncompressed size, so <see cref="MaxOutputSize"/> is the guard against a stream that expands without
/// bound. Sizes a stream declares (the window, and the length of each meta-block) are checked against the limits before any memory
/// is committed for them.
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BrotliDecompressionOptions
{
    /// <summary>The default for <see cref="MaxWindowSize"/>: the largest window the format allows, 2<sup>24</sup> - 16 bytes (RFC 7932 section 9.1).</summary>
    public const long DefaultMaxWindowSize = (1L << 24) - 16;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>The options with every default.</summary>
    public static BrotliDecompressionOptions Default { get; } = new();

    /// <summary>
    /// The largest sliding window, in bytes, a stream may declare. A stream that declares more raises
    /// <see cref="CompressionLimitException"/> before decoding starts. The decoder keeps a ring buffer of the declared window, rounded
    /// up to a power of two, but grows it only as the stream's output needs it. Default <see cref="DefaultMaxWindowSize"/>
    /// (the format maximum, so no valid stream is refused); lower it to bound memory for untrusted input. The window of a stream
    /// is 2<sup>n</sup> - 16 for n from 10 to 24.
    /// </summary>
    public long MaxWindowSize { get; init; } = DefaultMaxWindowSize;

    /// <summary>
    /// The most bytes the whole stream may decompress to. A meta-block that would take the total past it raises
    /// <see cref="CompressionLimitException"/> before it produces any output. Default <see cref="DefaultMaxOutputSize"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// True to leave bytes after the end of the stream alone; the one-shot methods and <see cref="BrotliStream"/> then ignore them.
    /// False (the default) to raise <see cref="CompressionFormatException"/> when any follow.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}
