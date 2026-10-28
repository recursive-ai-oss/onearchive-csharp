using System.Buffers;
using RecursiveAI.OneCompress.BZip2;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;

namespace RecursiveAI.Fuzz;

/// <summary>
/// The fuzz target for the bzip2 encoder. The input picks the level, the longest code length, the size of the pieces the data is
/// written in and the size of the output buffer, and the rest is expanded into data with long runs and far repeats. The output must
/// decode to the data, and the one-call, span-by-span and stream paths must write the same bytes.
/// </summary>
internal static class BZip2EncoderTargets
{
    public static FuzzTarget RoundTrip { get; } = new("bzip2-encode-roundtrip", static data => Run(data), static _ => false);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 6)
        {
            return;
        }

        int level = 1 + (input[0] % 9);
        int maxLength = 9 + (input[1] % 12);
        int piece = 1 + (input[2] * 509 % 70_000);
        int outSize = 1 + (input[3] * 31 % 3000);
        byte[] data = DeflateEncoderTargets.Expand(input[5..], level <= 2 ? 450_000 : 200_000);
        var options = new BZip2Options { Level = level, MaxCodeLength = maxLength };

        byte[] whole = BZ.Compress(data, options);
        if (!BZ.Decompress(whole).AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException($"one call: round trip differs ({data.Length} bytes, level {level}, longest code {maxLength})");
        }

        var encoder = new BZip2Encoder(options);
        var spans = new List<byte>();
        byte[] buffer = new byte[outSize];
        int offset = 0;
        while (true)
        {
            int take = Math.Min(piece, data.Length - offset);
            bool last = offset + take == data.Length;
            ReadOnlySpan<byte> source = data.AsSpan(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                spans.AddRange(buffer.AsSpan(0, written).ToArray());
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
        }

        if (!whole.AsSpan().SequenceEqual(spans.ToArray()))
        {
            throw new InvalidOperationException($"span path differs from the one-call output (level {level})");
        }

        var sink = new MemoryStream();
        using (var stream = new BZip2Stream(sink, options, leaveOpen: true))
        {
            for (int at = 0; at < data.Length; at += piece)
            {
                stream.Write(data, at, Math.Min(piece, data.Length - at));
            }
        }

        if (!whole.AsSpan().SequenceEqual(sink.ToArray()))
        {
            throw new InvalidOperationException($"stream path differs from the one-call output (level {level})");
        }
    }
}
