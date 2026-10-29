namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// Maps a compression layer to its codec through the registry, so this file names no codec type. A new layer is added to
/// <see cref="Codec"/>, <see cref="CanCompress"/> and the codec class.
/// </summary>
internal static class CompressionFilters
{
    public static Stream OpenDecompress(ArchiveCompression compression, Stream source, ArchiveLimits limits) =>
        Codec(compression).OpenDecompress(source, limits);

    /// <summary>True for the layers that can be written.</summary>
    public static bool CanCompress(ArchiveCompression compression) => compression is ArchiveCompression.None or ArchiveCompression.Zstd or ArchiveCompression.Lz4 or ArchiveCompression.Gzip or ArchiveCompression.Bzip2 or ArchiveCompression.Xz or ArchiveCompression.Lzip or ArchiveCompression.Brotli;

    public static Stream OpenCompress(ArchiveCompression compression, Stream sink, int? level, bool leaveOpen) =>
        Codec(compression).OpenCompress(sink, level, leaveOpen);

    private static ArchiveCodec Codec(ArchiveCompression compression)
    {
        CodecId? id = compression switch
        {
            ArchiveCompression.Zstd => CodecId.Zstd,
            ArchiveCompression.Lz4 => CodecId.Lz4,
            ArchiveCompression.Gzip => CodecId.Deflate,
            ArchiveCompression.Bzip2 => CodecId.BZip2,
            ArchiveCompression.Xz => CodecId.Xz,
            ArchiveCompression.Lzip => CodecId.Lzip,
            ArchiveCompression.Brotli => CodecId.Brotli,
            _ => null,
        };
        if (id is not { } known)
        {
            throw new ArchiveUnsupportedException("archive", $"{compression} compression (not yet available; use zstd, lz4, gzip, bzip2, xz, lzip or brotli)");
        }

        return CodecRegistry.Require(known, "archive", $"{compression} compression");
    }
}
