namespace RecursiveAI.OneCompress.Lzma;

public static partial class Lzma
{
    /// <summary>Compresses a buffer to the <c>.lzma</c> format.</summary>
    /// <param name="source">The data.</param>
    /// <param name="options">Preset and parameters; <see langword="null"/> for <see cref="LzmaOptions.Default"/>. The header declares the length, and no end marker is written unless <see cref="LzmaOptions.WriteEndMarker"/> is set.</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, LzmaOptions? options = null) =>
        OneShotEncode.Run(new LzmaEncoder(options, source.Length), source);

    /// <summary>Compresses a buffer to the <c>.lzma</c> format into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the compressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Preset and parameters; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, LzmaOptions? options = null) =>
        OneShotEncode.TryRun(new LzmaEncoder(options, source.Length), source, destination, out bytesWritten);
}

public static partial class Lzma2
{
    /// <summary>Compresses a buffer to a raw LZMA2 stream.</summary>
    /// <param name="source">The data.</param>
    /// <param name="dictionaryProperty">Receives the dictionary size byte the container must store.</param>
    /// <param name="options">Preset and parameters; <see langword="null"/> for the defaults. <c>Lc + Lp</c> must be at most 4.</param>
    /// <returns>The compressed bytes, ending with the zero control byte.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, out byte dictionaryProperty, LzmaOptions? options = null)
    {
        var encoder = new Lzma2Encoder(options, source.Length);
        dictionaryProperty = encoder.DictionaryProperty;
        return OneShotEncode.Run(encoder, source);
    }
}
