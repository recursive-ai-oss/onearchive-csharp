using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneArchive;

/// <summary>The container formats OneArchive can read or write. More members are added as containers land.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ArchiveFormat
{
    /// <summary>The format is not known.</summary>
    Unknown = 0,

    /// <summary>tar: ustar, pax and GNU variants (see <c>RecursiveAI.OneArchive.Tar</c>).</summary>
    Tar = 1,

    /// <summary>zip (see <c>RecursiveAI.OneArchive.Zip</c>): zip64, data descriptors, store, deflate and zstd entries.</summary>
    Zip = 2,

    /// <summary>One compressed file with no container, such as <c>notes.txt.gz</c>, <c>.zst</c> or <c>.lz4</c>: an archive of one entry.</summary>
    SingleStream = 3,

    /// <summary>7z (see <c>RecursiveAI.OneArchive.SevenZip</c>): read only for now.</summary>
    SevenZip = 4,

    /// <summary>cpio (see <c>RecursiveAI.OneArchive.Cpio</c>): newc, crc and odc, read and write.</summary>
    Cpio = 5,

    /// <summary>ar (see <c>RecursiveAI.OneArchive.Ar</c>): common, GNU and BSD variants, read and write; also <c>.deb</c> packages.</summary>
    Ar = 6,

    /// <summary>Microsoft cabinet (see <c>RecursiveAI.OneArchive.Cab</c>): read only; stored, MSZIP and LZX folders.</summary>
    Cab = 7,

    /// <summary>ISO 9660 (see <c>RecursiveAI.OneArchive.Iso</c>): the primary hierarchy with Rock Ridge, and Joliet, read and write.</summary>
    Iso = 8,
}

/// <summary>A compression layer wrapped around a whole archive, as in <c>.tar.zst</c>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ArchiveCompression
{
    /// <summary>The archive is stored as is.</summary>
    None = 0,

    /// <summary>Zstandard (<c>.zst</c>).</summary>
    Zstd = 1,

    /// <summary>LZ4 frame format (<c>.lz4</c>).</summary>
    Lz4 = 2,

    /// <summary>gzip (<c>.gz</c>). Read and written.</summary>
    Gzip = 3,

    /// <summary>bzip2 (<c>.bz2</c>). Read and written.</summary>
    Bzip2 = 4,

    /// <summary>xz (<c>.xz</c>). Read and written; a read is limited by <see cref="ArchiveLimits.MaxWindowSize"/> (the xz dictionary size).</summary>
    Xz = 5,

    /// <summary>lzip (<c>.lz</c>). Read and written; a read is limited by <see cref="ArchiveLimits.MaxWindowSize"/> (the lzip dictionary size).</summary>
    Lzip = 6,

    /// <summary>
    /// Brotli (<c>.br</c>). Read and written. RFC 7932 defines no magic number, so brotli is chosen only from a file name
    /// (<c>.br</c>, <c>.tar.br</c>, <c>.tbr</c>) or <see cref="ArchiveOpenOptions.Compression"/>, never guessed from content. A read is
    /// limited by <see cref="ArchiveLimits.MaxWindowSize"/>; writing uses quality 5 and a 4 MiB window unless a level is given.
    /// </summary>
    Brotli = 7,
}

/// <summary>What kind of filesystem object an archive entry describes.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ArchiveEntryKind
{
    /// <summary>A regular file; its data is the entry's content.</summary>
    File = 0,

    /// <summary>A directory.</summary>
    Directory = 1,

    /// <summary>A symbolic link; <see cref="ArchiveEntry.LinkTarget"/> is the path it points to.</summary>
    Symlink = 2,

    /// <summary>A hard link to another entry of the same archive; <see cref="ArchiveEntry.LinkTarget"/> names it.</summary>
    HardLink = 3,

    /// <summary>A character device node.</summary>
    CharacterDevice = 4,

    /// <summary>A block device node.</summary>
    BlockDevice = 5,

    /// <summary>A named pipe.</summary>
    Fifo = 6,
}
