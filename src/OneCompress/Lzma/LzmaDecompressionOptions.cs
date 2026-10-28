using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>Options for decompressing LZMA and LZMA2 data. The limits protect the caller from headers that ask for more memory or output than it should.</summary>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzmaDecompressionOptions
{
    /// <summary>The default for <see cref="MaxDictionarySize"/>: 64 MiB, the dictionary of the largest preset of the reference tool (<c>-9</c>).</summary>
    public const long DefaultMaxDictionarySize = 64L << 20;

    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static LzmaDecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest dictionary size, in bytes, a stream may declare. A header that declares more raises
    /// <see cref="CompressionLimitException"/> before any memory is reserved. Memory use is the declared size at most (the window grows
    /// as output is produced) plus the probability tables, at most 6 MiB. Default <see cref="DefaultMaxDictionarySize"/>.
    /// </summary>
    public long MaxDictionarySize { get; init; } = DefaultMaxDictionarySize;

    /// <summary>
    /// Gets the largest total output, in bytes, a decoder produces between resets. A stream that declares a larger size is rejected
    /// before decoding, and output that would pass the limit raises <see cref="CompressionLimitException"/>. Default <see cref="DefaultMaxOutputSize"/>.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets a value indicating whether bytes after the end of the stream are ignored by the one-call methods and the stream class.
    /// Default false: trailing bytes raise <see cref="CompressionFormatException"/>. The span decoder never looks at them: it reports
    /// <see cref="System.Buffers.OperationStatus.Done"/> and leaves them unconsumed.
    /// </summary>
    public bool AllowTrailingData { get; init; }
}
