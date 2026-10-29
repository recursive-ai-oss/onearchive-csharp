using RecursiveAI.OneCompress.Brotli;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Xz;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Internal;

/// <summary>
/// The one place that maps a compression layer to a codec. A new codec
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
        ArchiveCompression.Xz => new XzStream(source, new XzDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }),
        ArchiveCompression.Lzip => new LzipStream(source, new LzipDecompressionOptions { MaxDictionarySize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }),
        // RFC 7932 section 9.1: a stream declares its own window; the limit keeps an untrusted one from sizing the ring buffer.
        ArchiveCompression.Brotli => new BrotliStream(source, new BrotliDecompressionOptions { MaxWindowSize = limits.MaxWindowSize, MaxOutputSize = long.MaxValue }),
        _ => throw Unsupported(compression),
    };

    /// <summary>True for the layers that can be written.</summary>
    public static bool CanCompress(ArchiveCompression compression) => compression is ArchiveCompression.None or ArchiveCompression.Zstd or ArchiveCompression.Lz4 or ArchiveCompression.Gzip or ArchiveCompression.Bzip2 or ArchiveCompression.Xz or ArchiveCompression.Lzip or ArchiveCompression.Brotli;

    public static Stream OpenCompress(ArchiveCompression compression, Stream sink, int? level, bool leaveOpen) => compression switch
    {
        ArchiveCompression.Zstd => new ZstdStream(sink, new ZstdCompressionOptions { Level = level ?? 3, Checksum = true }, leaveOpen),
        ArchiveCompression.Lz4 => new Lz4Stream(sink, new Lz4CompressionOptions { Level = level ?? Lz4CompressionOptions.DefaultLevel }, leaveOpen),
        ArchiveCompression.Gzip => new DeflateEncoderStream(sink, DeflateFormat.GZip, new DeflateOptions { Level = level ?? DeflateOptions.DefaultLevel }, leaveOpen),
        ArchiveCompression.Bzip2 => new RecursiveAI.OneCompress.BZip2.BZip2Stream(sink, new RecursiveAI.OneCompress.BZip2.BZip2Options { Level = Math.Clamp(level ?? RecursiveAI.OneCompress.BZip2.BZip2Options.DefaultLevel, 1, 9) }, leaveOpen),
        ArchiveCompression.Xz => new XzEncoderStream(sink, new XzCompressionOptions { Lzma = new RecursiveAI.OneCompress.Lzma.LzmaOptions { Preset = level ?? RecursiveAI.OneCompress.Lzma.LzmaOptions.DefaultPreset } }, leaveOpen),
        ArchiveCompression.Lzip => new LzipEncoderStream(sink, new LzipOptions { Level = level ?? LzipOptions.DefaultLevel }, leaveOpen),
        // Quality 5 (the tool's own default is 11): the encoder here is far slower at 10 and 11, and 5 is the first quality with block
        // splitting and context modelling. Window 22 bits (4 MiB, the tool's default) stays inside the default ArchiveLimits.MaxWindowSize.
        ArchiveCompression.Brotli => new BrotliStream(sink, new BrotliOptions { Quality = level ?? DefaultBrotliQuality, WindowBits = BrotliOptions.DefaultWindowBits }, leaveOpen),
        _ => throw Unsupported(compression),
    };

    private const int DefaultBrotliQuality = 5;

    private static ArchiveUnsupportedException Unsupported(ArchiveCompression compression) =>
        new("archive", $"{compression} compression (not yet available; use zstd, lz4, gzip, bzip2, xz, lzip or brotli)");
}
