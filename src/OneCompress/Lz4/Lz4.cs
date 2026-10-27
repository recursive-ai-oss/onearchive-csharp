using System.Buffers;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>One-call compression and decompression of LZ4 frames (LZ4 Frame Format), including concatenated, skippable and legacy frames when decoding. For raw blocks use <see cref="Lz4Block"/>.</summary>
public static class Lz4
{
    /// <summary>Compresses <paramref name="source"/> into one frame in a new array.</summary>
    /// <param name="source">The bytes to compress; may be empty.</param>
    /// <param name="options">The level, block layout, checksums and dictionary; <see langword="null"/> for the defaults (level 1, 4 MiB independent blocks, content checksum, the size in the header).</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Lz4CompressionOptions.ContentSize"/> is set and differs from the length of <paramref name="source"/>.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, Lz4CompressionOptions? options = null)
    {
        var encoder = new Lz4Encoder(options);
        long bound = Lz4Encoder.GetMaxCompressedLength(source.Length, options);
        byte[] buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(bound, Array.MaxLength));
        try
        {
            if (!encoder.TryCompressFrame(source, buffer, out int written))
            {
                throw new InvalidOperationException("The frame did not fit its bound.");
            }

            return buffer.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Compresses <paramref name="source"/> into one frame in <paramref name="destination"/>.</summary>
    /// <param name="source">The bytes to compress; may be empty.</param>
    /// <param name="destination">Receives the frame; <see cref="Lz4Encoder.GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="options">The level, block layout, checksums and dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, Lz4CompressionOptions? options = null)
    {
        if (!new Lz4Encoder(options).TryCompressFrame(source, destination, out int written))
        {
            throw new ArgumentException("The destination is too small for the frame.", nameof(destination));
        }

        return written;
    }

    /// <summary>
    /// Compresses into a caller-supplied buffer with an encoder the caller keeps, so repeated calls allocate nothing. Any frame the
    /// encoder had in progress is abandoned.
    /// </summary>
    /// <param name="encoder">The encoder, configured with the options and dictionary to use.</param>
    /// <param name="source">The bytes to compress; may be empty.</param>
    /// <param name="destination">Receives the frame.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    public static bool TryCompress(Lz4Encoder encoder, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        return encoder.TryCompressFrame(source, destination, out bytesWritten);
    }

    /// <summary>Decompresses one or more LZ4 frames into a new array.</summary>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>The decompressed bytes of all frames, in order.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>; nothing is allocated for the excess.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, Lz4DecompressionOptions? options = null)
    {
        options ??= Lz4DecompressionOptions.Default;
        var decoder = new Lz4Decoder(options);
        if (Lz4Decoder.TryGetDecompressedSize(source, out long size))
        {
            if (size > options.MaxOutputSize)
            {
                Lz4Format.ThrowLimit("MaxOutputSize", options.MaxOutputSize, size);
            }

            if (size > Array.MaxLength)
            {
                Lz4Format.ThrowLimit("array length", Array.MaxLength, size);
            }

            byte[] exact = GC.AllocateUninitializedArray<byte>((int)size);
            if (!decoder.TryDecompress(source, exact, out int n) || n != size)
            {
                throw new InvalidOperationException("The frames decoded to a different size than they declared.");
            }

            return exact;
        }

        // The size is not declared: decode in pieces into a growing buffer.
        byte[] buffer = new byte[(int)Math.Clamp((long)source.Length * 4, 4096, Array.MaxLength)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], buffer.AsSpan(written), out int c, out int w);
            consumed += c;
            written += w;
            if (status == OperationStatus.DestinationTooSmall)
            {
                if (buffer.Length >= Array.MaxLength)
                {
                    Lz4Format.ThrowLimit("array length", Array.MaxLength, (long)buffer.Length + 1);
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, Array.MaxLength));
                continue;
            }

            if (status == OperationStatus.NeedMoreData || consumed >= source.Length)
            {
                decoder.ThrowIfIncomplete();
                break;
            }
        }

        return written == buffer.Length ? buffer : buffer.AsSpan(0, written).ToArray();
    }

    /// <summary>Decompresses LZ4 frames into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>, or does not fit in <paramref name="destination"/> (<c>LimitName</c> is <c>destination.Length</c>).</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, Lz4DecompressionOptions? options = null)
    {
        if (!TryDecompress(source, destination, out int written, options))
        {
            Lz4Format.ThrowLimit("destination.Length", destination.Length, (long)destination.Length + 1);
        }

        return written;
    }

    /// <summary>Decompresses LZ4 frames into a caller-supplied buffer, reporting whether it was large enough.</summary>
    /// <param name="source">The compressed data: one or more frames, ending at a frame boundary.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, or a checksum does not match.</exception>
    /// <exception cref="CompressionLimitException">The data exceeds a limit in <paramref name="options"/>.</exception>
    /// <exception cref="CompressionDictionaryException">A frame names a dictionary that was not supplied.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Lz4DecompressionOptions? options = null) =>
        new Lz4Decoder(options).TryDecompress(source, destination, out bytesWritten);

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
    public static bool TryDecompress(Lz4Decoder decoder, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        return decoder.TryDecompress(source, destination, out bytesWritten);
    }
}
