using System.Buffers;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Deflate64;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Fuzz targets for Deflate64. The decoder target decodes the input three ways, all at once, through the span decoder fed and
/// drained in odd pieces, and through <see cref="Deflate64Stream"/>, and treats any disagreement, or any exception outside the
/// library's own hierarchy, as a finding. The first byte chooses the options (an output limit, trailing data). The round-trip
/// target lets the input pick the level, the write sizes, the flushes and the output buffer size, and expands the rest into data
/// with long runs and far repeats; output our decoder does not turn back into the input is a finding, and so is a difference
/// between the one-call and the streaming output when no flush is involved.
/// </summary>
internal static class Deflate64Targets
{
    public static FuzzTarget Decode { get; } = new("deflate64-decode", static data => RunDecode(data), static e => e is CompressionException);

    public static FuzzTarget RoundTrip { get; } = new("deflate64-encode-roundtrip", static data => RunRoundTrip(data), static _ => false);

    private static Deflate64DecompressionOptions Options(byte choice) => new()
    {
        AllowTrailingData = (choice & 1) != 0,
        MaxOutputSize = (choice & 2) != 0 ? 1 << 16 : 1 << 24,
    };

    private static void RunDecode(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        Deflate64DecompressionOptions options = Options(input[0]);
        byte[] data = input[1..].ToArray();
        (byte[]? whole, string? wholeError) = Attempt(() => Deflate64.Decompress(data, options));
        (byte[]? pieces, string? piecesError) = Attempt(() => Pieces(data, options));
        (byte[]? streamed, string? streamError) = Attempt(() => ThroughStream(data, options));
        if ((wholeError is null) != (piecesError is null) || (wholeError is null) != (streamError is null))
        {
            throw new InvalidOperationException($"paths disagree: one-shot {wholeError ?? "ok"}, pieces {piecesError ?? "ok"}, stream {streamError ?? "ok"}");
        }

        if (whole is not null && (!whole.AsSpan().SequenceEqual(pieces) || !whole.AsSpan().SequenceEqual(streamed)))
        {
            throw new InvalidOperationException("paths produced different output");
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

    private static byte[] Pieces(byte[] data, Deflate64DecompressionOptions options)
    {
        if (data.Length == 0)
        {
            throw new CompressionFormatException("deflate64", 0, "a deflate64 stream", isTruncated: true);
        }

        var decoder = new Deflate64Decoder(options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data[0] * 37 % 3000)];
        int pos = 0;
        int step = 1 + (data[^1] * 11 % 700);
        while (true)
        {
            OperationStatus status = decoder.Decompress(data.AsSpan(pos, Math.Min(step, data.Length - pos)), buffer, out int c, out int w);
            pos += c;
            output.Write(buffer, 0, w);
            if (status == OperationStatus.Done)
            {
                if (pos < data.Length && !options.AllowTrailingData)
                {
                    throw new CompressionFormatException("deflate64", pos, "the input to end with the stream");
                }

                return output.ToArray();
            }

            if (status == OperationStatus.NeedMoreData && pos >= data.Length)
            {
                decoder.ThrowIfIncomplete();
                throw new InvalidOperationException("the decoder wants more data but reports no error");
            }
        }
    }

    private static byte[] ThroughStream(byte[] data, Deflate64DecompressionOptions options)
    {
        using var stream = new Deflate64Stream(new MemoryStream(data), options);
        var output = new MemoryStream();
        byte[] buffer = new byte[1 + (data.Length > 0 ? data[0] * 53 % 5000 : 0)];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }

        return output.ToArray();
    }

    private static void RunRoundTrip(ReadOnlySpan<byte> input)
    {
        if (input.Length < 6)
        {
            return;
        }

        int level = input[0] % 10;
        int piece = 1 + (input[2] * 509 % 70_000);
        int outSize = 1 + (input[3] * 31 % 3000);
        int flushPlan = input[4];
        byte[] data = DeflateEncoderTargets.Expand(input[5..], level >= 8 ? 150_000 : 400_000);
        var options = new Deflate64Options { Level = level };

        byte[] whole = Deflate64.Compress(data, options);
        Check(whole, data, "one call");

        var encoder = new Deflate64Encoder(options);
        var compressed = new List<byte>();
        byte[] buffer = new byte[outSize];
        int offset = 0;
        int writes = 0;
        bool flushed = false;
        while (true)
        {
            int take = Math.Min(piece, data.Length - offset);
            bool last = offset + take == data.Length;
            ReadOnlySpan<byte> source = data.AsSpan(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                compressed.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            writes++;
            if (last)
            {
                break;
            }

            if ((flushPlan >> (writes % 4 * 2) & 3) == 3)
            {
                flushed = true;
                OperationStatus status;
                do
                {
                    status = encoder.Flush(buffer, out int written);
                    compressed.AddRange(buffer.AsSpan(0, written).ToArray());
                }
                while (status == OperationStatus.DestinationTooSmall);
            }
        }

        byte[] streamed = [.. compressed];
        Check(streamed, data, "streaming");
        if (!flushed && !whole.AsSpan().SequenceEqual(streamed))
        {
            throw new InvalidOperationException($"streaming output differs from the one-call output (level {level})");
        }
    }

    private static void Check(byte[] compressed, byte[] expected, string how)
    {
        byte[] back = Deflate64.Decompress(compressed);
        if (!back.AsSpan().SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{how}: round trip differs ({expected.Length} bytes)");
        }
    }
}
