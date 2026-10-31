using System.Buffers;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// Decoding of the length-prefixed block written by <see cref="Lz4BlockEncoder.TryCompressPrefixed"/>: a varint of the uncompressed
/// length (7 bits per byte, least significant group first, high bit set on every byte but the last; at most 5 bytes and at most
/// 2^31-1), then one raw LZ4 block that runs to the end of the input. This is <b>not</b> an LZ4 frame, and LZ4 tools do not read it;
/// it saves the 11 fixed bytes of a frame for payloads too small to pay for them.
/// </summary>
/// <remarks>The declared length is checked against what the block really decodes to; a mismatch is a <see cref="CompressionFormatException"/>.</remarks>
public static class Lz4PrefixedBlock
{
    private const string Name = "lz4 length-prefixed block";

    /// <summary>Reads the uncompressed length at the start of <paramref name="source"/>.</summary>
    /// <param name="source">The length-prefixed block.</param>
    /// <param name="decompressedSize">The declared uncompressed length; 0 when the method returns false.</param>
    /// <param name="prefixLength">The number of bytes the length takes.</param>
    /// <returns>True when a well-formed length was read.</returns>
    public static bool TryGetDecompressedSize(ReadOnlySpan<byte> source, out int decompressedSize, out int prefixLength)
    {
        decompressedSize = 0;
        prefixLength = 0;
        long value = 0;
        for (int i = 0; i < 5 && i < source.Length; i++)
        {
            byte b = source[i];
            value |= (long)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                if (value > int.MaxValue)
                {
                    return false;
                }

                decompressedSize = (int)value;
                prefixLength = i + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>Decodes a length-prefixed block into a new array.</summary>
    /// <param name="source">The length-prefixed block, exactly.</param>
    /// <param name="options">The dictionary and the output limit; <see langword="null"/> for the defaults.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="CompressionFormatException">The length is missing or malformed, the block is invalid, or it does not decode to the declared length.</exception>
    /// <exception cref="CompressionLimitException">The declared length exceeds <see cref="Lz4BlockOptions.MaxOutputSize"/>; nothing is allocated for it.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, Lz4BlockOptions? options = null)
    {
        options ??= Lz4BlockOptions.Default;
        int size = ReadLength(source, out int prefix);
        if (size > options.MaxOutputSize)
        {
            Lz4Format.ThrowLimit(nameof(Lz4BlockOptions.MaxOutputSize), options.MaxOutputSize, size);
        }

        byte[] result = new byte[size];
        DecodeInto(source, prefix, size, result, options);
        return result;
    }

    /// <summary>Decodes a length-prefixed block into <paramref name="destination"/>. Allocation-free without a dictionary.</summary>
    /// <param name="source">The length-prefixed block, exactly.</param>
    /// <param name="destination">Receives the decoded bytes.</param>
    /// <param name="written">The number of bytes written (the declared length); 0 when the method returns false.</param>
    /// <param name="options">The dictionary (the output limit does not apply: the destination is the limit); <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when the declared length is more than <paramref name="destination"/> holds.</returns>
    /// <exception cref="CompressionFormatException">The length is missing or malformed, the block is invalid, or it does not decode to the declared length.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int written, Lz4BlockOptions? options = null)
    {
        written = 0;
        int size = ReadLength(source, out int prefix);
        if (size > destination.Length)
        {
            return false;
        }

        DecodeInto(source, prefix, size, destination, options);
        written = size;
        return true;
    }

    // Writes the varint of length into destination; returns its size, or 0 when destination is too small.
    internal static int WriteLength(Span<byte> destination, int length)
    {
        int n = 0;
        uint v = (uint)length;
        while (v >= 0x80)
        {
            if (n >= destination.Length)
            {
                return 0;
            }

            destination[n++] = (byte)(v | 0x80);
            v >>= 7;
        }

        if (n >= destination.Length)
        {
            return 0;
        }

        destination[n++] = (byte)v;
        return n;
    }

    private static int ReadLength(ReadOnlySpan<byte> source, out int prefix)
    {
        if (!TryGetDecompressedSize(source, out int size, out prefix))
        {
            if (source.Length < 5 && (source.IsEmpty || (source[^1] & 0x80) != 0))
            {
                throw new CompressionFormatException(Name, source.Length, "a varint uncompressed length", isTruncated: true);
            }

            throw new CompressionFormatException(Name, 0, "an uncompressed length of at most 2^31-1 in at most 5 bytes");
        }

        return size;
    }

    private static void DecodeInto(ReadOnlySpan<byte> source, int prefix, int size, Span<byte> destination, Lz4BlockOptions? options)
    {
        ReadOnlySpan<byte> block = source[prefix..];
        Lz4Dictionary? dictionary = options?.Dictionary;
        int written;
        bool ok;
        if (dictionary is null || dictionary.Length == 0)
        {
            ok = Lz4BlockDecoder.TryDecode(block, destination[..size], 0, out written);
        }
        else
        {
            int history = dictionary.Length;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(checked(history + size));
            try
            {
                dictionary.Content.CopyTo(buffer);
                ok = Lz4BlockDecoder.TryDecode(block, buffer.AsSpan(0, history + size), history, out written);
                if (ok)
                {
                    buffer.AsSpan(history, written).CopyTo(destination);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        if (!ok)
        {
            throw new CompressionFormatException(Name, prefix, $"a block that decodes to the declared {size} bytes, but it decodes to more");
        }

        if (written != size)
        {
            throw new CompressionFormatException(Name, prefix, $"a block that decodes to the declared {size} bytes, but it decodes to {written}");
        }
    }
}
