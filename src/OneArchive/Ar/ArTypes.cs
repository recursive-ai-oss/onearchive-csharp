
namespace RecursiveAI.OneArchive.Ar;

/// <summary>The ar variants OneArchive writes.</summary>
public enum ArFormat
{
    /// <summary>
    /// The GNU variant: a name is stored as <c>name/</c> in the 16-byte field, and names that do not fit go to a <c>//</c>
    /// table of long names that comes first in the archive and is referred to as <c>/offset</c>. This is what GNU ar and
    /// <c>.deb</c> packages use.
    /// </summary>
    Gnu = 0,

    /// <summary>The BSD variant: names of up to 16 bytes without spaces are stored in the field, others as <c>#1/length</c> with the name at the start of the data.</summary>
    Bsd = 1,

    /// <summary>The common System V form: <c>name/</c> in the field with no long-name table. Names above 15 bytes cannot be written.</summary>
    Common = 2,
}

/// <summary>What an ar member is.</summary>
public enum ArMemberKind
{
    /// <summary>An ordinary file.</summary>
    Regular = 0,

    /// <summary>A symbol table (<c>/</c>, <c>/SYM64/</c>, <c>__.SYMDEF</c> and its variants). Its data is not interpreted.</summary>
    SymbolTable = 1,

    /// <summary>The GNU long-name table (<c>//</c>).</summary>
    LongNameTable = 2,
}

/// <summary>How a member's name was stored.</summary>
public enum ArNameStyle
{
    /// <summary>In the 16-byte header field (trailing spaces, and for the GNU variant a closing <c>/</c>, removed).</summary>
    Inline = 0,

    /// <summary>As <c>/offset</c> into the <c>//</c> long-name table.</summary>
    GnuTable = 1,

    /// <summary>As <c>#1/length</c>, with the name at the start of the member's data.</summary>
    BsdInline = 2,
}

/// <summary>An ar member with every header field.</summary>
/// <remarks>
/// <see cref="ArchiveEntry.Size"/> is the size of the content, without a BSD name that precedes it in the stored data.
/// <see cref="ArchiveEntry.Mode"/> keeps the twelve permission bits; <see cref="RawMode"/> is the octal field as stored.
/// ar has no directories: every entry is a regular file.
/// </remarks>
public sealed class ArEntry : ArchiveEntry
{
    /// <summary>Gets what the member is.</summary>
    public ArMemberKind MemberKind { get; init; }

    /// <summary>Gets how the name was stored.</summary>
    public ArNameStyle NameStyle { get; init; }

    /// <summary>Gets the offset of the 60-byte header from the start of the archive. Zero for entries to write.</summary>
    public long HeaderOffset { get; init; }

    /// <summary>Gets the offset of the content from the start of the archive. Zero for entries to write.</summary>
    public long DataOffset { get; init; }

    /// <summary>Gets the 16-byte name field as stored, with trailing spaces removed.</summary>
    public string RawName { get; init; } = string.Empty;

    /// <summary>Gets the whole mode field (octal), including file type bits as most tools write them (100644).</summary>
    public uint RawMode { get; init; }
}

/// <summary>Options for <see cref="ArReader"/>.</summary>
public sealed class ArReaderOptions
{
    /// <summary>The default most bytes of long-name table that are read into memory.</summary>
    public const int DefaultMaxNameTableSize = 8 << 20;

    /// <summary>Gets the default options.</summary>
    public static ArReaderOptions Default { get; } = new();

    /// <summary>Gets the limits applied while reading. Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets the most bytes of the GNU long-name table (<c>//</c>) read into memory. Default 8 MiB; a larger table raises
    /// <see cref="RecursiveAI.OneCompress.CompressionLimitException"/>.
    /// </summary>
    public int MaxNameTableSize { get; init; } = DefaultMaxNameTableSize;

    /// <summary>
    /// Gets a value saying whether symbol tables and the long-name table are returned as entries (named <c>/</c>, <c>//</c>,
    /// <c>__.SYMDEF</c>...). Default: <see langword="false"/>: they are read for their offsets and left out, since they are
    /// not files. Their names are not safe extraction paths.
    /// </summary>
    public bool IncludeSpecialMembers { get; init; }
}

/// <summary>Options for <see cref="ArWriter"/>.</summary>
public sealed class ArWriterOptions
{
    /// <summary>The default amount of output the GNU variant holds in memory before it moves to a temporary file.</summary>
    public const long DefaultMaxMemoryBuffer = 16L << 20;

    /// <summary>Gets the default options.</summary>
    public static ArWriterOptions Default { get; } = new();

    /// <summary>Gets the variant to write. Default: <see cref="ArFormat.Gnu"/>.</summary>
    public ArFormat Format { get; init; }

    /// <summary>
    /// Gets how much output the GNU variant keeps in memory before moving to a temporary file. The long-name table
    /// precedes the members it names, so the members are held until <c>Finish</c>. Default 16 MiB. Other variants
    /// stream and ignore this.
    /// </summary>
    public long MaxMemoryBuffer { get; init; } = DefaultMaxMemoryBuffer;
}
