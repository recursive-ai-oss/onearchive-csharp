
namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// One-call compression and decompression of a gzip file (RFC 1952), one or more members. Everything here is built on <see cref="DeflateDecoder"/>.
/// </summary>
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

    /// <summary>Compresses a buffer to a gzip file (RFC 1952), one member.</summary>
    /// <param name="source">The data.</param>
    /// <param name="options">The level, strategy, dictionary and header fields; <see langword="null"/> for <see cref="DeflateOptions.Default"/>.</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, DeflateOptions? options = null) =>
        DeflateCompressOneShot.Compress(DeflateFormat.GZip, source, options);

    /// <summary>Compresses a buffer to a gzip file (RFC 1952), one member into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the compressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">The options; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> cannot hold the whole output (<see cref="GetMaxCompressedLength"/> bytes always suffice).</returns>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, DeflateOptions? options = null) =>
        DeflateCompressOneShot.TryCompress(DeflateFormat.GZip, source, destination, out bytesWritten, options);

    /// <summary>Gets the most bytes <see cref="Compress"/> can return for input of <paramref name="inputLength"/> bytes.</summary>
    /// <param name="inputLength">The input size.</param>
    /// <param name="options">The options (gzip header fields change the bound); <see langword="null"/> for the defaults.</param>
    /// <returns>The bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative or too large.</exception>
    public static long GetMaxCompressedLength(long inputLength, DeflateOptions? options = null) =>
        DeflateEncoder.GetMaxCompressedLength(DeflateFormat.GZip, inputLength, options);
}
