using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.BZip2;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;

namespace RecursiveAI.Fuzz;

/// <summary>
/// The fuzz target for the bzip2 decoder. It decodes the input three ways, all at once, through the span decoder fed and drained
/// in odd pieces, and through the read-mode stream, and treats any disagreement between them, or any exception outside the
/// library's own hierarchy, as a finding. The first byte of the input chooses options (checksum verification, trailing data), so
/// the fuzzer need not guess CRCs to reach the data behind them; when the checksum is off the fuzzer's blocks are decoded in full.
/// </summary>
internal static class BZip2Targets
{
    // Small limits keep each execution cheap and still exercise the limit path.
    private static BZip2DecompressionOptions Options(byte choice) => new()
    {
        MaxOutputSize = 1 << 22,
        VerifyChecksum = (choice & 1) == 0,
        AllowTrailingData = (choice & 2) != 0,
    };

    public static FuzzTarget Decode { get; } = new("bzip2-decode", static data => Run(data), static e => e is CompressionException);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        BZip2DecompressionOptions options = Options(input[0]);
        byte[] data = input[1..].ToArray();
        (byte[]? whole, string? wholeError) = Attempt(() => BZ.Decompress(data, options));
        (byte[]? pieces, string? piecesError) = Attempt(() => Pieces(data, options));
        if ((wholeError is null) != (piecesError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, pieces {piecesError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(pieces))
        {
            throw new InvalidOperationException("paths produced different output");
        }

        (byte[]? streamed, string? streamError) = Attempt(() => ThroughStream(data, options));
        if ((wholeError is null) != (streamError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, stream {streamError ?? "ok"}");
        }

        if (whole is not null && !whole.AsSpan().SequenceEqual(streamed))
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

    // The span decoder with input and output cut at sizes taken from the data itself. Like the one-shot path, it decodes streams one
    // after the other and stops at trailing data that does not start with the signature when that is allowed.
    private static byte[] Pieces(byte[] data, BZip2DecompressionOptions options)
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("bzip2", 0, "a bzip2 stream", isTruncated: true);
        }

        var decoder = new BZip2Decoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 3000)];
        int pos = 0;
        int step = 1 + (data[^1] * 11 % 700);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done && pos < data.Length && options.AllowTrailingData && !StartsLikeAStream(data.AsSpan(pos)))
            {
                break;
            }

            if (pos >= data.Length && status != OperationStatus.DestinationTooSmall && !(status == OperationStatus.Done && pos < data.Length))
            {
                break;
            }
        }

        decoder.ThrowIfIncomplete();
        return output.ToArray();
    }

    private static bool StartsLikeAStream(ReadOnlySpan<byte> rest)
    {
        ReadOnlySpan<byte> magic = "BZh"u8;
        int n = Math.Min(rest.Length, magic.Length);
        return rest[..n].SequenceEqual(magic[..n]);
    }

    private static byte[] ThroughStream(byte[] data, BZip2DecompressionOptions options)
    {
        using var stream = new BZip2Stream(new MemoryStream(data), options);
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
