using System.Buffers;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// One-call Zstandard operations. Everything here is built on <see cref="ZstdDecoder"/>.
/// </summary>
public static class Zstd
{
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
