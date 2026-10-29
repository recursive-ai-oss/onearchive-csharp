using System.Diagnostics.CodeAnalysis;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneArchive.Cpio;
using RecursiveAI.OneArchive.Iso;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive;

/// <summary>Options for <see cref="Archive.Open(string, ArchiveOpenOptions?)"/> and everything built on it.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public class ArchiveOpenOptions
{
    /// <summary>Gets the limits applied while reading. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets a name whose extension helps detection when the data has no recognisable magic bytes (an empty tar, for
    /// example). Used for stream sources; a path source uses its own name. Default none.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>Gets the container format to use instead of detecting it. Default <see langword="null"/>: detect.</summary>
    public ArchiveFormat? Format { get; init; }

    /// <summary>Gets the compression layer to use instead of detecting it. Default <see langword="null"/>: detect.</summary>
    public ArchiveCompression? Compression { get; init; }

    /// <summary>Gets a value indicating whether a stream passed to <c>Archive.Open</c> stays open after the archive is disposed. Default false. Streams the library opens itself are always closed.</summary>
    public bool LeaveOpen { get; init; }

    /// <summary>Gets the tar reading options (the limits above override <see cref="TarReaderOptions.Limits"/>). Default: the tar defaults.</summary>
    public TarReaderOptions? Tar { get; init; }

    /// <summary>Gets the cpio reading options (the limits above override <see cref="CpioReaderOptions.Limits"/>). Default: the cpio defaults.</summary>
    public CpioReaderOptions? Cpio { get; init; }

    /// <summary>Gets the cabinet reading options (the limits above override <see cref="CabReaderOptions.Limits"/>). Default: the cabinet defaults.</summary>
    public CabReaderOptions? Cab { get; init; }

    /// <summary>Gets the ISO 9660 reading options (the limits above override <see cref="IsoReaderOptions.Limits"/>). Default: the ISO 9660 defaults.</summary>
    public IsoReaderOptions? Iso { get; init; }

    /// <summary>Gets the ar reading options (the limits above override <see cref="ArReaderOptions.Limits"/>). Default: the ar defaults.</summary>
    public ArReaderOptions? Ar { get; init; }

    /// <summary>
    /// Gets the password for encrypted zip and 7z archives; it overrides the one in <see cref="Zip"/> and <see cref="SevenZip"/> when set.
    /// Default empty. A missing or wrong password raises <see cref="ArchivePasswordException"/>.
    /// </summary>
    public ArchivePassword Password { get; init; }

    /// <summary>Gets the zip reading options (the limits above override <see cref="ZipReaderOptions.Limits"/>). Default: the zip defaults.</summary>
    public ZipReaderOptions? Zip { get; init; }

    /// <summary>Gets the RAR reading options (the limits above override <see cref="RarReaderOptions.Limits"/>). Default: the RAR defaults; for a path source the later volumes of a set are found from the file name.</summary>
    public RarReaderOptions? Rar { get; init; }

    /// <summary>Gets the 7z reading options (the limits above override <see cref="SevenZipReaderOptions.Limits"/>). Default: the 7z defaults.</summary>
    public SevenZipReaderOptions? SevenZip { get; init; }
}

/// <summary>What to do with an entry whose path is absolute, contains <c>..</c>, a drive letter or other unsafe parts.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum UnsafePathHandling
{
    /// <summary>Throw <see cref="ArchiveSafetyException"/>. The default.</summary>
    Reject = 0,

    /// <summary>
    /// Rewrite the path so that it stays inside the target: drive letters and leading separators are dropped and
    /// <c>.</c> and <c>..</c> components are removed. Names containing NUL are still rejected.
    /// </summary>
    Sanitize = 1,
}

/// <summary>What to do with link entries.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum LinkPolicy
{
    /// <summary>
    /// Create links whose target stays inside the target directory and throw <see cref="ArchiveSafetyException"/> for
    /// the others. The default.
    /// </summary>
    Safe = 0,

    /// <summary>Ignore link entries.</summary>
    Skip = 1,

    /// <summary>
    /// Create symbolic links whatever they point at (absolute targets and <c>..</c> included). Writing through a
    /// symbolic link stays refused unless <see cref="ArchiveExtractOptions.AllowWriteThroughSymlinks"/> is set.
    /// Hard links keep the <see cref="Safe"/> rules, because a hard link is extracted as a copy of a file the
    /// extraction itself wrote.
    /// </summary>
    Unsafe = 2,
}

/// <summary>What to do with device, FIFO and other special entries.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum SpecialFilePolicy
{
    /// <summary>Throw <see cref="ArchiveSafetyException"/>. The default.</summary>
    Reject = 0,

    /// <summary>Ignore the entry. OneArchive never creates device nodes or FIFOs.</summary>
    Skip = 1,
}

/// <summary>What to do when an entry's destination already exists.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ExistingFilePolicy
{
    /// <summary>Throw <see cref="ArchiveSafetyException"/> (rule <c>existing-file</c>). The default. Directories may already exist.</summary>
    Fail = 0,

    /// <summary>Replace the file (or link) with the entry. A directory is never replaced.</summary>
    Overwrite = 1,

    /// <summary>Keep what exists and skip the entry.</summary>
    Skip = 2,
}

/// <summary>
/// Options for extraction. Everything that could write outside the target directory is refused unless an option here
/// says otherwise; the defaults are the safe ones.
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ArchiveExtractOptions : ArchiveOpenOptions
{
    /// <summary>Gets how unsafe paths are handled. Default <see cref="UnsafePathHandling.Reject"/>.</summary>
    public UnsafePathHandling UnsafePaths { get; init; }

    /// <summary>Gets how symbolic links are handled. Default <see cref="LinkPolicy.Safe"/>.</summary>
    public LinkPolicy Symlinks { get; init; }

    /// <summary>Gets how hard links are handled (extracted as copies). Default <see cref="LinkPolicy.Safe"/>.</summary>
    public LinkPolicy HardLinks { get; init; }

    /// <summary>
    /// Gets a value indicating whether an entry may be written under a path component that is a symbolic link (one
    /// from the archive or one that was already there). Default false: such an entry throws <see cref="ArchiveSafetyException"/>.
    /// </summary>
    public bool AllowWriteThroughSymlinks { get; init; }

    /// <summary>Gets how device, FIFO and other special entries are handled. Default <see cref="SpecialFilePolicy.Reject"/>.</summary>
    public SpecialFilePolicy SpecialFiles { get; init; }

    /// <summary>Gets how existing destinations are handled. Default <see cref="ExistingFilePolicy.Fail"/>.</summary>
    public ExistingFilePolicy Existing { get; init; }

    /// <summary>Gets a value indicating whether permission bits are applied (on platforms that have them). Default true.</summary>
    public bool RestorePermissions { get; init; } = true;

    /// <summary>Gets a value indicating whether setuid, setgid and sticky bits are kept. Default false: only the nine <c>rwx</c> bits are applied.</summary>
    public bool KeepSpecialModeBits { get; init; }

    /// <summary>Gets a value indicating whether modification times of files and directories are applied. Default true.</summary>
    public bool RestoreTimes { get; init; } = true;

    /// <summary>Gets a predicate that selects entries to extract; <see langword="null"/> extracts all. Skipped entries still count against the limits.</summary>
    public Func<ArchiveEntry, bool>? Filter { get; init; }
}

/// <summary>Options for <see cref="Archive.Create(string, ArchiveCreateOptions?)"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ArchiveCreateOptions
{
    /// <summary>Gets the container format. Default <see langword="null"/>: from the file extension, else tar.</summary>
    public ArchiveFormat? Format { get; init; }

    /// <summary>Gets the compression layer. Default <see langword="null"/>: from the file extension, else none.</summary>
    public ArchiveCompression? Compression { get; init; }

    /// <summary>
    /// Gets the compression level, in the numbering of the codec's reference tool. Default <see langword="null"/>:
    /// Zstandard 3, LZ4 1.
    /// </summary>
    public int? CompressionLevel { get; init; }

    /// <summary>
    /// Gets a value indicating whether the output is made reproducible regardless of the source files: entries are
    /// sorted by name (ordinal), times are set to <see cref="NormalizedTime"/>, owners to 0 with no names, and modes
    /// to 0644 (0755 for directories and files with an execute bit). Default false: times, owners and modes
    /// are what the caller or the file system gives, in the order the entries were added.
    /// </summary>
    public bool Normalize { get; init; }

    /// <summary>Gets the modification time used when <see cref="Normalize"/> is set. Default 1970-01-01T00:00:00Z.</summary>
    public DateTimeOffset NormalizedTime { get; init; } = DateTimeOffset.UnixEpoch;

    /// <summary>Gets a value indicating whether symbolic links found while adding a directory are followed (their target's content stored) instead of stored as links. Default false.</summary>
    public bool FollowSymlinks { get; init; }

    /// <summary>Gets a value indicating whether a stream passed to <c>Archive.Create</c> stays open after <c>Save</c>. Default false.</summary>
    public bool LeaveOpen { get; init; }

    /// <summary>
    /// Gets the password for zip and 7z output; it overrides the one in <see cref="Zip"/> and <see cref="SevenZip"/> when set.
    /// Zip entries are encrypted with WinZip AES-256, 7z folders and header with AES-256. Other formats cannot be encrypted
    /// and raise <see cref="ArgumentException"/> when a password is given. Default empty.
    /// </summary>
    public ArchivePassword Password { get; init; }

    /// <summary>Gets the tar writing options. Default: the tar defaults (pax).</summary>
    public TarWriterOptions? Tar { get; init; }

    /// <summary>Gets the cpio writing options. Default: newc with 512-byte blocks.</summary>
    public CpioWriterOptions? Cpio { get; init; }

    /// <summary>Gets the ISO 9660 writing options. Default: level 3 with Joliet and Rock Ridge.</summary>
    public IsoWriterOptions? Iso { get; init; }

    /// <summary>Gets the ar writing options. Default: the GNU variant.</summary>
    public ArWriterOptions? Ar { get; init; }

    /// <summary>Gets the zip writing options (<see cref="CompressionLevel"/>, when set, overrides <see cref="ZipWriterOptions.Level"/>). Default: the zip defaults (Deflate).</summary>
    public ZipWriterOptions? Zip { get; init; }

    /// <summary>Gets the 7z writing options (<see cref="CompressionLevel"/>, when set, overrides <see cref="SevenZipWriterOptions.Level"/>). Default: the 7z defaults (LZMA2, level 5, solid).</summary>
    public SevenZipWriterOptions? SevenZip { get; init; }
}
