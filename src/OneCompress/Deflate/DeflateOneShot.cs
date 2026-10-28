using System.Buffers;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Deflate;

// The shared body of the one-line decompressors Deflate.Decompress, ZLib.Decompress and GZip.Decompress. Built on DeflateDecoder only.
internal static class DeflateOneShot
{
    // No Deflate stream expands by more than about 1032 to 1 (258 bytes from two bits), so a declared size above this
    // multiple of the input is not worth an up-front allocation.
    private const long MaxExpansion = 1100;

    public static byte[] Decompress(DeflateFormat format, ReadOnlySpan<byte> source, DeflateDecompressionOptions? options)
    {
        options ??= DeflateDecompressionOptions.Default;
        var decoder = new DeflateDecoder(format, options);
        string name = format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zlib", _ => "gzip" };
        if (source.IsEmpty)
        {
            throw new CompressionFormatException(name, 0, "a " + name + " stream", isTruncated: true);
        }

        long hint = 0;
        if (format == DeflateFormat.GZip && source.Length >= 18)
        {
            // ISIZE (RFC 1952 section 2.3.1) of the last member is a hint, trusted only up to a plausible expansion.
            long declared = BinaryPrimitives.ReadUInt32LittleEndian(source[^4..]);
            if (declared <= MaxExpansion * source.Length && declared <= options.MaxOutputSize)
            {
                hint = declared;
            }
        }

        if (hint == 0)
        {
            hint = Math.Min(Math.Min(4L * source.Length + 64, options.MaxOutputSize), 1 << 20);
        }

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
                    if (consumed == source.Length || !MoreUnits(format, source[consumed..], options))
                    {
                        // An output that was sized exactly (a correct gzip ISIZE) is returned as it is.
                        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
                    }

                    break;
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("Unreachable: the decoder reported more data needed at a boundary.");
                default: // DestinationTooSmall: grow
                    if (output.Length >= Array.MaxLength)
                    {
                        throw new CompressionLimitException(name, nameof(DeflateDecompressionOptions.MaxOutputSize), Array.MaxLength, (long)written + 1);
                    }

                    Array.Resize(ref output, (int)Math.Min((long)output.Length * 2, Array.MaxLength));
                    break;
            }
        }
    }

    // After a finished unit with input left: gzip continues with the next member; the other formats refuse trailing data.
    private static bool MoreUnits(DeflateFormat format, ReadOnlySpan<byte> rest, DeflateDecompressionOptions options)
    {
        if (format == DeflateFormat.GZip)
        {
            if (options.AllowTrailingData && !(rest.Length >= 2 && rest[0] == 0x1F && rest[1] == 0x8B))
            {
                return false;
            }

            return true; // the decoder will report a bad member
        }

        if (options.AllowTrailingData)
        {
            return false;
        }

        throw new CompressionFormatException(format == DeflateFormat.ZLib ? "zlib" : "deflate", 0, "the input to end with the stream");
    }

    public static bool TryDecompress(DeflateFormat format, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, DeflateDecompressionOptions? options)
    {
        options ??= DeflateDecompressionOptions.Default;
        var decoder = new DeflateDecoder(format, options);
        string name = format switch { DeflateFormat.Raw => "deflate", DeflateFormat.ZLib => "zlib", _ => "gzip" };
        bytesWritten = 0;
        if (source.IsEmpty)
        {
            throw new CompressionFormatException(name, 0, "a " + name + " stream", isTruncated: true);
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
                    if (consumed == source.Length || !MoreUnits(format, source[consumed..], options))
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
}
