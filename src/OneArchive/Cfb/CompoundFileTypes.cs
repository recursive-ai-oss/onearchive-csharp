
namespace RecursiveAI.OneArchive.Cfb;

/// <summary>The kind of a compound file directory entry ([MS-CFB] 2.6.1, Object Type). Unallocated entries are never returned.</summary>
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
public enum CfbColor : byte
{
    /// <summary>Red.</summary>
    Red = 0,

    /// <summary>Black.</summary>
    Black = 1,
}

/// <summary>The fields of a compound file header ([MS-CFB] 2.2), as stored.</summary>
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

    /// <summary>
    /// Gets the size the directory entry declares. Equal to <see cref="ArchiveEntry.Size"/> unless the reader recovered a damaged file
    /// (see <see cref="CompoundFileReaderOptions.Recovery"/>) and the stream's sectors do not hold that many bytes, in which case
    /// <see cref="ArchiveEntry.Size"/> is the shorter length that could be read.
    /// </summary>
    public long DeclaredSize { get; init; }

    /// <summary>
    /// Gets a value indicating whether recovery found something wrong with this entry: a stream shortened because its sector chain is
    /// broken, a directory entry with invalid fields, or an entry attached to the root because the tree no longer reaches it.
    /// Always false unless <see cref="CompoundFileReaderOptions.Recovery"/> is set and the file needed recovery. The reasons are in <see cref="CompoundFileReader.Damage"/>.
    /// </summary>
    public bool IsDamaged { get; init; }

    internal CompoundFileReader? Owner { get; init; }
}

/// <summary>What a <see cref="CfbDamage"/> record describes.</summary>
public enum CfbDamageKind
{
    /// <summary>A header field is inconsistent with the file ([MS-CFB] 2.2); a value derived from the file was used instead.</summary>
    HeaderInconsistent = 0,

    /// <summary>The file ends inside a sector of a table, the directory or the mini stream; the bytes that are there were used and the rest read as free or unallocated.</summary>
    Truncated = 1,

    /// <summary>The DIFAT or FAT is damaged: an entry points outside the file (which is also how a file cut short looks) or to a sector another chain uses, or the DIFAT chain is cut. Sectors the missing FAT part described are treated as free.</summary>
    AllocationTableUnusable = 2,

    /// <summary>The mini FAT or the mini stream is damaged; mini streams that need the lost part are shortened.</summary>
    MiniStreamUnusable = 3,

    /// <summary>The directory sector chain is cut or does not start where the header says; directory sectors were found by scanning.</summary>
    DirectoryUnusable = 4,

    /// <summary>A directory entry has invalid fields ([MS-CFB] 2.6.1); they were replaced by usable values or the entry was ignored.</summary>
    BadEntry = 5,

    /// <summary>A sibling or child link is invalid, loops, shares a subtree, or the siblings are not in name order ([MS-CFB] 2.6.4).</summary>
    BadLink = 6,

    /// <summary>An entry cannot be reached from the root; it was attached to the root.</summary>
    OrphanedEntry = 7,

    /// <summary>A stream's sector chain is shorter than its size, leaves the file, loops, or shares sectors with another chain; the stream was shortened.</summary>
    DamagedStream = 8,
}

/// <summary>One thing recovery skipped, fixed or found damaged in a compound file. See <see cref="CompoundFileReaderOptions.Recovery"/>.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Offset">The file offset of the field or structure concerned.</param>
/// <param name="Length">The bytes it covers; zero when it marks a place.</param>
/// <param name="StreamId">The directory entry concerned (<see cref="CfbEntry.StreamId"/>), or -1 when no entry is involved.</param>
/// <param name="EntryName">The stored name of that entry (<see cref="CfbEntry.RawName"/>); <see langword="null"/> when no entry is involved.</param>
/// <param name="Reason">A short description of what was expected and not found.</param>
public readonly record struct CfbDamage(CfbDamageKind Kind, long Offset, long Length, int StreamId, string? EntryName, string Reason);

/// <summary>Options for reading compound files.</summary>
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

    /// <summary>
    /// Gets a value indicating whether the reader recovers what it can from a damaged file. Default false: a file that breaks
    /// [MS-CFB] is refused with <see cref="RecursiveAI.OneCompress.CompressionFormatException"/>, exactly as without this option.
    /// When true, a file that passes the strict checks is read as usual and <see cref="CompoundFileReader.Damage"/> stays empty; otherwise the reader
    /// opens what it can and lists what it skipped or fixed. Recovery covers a file cut short, FAT/DIFAT/mini FAT entries pointing outside the file
    /// or at sectors another chain uses, chains that loop or end early, header fields that disagree with the file, directory entries with invalid fields,
    /// broken or looping links (entries that become unreachable are attached to the root) and a directory chain that is lost (directory sectors are found
    /// by scanning for the root entry). A stream whose sectors cannot be followed to its declared size is shortened to what can be read and flagged
    /// (<see cref="CfbEntry.IsDamaged"/>). Sectors are never shared between recovered chains, so recovered output never exceeds the file's size.
    /// Not repaired: a file with no usable header, no FAT, or no directory at all still raises <see cref="RecursiveAI.OneCompress.CompressionFormatException"/>.
    /// All limits still apply.
    /// </summary>
    public bool Recovery { get; init; }

    /// <summary>
    /// Gets the most bytes recovery may examine while searching for lost directory sectors, in total. Default 1 GiB. The search reads each sector at most once,
    /// so this bounds its work. Exceeding it raises <see cref="RecursiveAI.OneCompress.CompressionLimitException"/>. Only used when <see cref="Recovery"/> is true.
    /// </summary>
    public long MaxRecoveryBytes { get; init; } = 1L << 30;
}
