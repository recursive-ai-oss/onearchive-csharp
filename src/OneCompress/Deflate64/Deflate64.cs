using System.Buffers;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>
/// One-call compression and decompression of raw Deflate64 data (zip method 9). Everything here is built on
/// <see cref="Deflate64Decoder"/> and <see cref="Deflate64Encoder"/>.
/// </summary>
public static class Deflate64
{
    /// <summary>Gets the most bytes <see cref="Compress"/> can produce for input of <paramref name="inputLength"/> bytes.</summary>
    /// <param name="inputLength">The input size.</param>
    /// <returns>The bound.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative or too large.</exception>
    public static long GetMaxCompressedLength(long inputLength) => Deflate64Encoder.GetMaxCompressedLength(inputLength);

    /// <summary>Compresses a buffer to one raw Deflate64 stream (the data of a zip entry with method 9).</summary>
    /// <param name="source">The data; may be empty (the result is then a two-byte stream).</param>
    /// <param name="options">The level; <see langword="null"/> for <see cref="Deflate64Options.Default"/>.</param>
    /// <returns>The compressed bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 9.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, Deflate64Options? options = null)
    {
        byte[] buffer = new byte[GetMaxCompressedLength(source.Length)];
        TryCompress(source, buffer, out int written, options);
        return buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Compresses a buffer into a caller-supplied buffer.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the stream; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">The level; <see langword="null"/> for the default.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 9.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Deflate64Options? options = null)
    {
        var encoder = new Deflate64Encoder(options);
        OperationStatus status = encoder.Compress(source, destination, out _, out int written, isFinalBlock: true);
        bytesWritten = status == OperationStatus.Done ? written : 0;
        return status == OperationStatus.Done;
    }

    /// <summary>Decompresses a buffer holding one raw Deflate64 stream.</summary>
    /// <param name="source">The compressed data. It must end where the stream ends unless <see cref="Deflate64DecompressionOptions.AllowTrailingData"/> is set.</param>
    /// <param name="options">Limits; <see langword="null"/> for <see cref="Deflate64DecompressionOptions.Default"/>.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or bytes follow the end of the stream.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="Deflate64DecompressionOptions.MaxOutputSize"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, Deflate64DecompressionOptions? options = null)
    {
        options ??= Deflate64DecompressionOptions.Default;
        var decoder = new Deflate64Decoder(options);
        if (source.IsEmpty)
        {
            throw new CompressionFormatException("deflate64", 0, "a deflate64 stream", isTruncated: true);
        }

        byte[] output = new byte[(int)Math.Min(Math.Min(4L * source.Length + 64, options.MaxOutputSize), 1 << 20)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], output.AsSpan(written), out int c, out int w);
            consumed += c;
            written += w;
            switch (status)
            {
                case OperationStatus.Done:
                    CheckEnd(source, consumed, options);
                    return output.AsSpan(0, written).ToArray();
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("Unreachable: the decoder reported more data needed at a boundary.");
                default:
                    if (output.Length >= Array.MaxLength)
                    {
                        throw new CompressionLimitException("deflate64", nameof(Deflate64DecompressionOptions.MaxOutputSize), Array.MaxLength, (long)written + 1);
                    }

                    Array.Resize(ref output, (int)Math.Min((long)Math.Max(output.Length, 4096) * 2, Array.MaxLength));
                    break;
            }
        }
    }

    /// <summary>Decompresses a buffer holding one raw Deflate64 stream into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small for the whole output.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or bytes follow the end of the stream.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="Deflate64DecompressionOptions.MaxOutputSize"/>.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Deflate64DecompressionOptions? options = null)
    {
        options ??= Deflate64DecompressionOptions.Default;
        var decoder = new Deflate64Decoder(options);
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            throw new CompressionFormatException("deflate64", 0, "a deflate64 stream", isTruncated: true);
        }

        OperationStatus status = decoder.Decompress(source, destination, out int consumed, out int written);
        switch (status)
        {
            case OperationStatus.Done:
                CheckEnd(source, consumed, options);
                bytesWritten = written;
                return true;
            case OperationStatus.NeedMoreData:
                decoder.ThrowIfIncomplete();
                throw new InvalidOperationException("Unreachable: the decoder reported more data needed at a boundary.");
            default:
                return false;
        }
    }

    private static void CheckEnd(ReadOnlySpan<byte> source, int consumed, Deflate64DecompressionOptions options)
    {
        if (consumed != source.Length && !options.AllowTrailingData)
        {
            throw new CompressionFormatException("deflate64", consumed, "the input to end with the stream");
        }
    }
}
