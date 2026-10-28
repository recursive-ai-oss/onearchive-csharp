using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// brotli compression at every quality, at other window sizes, and written in pieces with flushes: the bytes must not depend on the
/// platform or vector support. The shortest-path qualities (10 and 11) run on the smaller inputs only.
/// </summary>
internal static class BrotliEncoderEntries
{
    private static readonly string[] s_small = ["empty", "one-byte", "text-small", "text-64k", "binary-1m"];

    [ModuleInitializer]
    internal static void Register()
    {
        for (int quality = 0; quality <= 11; quality++)
        {
            int q = quality;
            if (q >= 10)
            {
                Matrix.Add("brotli-encode", $"q{q}-w22", (input, _) => BR.Compress(input, new BrotliOptions { Quality = q, WindowBits = 22 }), s_small);
            }
            else
            {
                Matrix.Add("brotli-encode", $"q{q}-w22", (input, _) => BR.Compress(input, new BrotliOptions { Quality = q, WindowBits = 22 }));
            }
        }

        foreach (int window in new[] { 10, 16, 24 })
        {
            foreach (int quality in new[] { 1, 5, 9 })
            {
                int w = window;
                int q = quality;
                Matrix.Add("brotli-encode", $"q{q}-w{w}", (input, _) => BR.Compress(input, new BrotliOptions { Quality = q, WindowBits = w }), "empty", "text-64k", "binary-1m", "runs-1m");
            }
        }

        Matrix.Add("brotli-encode", "q3-pieces-flush", (input, _) => Pieces(input, 3), "text-64k", "binary-1m", "runs-1m");
        Matrix.Add("brotli-encode", "q6-pieces-flush", (input, _) => Pieces(input, 6), "text-64k", "binary-1m", "runs-1m");
    }

    private static byte[] Pieces(ReadOnlySpan<byte> input, int quality)
    {
        var encoder = new BrotliEncoder(new BrotliOptions { Quality = quality, WindowBits = 20 });
        var output = new List<byte>();
        byte[] buffer = new byte[777];
        int[] pieces = [1, 4999, 10000, 33333, 70001, 250000];
        int offset = 0;
        int step = 0;
        while (true)
        {
            int take = Math.Min(pieces[step % pieces.Length], input.Length - offset);
            bool last = offset + take == input.Length;
            ReadOnlySpan<byte> source = input.Slice(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, last);
                output.AddRange(buffer.AsSpan(0, written).ToArray());
                source = source[consumed..];
                if (status == OperationStatus.Done || (status == OperationStatus.NeedMoreData && source.IsEmpty))
                {
                    break;
                }
            }

            offset += take;
            step++;
            if (last)
            {
                return [.. output];
            }

            if (step % 3 == 0)
            {
                OperationStatus flush;
                do
                {
                    flush = encoder.Flush(buffer, out int written);
                    output.AddRange(buffer.AsSpan(0, written).ToArray());
                }
                while (flush == OperationStatus.DestinationTooSmall);
            }
        }
    }
}
