
namespace RecursiveAI.OneArchive;

/// <summary>
/// Limits that bound what a hostile archive can make a reader or extractor do. All are on by default; raise them
/// knowingly. A limit that is hit raises <see cref="RecursiveAI.OneCompress.CompressionLimitException"/> with
/// <c>LimitName</c> set to the property's name, never a truncated result.
/// </summary>
public sealed class ArchiveLimits
{
    /// <summary>The default for <see cref="MaxTotalSize"/>: 1 GiB.</summary>
    public const long DefaultMaxTotalSize = 1L << 30;

    /// <summary>The default for <see cref="MaxEntryCount"/>: 100,000.</summary>
    public const int DefaultMaxEntryCount = 100_000;

    /// <summary>The default for <see cref="MaxPathLength"/>: 4096 characters.</summary>
    public const int DefaultMaxPathLength = 4096;

    /// <summary>The default for <see cref="MaxPathDepth"/>: 64 components.</summary>
    public const int DefaultMaxPathDepth = 64;

    /// <summary>The default for <see cref="MaxExpansionRatio"/>: 1000.</summary>
    public const long DefaultMaxExpansionRatio = 1000;

    /// <summary>The default for <see cref="MaxWindowSize"/>: 8 MiB.</summary>
    public const long DefaultMaxWindowSize = 8L << 20;

    /// <summary>The default for <see cref="MaxPpmdMemorySize"/>: 256 MiB.</summary>
    public const long DefaultMaxPpmdMemorySize = 256L << 20;

    /// <summary>
    /// The number of produced bytes below which the expansion ratio is not checked: 32 MiB. A tiny archive that
    /// legitimately expands a lot is not a threat until it has expanded a lot.
    /// </summary>
    public const long ExpansionRatioGraceBytes = 32L << 20;

    /// <summary>Gets the shared default limits.</summary>
    public static ArchiveLimits Default { get; } = new();

    /// <summary>
    /// Gets the most content bytes all entries together may declare, counting a sparse file at its full size.
    /// The sum of declared sizes is checked as each header is read, before any data is. Default 1 GiB.
    /// </summary>
    public long MaxTotalSize { get; init; } = DefaultMaxTotalSize;

    /// <summary>Gets the most entries an archive may hold. Default 100,000.</summary>
    public int MaxEntryCount { get; init; } = DefaultMaxEntryCount;

    /// <summary>Gets the longest entry name or link target, in characters. Default 4096.</summary>
    public int MaxPathLength { get; init; } = DefaultMaxPathLength;

    /// <summary>Gets the most components (directory levels plus the final name) an entry name may have. Default 64.</summary>
    public int MaxPathDepth { get; init; } = DefaultMaxPathDepth;

    /// <summary>
    /// Gets the most bytes the reader may produce per byte of compressed input (tar bytes plus the holes of sparse
    /// files, against bytes read from the archive file). Checked only once more than
    /// <see cref="ExpansionRatioGraceBytes"/> were produced. Default 1000. Applies to archives opened through
    /// <see cref="Archive"/>; zero or negative turns the check off.
    /// </summary>
    public long MaxExpansionRatio { get; init; } = DefaultMaxExpansionRatio;

    /// <summary>
    /// Gets the largest decompression window a compressed archive may declare, which bounds the decoder's memory.
    /// Default 8 MiB (every Zstandard level up to 19).
    /// </summary>
    public long MaxWindowSize { get; init; } = DefaultMaxWindowSize;

    /// <summary>
    /// Gets the largest PPMd model an archive may declare, which bounds the decoder's memory: the decoder allocates all of it before it reads data.
    /// An entry that declares more raises <see cref="RecursiveAI.OneCompress.CompressionLimitException"/> and nothing is allocated.
    /// Default 256 MiB, the most a zip method 98 header can state and above 7-Zip's highest 7z level (64 MiB).
    /// </summary>
    public long MaxPpmdMemorySize { get; init; } = DefaultMaxPpmdMemorySize;
}
