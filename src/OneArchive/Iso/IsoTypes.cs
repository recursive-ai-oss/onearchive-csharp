
namespace RecursiveAI.OneArchive.Iso;

/// <summary>Which directory hierarchy of an ISO 9660 image a reader lists or an entry came from.</summary>
public enum IsoTree
{
    /// <summary>
    /// For <see cref="IsoReaderOptions.Tree"/>: read the primary hierarchy when it carries Rock Ridge, otherwise the Joliet
    /// hierarchy if the image has one, otherwise the primary hierarchy. Never reported on an entry.
    /// </summary>
    Auto = 0,

    /// <summary>The hierarchy of the primary volume descriptor (ECMA-119 clause 8.4), with its identifiers and, when present, Rock Ridge names.</summary>
    Primary = 1,

    /// <summary>The hierarchy of a Joliet supplementary volume descriptor (UCS-2 names, up to 64 characters).</summary>
    Joliet = 2,
}

/// <summary>The flags of an ISO 9660 directory record (ECMA-119 clause 9.1.6).</summary>
[Flags]
public enum IsoRecordAttributes : byte
{
    /// <summary>No flag is set.</summary>
    None = 0,

    /// <summary>The file is hidden (existence bit).</summary>
    Hidden = 0x01,

    /// <summary>The record describes a directory.</summary>
    Directory = 0x02,

    /// <summary>The file is an associated file.</summary>
    Associated = 0x04,

    /// <summary>The file has a record format in its extended attribute record.</summary>
    Record = 0x08,

    /// <summary>Owner and permissions are in the extended attribute record.</summary>
    Protection = 0x10,

    /// <summary>The file continues in the next directory record of the same name (multi-extent).</summary>
    MultiExtent = 0x80,
}

/// <summary>One extent of a file: a run of whole logical blocks (the last one may be partly used).</summary>
/// <param name="Location">The logical block number of the first block of data.</param>
/// <param name="Length">The number of data bytes in the extent.</param>
public readonly record struct IsoExtent(long Location, long Length);

/// <summary>The Rock Ridge (IEEE P1282) attributes of a directory record, as stored.</summary>
public sealed class IsoRockRidge
{
    /// <summary>Gets the whole POSIX file mode of the <c>PX</c> entry: file type bits and permission bits.</summary>
    public uint RawMode { get; init; }

    /// <summary>Gets the POSIX link count (<c>PX</c>).</summary>
    public long Links { get; init; }

    /// <summary>Gets the file serial number (<c>PX</c>, 44-byte form); zero if the entry has the 36-byte form. Records that share it are links.</summary>
    public long SerialNumber { get; init; }

    /// <summary>Gets the device number of a device file (<c>PN</c>): the high 32 bits and the low 32 bits joined.</summary>
    public ulong DeviceNumber { get; init; }

    /// <summary>Gets the creation time (<c>TF</c>), or <see langword="null"/>.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Gets the backup time (<c>TF</c>), or <see langword="null"/>.</summary>
    public DateTimeOffset? BackupTime { get; init; }

    /// <summary>Gets the expiration time (<c>TF</c>), or <see langword="null"/>.</summary>
    public DateTimeOffset? ExpirationTime { get; init; }

    /// <summary>Gets the effective time (<c>TF</c>), or <see langword="null"/>.</summary>
    public DateTimeOffset? EffectiveTime { get; init; }

    /// <summary>Gets a value indicating whether the record has an <c>RE</c> entry: it is the real position of a relocated directory.</summary>
    public bool IsRelocated { get; init; }

    /// <summary>Gets a value indicating whether the record is a <c>CL</c> placeholder for a directory that was moved elsewhere (the entry then describes the moved directory).</summary>
    public bool IsChildLink { get; init; }

    /// <summary>Gets the uncompressed size of a zisofs file (<c>ZF</c>), or <see langword="null"/> for an ordinary file.</summary>
    public long? ZisofsSize { get; init; }
}

/// <summary>An entry of an ISO 9660 image with the fields of its directory record.</summary>
/// <remarks>
/// <see cref="ArchiveEntry.Name"/> is the path in the hierarchy that was read: Rock Ridge names when the primary hierarchy carries
/// them, otherwise the Joliet or the ISO 9660 identifiers (version suffix <c>;1</c> and a trailing dot removed). <see cref="IsoPath"/>
/// always holds the identifiers of that hierarchy, so a Rock Ridge listing still shows the ISO 9660 name set, and
/// <see cref="Tree"/> says which hierarchy it is. <see cref="ArchiveEntry.Size"/> is the file's size (the sum of its extents);
/// symbolic links, devices and directories have size zero.
/// </remarks>
public sealed class IsoEntry : ArchiveEntry
{
    /// <summary>Gets the hierarchy this entry was read from.</summary>
    public IsoTree Tree { get; init; }

    /// <summary>Gets the path of identifiers in <see cref="Tree"/>, with <c>/</c> separators and no version suffixes.</summary>
    public string IsoPath { get; init; } = string.Empty;

    /// <summary>Gets the file identifier of the last path component as stored (decoded; Joliet identifiers from UCS-2), version suffix included.</summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>Gets the extents that hold the content. A file larger than 4 GiB has several; a directory has one.</summary>
    public IReadOnlyList<IsoExtent> Extents { get; init; } = [];

    /// <summary>Gets the logical block number of the first extent, or zero if there is none.</summary>
    public long ExtentLocation => Extents.Count > 0 ? Extents[0].Location : 0;

    /// <summary>Gets the directory record flags.</summary>
    public IsoRecordAttributes Flags { get; init; }

    /// <summary>Gets the offset of the directory record from the start of the image.</summary>
    public long RecordOffset { get; init; }

    /// <summary>Gets the Rock Ridge attributes, or <see langword="null"/> when the hierarchy has none.</summary>
    public IsoRockRidge? RockRidge { get; init; }

    /// <summary>Gets the recording time of the directory record (ECMA-119 clause 9.1.5), or <see langword="null"/> if it is not a valid date.</summary>
    public DateTimeOffset? RecordingTime { get; init; }

    /// <summary>Gets a value indicating whether the content is zisofs-compressed; reading it raises <see cref="ArchiveUnsupportedException"/>.</summary>
    public bool IsZisofs => RockRidge?.ZisofsSize is not null;
}

/// <summary>What the volume descriptors of an image say.</summary>
public sealed class IsoVolumeInfo
{
    /// <summary>Gets the volume identifier.</summary>
    public string VolumeId { get; init; } = string.Empty;

    /// <summary>Gets the system identifier.</summary>
    public string SystemId { get; init; } = string.Empty;

    /// <summary>Gets the volume set identifier.</summary>
    public string VolumeSetId { get; init; } = string.Empty;

    /// <summary>Gets the publisher identifier.</summary>
    public string PublisherId { get; init; } = string.Empty;

    /// <summary>Gets the data preparer identifier.</summary>
    public string PreparerId { get; init; } = string.Empty;

    /// <summary>Gets the application identifier.</summary>
    public string ApplicationId { get; init; } = string.Empty;

    /// <summary>Gets the volume creation time, or <see langword="null"/> when the descriptor says "not specified".</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Gets the volume modification time, or <see langword="null"/>.</summary>
    public DateTimeOffset? ModificationTime { get; init; }

    /// <summary>Gets the size of the volume in logical blocks, as the primary descriptor declares it.</summary>
    public long VolumeSpaceBlocks { get; init; }

    /// <summary>Gets a value indicating whether the image has a Joliet supplementary descriptor.</summary>
    public bool HasJoliet { get; init; }

    /// <summary>Gets the Joliet level (1 to 3), or zero.</summary>
    public int JolietLevel { get; init; }

    /// <summary>Gets a value indicating whether the primary hierarchy uses the System Use Sharing Protocol with Rock Ridge entries.</summary>
    public bool HasRockRidge { get; init; }

    /// <summary>Gets the extension identifiers of the <c>ER</c> entries in the root directory (for example <c>IEEE_1282</c>).</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Gets the block of the El Torito boot catalog, or <see langword="null"/> if the image has no boot record. Boot catalogs are listed, not interpreted.</summary>
    public long? BootCatalogBlock { get; init; }
}

/// <summary>Options for reading ISO 9660 images.</summary>
public sealed class IsoReaderOptions
{
    /// <summary>The options with every default.</summary>
    public static IsoReaderOptions Default { get; } = new();

    /// <summary>Gets the limits (entry count, total size, path length and depth). Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>Gets the hierarchy to list. Default <see cref="IsoTree.Auto"/>.</summary>
    public IsoTree Tree { get; init; }

    /// <summary>
    /// Gets a value indicating whether Rock Ridge entries are used (names, modes, owners, times, symbolic links, devices). Default true.
    /// Only the primary hierarchy carries them.
    /// </summary>
    public bool UseRockRidge { get; init; } = true;

    /// <summary>Gets the largest directory, in bytes, the reader will load. Default 16 MiB. Larger ones raise <see cref="RecursiveAI.OneCompress.CompressionLimitException"/>.</summary>
    public long MaxDirectorySize { get; init; } = 16L << 20;

    /// <summary>Gets the most system use bytes one directory record may have, counting its continuation areas. Default 64 KiB.</summary>
    public int MaxSystemUseSize { get; init; } = 64 * 1024;

    /// <summary>
    /// Gets the most bytes of SUSP continuation areas (<c>CE</c> entries) read over the whole image. Default 128 MiB. Records may
    /// point at the same area, so without a total a small image could make the reader parse gigabytes.
    /// </summary>
    public long MaxContinuationBytes { get; init; } = 128L << 20;
}

/// <summary>Options for writing ISO 9660 images.</summary>
/// <remarks>
/// The image depends only on the entries and these options: no clock is read. Volume dates are written as "not specified"
/// unless <see cref="VolumeTime"/> is set, and an entry without a modification time gets <see cref="DefaultTime"/>.
/// </remarks>
public sealed class IsoWriterOptions
{
    /// <summary>The options with every default.</summary>
    public static IsoWriterOptions Default { get; } = new();

    /// <summary>
    /// Gets the interchange level of the primary hierarchy (ECMA-119 clause 10): 1 (8.3 names, 8-character directories), 2 (names up
    /// to 30 characters) or 3 (as 2, and files of 4 GiB or more in several extents). Default 3: the real names travel in Rock
    /// Ridge and Joliet, so the level only decides how much the plain ISO 9660 names are cut and whether huge files are allowed.
    /// </summary>
    public int Level { get; init; } = 3;

    /// <summary>Gets a value indicating whether a Joliet hierarchy is written. Default true.</summary>
    public bool Joliet { get; init; } = true;

    /// <summary>Gets a value indicating whether Rock Ridge entries are written into the primary hierarchy. Default true.</summary>
    public bool RockRidge { get; init; } = true;

    /// <summary>Gets the volume identifier (up to 32 characters, d-characters). Default <c>CDROM</c>.</summary>
    public string VolumeId { get; init; } = "CDROM";

    /// <summary>Gets the system identifier (up to 32 a-characters). Default empty.</summary>
    public string SystemId { get; init; } = string.Empty;

    /// <summary>Gets the volume set identifier (up to 128 characters). Default empty.</summary>
    public string VolumeSetId { get; init; } = string.Empty;

    /// <summary>Gets the publisher identifier (up to 128 characters). Default empty.</summary>
    public string PublisherId { get; init; } = string.Empty;

    /// <summary>Gets the data preparer identifier (up to 128 characters). Default empty.</summary>
    public string PreparerId { get; init; } = string.Empty;

    /// <summary>Gets the application identifier (up to 128 characters). Default empty.</summary>
    public string ApplicationId { get; init; } = string.Empty;

    /// <summary>Gets the creation and modification time written to the volume descriptors. Default <see langword="null"/>: "not specified".</summary>
    public DateTimeOffset? VolumeTime { get; init; }

    /// <summary>Gets the time used for entries (and implied directories) that have none. Default 1970-01-01T00:00:00Z.</summary>
    public DateTimeOffset DefaultTime { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>Gets the most content bytes held in memory before the writer moves its spool to a temporary file. Default 64 MiB.</summary>
    public long MaxMemoryBuffer { get; init; } = 64L << 20;
}
