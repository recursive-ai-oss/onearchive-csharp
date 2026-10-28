using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lzx;

/// <summary>One-call LZX decompression for a stream that is a sequence of frames, as in a cabinet folder.</summary>
/// <remarks>
/// The frames are decoded in order from the concatenated compressed bytes: each frame ends on a 16-bit boundary of the input, which is
/// where the next begins. LZX does not store the uncompressed size or the window size, so both are arguments. Built on
/// <see cref="LzxDecoder"/>.
/// </remarks>
[Experimental("ONECOMPRESS009", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class Lzx
{
    /// <summary>Decompresses an LZX stream.</summary>
    /// <param name="source">The compressed frames, one after another.</param>
    /// <param name="windowBits">The window size as a power of two, 15 to 21.</param>
    /// <param name="uncompressedSize">The exact size of the decompressed data.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="windowBits"/> is outside 15 to 21 or <paramref name="uncompressedSize"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">The data is malformed or ends early.</exception>
    /// <exception cref="CompressionLimitException">The size exceeds <see cref="LzxDecompressionOptions.MaxOutputSize"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int windowBits, int uncompressedSize, LzxDecompressionOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(uncompressedSize);
        options ??= LzxDecompressionOptions.Default;
        if (uncompressedSize > options.MaxOutputSize)
        {
            throw new CompressionLimitException(LzxDecoder.FormatName, nameof(LzxDecompressionOptions.MaxOutputSize), options.MaxOutputSize, uncompressedSize);
        }

        var decoder = new LzxDecoder(windowBits, options);
        byte[] output = new byte[uncompressedSize];
        int written = 0;
        int read = 0;
        while (written < uncompressedSize)
        {
            int n = Math.Min(LzxDecoder.FrameSize, uncompressedSize - written);
            decoder.DecodeFrame(source[read..], output.AsSpan(written, n), out int consumed);
            read += consumed;
            written += n;
        }

        return output;
    }
}
