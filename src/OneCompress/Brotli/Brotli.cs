using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>One-call brotli compression and decompression (RFC 7932).</summary>
/// <remarks>
/// <para>Compression is built on <see cref="BrotliEncoder"/> and decompression on <see cref="BrotliDecoder"/>. The decompression methods decode exactly one stream: the input must be a complete brotli
/// stream, and bytes after its last meta-block raise <see cref="CompressionFormatException"/> unless
/// <see cref="BrotliDecompressionOptions.AllowTrailingData"/> is set.</para>
/// <para>Nothing the stream declares is trusted: the window is checked against
/// <see cref="BrotliDecompressionOptions.MaxWindowSize"/> and every meta-block against
/// <see cref="BrotliDecompressionOptions.MaxOutputSize"/> before memory is committed for it. The class has the name of its namespace as
/// the other codecs' do; the BCL's <c>System.IO.Compression.BrotliDecoder</c> and <c>BrotliStream</c> are different types.</para>
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class Brotli
{
    /// <summary>Compresses data to a complete brotli stream.</summary>
    /// <param name="source">The data.</param>
    /// <param name="options">The quality and window; <see langword="null"/> for <see cref="BrotliOptions.Default"/> (quality 11, 22 window bits).</param>
    /// <returns>The compressed stream.</returns>
    public static byte[] Compress(ReadOnlySpan<byte> source, BrotliOptions? options = null)
    {
        var encoder = new BrotliEncoder(options);
        var result = new List<byte[]>();
        byte[] chunk = new byte[(int)Math.Min(Math.Max(source.Length / 2, 4096), 1 << 20)];
        int consumed = 0;
        long total = 0;
        while (true)
        {
            OperationStatus status = encoder.Compress(source.Slice(consumed), chunk, out int c, out int w, isFinalBlock: true);
            consumed += c;
            if (w > 0)
            {
                result.Add(chunk.AsSpan(0, w).ToArray());
                total += w;
            }

            if (status == OperationStatus.Done)
            {
                break;
            }
        }

        byte[] output = new byte[total];
        int at = 0;
        foreach (byte[] part in result)
        {
            part.CopyTo(output, at);
            at += part.Length;
        }

        return output;
    }

    /// <summary>Compresses data to a complete brotli stream in a buffer the caller provides.</summary>
    /// <param name="source">The data.</param>
    /// <param name="destination">Receives the compressed stream; <see cref="BrotliEncoder.GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="bytesWritten">The number of bytes written; 0 when the method returns false.</param>
    /// <param name="options">The quality and window; <see langword="null"/> for <see cref="BrotliOptions.Default"/>.</param>
    /// <returns>True if the whole stream fits in <paramref name="destination"/>.</returns>
    public static bool TryCompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, BrotliOptions? options = null)
    {
        var encoder = new BrotliEncoder(options);
        OperationStatus status = encoder.Compress(source, destination, out _, out int written, isFinalBlock: true);
        bytesWritten = status == OperationStatus.Done ? written : 0;
        return status == OperationStatus.Done;
    }

    /// <summary>Decompresses a complete brotli stream.</summary>
    /// <param name="source">The compressed stream.</param>
    /// <param name="options">Limits and settings; <see langword="null"/> for the defaults.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="CompressionFormatException">The input is not a valid, complete brotli stream (<see cref="CompressionFormatException.IsTruncated"/> is set if it ends early), or bytes follow the stream and trailing data is not allowed.</exception>
    /// <exception cref="CompressionLimitException">The window or the output exceeds a limit in <paramref name="options"/>.</exception>
    public static byte[] Decompress(ReadOnlySpan<byte> source, BrotliDecompressionOptions? options = null)
    {
        options ??= BrotliDecompressionOptions.Default;
        var decoder = new BrotliDecoder(options);
        long hint = Math.Min(Math.Min((4L * source.Length) + 64, options.MaxOutputSize), 1 << 20);
        byte[] output = new byte[Math.Max(hint, 64)];
        int consumed = 0;
        int written = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(source.Slice(consumed), output.AsSpan(written), out int c, out int w);
            consumed += c;
            written += w;
            switch (status)
            {
                case OperationStatus.Done:
                    CheckTrailing(source, consumed, options);
                    return written == output.Length ? output : output.AsSpan(0, written).ToArray();
                case OperationStatus.NeedMoreData:
                    decoder.ThrowIfIncomplete();
                    throw new InvalidOperationException("Unreachable: the decoder needed data but reports a complete stream.");
                default:
                    if (output.Length >= Array.MaxLength)
                    {
                        throw new CompressionLimitException(BrotliDecoder.FormatName, nameof(BrotliDecompressionOptions.MaxOutputSize), Array.MaxLength, (long)written + 1);
                    }

                    Array.Resize(ref output, (int)Math.Min((long)output.Length * 2, Array.MaxLength));
                    break;
            }
        }
    }

    /// <summary>Decompresses a complete brotli stream into a buffer the caller provides.</summary>
    /// <param name="source">The compressed stream.</param>
    /// <param name="destination">Receives the decompressed bytes.</param>
    /// <param name="bytesWritten">The number of bytes written; 0 when the method returns false.</param>
    /// <param name="options">Limits and settings; <see langword="null"/> for the defaults.</param>
    /// <returns>True if the whole stream was decompressed into <paramref name="destination"/>; false if the destination is too small.</returns>
    /// <exception cref="CompressionFormatException">The input is not a valid, complete brotli stream, or bytes follow it and trailing data is not allowed.</exception>
    /// <exception cref="CompressionLimitException">The window or the output exceeds a limit in <paramref name="options"/>.</exception>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, BrotliDecompressionOptions? options = null)
    {
        options ??= BrotliDecompressionOptions.Default;
        var decoder = new BrotliDecoder(options);
        bytesWritten = 0;
        OperationStatus status = decoder.Decompress(source, destination, out int consumed, out int written);
        switch (status)
        {
            case OperationStatus.Done:
                CheckTrailing(source, consumed, options);
                bytesWritten = written;
                return true;
            case OperationStatus.NeedMoreData:
                decoder.ThrowIfIncomplete();
                throw new InvalidOperationException("Unreachable: the decoder needed data but reports a complete stream.");
            default:
                return false;
        }
    }

    private static void CheckTrailing(ReadOnlySpan<byte> source, int consumed, BrotliDecompressionOptions options)
    {
        if (!options.AllowTrailingData && consumed < source.Length)
        {
            throw new CompressionFormatException(BrotliDecoder.FormatName, consumed, "the end of the input after the last meta-block");
        }
    }
}
