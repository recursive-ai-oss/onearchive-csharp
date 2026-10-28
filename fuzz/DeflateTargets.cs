using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for the raw Deflate, zlib and gzip decoders. Each decodes the input two ways, all at once and through the span
/// decoder fed and drained in odd pieces, and treats any disagreement between them, or any exception outside the library's
/// own hierarchy, as a finding. The first byte of the input chooses options (checksum verification, a preset dictionary), so
/// the fuzzer does not have to guess checksums to reach the data behind them.
/// </summary>
internal static class DeflateTargets
{
    private static readonly byte[] s_dictionary = MakeDictionary();

    private static byte[] MakeDictionary()
    {
        byte[] data = new byte[6000];
        var r = new Random(9);
        string[] words = ["tick", "entity", "pos", "health", "state", "item", "count", "chunk"];
        int at = 0;
        while (at < data.Length)
        {
            string w = words[r.Next(words.Length)] + " ";
            for (int i = 0; i < w.Length && at < data.Length; i++)
            {
                data[at++] = (byte)w[i];
            }
        }

        return data;
    }

    // Small limits keep each execution cheap and still exercise every limit path.
    private static DeflateDecompressionOptions Options(byte choice) => new()
    {
        MaxOutputSize = 1 << 22,
        VerifyChecksum = (choice & 1) == 0,
        Dictionary = (choice & 2) != 0 ? s_dictionary : default,
        MaxHeaderFieldSize = (choice & 4) != 0 ? 64 : 65535,
    };

    public static FuzzTarget Raw { get; } = new("deflate-decode", static data => Run(DeflateFormat.Raw, data), IsExpected);

    public static FuzzTarget ZLib { get; } = new("zlib-decode", static data => Run(DeflateFormat.ZLib, data), IsExpected);

    public static FuzzTarget GZip { get; } = new("gzip-decode", static data => Run(DeflateFormat.GZip, data), IsExpected);

    private static bool IsExpected(Exception e) => e is CompressionException;

    private static void Run(DeflateFormat format, ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        DeflateDecompressionOptions options = Options(input[0]);
        byte[] data = Repair(format, input[1..].ToArray());
        (byte[]? whole, string? wholeError) = Attempt(() => OneShot(format, data, options));
        (byte[]? pieces, string? piecesError) = Attempt(() => Pieces(format, data, options));
        if ((wholeError is null) != (piecesError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, pieces {piecesError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(pieces))
        {
            throw new InvalidOperationException("paths produced different output");
        }

        (byte[]? streamed, string? streamError) = Attempt(() => ThroughStream(format, data, options));
        if ((wholeError is null) != (streamError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, stream {streamError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(streamed))
        {
            throw new InvalidOperationException("stream produced different output");
        }
    }

    // Mutated headers would nearly always fail the header checks; repairing the check bits lets the fuzzer reach the blocks.
    private static byte[] Repair(DeflateFormat format, byte[] d)
    {
        if (format == DeflateFormat.ZLib && d.Length >= 2)
        {
            // CMF: method 8, window of at most 32 KiB; FLG: keep FDICT and FLEVEL, set FCHECK so the pair is a multiple of 31.
            d[0] = (byte)((d[0] & 0x70) | 8);
            int flg = d[1] & 0xE0;
            d[1] = (byte)(flg | ((31 - (((d[0] << 8) | flg) % 31)) % 31));
            if ((flg & 0x20) != 0 && d.Length >= 6)
            {
                // Name the dictionary the options carry, so the fuzzer reaches the blocks behind it.
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(2), RecursiveAI.OneCompress.Checksums.Adler32.Compute(s_dictionary));
            }
        }
        else if (format == DeflateFormat.GZip && d.Length >= 4)
        {
            d[0] = 0x1F;
            d[1] = 0x8B;
            d[2] = 8;
            d[3] &= 0x1F;
        }

        return d;
    }

    private static byte[] OneShot(DeflateFormat format, byte[] data, DeflateDecompressionOptions options) => format switch
    {
        DeflateFormat.Raw => Deflate.Decompress(data, options),
        DeflateFormat.ZLib => RecursiveAI.OneCompress.Deflate.ZLib.Decompress(data, options),
        _ => RecursiveAI.OneCompress.Deflate.GZip.Decompress(data, options),
    };

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

    // The span decoder with input and output cut at sizes taken from the data itself. One-shot decoding stops at the end
    // of the first stream for raw Deflate and zlib and refuses what follows, so the pieces path does the same.
    private static byte[] Pieces(DeflateFormat format, byte[] data, DeflateDecompressionOptions options)
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("deflate", 0, "a stream", isTruncated: true);
        }

        var decoder = new DeflateDecoder(format, options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 3000)];
        int pos = 0;
        int step = 1 + (data[^1] * 11 % 700);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done && pos < data.Length && format != DeflateFormat.GZip && !options.AllowTrailingData)
            {
                throw new CompressionFormatException("deflate", pos, "the input to end with the stream");
            }

            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < data.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    private static byte[] ThroughStream(DeflateFormat format, byte[] data, DeflateDecompressionOptions options)
    {
        using var stream = new DeflateCodecStream(new MemoryStream(data), format, options);
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
