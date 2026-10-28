using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// One-call decompression of a gzip file (RFC 1952), one or more members. Everything here is built on <see cref="DeflateDecoder"/>.
/// </summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class GZip
{
    /// <summary>Decompresses a buffer holding a gzip file (RFC 1952), one or more members. Concatenated members are decoded one after the other and their output is joined, as the gzip tool does.</summary>
    /// <param name="source">The compressed data. It must end where the stream ends (see <see cref="DeflateDecompressionOptions.AllowTrailingData"/>).</param>
    /// <param name="options">Limits and the preset dictionary; <see langword="null"/> for <see cref="DeflateDecompressionOptions.Default"/>.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, a checksum does not match, or input follows the end of the stream.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="DeflateDecompressionOptions.MaxOutputSize"/>; this is raised before the memory is allocated.</exception>
    /// <exception cref="CompressionDictionaryException">A zlib stream needs a preset dictionary that was not supplied or does not match.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, DeflateDecompressionOptions? options = null) =>
        DeflateOneShot.Decompress(DeflateFormat.GZip, source, options);

    /// <summary>Decompresses a buffer holding a gzip file (RFC 1952), one or more members into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Limits and the preset dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small for the whole output.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, a checksum does not match, or input follows the end of the stream.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="DeflateDecompressionOptions.MaxOutputSize"/>.</exception>
    /// <exception cref="CompressionDictionaryException">A zlib stream needs a preset dictionary that was not supplied or does not match.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, DeflateDecompressionOptions? options = null) =>
        DeflateOneShot.TryDecompress(DeflateFormat.GZip, source, destination, out bytesWritten, options);
}
