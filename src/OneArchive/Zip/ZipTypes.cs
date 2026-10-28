using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>A zip compression method id (PKWARE APPNOTE 4.4.5). Other values may be read from an archive and are listed but not extracted.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZipMethod : ushort
{
    /// <summary>Stored without compression.</summary>
    Store = 0,

    /// <summary>Deflate (RFC 1951).</summary>
    Deflate = 8,

    /// <summary>Deflate64 (APPNOTE 5.6): Deflate with a 64 KiB window and longer matches.</summary>
    Deflate64 = 9,

    /// <summary>bzip2. Read and written.</summary>
    BZip2 = 12,

    /// <summary>LZMA with the APPNOTE 5.8 header. Read only.</summary>
    Lzma = 14,

    /// <summary>Zstandard (RFC 8878), one frame per entry.</summary>
    Zstd = 93,

    /// <summary>xz. Read only.</summary>
    Xz = 95,

    /// <summary>PPMd. Listed, not yet supported.</summary>
    Ppmd = 98,

    /// <summary>WinZip AES encryption wrapper (the real method is inside the AES extra field). Listed, not yet supported.</summary>
    Aes = 99,
}

/// <summary>One record of an extra field area: a 16-bit header id and its data (APPNOTE 4.5.1).</summary>
/// <param name="Id">The header id, for example 0x5455 for the extended timestamp.</param>
/// <param name="Data">The record's data, without the id and size.</param>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly record struct ZipExtraField(ushort Id, ReadOnlyMemory<byte> Data);

/// <summary>A zip entry with the header fields the archive carried.</summary>
/// <remarks>
/// <see cref="ArchiveEntry.Size"/> is the uncompressed size. When the archive is read forward-only and the entry's sizes
/// follow its data in a data descriptor, the size is not known at this point: <see cref="IsSizeKnown"/> is false and
/// <see cref="ArchiveEntry.Size"/> is 0; the content stream still ends at the right place.
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZipEntry : ArchiveEntry
{
    private static readonly IReadOnlyList<ZipExtraField> s_noFields = [];

    /// <summary>Gets the compression method.</summary>
    public ZipMethod Method { get; init; }

    /// <summary>Gets the general purpose bit flag (APPNOTE 4.4.4).</summary>
    public ushort Flags { get; init; }

    /// <summary>Gets the CRC-32 of the uncompressed data as stored in the header. Zero while it is unknown (<see cref="IsSizeKnown"/> is false).</summary>
    public uint Crc32 { get; init; }

    /// <summary>Gets the compressed size in bytes. Zero while it is unknown (<see cref="IsSizeKnown"/> is false).</summary>
    public long CompressedSize { get; init; }

    /// <summary>Gets the "version made by" field: the host system in the high byte, the zip specification version times ten in the low byte.</summary>
    public ushort VersionMadeBy { get; init; }

    /// <summary>Gets the "version needed to extract" field.</summary>
    public ushort VersionNeeded { get; init; }

    /// <summary>Gets the host system that made the entry (0 for FAT, 3 for Unix, 10 for NTFS, and so on).</summary>
    public byte HostSystem => (byte)(VersionMadeBy >> 8);

    /// <summary>Gets the internal file attributes (bit 0: probably text).</summary>
    public ushort InternalAttributes { get; init; }

    /// <summary>Gets the external file attributes. For a Unix host the high 16 bits are the file mode, including the file type.</summary>
    public uint ExternalAttributes { get; init; }

    /// <summary>Gets the entry's comment (empty if none). Only the central directory carries comments.</summary>
    public string Comment { get; init; } = string.Empty;

    /// <summary>Gets the offset of the entry's local header from the start of the archive; -1 when the entry was read forward-only and the position is not tracked by the format.</summary>
    public long LocalHeaderOffset { get; init; }

    /// <summary>Gets the name exactly as stored, before decoding.</summary>
    public ReadOnlyMemory<byte> RawName { get; init; }

    /// <summary>Gets the records of the extra field (central directory copy when the archive was read from its directory, the local header's otherwise).</summary>
    public IReadOnlyList<ZipExtraField> ExtraFields { get; init; } = s_noFields;

    /// <summary>Gets a value indicating whether the name is flagged as UTF-8 (bit 11).</summary>
    public bool IsUtf8Name => (Flags & 0x800) != 0;

    /// <summary>Gets a value indicating whether the sizes and CRC follow the data in a data descriptor (bit 3).</summary>
    public bool HasDataDescriptor => (Flags & 8) != 0;

    /// <summary>Gets a value indicating whether the entry is encrypted (bit 0, strong encryption, or the AES wrapper method).</summary>
    public bool IsEncrypted => (Flags & 0x41) != 0 || Method == ZipMethod.Aes;

    /// <summary>Gets a value indicating whether the entry has a zip64 extended information field.</summary>
    public bool UsesZip64 { get; init; }

    /// <summary>Gets a value indicating whether <see cref="ArchiveEntry.Size"/> and <see cref="CompressedSize"/> are known when the entry is listed. False only for forward-only reads of entries with a data descriptor.</summary>
    public bool IsSizeKnown { get; init; } = true;

    // The first byte after this entry's data that belongs to something else (the next local header, or the central
    // directory). A local header whose data would run past it is an overlap.
    internal long Boundary { get; init; }

    // The uncompressed size the header declares, kept for entries (links, directories) whose public Size is zero.
    internal long DeclaredSize { get; init; }
}

/// <summary>Options for <see cref="ZipReader"/> and the zip part of <see cref="ArchiveOpenOptions"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZipReaderOptions
{
    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static ZipReaderOptions Default { get; } = new();

    /// <summary>Gets the limits applied while reading. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>Gets a value indicating whether entry data is checked against the CRC-32 and sizes in the headers. Default true. A mismatch raises <see cref="RecursiveAI.OneCompress.CompressionFormatException"/> when the last byte is read.</summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the reader walks local headers from the start even when the stream can seek.
    /// Default false: a seekable stream is read through its central directory, which is authoritative.
    /// </summary>
    public bool ForceStreaming { get; init; }

    /// <summary>
    /// Gets the most bytes of central directory the reader holds in memory (names, extra fields and comments of every entry).
    /// Default 128 MiB. A larger directory raises <see cref="RecursiveAI.OneCompress.CompressionLimitException"/>.
    /// </summary>
    public long MaxCentralDirectorySize { get; init; } = 128L << 20;

    /// <summary>Gets the encoding for names that are not flagged UTF-8 and have no Unicode path field. Default <see langword="null"/>: IBM code page 437, as APPNOTE 4.4.17 says.</summary>
    public Encoding? LegacyNameEncoding { get; init; }
}

/// <summary>When the writer uses zip64 structures.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZipZip64Mode
{
    /// <summary>Only where a value does not fit: an entry of 3.75 GiB or more gets a zip64 field in its local header; the directory gets zip64 fields for the values that overflow; the zip64 end record is written when the entry count is 65535 or more or an offset or size overflows.</summary>
    Auto = 0,

    /// <summary>Every local header carries a zip64 field and the zip64 end record is always written.</summary>
    Always = 1,

    /// <summary>Never; an entry or archive that does not fit raises <see cref="ArgumentException"/>.</summary>
    Never = 2,
}

/// <summary>Whether the writer puts sizes and CRC in the local header or after the data.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZipDataDescriptorMode
{
    /// <summary>Descriptors when the output cannot seek, and local headers patched in place when it can.</summary>
    Auto = 0,

    /// <summary>Always write a data descriptor after the data (bit 3). The bytes are the same for any output stream.</summary>
    Always = 1,

    /// <summary>Never; needs an output that can seek.</summary>
    Never = 2,
}

/// <summary>Options for <see cref="ZipWriter"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZipWriterOptions
{
    /// <summary>Gets the method for file entries with content. Default <see cref="ZipMethod.Deflate"/>. Store, Deflate, Deflate64 and Zstd can be written. Directories, links and empty files are stored.</summary>
    public ZipMethod Method { get; init; } = ZipMethod.Deflate;

    /// <summary>Gets the compression level in the numbering of the reference tool. Default <see langword="null"/>: Deflate 6, Deflate64 6, Zstandard 3. Deflate64 has levels 0 to 9; higher values mean 9.</summary>
    public int? Level { get; init; }

    /// <summary>Gets when zip64 structures are used. Default <see cref="ZipZip64Mode.Auto"/>.</summary>
    public ZipZip64Mode Zip64 { get; init; }

    /// <summary>Gets whether data descriptors are used. Default <see cref="ZipDataDescriptorMode.Auto"/>.</summary>
    public ZipDataDescriptorMode DataDescriptors { get; init; }

    /// <summary>Gets the archive comment, written UTF-8 encoded; at most 65535 bytes. Default none.</summary>
    public string? Comment { get; init; }
}
