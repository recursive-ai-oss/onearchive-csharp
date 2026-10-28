using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// The one place that maps a compression layer to a codec. A new codec (gzip once inflate lands, then bzip2 and xz)
/// is added to the two switches here and nowhere else.
/// </summary>
internal static class CompressionFilters
{
    public static Stream OpenDecompress(ArchiveCompression compression, Stream source, ArchiveLimits limits) => compression switch
    {
        // MaxOutputSize is unbounded here: the archive reader bounds what it takes (total size, expansion ratio).
        ArchiveCompression.Zstd => new ZstdStream(source, new ZstdDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }),
        ArchiveCompression.Lz4 => new Lz4Stream(source, new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue }),
        ArchiveCompression.Gzip => new DeflateCodecStream(source, DeflateFormat.GZip, new DeflateDecompressionOptions { MaxOutputSize = long.MaxValue }),
        ArchiveCompression.Bzip2 => new RecursiveAI.OneCompress.BZip2.BZip2Stream(source, new RecursiveAI.OneCompress.BZip2.BZip2DecompressionOptions { MaxOutputSize = long.MaxValue }),
        _ => throw Unsupported(compression),
    };

    /// <summary>True for the layers that can be written.</summary>
    public static bool CanCompress(ArchiveCompression compression) => compression is ArchiveCompression.None or ArchiveCompression.Zstd or ArchiveCompression.Lz4 or ArchiveCompression.Gzip or ArchiveCompression.Bzip2;

    public static Stream OpenCompress(ArchiveCompression compression, Stream sink, int? level, bool leaveOpen) => compression switch
    {
        ArchiveCompression.Zstd => new ZstdStream(sink, new ZstdCompressionOptions { Level = level ?? 3, Checksum = true }, leaveOpen),
        ArchiveCompression.Lz4 => new Lz4Stream(sink, new Lz4CompressionOptions { Level = level ?? Lz4CompressionOptions.DefaultLevel }, leaveOpen),
        ArchiveCompression.Gzip => new DeflateEncoderStream(sink, DeflateFormat.GZip, new DeflateOptions { Level = level ?? DeflateOptions.DefaultLevel }, leaveOpen),
        ArchiveCompression.Bzip2 => new RecursiveAI.OneCompress.BZip2.BZip2Stream(sink, new RecursiveAI.OneCompress.BZip2.BZip2Options { Level = Math.Clamp(level ?? RecursiveAI.OneCompress.BZip2.BZip2Options.DefaultLevel, 1, 9) }, leaveOpen),
        _ => throw Unsupported(compression),
    };

    private static ArchiveUnsupportedException Unsupported(ArchiveCompression compression) =>
        new("archive", $"{compression} compression (not yet available; use zstd, lz4, gzip or bzip2)");
}
