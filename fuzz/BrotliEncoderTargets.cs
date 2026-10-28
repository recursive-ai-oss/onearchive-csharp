using System.Buffers;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.Fuzz;

/// <summary>
/// The fuzz target for the brotli encoder. The input picks the quality, the window, the size of the pieces the data is written in, the size
/// of the output buffer and where flushes fall, and the rest is expanded into data with long runs and far repeats. The output must decode
/// to the data; without flushes the span path must write the same bytes as the one-call path.
/// </summary>
internal static class BrotliEncoderTargets
{
    public static FuzzTarget RoundTrip { get; } = new("brotli-encode-roundtrip", static data => Run(data), static _ => false);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 6)
        {
            return;
        }

        int quality = input[0] % 12;
        int window = 10 + (input[1] % 15);
        int piece = 1 + (input[2] * 509 % 70_000);
        int outSize = 1 + (input[3] * 31 % 3000);
        int flushPlan = input[4];
        byte[] data = DeflateEncoderTargets.Expand(input[5..], quality >= 10 ? 30_000 : 200_000);
        var options = new BrotliOptions { Quality = quality, WindowBits = window };

        byte[] whole = BR.Compress(data, options);
        if (!BR.Decompress(whole).AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"one call: round trip differs ({data.Length} bytes, quality {quality}, window {window})");
        }

        var encoder = new BrotliEncoder(options);
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

            writes++;
        }

        byte[] spans = compressed.ToArray();
        if (!BR.Decompress(spans).AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"span path: round trip differs (quality {quality}, window {window}, flushed {flushed})");
        }

        if (!flushed && !whole.AsSpan().SequenceEqual(spans))
        {
            throw new InvalidOperationException($"span path differs from the one-call output (quality {quality}, window {window})");
        }
    }
}
