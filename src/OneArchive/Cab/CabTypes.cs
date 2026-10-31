using System.Text;

namespace RecursiveAI.OneArchive.Cab;

/// <summary>The compression method of a cabinet folder ([MS-CAB] 2.2, <c>typeCompress</c>).</summary>
public enum CabCompression
{
    /// <summary>No compression.</summary>
    None = 0,

    /// <summary>MSZIP: deflate blocks, one per data block, with the previous 32 KiB of output as history.</summary>
    MsZip = 1,

    /// <summary>Quantum. No public specification exists, so reading it raises <see cref="ArchiveUnsupportedException"/>.</summary>
    Quantum = 2,

    /// <summary>LZX with a window of 2<sup>15</sup> to 2<sup>21</sup> bytes.</summary>
    Lzx = 3,
}

/// <summary>The <c>flags</c> field of the cabinet header ([MS-CAB] 2.1).</summary>
[Flags]
public enum CabHeaderFeatures : ushort
{
    /// <summary>No flag is set.</summary>
    None = 0,

    /// <summary>This cabinet is not the first of a set (<c>cfhdrPREV_CABINET</c>).</summary>
    PreviousCabinet = 0x0001,

    /// <summary>This cabinet is not the last of a set (<c>cfhdrNEXT_CABINET</c>).</summary>
    NextCabinet = 0x0002,

    /// <summary>The header, folders and data blocks carry reserved areas (<c>cfhdrRESERVE_PRESENT</c>).</summary>
    ReservePresent = 0x0004,
}

/// <summary>The <c>attribs</c> field of a file ([MS-CAB] 2.3).</summary>
[Flags]
public enum CabAttributes : ushort
{
    /// <summary>No attribute is set.</summary>
    None = 0,

    /// <summary>Read-only (<c>_A_RDONLY</c>).</summary>
    ReadOnly = 0x01,

    /// <summary>Hidden (<c>_A_HIDDEN</c>).</summary>
    Hidden = 0x02,

    /// <summary>System file (<c>_A_SYSTEM</c>).</summary>
    System = 0x04,

    /// <summary>Modified since the last backup (<c>_A_ARCH</c>).</summary>
    Archive = 0x20,

    /// <summary>Run after extraction (<c>_A_EXEC</c>).</summary>
    Execute = 0x40,

    /// <summary>The name is UTF-8 (<c>_A_NAME_IS_UTF</c>).</summary>
    NameIsUtf = 0x80,
}

/// <summary>How a file relates to the neighbouring cabinets of a set ([MS-CAB] 2.3, <c>iFolder</c>).</summary>
public enum CabContinuation
{
    /// <summary>The file lies completely in this cabinet.</summary>
    None = 0,

    /// <summary>The file starts in the previous cabinet (<c>ifoldCONTINUED_FROM_PREV</c>).</summary>
    FromPrevious = 1,

    /// <summary>The file goes on in the next cabinet (<c>ifoldCONTINUED_TO_NEXT</c>).</summary>
    ToNext = 2,

    /// <summary>The file starts in the previous cabinet and goes on in the next (<c>ifoldCONTINUED_PREV_AND_NEXT</c>).</summary>
    FromPreviousToNext = 3,
}

/// <summary>The cabinet header ([MS-CAB] 2.1): every field the format defines.</summary>
public sealed class CabHeader
{
    /// <summary>Gets the cabinet size the header declares (<c>cbCabinet</c>).</summary>
    public long CabinetSize { get; init; }

    /// <summary>Gets the offset of the first file entry (<c>coffFiles</c>).</summary>
    public long FilesOffset { get; init; }

    /// <summary>Gets the format version (major 1, minor 3 for every cabinet written today).</summary>
    public Version FormatVersion { get; init; } = new(1, 3);

    /// <summary>Gets the number of folders (<c>cFolders</c>).</summary>
    public int FolderCount { get; init; }

    /// <summary>Gets the number of file entries (<c>cFiles</c>).</summary>
    public int FileCount { get; init; }

    /// <summary>Gets the cabinet header flags ([MS-CAB] 2.1): whether the cabinet continues from a previous one, continues in a next one, or has reserved header space.</summary>
    public CabHeaderFeatures Flags { get; init; }

    /// <summary>Gets the identifier shared by the cabinets of a set (<c>setID</c>).</summary>
    public int SetId { get; init; }

    /// <summary>Gets the position of this cabinet in its set, from 0 (<c>iCabinet</c>).</summary>
    public int CabinetIndex { get; init; }

    /// <summary>Gets the application data in the header's reserved area (<c>abReserve</c>); empty if there is none.</summary>
    public ReadOnlyMemory<byte> HeaderReserve { get; init; }

    /// <summary>Gets the size of the reserved area in each folder entry (<c>cbCFFolder</c>).</summary>
    public int FolderReserveSize { get; init; }

    /// <summary>Gets the size of the reserved area in each data block (<c>cbCFData</c>).</summary>
    public int DataReserveSize { get; init; }

    /// <summary>Gets the file name of the previous cabinet of the set, or <see langword="null"/> if the flag is not set.</summary>
    public string? PreviousCabinet { get; init; }

    /// <summary>Gets the media label of the previous cabinet, or <see langword="null"/>.</summary>
    public string? PreviousDisk { get; init; }

    /// <summary>Gets the file name of the next cabinet of the set, or <see langword="null"/> if the flag is not set.</summary>
    public string? NextCabinet { get; init; }

    /// <summary>Gets the media label of the next cabinet, or <see langword="null"/>.</summary>
    public string? NextDisk { get; init; }
}

/// <summary>One folder of a cabinet ([MS-CAB] 2.2): a run of data blocks that holds the compressed bytes of its files one after another.</summary>
public sealed class CabFolder
{
    /// <summary>Gets the position of the folder in the cabinet.</summary>
    public int Index { get; init; }

    /// <summary>Gets the offset of the folder's first data block (<c>coffCabStart</c>).</summary>
    public long DataOffset { get; init; }

    /// <summary>Gets the number of data blocks in this cabinet (<c>cCFData</c>).</summary>
    public int BlockCount { get; init; }

    /// <summary>Gets the compression method.</summary>
    public CabCompression Compression { get; init; }

    /// <summary>Gets the whole <c>typeCompress</c> value, including the LZX window bits.</summary>
    public int RawCompressionType { get; init; }

    /// <summary>Gets the LZX window as a power of two (15 to 21), or 0 for the other methods.</summary>
    public int WindowBits { get; init; }

    /// <summary>Gets the application data in the folder's reserved area; empty if there is none.</summary>
    public ReadOnlyMemory<byte> Reserve { get; init; }
}

/// <summary>A file of a cabinet ([MS-CAB] 2.3). <see cref="ArchiveEntry.Name"/> uses <c>/</c> between components.</summary>
public sealed class CabEntry : ArchiveEntry
{
    /// <summary>Gets the position of the entry in the cabinet's file table.</summary>
    public int Index { get; init; }

    /// <summary>Gets the index of the folder that holds the data (the special <c>iFolder</c> values are resolved).</summary>
    public int FolderIndex { get; init; }

    /// <summary>Gets the <c>iFolder</c> field as stored.</summary>
    public int RawFolderIndex { get; init; }

    /// <summary>Gets the offset of the file's data within the folder's uncompressed stream (<c>uoffFolderStart</c>).</summary>
    public long FolderOffset { get; init; }

    /// <summary>Gets the file attributes.</summary>
    public CabAttributes Attributes { get; init; }

    /// <summary>Gets whether the file continues from or into another cabinet. Such a file cannot be read from this cabinet alone.</summary>
    public CabContinuation Continuation { get; init; }

    /// <summary>Gets the name bytes as stored.</summary>
    public ReadOnlyMemory<byte> RawName { get; init; }

    /// <summary>Gets the DOS date and time as stored (date in the high 16 bits).</summary>
    public uint RawDosDateTime { get; init; }
}

/// <summary>Options for reading cabinets.</summary>
public sealed class CabReaderOptions
{
    /// <summary>The options with every default.</summary>
    public static CabReaderOptions Default { get; } = new();

    /// <summary>Gets the limits (entry count, total size, path length and depth, expansion ratio, window size). Default: <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets whether the checksum of each data block is checked when it is not zero. A mismatch raises
    /// <see cref="RecursiveAI.OneCompress.CompressionFormatException"/>. Default true.
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the encoding for file names that are not marked as UTF-8 and contain bytes above 0x7F ([MS-CAB] 2.3 leaves it to the
    /// locale). Default: ISO 8859-1, which maps each byte to the code point of the same value.
    /// </summary>
    public Encoding? LegacyNameEncoding { get; init; }

}
