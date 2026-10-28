using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
using LzipApi = RecursiveAI.OneCompress.Lzip.Lzip;
using LzmaApi = RecursiveAI.OneCompress.Lzma.Lzma;
using XzApi = RecursiveAI.OneCompress.Xz.Xz;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the LZMA family decoders. Each decodes the input all at once and through the span decoder fed and drained in odd
/// pieces (and through the stream class where there is one), and treats any disagreement between the paths, or any exception outside the
/// library's own hierarchy, as a finding. The first byte of the input chooses options and, for <c>lzma-decode</c>, which format to read, so the
/// fuzzer need not guess a header to reach the data behind it.
/// </summary>
internal static class LzmaTargets
{
    private const int Limit = 1 << 22;

    public static FuzzTarget Lzma { get; } = new("lzma-decode", static data => RunLzma(data), static e => e is CompressionException);

    public static FuzzTarget Xz { get; } = new("xz-decode", static data => RunXz(data), static e => e is CompressionException);

    public static FuzzTarget Lzip { get; } = new("lzip-decode", static data => RunLzip(data), static e => e is CompressionException);

    private static void RunLzma(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
        {
            return;
        }

        byte mode = input[0];
        byte[] data = input[1..].ToArray();
        var options = new LzmaDecompressionOptions { MaxOutputSize = Limit, MaxDictionarySize = 1 << 26, AllowTrailingData = (mode & 1) != 0 };
        switch ((mode >> 1) % 3)
        {
            case 0:
                Compare(
                    () => LzmaApi.Decompress(data, options),
                    () => Pieces(new LzmaDecoder(options), data, single: true, options.AllowTrailingData),
                    () => Stream(new LzmaStream(new MemoryStream(data), options), data));
                break;
            case 1:
                {
                    // Raw LZMA2: the next byte is the dictionary property.
                    byte prop = (byte)(data[0] % 41);
                    byte[] body = data[1..];
                    Compare(
                        () => Lzma2.Decompress(body, prop, options),
                        () => Pieces(new Lzma2Decoder(prop, options), body, single: true, options.AllowTrailingData),
                        null);
                    break;
                }

            default:
                {
                    // Raw LZMA: the next byte holds lc/lp/pb, the size is unknown so an end marker is needed.
                    if (!LzmaProperties.TryParsePropertiesByte(data[0], out int lc, out int lp, out int pb))
                    {
                        return;
                    }

                    var props = new LzmaProperties(lc, lp, pb, 1u << (12 + (mode >> 4) % 12));
                    byte[] body = data[1..];
                    Compare(
                        () => OneShotRaw(props, body, options),
                        () => Pieces(LzmaDecoder.CreateRaw(props, -1, options), body, single: true, options.AllowTrailingData),
                        null);
                    break;
                }
        }
    }

    private static byte[] OneShotRaw(LzmaProperties props, byte[] body, LzmaDecompressionOptions options)
    {
        var decoder = LzmaDecoder.CreateRaw(props, -1, options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 << 16];
        int pos = 0;
        while (true)
        {
            OperationStatus status = decoder.Decompress(body.AsSpan(pos), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done)
            {
                if (pos < body.Length && !options.AllowTrailingData)
                {
                    throw new CompressionFormatException("lzma", pos, "the end of the data");
                }

                return output.ToArray();
            }

            if (status == OperationStatus.NeedMoreData)
            {
                decoder.ThrowIfIncomplete();
                return output.ToArray();
            }
        }
    }

    private static void RunXz(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        byte choice = input[0];
        byte[] data = input[1..].ToArray();
        var options = new XzDecompressionOptions
        {
            MaxOutputSize = Limit,
            MaxDictionarySize = 1 << 26,
            VerifyChecks = (choice & 1) == 0,
            AllowTrailingData = (choice & 2) != 0,
            AllowUnsupportedChecks = (choice & 4) != 0,
        };
        Compare(
            () => XzApi.Decompress(data, options),
            () => Pieces(new XzDecoder(options), data, single: false, options.AllowTrailingData),
            () => Stream(new XzStream(new MemoryStream(data), options), data));
    }

    private static void RunLzip(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        byte choice = input[0];
        byte[] data = input[1..].ToArray();
        var options = new LzipDecompressionOptions
        {
            MaxOutputSize = Limit,
            MaxDictionarySize = 1 << 26,
            VerifyChecksum = (choice & 1) == 0,
            AllowTrailingData = (choice & 2) != 0,
        };
        Compare(
            () => LzipApi.Decompress(data, options),
            () => Pieces(new LzipDecoder(options), data, single: false, options.AllowTrailingData),
            () => Stream(new LzipStream(new MemoryStream(data), options), data));
    }

    private static void Compare(Func<byte[]> whole, Func<byte[]> pieces, Func<byte[]>? stream)
    {
        (byte[]? a, string? aError) = Attempt(whole);
        (byte[]? b, string? bError) = Attempt(pieces);
        if ((aError is null) != (bError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {aError ?? "ok"}, pieces {bError ?? "ok"}");
        }

        if (a is not null && !a.AsSpan().SequenceEqual(b))
        {
            throw new InvalidOperationException("paths produced different output");
        }

        if (stream is null)
        {
            return;
        }

        (byte[]? c, string? cError) = Attempt(stream);
        if ((aError is null) != (cError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {aError ?? "ok"}, stream {cError ?? "ok"}");
        }

        if (a is not null && !a.AsSpan().SequenceEqual(c))
        {
            throw new InvalidOperationException("stream produced different output");
        }
    }

    private static (byte[]?, string?) Attempt(Func<byte[]> decode)
    {
        try
        {
            return (decode(), null);
        }
        catch (CompressionException e)
        {
            return (null, e.GetType().Name);
        }
    }

    // A span decoder fed and drained in pieces whose sizes come from the data. The callers' one-shot paths treat an
    // empty input as truncated and stop at the first Done for single-stream formats; this mirrors that.
    private static byte[] Pieces<T>(T decoder, byte[] data, bool single, bool allowTrailing)
        where T : class
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("input", 0, "a stream", isTruncated: true);
        }

        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 3000)];
        int step = 1 + (data[^1] * 11 % 700);
        int pos = 0;
        while (true)
        {
            OperationStatus status = Step(decoder, data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done)
            {
                if (single)
                {
                    if (pos < data.Length && !allowTrailing)
                    {
                        throw new CompressionFormatException("input", pos, "the end of the data");
                    }

                    return output.ToArray();
                }

                if (c == 0 && w == 0)
                {
                    return output.ToArray();
                }
            }

            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < data.Length))
            {
                break;
            }

            if (c == 0 && w == 0 && status == OperationStatus.NeedMoreData && step < data.Length - pos)
            {
                step++;
            }
        }

        Finish(decoder);
        return output.ToArray();
    }

    private static OperationStatus Step<T>(T decoder, ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written)
    {
        return decoder switch
        {
            LzmaDecoder d => d.Decompress(source, destination, out consumed, out written),
            Lzma2Decoder d => d.Decompress(source, destination, out consumed, out written),
            XzDecoder d => d.Decompress(source, destination, out consumed, out written),
            LzipDecoder d => d.Decompress(source, destination, out consumed, out written),
            _ => throw new InvalidOperationException(),
        };
    }

    private static void Finish<T>(T decoder)
    {
        switch (decoder)
        {
            case LzmaDecoder d: d.ThrowIfIncomplete(); break;
            case Lzma2Decoder d: d.ThrowIfIncomplete(); break;
            case XzDecoder d: d.ThrowIfIncomplete(); break;
            case LzipDecoder d: d.ThrowIfIncomplete(); break;
        }
    }

    private static byte[] Stream(Stream stream, byte[] data)
    {
        using (stream)
        {
            var output = new MemoryStream();
            byte[] buffer = new byte[1 + (data.Length > 0 ? data[0] * 53 % 5000 : 0)];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }
    }
}
