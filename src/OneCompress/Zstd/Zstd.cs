using System.Buffers;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// One-call Zstandard operations. Everything here is built on <see cref="ZstdDecoder"/> and <see cref="ZstdEncoder"/>.
/// </summary>
public static class Zstd
{
    /// <summary>Compresses a buffer into one Zstandard frame.</summary>
    /// <param name="source">The data; may be empty.</param>
    /// <param name="options">Level, checksum, dictionary and so on; <see langword="null"/> for <see cref="ZstdCompressionOptions.Default"/> (level 3).</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level in <paramref name="options"/> is not supported.</exception>
    /// <remarks>
    /// Every call builds a new <see cref="ZstdEncoder"/>, with its tables. Code that compresses many buffers should keep an
    /// encoder and call <see cref="ZstdEncoder.CompressFrame"/>, which reuses everything.
    /// </remarks>
    public static byte[] Compress(ReadOnlySpan<byte> source, ZstdCompressionOptions? options = null)
    {
        var encoder = new ZstdEncoder(options);
        byte[] buffer = new byte[ZstdEncoder.GetMaxCompressedLength(source.Length)];
        int written = encoder.CompressFrame(source, buffer);
        return buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Compresses a buffer into one Zstandard frame in a caller-supplied buffer.</summary>
    /// <param name="source">The data; may be empty.</param>
    /// <param name="destination">Receives the frame.</param>
    /// <param name="bytesWritten">The frame's length, or 0 when the destination is too small.</param>
    /// <param name="options">Level, checksum, dictionary and so on; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level in <paramref name="options"/> is not supported.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, ZstdCompressionOptions? options = null) =>
        new ZstdEncoder(options).TryCompressFrame(source, destination, out bytesWritten);

    /// <summary>Gets the size of a buffer that always holds the frame <see cref="Compress"/> makes of <paramref name="sourceLength"/> bytes.</summary>
    /// <param name="sourceLength">The number of bytes to compress.</param>
    /// <returns>The bound in bytes.</returns>
    public static int GetMaxCompressedLength(int sourceLength) => ZstdEncoder.GetMaxCompressedLength(sourceLength);

    /// <summary>
    /// Decompresses a buffer holding one or more Zstandard frames (skippable frames are passed to the options'
    /// handler and otherwise skipped) and returns the concatenated content.
    /// </summary>
    /// <param name="source">The compressed data. It must end at a frame boundary.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for <see cref="ZstdDecompressionOptions.Default"/>.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>; this is raised before the memory is allocated.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, ZstdDecompressionOptions? options = null)
    {
        options ??= ZstdDecompressionOptions.Default;
        var decoder = new ZstdDecoder(options);
        if (source.IsEmpty)
        {
            ZstdFormat.ThrowTruncated(0, "a Zstandard frame");
        }

        // A declared content size is trusted for the first allocation only up to a plausible expansion of the
        // input, so a tiny hostile frame cannot make this call reserve the whole limit; larger outputs grow.
        long hint = 0;
        if (ZstdDecoder.TryReadFrameHeader(source, out ZstdFrameHeader header) && !header.IsSkippable && header.HasContentSize)
        {
            long plausible = (1L << 20) + (255L * source.Length);
            if (header.ContentSize <= (ulong)Math.Min(plausible, options.MaxOutputSize))
            {
                hint = (long)header.ContentSize;
            }
        }

        byte[] output = new byte[Math.Min(hint, Array.MaxLength)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], output.AsSpan(written), out int c, out int w);
            consumed += c;
            written += w;
            if (status == OperationStatus.DestinationTooSmall)
            {
                if (output.Length >= Math.Min(options.MaxOutputSize, Array.MaxLength))
                {
                    throw new CompressionLimitException(ZstdFormat.Name, "MaxOutputSize", options.MaxOutputSize, (long)output.Length + 1);
                }

                long grown = Math.Max(4096, 2L * output.Length);
                Array.Resize(ref output, (int)Math.Min(grown, Math.Min(options.MaxOutputSize, Array.MaxLength)));
                continue;
            }

            if (consumed == source.Length && (status == OperationStatus.NeedMoreData || status == OperationStatus.Done))
            {
                decoder.ThrowIfIncomplete();
                break;
            }
        }

        if (written != output.Length)
        {
            Array.Resize(ref output, written);
        }

        return output;
    }

    /// <summary>Decompresses into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small for the content.</exception>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, ZstdDecompressionOptions? options = null)
    {
        if (!TryDecompress(source, destination, out int written, options))
        {
            throw new ArgumentException("The destination buffer is too small for the decompressed data.", nameof(destination));
        }

        return written;
    }

    /// <summary>Decompresses into a caller-supplied buffer, reporting whether it was large enough.</summary>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, ZstdDecompressionOptions? options = null)
    {
        var decoder = new ZstdDecoder(options);
        return TryDecompress(decoder, source, destination, out bytesWritten);
    }

    /// <summary>
    /// Decompresses into a caller-supplied buffer with a decoder the caller keeps, so repeated calls allocate nothing.
    /// The decoder is reset first.
    /// </summary>
    /// <param name="decoder">The decoder, configured with the options and dictionaries to use.</param>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit of the decoder's options.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static bool TryDecompress(ZstdDecoder decoder, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        decoder.Reset();
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            ZstdFormat.ThrowTruncated(0, "a Zstandard frame");
        }

        int consumed = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], destination[bytesWritten..], out int c, out int w);
            consumed += c;
            bytesWritten += w;
            if (status == OperationStatus.DestinationTooSmall)
            {
                return false;
            }

            if (consumed == source.Length)
            {
                decoder.ThrowIfIncomplete();
                return true;
            }
        }
    }
}
