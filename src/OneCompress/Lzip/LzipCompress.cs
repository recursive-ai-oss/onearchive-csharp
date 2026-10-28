using RecursiveAI.OneCompress.Lzma;

namespace RecursiveAI.OneCompress.Lzip;

public static partial class Lzip
{
    /// <summary>Compresses a buffer to the lzip format.</summary>
    /// <param name="source">The data.</param>
    /// <param name="options">Level, dictionary size and member size; <see langword="null"/> for <see cref="LzipOptions.Default"/> (level 6, one member).</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, LzipOptions? options = null) =>
        OneShotEncode.Run(new LzipEncoder(options, source.Length), source);

    /// <summary>Compresses a buffer to the lzip format into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the compressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Level, dictionary size and member size; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, LzipOptions? options = null) =>
        OneShotEncode.TryRun(new LzipEncoder(options, source.Length), source, destination, out bytesWritten);
}
