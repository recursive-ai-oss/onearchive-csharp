using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneArchive.Cpio;

/// <summary>The cpio header variants OneArchive reads and writes.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum CpioFormat
{
    /// <summary>The portable ASCII format (<c>070707</c>, POSIX "odc"): 76-byte headers of octal fields, no padding.</summary>
    Odc = 0,

    /// <summary>The "new ASCII" format (<c>070701</c>): 110-byte headers of hexadecimal fields, names and data padded to 4 bytes.</summary>
    Newc = 1,

    /// <summary>The newc layout with a byte-sum checksum of the data (<c>070702</c>).</summary>
    Crc = 2,
}

/// <summary>A cpio member with every header field.</summary>
/// <remarks>
/// <see cref="ArchiveEntry.Size"/> is the content size the entry yields; it is zero for everything but regular files.
/// <see cref="ArchiveEntry.Mode"/> keeps the twelve permission bits and <see cref="RawMode"/> the whole stored mode.
/// A socket is reported as <see cref="ArchiveEntryKind.Fifo"/> (it is never created either way); <see cref="RawMode"/> tells them apart.
/// For a symbolic link the stored data is the target, reported as <see cref="ArchiveEntry.LinkTarget"/>.
/// </remarks>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CpioEntry : ArchiveEntry
{
    /// <summary>Gets the header variant this entry was read with, or should be written with when passed to a writer that uses it.</summary>
    public CpioFormat Format { get; init; }

    /// <summary>Gets the offset of the header from the start of the archive. Zero for entries to write.</summary>
    public long HeaderOffset { get; init; }

    /// <summary>Gets the offset of the stored data from the start of the archive. Zero for entries to write.</summary>
    public long DataOffset { get; init; }

    /// <summary>Gets the size the header states (the size of the stored data, including a symbolic link's target).</summary>
    public long StoredSize { get; init; }

    /// <summary>Gets the inode number. When writing, zero means "take the next number of the writer's counter".</summary>
    public long Inode { get; init; }

    /// <summary>Gets the link count the header states. When writing, zero means 2 for a directory and 1 for anything else.</summary>
    public long NLink { get; init; }

    /// <summary>Gets the major number of the device the file lived on (newc, crc) or the whole device number (odc).</summary>
    public long FileSystemDeviceMajor { get; init; }

    /// <summary>Gets the minor number of the device the file lived on (newc, crc); zero for odc.</summary>
    public long FileSystemDeviceMinor { get; init; }

    /// <summary>Gets the complete stored mode: file type bits and permission bits.</summary>
    public uint RawMode { get; init; }

    /// <summary>Gets the byte sum the header states (crc format; zero otherwise). The writer computes it and ignores this value.</summary>
    public uint Checksum { get; init; }

    /// <summary>
    /// Gets a value saying this header is one of several hard links to a file (newc and crc store a file with a link
    /// count above one once, with the last of its names) and states no data itself.
    /// </summary>
    public bool IsLinkStub { get; init; }

    /// <summary>
    /// Gets a value saying the content of this entry comes from a later member that holds the data of the same file.
    /// Only a reader on a seekable stream can do that; on a forward-only stream a stub reads as empty.
    /// </summary>
    public bool ContentFromLaterLink { get; init; }
}

/// <summary>Options for <see cref="CpioReader"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CpioReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static CpioReaderOptions Default { get; } = new();

    /// <summary>Gets the limits applied while reading. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets a value saying whether the byte sum of a crc-format entry is checked once its data has been read from the
    /// start to the end. Default: <see langword="true"/>. Entries that are skipped or read by seeking are not checked.
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets a value saying whether the archive must end with the <c>TRAILER!!!</c> entry. Default: <see langword="true"/>,
    /// so an archive cut at an entry boundary is an error and not a shorter archive.
    /// </summary>
    public bool RequireTrailer { get; init; } = true;

    /// <summary>
    /// Gets a value saying whether a reader on a seekable stream gives the content of a newc or crc hard-link stub by
    /// finding the member that holds the data. Default: <see langword="true"/>. When off, a stub reads as empty.
    /// </summary>
    public bool ResolveLinkStubs { get; init; } = true;
}

/// <summary>Options for <see cref="CpioWriter"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class CpioWriterOptions
{
    /// <summary>The default block size of the padding after the trailer.</summary>
    public const int DefaultBlockSize = 512;

    /// <summary>The default most data that the crc format buffers to learn the checksum of content that cannot seek.</summary>
    public const long DefaultMaxBufferedSize = 64L << 20;

    /// <summary>Gets the default options.</summary>
    public static CpioWriterOptions Default { get; } = new();

    /// <summary>Gets the header variant. Default: <see cref="CpioFormat.Newc"/>.</summary>
    public CpioFormat Format { get; init; } = CpioFormat.Newc;

    /// <summary>
    /// Gets the block size the archive is padded to with zero bytes after the trailer. Default 512, which is what
    /// GNU cpio writes. 1 means no padding.
    /// </summary>
    public int BlockSize { get; init; } = DefaultBlockSize;

    /// <summary>
    /// Gets the most content that is held in memory to learn a crc-format checksum when the content stream cannot seek
    /// (the header comes before the data). Default 64 MiB; seekable content is read twice instead.
    /// </summary>
    public long MaxBufferedSize { get; init; } = DefaultMaxBufferedSize;
}
