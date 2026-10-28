using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// One-call decompression of bzip2 data. Everything here is built on <see cref="BZip2Decoder"/>. A buffer may hold several streams
/// one after the other (as <c>cat a.bz2 b.bz2</c> or a parallel compressor writes them); their outputs are joined.
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class BZip2
{
    // The first stage run-length coding expands 5 bytes to at most 259, and the Huffman and move-to-front stages
    // pack a run of equal bytes far tighter still, so there is no useful bound on the expansion; start small and double.
    private const int InitialFactor = 4;

    /// <summary>Decompresses a buffer holding one or more bzip2 streams.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="options">Limits and checks; <see langword="null"/> for <see cref="BZip2DecompressionOptions.Default"/>.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, a CRC does not match, or input that is not a stream follows the last one (see <see cref="BZip2DecompressionOptions.AllowTrailingData"/>).</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="BZip2DecompressionOptions.MaxOutputSize"/>; the output buffer never grows past the limit.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, BZip2DecompressionOptions? options = null)
    {
        options ??= BZip2DecompressionOptions.Default;
        var decoder = new BZip2Decoder(options);
        if (source.IsEmpty)
        {
            throw new CompressionFormatException("bzip2", 0, "a bzip2 stream", isTruncated: true);
        }

        long hint = Math.Min(Math.Min((long)InitialFactor * source.Length + 64, options.MaxOutputSize), 1 << 20);
        byte[] output = new byte[Math.Min(hint, Array.MaxLength)];
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
                    if (consumed == source.Length || !MoreStreams(source[consumed..], options))
                    {
                        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
                    }

                    break;
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("Unreachable: the decoder reported more data needed at a boundary.");
                default: // DestinationTooSmall: grow
                    if (output.Length >= Array.MaxLength || output.Length >= options.MaxOutputSize)
                    {
                        throw new CompressionLimitException("bzip2", nameof(BZip2DecompressionOptions.MaxOutputSize), Array.MaxLength, (long)written + 1);
                    }

                    Array.Resize(ref output, (int)Math.Min(Math.Min((long)output.Length * 2, Array.MaxLength), options.MaxOutputSize));
                    break;
            }
        }
    }

    /// <summary>Decompresses a buffer holding one or more bzip2 streams into a caller-supplied buffer.</summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written, or 0 when the destination is too small.</param>
    /// <param name="options">Limits and checks; <see langword="null"/> for the defaults.</param>
    /// <returns>True on success; false when <paramref name="destination"/> is too small for the whole output.</returns>
    /// <exception cref="CompressionFormatException">The data is empty, truncated or corrupt, a CRC does not match, or input that is not a stream follows the last one.</exception>
    /// <exception cref="CompressionLimitException">The output passes <see cref="BZip2DecompressionOptions.MaxOutputSize"/>.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, BZip2DecompressionOptions? options = null)
    {
        options ??= BZip2DecompressionOptions.Default;
        var decoder = new BZip2Decoder(options);
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            throw new CompressionFormatException("bzip2", 0, "a bzip2 stream", isTruncated: true);
        }

        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source[consumed..], destination[written..], out int c, out int w);
            consumed += c;
            written += w;
            switch (status)
            {
                case OperationStatus.Done:
                    if (consumed == source.Length || !MoreStreams(source[consumed..], options))
                    {
                        bytesWritten = written;
                        return true;
                    }

                    break;
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("Unreachable: the decoder reported more data needed at a boundary.");
                default:
                    return false;
            }
        }
    }

    // After a finished stream with input left: another stream follows if it starts like one. Anything else is trailing data,
    // which is an error unless allowed; the decoder then reports it as a bad signature.
    internal static bool MoreStreams(ReadOnlySpan<byte> rest, BZip2DecompressionOptions options)
    {
        if (!options.AllowTrailingData)
        {
            return true;
        }

        ReadOnlySpan<byte> magic = "BZh"u8;
        int n = Math.Min(rest.Length, magic.Length);
        return rest[..n].SequenceEqual(magic[..n]);
    }
}
