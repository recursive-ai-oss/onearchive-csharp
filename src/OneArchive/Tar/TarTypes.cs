using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneArchive.Tar;

/// <summary>The tar header dialect of an entry (when read) or of the archive (when written).</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum TarFormat
{
    /// <summary>Version 7: no magic. Read only.</summary>
    V7 = 0,

    /// <summary>POSIX ustar: magic <c>ustar\0</c>, version <c>00</c>, name split into prefix and name.</summary>
    Ustar = 1,

    /// <summary>ustar plus pax extended headers (POSIX.1-2001). The writer's default.</summary>
    Pax = 2,

    /// <summary>GNU tar: magic <c>ustar </c>, version <c> \0</c>, long-name records and old-style sparse files. Read only.</summary>
    Gnu = 3,
}

/// <summary>How a sparse file's holes were described in the archive.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum TarSparseFormat
{
    /// <summary>The entry is not sparse.</summary>
    None = 0,

    /// <summary>The old GNU format: type <c>S</c>, a map in the header and extension blocks.</summary>
    OldGnu = 1,

    /// <summary>GNU sparse 0.0: offset and size pax records, repeated for each region.</summary>
    Pax00 = 2,

    /// <summary>GNU sparse 0.1: one <c>GNU.sparse.map</c> pax record.</summary>
    Pax01 = 3,

    /// <summary>GNU sparse 1.0: the map is written at the start of the entry's data.</summary>
    Pax10 = 4,
}

/// <summary>A run of stored data in a sparse file: <see cref="Length"/> bytes that belong at <see cref="Offset"/> of the full file.</summary>
/// <param name="Offset">Where the data belongs in the full file.</param>
/// <param name="Length">How many bytes are stored.</param>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly record struct TarSparseRegion(long Offset, long Length);

/// <summary>A tar entry with every header field the archive carried.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class TarEntry : ArchiveEntry
{
    private static readonly IReadOnlyList<TarSparseRegion> s_noRegions = [];
    private static readonly IReadOnlyDictionary<string, string> s_noRecords = new Dictionary<string, string>();

    /// <summary>Gets the dialect of the entry's header.</summary>
    public TarFormat Format { get; init; }

    /// <summary>Gets the type flag byte of the entry's header, for example <c>'0'</c> or <c>'5'</c> (NUL is reported as <c>'0'</c>).</summary>
    public byte TypeFlag { get; init; }

    /// <summary>Gets the offset of the entry's first header block (the pax or long-name records that belong to it come first) from where the reader started.</summary>
    public long HeaderOffset { get; init; }

    /// <summary>Gets the offset of the entry's stored data from where the reader started.</summary>
    public long DataOffset { get; init; }

    /// <summary>Gets the number of data bytes stored in the archive. Differs from <see cref="ArchiveEntry.Size"/> for sparse files.</summary>
    public long StoredSize { get; init; }

    /// <summary>Gets the size field of the header as written (before a pax <c>size</c> or sparse real size applied).</summary>
    public long HeaderSize { get; init; }

    /// <summary>Gets the header checksum.</summary>
    public uint Checksum { get; init; }

    /// <summary>Gets the name field of the header, joined to the ustar prefix, before any long-name or pax record replaced it.</summary>
    public string StoredName { get; init; } = string.Empty;

    /// <summary>Gets the link name field of the header before any long-name or pax record replaced it.</summary>
    public string StoredLinkName { get; init; } = string.Empty;

    /// <summary>Gets the magic field of the header, with trailing NULs removed.</summary>
    public string Magic { get; init; } = string.Empty;

    /// <summary>Gets the version field of the header.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Gets the mode field of the header, all bits.</summary>
    public uint RawMode { get; init; }

    /// <summary>Gets how the entry's holes were described.</summary>
    public TarSparseFormat SparseFormat { get; init; }

    /// <summary>Gets the stored regions of a sparse file, in file order; empty if the entry is not sparse.</summary>
    public IReadOnlyList<TarSparseRegion> SparseRegions { get; init; } = s_noRegions;

    /// <summary>
    /// Gets every pax record that applied to the entry (global records overridden by the entry's own), by key.
    /// Empty for entries without pax headers.
    /// </summary>
    public IReadOnlyDictionary<string, string> PaxRecords { get; init; } = s_noRecords;
}

/// <summary>Options for <see cref="TarReader"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class TarReaderOptions
{
    /// <summary>The default for <see cref="MaxExtendedHeaderSize"/>: 1 MiB.</summary>
    public const int DefaultMaxExtendedHeaderSize = 1 << 20;

    /// <summary>The default for <see cref="MaxSparseRegions"/>: 100,000.</summary>
    public const int DefaultMaxSparseRegions = 100_000;

    /// <summary>Gets the shared default options.</summary>
    public static TarReaderOptions Default { get; } = new();

    /// <summary>Gets the limits applied to the entries. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>Gets the most bytes one pax or GNU long-name record block may hold. Checked before it is buffered. Default 1 MiB.</summary>
    public int MaxExtendedHeaderSize { get; init; } = DefaultMaxExtendedHeaderSize;

    /// <summary>Gets the most regions a sparse file's map may list. Default 100,000.</summary>
    public int MaxSparseRegions { get; init; } = DefaultMaxSparseRegions;

    /// <summary>
    /// Gets a value indicating whether zero blocks are skipped instead of ending the archive, so that concatenated
    /// archives read as one (GNU tar's <c>-i</c>). Default false.
    /// </summary>
    public bool IgnoreZeroBlocks { get; init; }

    /// <summary>
    /// Gets a value indicating whether an archive must end with a zero block. A stream that stops exactly between two
    /// entries is otherwise indistinguishable from a complete one, so by default it is reported as truncated.
    /// Ignored when <see cref="IgnoreZeroBlocks"/> is set. Default true.
    /// </summary>
    public bool RequireEndOfArchive { get; init; } = true;
}

/// <summary>Options for <see cref="TarWriter"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class TarWriterOptions
{
    /// <summary>The default record size: 10240 bytes (a blocking factor of 20).</summary>
    public const int DefaultRecordSize = 10240;

    /// <summary>Gets the shared default options.</summary>
    public static TarWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the dialect to write: <see cref="TarFormat.Pax"/> (the default) or <see cref="TarFormat.Ustar"/>. With
    /// ustar an entry that does not fit the fixed fields throws <see cref="ArchiveUnsupportedException"/>; with
    /// pax it gets an extended header holding the fields that do not fit.
    /// </summary>
    public TarFormat Format { get; init; } = TarFormat.Pax;

    /// <summary>
    /// Gets the size to pad the finished archive to, a multiple of 512. Default 10240, as GNU tar and bsdtar write it;
    /// 512 writes no padding beyond the two end blocks.
    /// </summary>
    public int RecordSize { get; init; } = DefaultRecordSize;
}
