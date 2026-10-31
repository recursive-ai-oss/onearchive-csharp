
namespace RecursiveAI.OneArchive.Rar;

/// <summary>The operating system that created an entry (RAR 5.0 technote, "File header and service header", Host OS).</summary>
public enum RarHostOs
{
    /// <summary>Windows; <see cref="RarEntry.Attributes"/> holds DOS-style attribute bits.</summary>
    Windows = 0,

    /// <summary>Unix; <see cref="RarEntry.Attributes"/> holds the <c>st_mode</c> value.</summary>
    Unix = 1,
}

/// <summary>The file-system redirection an entry carries (technote, "File system redirection record").</summary>
public enum RarRedirectionType
{
    /// <summary>The entry has no redirection.</summary>
    None = 0,

    /// <summary>A Unix symbolic link.</summary>
    UnixSymlink = 1,

    /// <summary>A Windows symbolic link.</summary>
    WindowsSymlink = 2,

    /// <summary>A Windows junction.</summary>
    WindowsJunction = 3,

    /// <summary>A hard link to another entry.</summary>
    HardLink = 4,

    /// <summary>A copy of another entry's file, stored without data.</summary>
    FileCopy = 5,
}

/// <summary>Why a RAR archive or entry cannot be read yet.</summary>
public enum RarUnsupportedKind
{
    /// <summary>The entry is compressed (method 1 to 5); only stored entries can be extracted.</summary>
    CompressedEntry = 0,

    /// <summary>The archive uses the RAR 4 container format.</summary>
    Rar4Archive = 1,

    /// <summary>The archive's headers are encrypted.</summary>
    EncryptedHeaders = 2,

    /// <summary>The entry's data is encrypted.</summary>
    EncryptedEntry = 3,
}

/// <summary>The error for RAR content that is recognised but cannot be read yet.</summary>
/// <remarks>
/// The entry model and the reader are complete for listing; only the decompressor for methods 1 to 5, the RAR 4 container and
/// AES-256 encryption are missing. Catch <see cref="ArchiveUnsupportedException"/> to handle all unsupported features alike.
/// </remarks>
public sealed class RarUnsupportedException : ArchiveUnsupportedException
{
    /// <summary>Creates the error.</summary>
    /// <param name="kind">What is unsupported.</param>
    /// <param name="message">The complete message.</param>
    /// <param name="entryName">The entry concerned, or <see langword="null"/>.</param>
    /// <param name="method">The compression method of the entry (0 to 5), or -1.</param>
    /// <param name="dictionarySize">The dictionary size the entry needs, or 0.</param>
    public RarUnsupportedException(RarUnsupportedKind kind, string message, string? entryName = null, int method = -1, long dictionarySize = 0)
        : base("rar", message, "rar: " + message)
    {
        Kind = kind;
        EntryName = entryName;
        Method = method;
        DictionarySize = dictionarySize;
    }

    /// <summary>Creates the error with a default message.</summary>
    public RarUnsupportedException()
        : this(RarUnsupportedKind.CompressedEntry, "RAR compressed entries are not supported yet")
    {
    }

    /// <summary>Creates the error with a message.</summary>
    /// <param name="message">The message.</param>
    public RarUnsupportedException(string message)
        : this(RarUnsupportedKind.CompressedEntry, message)
    {
    }

    /// <summary>Creates the error with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public RarUnsupportedException(string message, Exception? innerException)
        : this(RarUnsupportedKind.CompressedEntry, message)
    {
        _ = innerException;
    }

    /// <summary>Gets what is unsupported.</summary>
    public RarUnsupportedKind Kind { get; }

    /// <summary>Gets the name of the entry concerned, if the error is about one entry.</summary>
    public string? EntryName { get; }

    /// <summary>Gets the compression method (0 to 5) of the entry, or -1.</summary>
    public int Method { get; }

    /// <summary>Gets the dictionary size in bytes the entry declares, or 0.</summary>
    public long DictionarySize { get; }
}

/// <summary>
/// The archive-level header of a RAR 5 archive (technote, "Main archive header") and what its extra area and the optional
/// comment service header add.
/// </summary>
public sealed class RarArchiveHeader
{
    /// <summary>Gets the offset of the archive signature from the start of the stream; non-zero when a self-extracting module precedes it.</summary>
    public long SignatureOffset { get; init; }

    /// <summary>Gets the archive flags as stored (0x1 volume, 0x2 volume number present, 0x4 solid, 0x8 recovery record, 0x10 locked).</summary>
    public int ArchiveFlags { get; init; }

    /// <summary>Gets a value indicating whether the archive is part of a multi-volume set.</summary>
    public bool IsVolume => (ArchiveFlags & 0x1) != 0;

    /// <summary>Gets a value indicating whether the archive was created as solid (each file may depend on the ones before it).</summary>
    public bool IsSolid => (ArchiveFlags & 0x4) != 0;

    /// <summary>Gets a value indicating whether the archive carries a recovery record.</summary>
    public bool HasRecoveryRecord => (ArchiveFlags & 0x8) != 0;

    /// <summary>Gets a value indicating whether the archive is locked against changes.</summary>
    public bool IsLocked => (ArchiveFlags & 0x10) != 0;

    /// <summary>Gets the zero-based volume number: 0 for a single archive and for the first volume.</summary>
    public long VolumeNumber { get; init; }

    /// <summary>
    /// Gets the distance from the quick-open service block back to this header, from the locator record, or <see langword="null"/>
    /// when the record is absent or the stored value is 0 (not filled in). The reader does not use quick-open data: it reads the
    /// headers in order, so the names listed are the names extracted.
    /// </summary>
    public long? QuickOpenOffset { get; init; }

    /// <summary>Gets the distance from the recovery record service block back to this header, from the locator record, or <see langword="null"/>.</summary>
    public long? RecoveryRecordOffset { get; init; }

    /// <summary>Gets the original archive name from the metadata record, truncated at its first zero byte, or <see langword="null"/>.</summary>
    public string? OriginalName { get; init; }

    /// <summary>Gets the original creation time of the archive from the metadata record, or <see langword="null"/>.</summary>
    public DateTimeOffset? OriginalCreationTime { get; init; }

    /// <summary>Gets the archive comment (UTF-8, from the <c>CMT</c> service header), or <see langword="null"/> if there is none.</summary>
    public string? Comment { get; init; }
}

/// <summary>A RAR 5 file entry with the fields of its file header and extra area.</summary>
/// <remarks>
/// <para><see cref="ArchiveEntry.Size"/> is the unpacked size. For an entry whose file header says the size is unknown
/// (<see cref="SizeIsUnknown"/>) it is the size of the first part's data. Directories, links and file copies have size 0.</para>
/// <para>An entry that continues in the next volume keeps the values of its first header. Its <see cref="Crc32"/> and
/// <see cref="Blake2sp"/> are <see langword="null"/>: the stored values cover only that volume's data, and the value for the whole
/// file is in the header of the last part.</para>
/// </remarks>
public sealed class RarEntry : ArchiveEntry
{
    /// <summary>Gets the operating system that created the entry; it decides how <see cref="Attributes"/> and the name are read.</summary>
    public RarHostOs HostOs { get; init; }

    /// <summary>Gets the host-specific attribute word as stored.</summary>
    public ulong Attributes { get; init; }

    /// <summary>Gets the compression method: 0 is stored, 1 (fastest) to 5 (best) are compressed.</summary>
    public int Method { get; init; }

    /// <summary>Gets the version of the compression algorithm (0 or 1); RAR 7 writes 1 only for dictionaries above 4 GB.</summary>
    public int CompressionVersion { get; init; }

    /// <summary>Gets a value indicating whether the entry continues the compression dictionary of the entries before it.</summary>
    public bool IsSolid { get; init; }

    /// <summary>Gets the dictionary size in bytes that extraction needs.</summary>
    public long DictionarySize { get; init; }

    /// <summary>Gets a value indicating whether the entry's data is stored as is, so it can be extracted without a decompressor.</summary>
    public bool IsStored => Method == 0;

    /// <summary>Gets the size of the data area of the first part (the whole packed size unless the entry spans volumes).</summary>
    public long PackedSize { get; init; }

    /// <summary>Gets a value indicating whether the header says the unpacked size is not known.</summary>
    public bool SizeIsUnknown { get; init; }

    /// <summary>Gets the CRC-32 of the unpacked data, if the header has one and covers the whole file.</summary>
    public uint? Crc32 { get; init; }

    /// <summary>Gets the 32-byte BLAKE2sp hash of the unpacked data from the file hash record, if present and covering the whole file.</summary>
    public ReadOnlyMemory<byte>? Blake2sp { get; init; }

    /// <summary>Gets the creation time from the file time record, if stored.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Gets the redirection kind of the entry (symbolic link, hard link, ...), or <see cref="RarRedirectionType.None"/>.</summary>
    public RarRedirectionType Redirection { get; init; }

    /// <summary>Gets a value indicating whether a link entry's target is a directory.</summary>
    public bool RedirectionTargetIsDirectory { get; init; }

    /// <summary>Gets the file version number from the file version record, or <see langword="null"/>.</summary>
    public long? FileVersion { get; init; }

    /// <summary>Gets a value indicating whether the entry's data is encrypted.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Gets the zero-based number of the volume holding the entry's first part.</summary>
    public int FirstVolume { get; init; }

    /// <summary>Gets a value indicating whether the entry's data continues in the next volume.</summary>
    public bool ContinuesInNextVolume { get; init; }

    /// <summary>Gets the offset of the file header in its volume, from the start of the volume's stream (so a self-extracting module counts).</summary>
    public long HeaderOffset { get; init; }

    /// <summary>Gets the offset of the first part's data in its volume, from the start of the volume's stream.</summary>
    public long DataOffset { get; init; }
}

/// <summary>Supplies the volumes after the first of a multi-volume set.</summary>
/// <param name="volumeNumber">The zero-based number of the volume wanted; the first volume (0) is the one the reader was given.</param>
/// <returns>A readable stream positioned at the start of the volume, or <see langword="null"/> when the set has no such part.
/// The reader owns the stream and disposes it when it moves on.</returns>
public delegate Stream? RarVolumeProvider(int volumeNumber);

/// <summary>Options for <see cref="RarReader"/>.</summary>
public sealed class RarReaderOptions
{
    /// <summary>Gets the shared default options.</summary>
    public static RarReaderOptions Default { get; } = new();

    /// <summary>Gets the limits applied to the entries. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets a value indicating whether the CRC-32 or BLAKE2sp of stored data is checked when an entry is read to its end. Default true.
    /// A mismatch raises <see cref="RecursiveAI.OneCompress.CompressionFormatException"/>. Header checksums are always checked.
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the function that supplies later volumes of a multi-volume set. Default <see langword="null"/>: a set that goes on past
    /// the stream raises <see cref="RecursiveAI.OneCompress.CompressionFormatException"/> (truncated) where the next volume is needed.
    /// <see cref="Archive"/> sets this from the file name when it opens a path.
    /// </summary>
    public RarVolumeProvider? Volumes { get; init; }

    /// <summary>
    /// Gets the longest self-extracting module the reader skips to find the signature. Default 1 MiB, the largest the technote mentions.
    /// Zero means the signature must be at the start.
    /// </summary>
    public int MaxSfxSize { get; init; } = 1 << 20;
}
