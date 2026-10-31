using System.Buffers;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// One-call seekable Zstandard: compress into independent frames with a seek table, and decompress a range of the
/// content. Built on <see cref="ZstdSeekableWriter"/> and <see cref="ZstdSeekableReader"/>.
/// </summary>
public static class ZstdSeekable
{
    /// <summary>Compresses data into a seekable file.</summary>
    /// <param name="source">The content.</param>
    /// <param name="options">Frame size, checksums and compression options; <see langword="null"/> for the defaults.</param>
    /// <returns>A seekable file: frames followed by the seek table.</returns>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, ZstdSeekableOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Compress(source, buffer, options);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Compresses data into a seekable file written to a buffer writer.</summary>
    /// <param name="source">The content.</param>
    /// <param name="destination">Receives the file.</param>
    /// <param name="options">Frame size, checksums and compression options; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException">An option is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public static void Compress(ReadOnlySpan<byte> source, IBufferWriter<byte> destination, ZstdSeekableOptions? options = null)
    {
        var writer = new ZstdSeekableWriter(destination, options);
        writer.Write(source);
        writer.Complete();
    }

    /// <summary>Reads the seek table of a seekable file.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="options">Limits; <see langword="null"/> for the defaults.</param>
    /// <exception cref="CompressionFormatException">The file has no valid seek table.</exception>
    /// <exception cref="CompressionLimitException">The table has more entries than <see cref="ZstdSeekableReaderOptions.MaxFrames"/>.</exception>
    public static ZstdSeekTable ReadTable(ReadOnlySpan<byte> file, ZstdSeekableReaderOptions? options = null) =>
        ZstdSeekTable.Read(file, (options ?? ZstdSeekableReaderOptions.Default).MaxFrames);

    /// <summary>Decompresses a range of a seekable file, touching only the frames that cover it.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="offset">Offset into the decompressed content.</param>
    /// <param name="destination">Receives the bytes.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>The number of bytes read: <c>destination.Length</c> unless the content ends first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">The file has no valid seek table or a frame is corrupt.</exception>
    /// <exception cref="CompressionLimitException">The file exceeds a limit.</exception>
    public static int DecompressRange(ReadOnlyMemory<byte> file, long offset, Span<byte> destination, ZstdSeekableReaderOptions? options = null) =>
        new ZstdSeekableReader(file, options).Read(offset, destination);

    /// <summary>Decompresses a range of a seekable file into a new array.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="offset">Offset into the decompressed content.</param>
    /// <param name="length">The number of bytes wanted.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <returns>The bytes: shorter than <paramref name="length"/> when the content ends first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> or <paramref name="length"/> is negative.</exception>
    /// <exception cref="CompressionFormatException">The file has no valid seek table or a frame is corrupt.</exception>
    /// <exception cref="CompressionLimitException">The file exceeds a limit, or the range is larger than <see cref="ZstdDecompressionOptions.MaxOutputSize"/>.</exception>
    public static byte[] DecompressRange(ReadOnlyMemory<byte> file, long offset, int length, ZstdSeekableReaderOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var reader = new ZstdSeekableReader(file, options);
        long maxOutput = (options ?? ZstdSeekableReaderOptions.Default).Decompression.MaxOutputSize;
        long available = Math.Max(0, Math.Min(length, reader.Length - offset));
        if (available > maxOutput)
        {
            throw new CompressionLimitException(ZstdSeekTable.FormatName, "MaxOutputSize", maxOutput, available);
        }

        byte[] result = new byte[available];
        int n = reader.Read(offset, result);
        return n == result.Length ? result : result.AsSpan(0, n).ToArray();
    }

    /// <summary>Decompresses a whole seekable file.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="options">Limits and dictionaries; <see langword="null"/> for the defaults.</param>
    /// <exception cref="CompressionFormatException">The file has no valid seek table or a frame is corrupt.</exception>
    /// <exception cref="CompressionLimitException">The content is larger than <see cref="ZstdDecompressionOptions.MaxOutputSize"/> or a frame exceeds a limit.</exception>
    public static byte[] Decompress(ReadOnlyMemory<byte> file, ZstdSeekableReaderOptions? options = null)
    {
        var reader = new ZstdSeekableReader(file, options);
        long maxOutput = (options ?? ZstdSeekableReaderOptions.Default).Decompression.MaxOutputSize;
        if (reader.Length > Math.Min(maxOutput, Array.MaxLength))
        {
            throw new CompressionLimitException(ZstdSeekTable.FormatName, "MaxOutputSize", Math.Min(maxOutput, Array.MaxLength), reader.Length);
        }

        byte[] result = new byte[reader.Length];
        reader.Read(0, result);
        return result;
    }
}
