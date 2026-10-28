using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// A reusable compressor for raw LZ4 blocks. It owns its match tables, so after construction every call allocates nothing; use it
/// instead of <see cref="Lz4Block.TryCompress"/> (which sets up a compressor on every call) when compressing many blocks, such as one
/// payload at a time. Each call produces an independent block that starts from the dictionary and nothing else, exactly as the
/// one-shot methods do, so the bytes are the same.
/// </summary>
/// <remarks>
/// <para>Besides plain blocks it writes the <em>length-prefixed block</em>: a varint of the uncompressed length (7 bits per byte,
/// least significant group first, high bit set on every byte but the last) followed by the block. That is not an LZ4 frame and no
/// LZ4 tool reads it; it is a compact envelope for payloads of a few dozen bytes or more, where a frame's 11 bytes of header and
/// end mark are a large part of the output. Decode it with <see cref="Lz4PrefixedBlock"/>.</para>
/// <para>An instance is not thread-safe.</para>
/// </remarks>
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class Lz4BlockEncoder
{
    private readonly Lz4BlockCompressor _compressor;
    private byte[] _scratch = [];

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The level and dictionary (the other options do not apply to blocks); <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is out of range.</exception>
    public Lz4BlockEncoder(Lz4CompressionOptions? options = null)
    {
        options ??= Lz4CompressionOptions.Default;
        options.Validate();
        _compressor = Lz4BlockCompressor.Create(options.Level);
        _compressor.SetDictionary(options.Dictionary);
    }

    /// <summary>Gets the most bytes a block of <paramref name="inputLength"/> bytes can compress to; a destination of this size always suffices.</summary>
    /// <param name="inputLength">The number of bytes to compress.</param>
    /// <returns>The bound, saturating at <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative.</exception>
    public static int GetMaxCompressedLength(int inputLength) => Lz4BlockWriter.GetMaxCompressedLength(inputLength);

    /// <summary>Gets the most bytes a length-prefixed block of <paramref name="inputLength"/> bytes can take: the block bound plus up to 5 bytes of length.</summary>
    /// <param name="inputLength">The number of bytes to compress.</param>
    /// <returns>The bound, saturating at <see cref="int.MaxValue"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputLength"/> is negative.</exception>
    public static int GetMaxPrefixedLength(int inputLength) => (int)Math.Min((long)int.MaxValue, (long)Lz4BlockWriter.GetMaxCompressedLength(inputLength) + 5);

    /// <summary>Compresses <paramref name="source"/> into one block, reporting whether it fit. Allocation-free once the encoder has seen an input of this size.</summary>
    /// <param name="source">The bytes to compress, at most <see cref="Lz4BlockCompressor.MaxInputLength"/>; may be empty (the block is then the single byte 0x00).</param>
    /// <param name="destination">Receives the block.</param>
    /// <param name="written">The size of the block; 0 when the method returns false.</param>
    /// <returns>True on success; false when the block does not fit in <paramref name="destination"/> (a destination of <see cref="GetMaxCompressedLength"/> bytes always suffices).</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is longer than one block can hold.</exception>
    public bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        int bound = GetMaxCompressedLength(source.Length);
        _compressor.Reset();
        if (destination.Length >= bound)
        {
            written = _compressor.Compress(source, 0, destination);
            return true;
        }

        // The compressor wants room for the worst case; a destination that is smaller may still hold the actual result.
        if (_scratch.Length < bound)
        {
            _scratch = new byte[bound];
        }

        int n = _compressor.Compress(source, 0, _scratch);
        if (n > destination.Length)
        {
            return false;
        }

        _scratch.AsSpan(0, n).CopyTo(destination);
        written = n;
        return true;
    }

    /// <summary>
    /// Compresses <paramref name="source"/> into a length-prefixed block: the uncompressed length as a varint, then the block.
    /// This is not an LZ4 frame; decode it with <see cref="Lz4PrefixedBlock"/>.
    /// </summary>
    /// <param name="source">The bytes to compress, at most <see cref="Lz4BlockCompressor.MaxInputLength"/>; may be empty.</param>
    /// <param name="destination">Receives the length and the block.</param>
    /// <param name="written">The total size; 0 when the method returns false.</param>
    /// <returns>True on success; false when the result does not fit in <paramref name="destination"/> (<see cref="GetMaxPrefixedLength"/> bytes always suffice).</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is longer than one block can hold.</exception>
    public bool TryCompressPrefixed(ReadOnlySpan<byte> source, Span<byte> destination, out int written)
    {
        written = 0;
        int prefix = Lz4PrefixedBlock.WriteLength(destination, source.Length);
        if (prefix == 0)
        {
            return false;
        }

        if (!TryCompress(source, destination[prefix..], out int n))
        {
            return false;
        }

        written = prefix + n;
        return true;
    }
}
