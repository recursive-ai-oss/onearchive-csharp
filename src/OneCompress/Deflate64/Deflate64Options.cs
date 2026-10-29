using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>Limits for decompressing Deflate64 data.</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Deflate64DecompressionOptions
{
    /// <summary>The default for <see cref="MaxOutputSize"/>: 1 GiB.</summary>
    public const long DefaultMaxOutputSize = 1L << 30;

    /// <summary>Gets the shared default options.</summary>
    public static Deflate64DecompressionOptions Default { get; } = new();

    /// <summary>
    /// Gets the most bytes the decoder will produce before it throws <see cref="CompressionLimitException"/>. The default is 1 GiB.
    /// The decoder never allocates for the output: it writes into the caller's buffer, and its own state is a 128 KiB window.
    /// </summary>
    public long MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets whether the one-call and stream readers accept bytes after the end of the stream. The default is <see langword="false"/>:
    /// they are an error. (A zip entry's data ends where the entry's compressed size says, so a reader gives the decoder exactly that.)
    /// </summary>
    public bool AllowTrailingData { get; init; }
}

/// <summary>Options for compressing to Deflate64.</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Deflate64Options
{
    /// <summary>The default level: 6.</summary>
    public const int DefaultLevel = 6;

    /// <summary>Gets the shared default options.</summary>
    public static Deflate64Options Default { get; } = new();

    /// <summary>
    /// Gets the compression level, 0 to 9. 0 stores; 1 to 4 are one fast lazy parse, 5 and 6 and 7 and 8 are optimal parses of two
    /// strengths and 9 is the strongest (levels within a group give identical output; see docs/deflate64-encoder.md). The default is 6.
    /// </summary>
    public int Level { get; init; } = DefaultLevel;
}
