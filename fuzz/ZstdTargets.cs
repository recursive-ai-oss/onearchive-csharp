using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the Zstandard decoder. Each decodes the input two ways and treats any disagreement between
/// them, or any exception outside the library's own hierarchy, as a finding.
/// </summary>
internal static class ZstdTargets
{
    private static readonly ZstdDictionary? s_formatted = Load("dict4.zdict");
    private static readonly ZstdDictionary? s_raw = Load("rawdict-small.bin");

    private static readonly ZstdDecompressionOptions s_plain = Options(null);

    // Small limits keep each execution cheap and still exercise every limit path.
    private static ZstdDecompressionOptions Options(ZstdDictionary? dictionary) => new()
    {
        MaxWindowSize = 1 << 22,
        MaxOutputSize = 1 << 24,
        Dictionary = dictionary,
        SkippableFrameHandler = static (_, _) => { },
        MaxSkippableFrameSize = 1 << 16,
    };

    private static ZstdDictionary? Load(string name)
    {
        string? dir = Environment.GetEnvironmentVariable("ZSTD_FUZZ_TESTDATA");
        if (dir is null)
        {
            return null;
        }

        string path = Path.Combine(dir, name);
        return File.Exists(path) ? ZstdDictionary.Parse(File.ReadAllBytes(path)) : null;
    }

    public static FuzzTarget Plain { get; } = new("zstd-decode", static data => Check(data.ToArray(), s_plain), IsExpected);

    public static FuzzTarget WithDictionary { get; } = new("zstd-decode-dict", static data =>
    {
        // The first byte picks the dictionary, so the fuzzer can mutate frames and choices together.
        if (data.IsEmpty)
        {
            return;
        }

        ZstdDictionary? d = (data[0] & 3) switch { 0 => null, 1 => s_formatted, _ => s_raw };
        Check(data[1..].ToArray(), Options(d));
    }, IsExpected);

    private static bool IsExpected(Exception e) => e is CompressionException;

    private static void Check(byte[] data, ZstdDecompressionOptions options)
    {
        // Way one: all at once. Way two: the streaming decoder fed and drained in odd pieces.
        (byte[]? whole, string? wholeError) = Run(() => Zstd.Decompress(data, options));
        (byte[]? pieces, string? piecesError) = Run(() => Chunked(data, options));
        if ((wholeError is null) != (piecesError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, chunked {piecesError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(pieces))
        {
            throw new InvalidOperationException("paths produced different output");
        }
    }

    private static (byte[]?, string?) Run(Func<byte[]> decode)
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

    private static byte[] Chunked(byte[] data, ZstdDecompressionOptions options)
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("zstd", 0, "a frame", isTruncated: true);
        }

        var decoder = new ZstdDecoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 5000)];
        int pos = 0;
        int step = 1 + (data[^1] * 11 % 700);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < data.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }
}
