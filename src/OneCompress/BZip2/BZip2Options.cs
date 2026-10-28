using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>Options for decompressing bzip2 data. The limits protect the caller from input that asks for more output than it should.</summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2DecompressionOptions
{
    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static BZip2DecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest total output, in bytes, a decoder produces between resets. Output that would pass it raises
    /// <see cref="CompressionLimitException"/> before it is written; a decoder with a destination buffer never writes past the limit.
    /// A block of at most 900 000 bytes can expand to about 46 MB through the first run-length stage, so this limit, and not the
    /// block size, is what bounds a hostile stream. The default is 1 GiB.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets a value indicating whether each block's CRC and the stream's combined CRC are verified. The default is true.
    /// Turning it off lets a caller read the bytes of a damaged block (the bzip2 tool delivers them too before it reports the error).
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether input after the last stream is ignored by <see cref="BZip2"/> and <see cref="BZip2Stream"/>
    /// when it does not start with <c>BZh</c>, instead of raising <see cref="CompressionFormatException"/>. Streams that follow each
    /// other are always decoded one after the other. The default is false.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}
