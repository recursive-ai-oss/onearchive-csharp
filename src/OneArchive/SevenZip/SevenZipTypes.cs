using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>One coder (compression method or filter) of a 7z folder, as stored in the header.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipCoder
{
    internal SevenZipCoder(ulong methodId, int numInStreams, int numOutStreams, byte[] properties)
    {
        MethodId = methodId;
        NumInStreams = numInStreams;
        NumOutStreams = numOutStreams;
        Properties = properties;
    }

    /// <summary>Gets the method ID: the ID bytes read as a big-endian number (<c>0x030101</c> is LZMA, <c>0x21</c> LZMA2, <c>0x040108</c> Deflate).</summary>
    public ulong MethodId { get; }

    /// <summary>Gets the number of input streams the coder reads when decoding (packed side). One for every method except BCJ2, which has four.</summary>
    public int NumInStreams { get; }

    /// <summary>Gets the number of output streams the coder produces when decoding. Always one for the methods that can be decoded.</summary>
    public int NumOutStreams { get; }

    /// <summary>Gets the coder's property bytes (for LZMA the lc/lp/pb byte and the dictionary size), possibly empty.</summary>
    public ReadOnlyMemory<byte> Properties { get; }
}

/// <summary>Connects the output stream of one coder to the input stream of another inside a folder.</summary>
/// <param name="InIndex">The index of the input stream (counted over all coders of the folder, in order) that is fed by the output.</param>
/// <param name="OutIndex">The index of the output stream (counted over all coders of the folder) that feeds the input.</param>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly record struct SevenZipBindPair(int InIndex, int OutIndex);

/// <summary>
/// A folder: a graph of coders that turns one or more packed streams into one unpacked stream, which holds the data
/// of one or more files back to back (a solid block when it holds several).
/// </summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipFolder
{
    internal SevenZipFolder(SevenZipCoder[] coders, SevenZipBindPair[] bindPairs, int[] packedStreams, int mainOutStream)
    {
        Coders = coders;
        BindPairs = bindPairs;
        PackedStreams = packedStreams;
        MainOutStream = mainOutStream;
        UnpackSizes = [];
        SubStreamSizes = [];
        SubStreamCrcs = [];
    }

    /// <summary>Gets the coders, in header order.</summary>
    public IReadOnlyList<SevenZipCoder> Coders { get; }

    /// <summary>Gets the bonds between coders.</summary>
    public IReadOnlyList<SevenZipBindPair> BindPairs { get; }

    /// <summary>Gets, for each packed stream of the folder, the index of the coder input stream it feeds.</summary>
    public IReadOnlyList<int> PackedStreams { get; }

    /// <summary>Gets the index of the output stream that no bond consumes: the folder's result.</summary>
    public int MainOutStream { get; }

    /// <summary>Gets the unpacked size of every coder output stream.</summary>
    public IReadOnlyList<long> UnpackSizes { get; internal set; }

    /// <summary>Gets the size of the folder's result (the unpacked size of <see cref="MainOutStream"/>).</summary>
    public long UnpackSize => UnpackSizes[MainOutStream];

    /// <summary>Gets the CRC-32 of the folder's result, if the header records one.</summary>
    public uint? Crc { get; internal set; }

    /// <summary>Gets the index of the folder's first packed stream among the archive's packed streams.</summary>
    public int FirstPackStream { get; internal set; }

    /// <summary>Gets the sizes of the files (substreams) stored in this folder, in order.</summary>
    public IReadOnlyList<long> SubStreamSizes { get; internal set; }

    /// <summary>Gets the CRC-32 of each substream; null where the header has none.</summary>
    public IReadOnlyList<uint?> SubStreamCrcs { get; internal set; }

    internal int TotalInStreams
    {
        get
        {
            int n = 0;
            foreach (SevenZipCoder c in Coders)
            {
                n += c.NumInStreams;
            }

            return n;
        }
    }
}

/// <summary>An entry of a 7z archive with the header fields that <see cref="ArchiveEntry"/> has no property for.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipEntry : ArchiveEntry
{
    /// <summary>Gets the position of the entry in the header's file list, anti-items included.</summary>
    public int Index { get; init; }

    /// <summary>Gets the creation time, if the header has one.</summary>
    public DateTimeOffset? CreationTime { get; init; }

    /// <summary>Gets the Windows attribute word as stored; when bit <c>0x8000</c> is set the high 16 bits hold the Unix mode (<c>st_mode</c>).</summary>
    public uint? Attributes { get; init; }

    /// <summary>Gets a value indicating whether the entry has no data stream (directories, empty files and anti-items).</summary>
    public bool IsEmptyStream { get; init; }

    /// <summary>Gets a value indicating whether the entry is an anti-item: a marker that the named file was deleted. <see cref="SevenZipReader.GetNextEntry"/> never returns anti-items; <see cref="SevenZipReader.Items"/> lists them.</summary>
    public bool IsAnti { get; init; }

    /// <summary>Gets the CRC-32 of the content as stored in the header, if any.</summary>
    public uint? Crc32 { get; init; }

    /// <summary>Gets the index of the folder that holds the content, or -1 for entries with no data stream.</summary>
    public int FolderIndex { get; init; } = -1;

    /// <summary>Gets the offset of the content inside the folder's unpacked stream.</summary>
    public long FolderOffset { get; init; }
}

/// <summary>Options for <see cref="SevenZipReader"/> and the 7z part of <see cref="ArchiveOpenOptions"/>.</summary>
[Experimental("ONEARCHIVE001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class SevenZipReaderOptions
{
    /// <summary>The default for <see cref="MaxHeaderSize"/>: 64 MiB.</summary>
    public const long DefaultMaxHeaderSize = 64L << 20;

    /// <summary>The default for <see cref="MaxCodersPerFolder"/>: 32.</summary>
    public const int DefaultMaxCodersPerFolder = 32;

    /// <summary>Gets the shared default options.</summary>
    public static SevenZipReaderOptions Default { get; } = new();

    /// <summary>Gets the limits to apply. Default <see cref="ArchiveLimits.Default"/>.</summary>
    public ArchiveLimits Limits { get; init; } = ArchiveLimits.Default;

    /// <summary>
    /// Gets a value indicating whether the CRC-32 of each file is checked when the file has been read to its end; a
    /// mismatch raises <see cref="RecursiveAI.OneCompress.CompressionFormatException"/>. The header CRCs are always
    /// checked. Default true.
    /// </summary>
    public bool VerifyChecksum { get; init; } = true;

    /// <summary>
    /// Gets the largest header, packed or unpacked, the reader will hold in memory. Checked against the declared sizes
    /// before anything is allocated. Default 64 MiB.
    /// </summary>
    public long MaxHeaderSize { get; init; } = DefaultMaxHeaderSize;

    /// <summary>Gets the most coders a folder may have; a longer graph is refused. Default 32.</summary>
    public int MaxCodersPerFolder { get; init; } = DefaultMaxCodersPerFolder;
}
