using System.Buffers;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>Settings for decoding raw LZ4 blocks with <see cref="Lz4Block"/>.</summary>
public sealed class Lz4BlockOptions
{
    /// <summary>The default for <see cref="MaxOutputSize"/>: 64 MiB.</summary>
    public const int DefaultMaxOutputSize = 64 << 20;

    /// <summary>Gets a shared instance with every option at its default.</summary>
    public static Lz4BlockOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest decoded size, in bytes, that <see cref="Lz4Block.Decompress(ReadOnlySpan{byte}, Lz4BlockOptions?)"/>
    /// allocates for. A block that decodes to more raises <see cref="CompressionLimitException"/> before anything is
    /// allocated. A block can expand 255-fold, so set this from what you expect, not from the compressed size. The default is 64 MiB.
    /// </summary>
    public int MaxOutputSize { get; init; } = DefaultMaxOutputSize;

    /// <summary>
    /// Gets the dictionary the block was compressed against, or <see langword="null"/> (the default) for none. Each call
    /// copies the dictionary next to the output; to decode many blocks against one large dictionary without that copy, lay
    /// the buffer out yourself and use <see cref="Lz4BlockDecoder"/>.
    /// </summary>
    public Lz4Dictionary? Dictionary { get; init; }
}

/// <summary>
/// One-call decoding of raw LZ4 blocks (LZ4 Block Format), the form used when the compressed size and a bound on the
/// decoded size travel outside the data. For the framed format use <see cref="Lz4"/>.
/// </summary>
public static class Lz4Block
{
    /// <summary>Gets the most bytes a block of <paramref name="inputLength"/> bytes can compress to; a destination of this size always suffices.</summary>
    /// <param name="inputLength">The number of bytes to compress.</param>
    /// <returns>The bound, saturating at <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative.</exception>
    public static int GetMaxCompressedLength(int inputLength) => Lz4BlockWriter.GetMaxCompressedLength(inputLength);

    /// <summary>Compresses <paramref name="source"/> into one block in a new array.</summary>
    /// <param name="source">The bytes to compress, at most <see cref="Lz4BlockCompressor.MaxInputLength"/>; may be empty (the block is then the single byte 0x00).</param>
    /// <param name="options">The level and dictionary (the other options do not apply to blocks); <see langword="null"/> for the defaults.</param>
    /// <returns>The compressed block. Decode it with <see cref="Decompress(ReadOnlySpan{byte}, Lz4BlockOptions?)"/>, which needs to be told the same dictionary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is longer than one block can hold.</exception>
    public static byte[] Compress(ReadOnlySpan<byte> source, Lz4CompressionOptions? options = null)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(GetMaxCompressedLength(source.Length));
        try
        {
            int n = CompressCore(source, buffer, options);
            return buffer.AsSpan(0, n).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Compresses <paramref name="source"/> into one block in <paramref name="destination"/>. For many blocks use a reusable <see cref="Lz4BlockEncoder"/>.</summary>
    /// <param name="source">The bytes to compress, at most <see cref="Lz4BlockCompressor.MaxInputLength"/>.</param>
    /// <param name="destination">Receives the block.</param>
    /// <param name="options">The level and dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>The size of the block.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too small for the block (a destination of <see cref="GetMaxCompressedLength"/> bytes never is), or <paramref name="source"/> is too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination, Lz4CompressionOptions? options = null)
    {
        if (!TryCompress(source, destination, out int written, options))
        {
            throw new ArgumentException("The destination is too small for the compressed block.", nameof(destination));
        }

        return written;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into one block, reporting whether it fit. Each call sets up a compressor and its match
    /// table (16 KiB at the default level); to compress many blocks, create one <see cref="Lz4BlockEncoder"/> and reuse it, which
    /// allocates nothing per call and writes the same bytes.
    /// </summary>
    /// <param name="source">The bytes to compress, at most <see cref="Lz4BlockCompressor.MaxInputLength"/>.</param>
    /// <param name="destination">Receives the block.</param>
    /// <param name="bytesWritten">The size of the block; meaningful only when the method returns true.</param>
    /// <param name="options">The level and dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when the block does not fit in <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is longer than one block can hold.</exception>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Lz4CompressionOptions? options = null)
    {
        int bound = GetMaxCompressedLength(source.Length);
        if (destination.Length >= bound)
        {
            bytesWritten = CompressCore(source, destination, options);
            return true;
        }

        // The compressor wants room for the worst case; use a scratch buffer and see whether the result fits.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bound);
        try
        {
            int n = CompressCore(source, buffer, options);
            bytesWritten = n;
            if (n > destination.Length)
            {
                return false;
            }

            buffer.AsSpan(0, n).CopyTo(destination);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int CompressCore(ReadOnlySpan<byte> source, Span<byte> destination, Lz4CompressionOptions? options)
    {
        options ??= Lz4CompressionOptions.Default;
        options.Validate();
        Lz4BlockCompressor compressor = Lz4BlockCompressor.Create(options.Level);
        compressor.SetDictionary(options.Dictionary);
        return compressor.Compress(source, 0, destination);
    }

    /// <summary>Decodes a block into a new array of exactly the decoded size.</summary>
    /// <param name="source">The compressed block, exactly.</param>
    /// <param name="options">The output limit and dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="CompressionFormatException">The block is truncated or invalid.</exception>
    /// <exception cref="CompressionLimitException">The block decodes to more than <see cref="Lz4BlockOptions.MaxOutputSize"/>; nothing was allocated for it.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, Lz4BlockOptions? options = null)
    {
        options ??= Lz4BlockOptions.Default;
        int history = options.Dictionary?.Length ?? 0;
        long size = Lz4BlockDecoder.GetDecompressedSize(source, history);
        if (size > options.MaxOutputSize)
        {
            Lz4Format.ThrowLimit("MaxOutputSize", options.MaxOutputSize, size);
        }

        byte[] result = GC.AllocateUninitializedArray<byte>((int)size);
        if (!TryDecompress(source, result, out int written, options) || written != size)
        {
            throw new InvalidOperationException("The block decoded to a different size than it measured.");
        }

        return result;
    }

    /// <summary>Decodes a block into <paramref name="destination"/>.</summary>
    /// <param name="source">The compressed block, exactly.</param>
    /// <param name="destination">Receives the decoded bytes.</param>
    /// <param name="options">The dictionary (the output limit does not apply); <see langword="null"/> for the defaults.</param>
    /// <returns>The number of bytes written, which may be less than the length of <paramref name="destination"/>.</returns>
    /// <exception cref="CompressionFormatException">The block is truncated or invalid.</exception>
    /// <exception cref="CompressionLimitException">The block decodes to more than fits in <paramref name="destination"/> (<c>LimitName</c> is <c>destination.Length</c>).</exception>
    public static int Decompress(ReadOnlySpan<byte> source, Span<byte> destination, Lz4BlockOptions? options = null)
    {
        if (!TryDecompress(source, destination, out int written, options))
        {
            Lz4Format.ThrowLimit("destination.Length", destination.Length, (long)destination.Length + 1);
        }

        return written;
    }

    /// <summary>Decodes a block into <paramref name="destination"/>, reporting whether it fit.</summary>
    /// <param name="source">The compressed block, exactly.</param>
    /// <param name="destination">Receives the decoded bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; meaningful only when the method returns true.</param>
    /// <param name="options">The dictionary; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small.</returns>
    /// <exception cref="CompressionFormatException">The block is truncated or invalid.</exception>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Lz4BlockOptions? options = null)
    {
        Lz4Dictionary? dictionary = options?.Dictionary;
        if (dictionary is null || dictionary.Length == 0)
        {
            return Lz4BlockDecoder.TryDecode(source, destination, 0, out bytesWritten);
        }

        // The dictionary is read in place: copying it next to the output would cost time proportional to its size for every block.
        return Lz4BlockDecoder.TryDecodeWithDictionary(source, destination, dictionary.Content, out bytesWritten);
    }
}
