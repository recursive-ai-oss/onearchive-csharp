
namespace RecursiveAI.OneArchive;

/// <summary>
/// One member of an archive, as every container reports it. A container's own reader may return a derived type
/// (<c>TarEntry</c>) that also exposes the raw header fields.
/// </summary>
/// <remarks>
/// Names use <c>/</c> as the separator and carry no trailing slash; <see cref="Kind"/> says whether the entry is a
/// directory. Names are exactly as the archive stores them: readers do not remove <c>..</c>, a leading <c>/</c> or
/// anything else. Extraction applies the path rules (see <see cref="ArchiveExtractOptions"/>).
/// Properties a format cannot store are <see langword="null"/> (or zero) when read and are left out when written.
/// </remarks>
public class ArchiveEntry
{
    private static readonly IReadOnlyDictionary<string, string> s_noExtras = new Dictionary<string, string>();

    /// <summary>Gets the path of the entry inside the archive, <c>/</c>-separated, without a trailing slash.</summary>
    public required string Name { get; init; }

    /// <summary>Gets what the entry is. Defaults to <see cref="ArchiveEntryKind.File"/>.</summary>
    public ArchiveEntryKind Kind { get; init; }

    /// <summary>
    /// Gets the size of the content in bytes. For a sparse file this is the full size with the holes; zero for
    /// everything except regular files.
    /// </summary>
    public long Size { get; init; }

    /// <summary>Gets the modification time, or <see langword="null"/> if the archive has none. Precision is 100 ns.</summary>
    public DateTimeOffset? ModifiedTime { get; init; }

    /// <summary>Gets the access time if the archive records one.</summary>
    public DateTimeOffset? AccessTime { get; init; }

    /// <summary>Gets the status-change time if the archive records one.</summary>
    public DateTimeOffset? ChangeTime { get; init; }

    /// <summary>Gets the Unix permission bits (including setuid, setgid and sticky), or <see langword="null"/>.</summary>
    public UnixFileMode? Mode { get; init; }

    /// <summary>Gets the numeric owner. Zero when the archive has none.</summary>
    public long Uid { get; init; }

    /// <summary>Gets the numeric group. Zero when the archive has none.</summary>
    public long Gid { get; init; }

    /// <summary>Gets the owner's name, if recorded.</summary>
    public string? UserName { get; init; }

    /// <summary>Gets the group's name, if recorded.</summary>
    public string? GroupName { get; init; }

    /// <summary>Gets the target of a symbolic or hard link; <see langword="null"/> for other kinds.</summary>
    public string? LinkTarget { get; init; }

    /// <summary>Gets the device major number of a device entry.</summary>
    public long DeviceMajor { get; init; }

    /// <summary>Gets the device minor number of a device entry.</summary>
    public long DeviceMinor { get; init; }

    /// <summary>
    /// Gets format-specific attributes that have no property above, by key. For tar these are the pax records that
    /// are not otherwise modelled (for example <c>SCHILY.xattr.user.name</c>). A writer emits them, sorted by key.
    /// </summary>
    public IReadOnlyDictionary<string, string> Extras { get; init; } = s_noExtras;
}
