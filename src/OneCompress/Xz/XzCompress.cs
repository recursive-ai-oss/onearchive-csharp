using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Xz;

public static partial class Xz
{
    /// <summary>Compresses a buffer to the xz format.</summary>
    /// <param name="source">The data.</param>
    /// <param name="options">Preset, check, block size and filters; <see langword="null"/> for <see cref="XzCompressionOptions.Default"/> (preset 6, CRC-64, one block).</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="ArgumentException">There are too many filters.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, XzCompressionOptions? options = null) =>
        OneShotEncode.Run(new XzEncoder(options, source.Length), source);

    /// <summary>Compresses a buffer to the xz format into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the compressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Preset, check, block size and filters; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, XzCompressionOptions? options = null) =>
        OneShotEncode.TryRun(new XzEncoder(options, source.Length), source, destination, out bytesWritten);
}
