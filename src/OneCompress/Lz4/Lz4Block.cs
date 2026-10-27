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
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, Lz4BlockOptions? options = null)
    {
        Lz4Dictionary? dictionary = options?.Dictionary;
        if (dictionary is null || dictionary.Length == 0)
        {
            return Lz4BlockDecoder.TryDecode(source, destination, 0, out bytesWritten);
        }

        int history = dictionary.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(history + destination.Length));
        try
        {
            dictionary.Content.CopyTo(buffer);
            bool ok = Lz4BlockDecoder.TryDecode(source, buffer.AsSpan(0, history + destination.Length), history, out bytesWritten);
            if (ok)
            {
                buffer.AsSpan(history, bytesWritten).CopyTo(destination);
            }

            return ok;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
