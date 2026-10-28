using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Xz;

/// <summary>The integrity check an xz stream carries for each block.</summary>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum XzCheck
{
    /// <summary>No check (4 bytes saved per block).</summary>
    None = 0,

    /// <summary>CRC-32 (4 bytes).</summary>
    Crc32 = 1,

    /// <summary>CRC-64 (8 bytes), the default of the reference tool.</summary>
    Crc64 = 4,

    /// <summary>SHA-256 (32 bytes).</summary>
    Sha256 = 10,
}

/// <summary>Options for decompressing xz data. The limits protect the caller from input that asks for more memory or output than it should.</summary>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class XzDecompressionOptions
{
    /// <summary>The default for <see cref="MaxDictionarySize"/>: 64 MiB, the dictionary of the largest preset of the reference tool (<c>-9</c>).</summary>
    public const long DefaultMaxDictionarySize = Lzma.LzmaDecompressionOptions.DefaultMaxDictionarySize;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = Lzma.LzmaDecompressionOptions.DefaultMaxOutputSize;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static XzDecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest LZMA2 dictionary size, in bytes, a block may declare. A block header that declares more raises
    /// <see cref="CompressionLimitException"/> before any memory is reserved. The window grows with the output, so a short block that
    /// declares a large dictionary costs little. Default <see cref="DefaultMaxDictionarySize"/>.
    /// </summary>
    public long MaxDictionarySize { get; init; } = DefaultMaxDictionarySize;

    /// <summary>Gets the largest total output, in bytes, a decoder produces between resets, over all streams. Default <see cref="DefaultMaxOutputSize"/>.</summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets a value indicating whether each block's check (CRC-32, CRC-64 or SHA-256) is verified. Default true. Turning it off skips the
    /// computation only; the structure (headers, sizes, index, footer) is always verified.
    /// </summary>
    public bool VerifyChecks { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a stream that uses a check type this library cannot compute (the types the format reserves) is
    /// decoded without verifying it. Default false: such a stream raises <see cref="CompressionFormatException"/>.
    /// </summary>
    public bool AllowUnsupportedChecks { get; init; }

    /// <summary>
    /// Gets a value indicating whether bytes after the last stream and its padding that do not start another stream are ignored.
    /// Default false: they raise <see cref="CompressionFormatException"/>. Zero padding in multiples of four bytes is always accepted.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}
