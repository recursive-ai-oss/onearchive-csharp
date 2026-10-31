using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneArchive.Cfb;

/// <summary>The kind of a compound file directory entry ([MS-CFB] 2.6.1, Object Type). Unallocated entries are never returned.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum CfbObjectType : byte
{
    /// <summary>A storage object: a directory that holds other objects.</summary>
    Storage = 1,

    /// <summary>A stream object: a file.</summary>
    Stream = 2,

    /// <summary>The root storage object (directory entry 0).</summary>
    RootStorage = 5,
}

/// <summary>The color of a directory entry in its sibling red-black tree ([MS-CFB] 2.6.1, Color Flag).</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum CfbColor : byte
{
    /// <summary>Red.</summary>
    Red = 0,

    /// <summary>Black.</summary>
    Black = 1,
}

/// <summary>The fields of a compound file header ([MS-CFB] 2.2), as stored.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CompoundFileHeader
{
    /// <summary>Gets the major version: 3 (512-byte sectors) or 4 (4096-byte sectors).</summary>
    public int MajorVersion { get; init; }

    /// <summary>Gets the minor version (normally 0x3E).</summary>
    public int MinorVersion { get; init; }

    /// <summary>Gets the sector size as a power of two: 9 for version 3, 12 for version 4.</summary>
    public int SectorShift { get; init; }

    /// <summary>Gets the mini sector size as a power of two: always 6.</summary>
    public int MiniSectorShift { get; init; }

    /// <summary>Gets the sector size in bytes.</summary>
    public int SectorSize => 1 << SectorShift;

    /// <summary>Gets the header CLSID (reserved; normally all zeroes).</summary>
    public Guid HeaderClassId { get; init; }

    /// <summary>Gets the number of directory sectors as stored (zero in version 3 files; informational).</summary>
    public uint DirectorySectorCount { get; init; }

    /// <summary>Gets the number of FAT sectors.</summary>
    public uint FatSectorCount { get; init; }

    /// <summary>Gets the first directory sector.</summary>
    public uint FirstDirectorySector { get; init; }

    /// <summary>Gets the transaction signature number (zero when transactions are not used).</summary>
    public uint TransactionSignature { get; init; }

    /// <summary>Gets the mini stream cutoff size: streams smaller than this live in the mini stream. Always 4096.</summary>
    public uint MiniStreamCutoff { get; init; }

    /// <summary>Gets the first mini FAT sector, or <c>ENDOFCHAIN</c> (0xFFFFFFFE) when there is none.</summary>
    public uint FirstMiniFatSector { get; init; }

    /// <summary>Gets the number of mini FAT sectors.</summary>
    public uint MiniFatSectorCount { get; init; }

    /// <summary>Gets the first DIFAT sector, or <c>ENDOFCHAIN</c> when the header's 109 entries are enough.</summary>
    public uint FirstDifatSector { get; init; }

    /// <summary>Gets the number of DIFAT sectors.</summary>
    public uint DifatSectorCount { get; init; }

    /// <summary>Gets the 109 DIFAT entries held in the header (unused ones are 0xFFFFFFFF).</summary>
    public IReadOnlyList<uint> HeaderDifat { get; init; } = [];
}

/// <summary>A storage or stream of a compound file, with the directory entry's fields ([MS-CFB] 2.6.1).</summary>
/// <remarks>
/// <see cref="ArchiveEntry.Name"/> is the full path from the root, built from the stored names by
/// <see cref="CompoundFileReader.SanitizeName(string)"/> so that it is safe to extract. <see cref="RawName"/> is the entry's own stored name.
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CfbEntry : ArchiveEntry
{
    /// <summary>Gets the stream ID: the entry's index in the directory array (0 is the root storage).</summary>
    public int StreamId { get; init; }

    /// <summary>Gets the stream ID of the storage that holds this entry; -1 for the root storage.</summary>
    public int ParentStreamId { get; init; }

    /// <summary>Gets what the directory entry is ([MS-CFB] 2.6.1): a storage (a directory), a stream (a file) or the root storage.</summary>
    public CfbObjectType ObjectType { get; init; }

    /// <summary>Gets the red-black tree color.</summary>
    public CfbColor Color { get; init; }

    /// <summary>Gets the left sibling's stream ID, or 0xFFFFFFFF (NOSTREAM).</summary>
    public uint LeftSiblingId { get; init; }

    /// <summary>Gets the right sibling's stream ID, or 0xFFFFFFFF (NOSTREAM).</summary>
    public uint RightSiblingId { get; init; }

    /// <summary>Gets the stream ID of the root of the children tree, or 0xFFFFFFFF (NOSTREAM).</summary>
    public uint ChildId { get; init; }

    /// <summary>Gets the object class (CLSID) of a storage; all zeroes for none and for streams.</summary>
    public Guid ClassId { get; init; }

    /// <summary>Gets the user-defined state bits of a storage.</summary>
    public uint StateBits { get; init; }

    /// <summary>Gets the creation time as a Windows FILETIME (100 ns ticks since 1601-01-01 UTC); zero when not recorded.</summary>
    public ulong CreationFileTime { get; init; }

    /// <summary>Gets the modified time as a Windows FILETIME; zero when not recorded.</summary>
    public ulong ModifiedFileTime { get; init; }

    /// <summary>Gets the creation time, or <see langword="null"/> when it is zero or outside the range of <see cref="DateTimeOffset"/>.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Gets the first sector of the stream (of the mini stream for the root).</summary>
    public uint StartSector { get; init; }

    /// <summary>Gets the entry's own stored name (a single path component, UTF-16, not sanitized).</summary>
    public required string RawName { get; init; }

    /// <summary>Gets whether the stream is stored in the mini stream (its size is below the cutoff).</summary>
    public bool IsInMiniStream { get; init; }

    internal CompoundFileReader? Owner { get; init; }
}

/// <summary>Options for reading compound files.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CompoundFileReaderOptions
{
    /// <summary>The default for <see cref="MaxDirectoryEntries"/>: 262,144 (32 MiB of directory).</summary>
    public const int DefaultMaxDirectoryEntries = 262144;

    /// <summary>The options with every default.</summary>
    public static CompoundFileReaderOptions Default { get; } = new();

    /// <summary>Gets the limits (entry count, total size, path length and depth). Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets the most directory entries, allocated or not, the directory chain may hold. Checked before the directory is read.
    /// Exceeding it raises <see cref="RecursiveAI.OneCompress.CompressionLimitException"/>. Default 262,144.
    /// </summary>
    public int MaxDirectoryEntries { get; init; } = DefaultMaxDirectoryEntries;
}
