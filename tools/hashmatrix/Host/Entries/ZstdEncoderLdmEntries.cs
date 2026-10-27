using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The Zstandard encoder with long-distance matching, at levels 1 and 3, one-shot and streamed (the stream's buffer slides several
/// times on the larger inputs). The inputs are doubled with a filler between the copies, so the second copy lies beyond the level's own
/// window and only the long-distance matcher can reach it.
/// </summary>
internal static class ZstdEncoderLdmEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int level in new[] { 1, 3, 6, 12 })
        {
            int l = level;
            Matrix.Add("zstd-encode", $"ldm-off-level{l}-doubled", (input, _) => Zstd.Compress(Doubled(input), new ZstdCompressionOptions { Level = l, WindowLog = 22 }), "text-64k", "text-3m", "binary-1m");
            Matrix.Add("zstd-encode", $"ldm-on-level{l}-doubled", (input, _) => Zstd.Compress(Doubled(input), new ZstdCompressionOptions { Level = l, WindowLog = 22, LongDistanceMatching = true }), "text-64k", "text-3m", "binary-1m", "runs-1m");
            Matrix.Add("zstd-encode", $"ldm-on-level{l}-default-window", (input, _) => Zstd.Compress(input, new ZstdCompressionOptions { Level = l, LongDistanceMatching = true, Checksum = true }), "empty", "one-byte", "text-small", "text-3m", "zeros-4m", "random-1m");
            Matrix.Add("zstd-encode", $"ldm-on-level{l}-tuned", (input, _) => Zstd.Compress(Doubled(input), new ZstdCompressionOptions { Level = l, WindowLog = 21, LongDistanceMatching = true, LongDistanceHashLog = 12, LongDistanceMinMatch = 32, LongDistanceBucketSizeLog = 3, LongDistanceHashRateLog = 5 }), "text-3m", "binary-1m");
            Matrix.Add("zstd-encode", $"ldm-on-level{l}-stream", (input, _) => Stream(Doubled(input), new ZstdCompressionOptions { Level = l, WindowLog = 20, LongDistanceMatching = true, ContentSize = Doubled(input).Length }), "text-3m", "binary-1m");
        }

        Matrix.Add("zstd-encode", "ldm-on-level3-stream-unknown-size", (input, _) => Stream(Doubled(input), new ZstdCompressionOptions { Level = 3, WindowLog = 20, LongDistanceMatching = true }), "text-3m", "binary-1m");
    }

    // The input, 600 KB of unrelated bytes, and the input again.
    internal static byte[] Doubled(ReadOnlySpan<byte> input)
    {
        byte[] filler = Inputs.Get("random-1m");
        byte[] data = new byte[(2 * input.Length) + 600_000];
        input.CopyTo(data);
        filler.AsSpan(0, 600_000).CopyTo(data.AsSpan(input.Length));
        input.CopyTo(data.AsSpan(input.Length + 600_000));
        return data;
    }

    internal static byte[] Stream(ReadOnlySpan<byte> input, ZstdCompressionOptions options)
    {
        var encoder = new ZstdEncoder(options);
        var result = new MemoryStream();
        byte[] buffer = new byte[10007];
        int position = 0;
        int piece = 77777;
        while (true)
        {
            int n = Math.Min(piece, input.Length - position);
            bool last = position + n == input.Length;
            int taken = 0;
            while (true)
            {
                OperationStatus status = encoder.Compress(input.Slice(position + taken, n - taken), buffer, out int consumed, out int written, last);
                taken += consumed;
                result.Write(buffer, 0, written);
                if (status == OperationStatus.Done)
                {
                    break;
                }
            }

            position += n;
            if (last)
            {
                return result.ToArray();
            }
        }
    }
}
