using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The Zstandard encoder at the optimal-parsing levels (16 to 22, and 11, 13, 15 whose small-source rows use them), over every standard input, one-shot; with a
/// formatted dictionary and with a raw-content dictionary; with checksum and no content size; and streamed in odd pieces (which
/// exercises the sliding window). The compressed bytes must be the same on every platform and vector width.
/// </summary>
internal static class ZstdEncoderOptEntries
{
    private static readonly int[] s_levels = [11, 13, 15, 16, 18, 19, 20, 22];

    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int level in s_levels)
        {
            int l = level;
            Matrix.Add("zstd-encode-opt", $"level{l}", (input, _) => Zstd.Compress(input, new ZstdCompressionOptions { Level = l }));
            Matrix.Add("zstd-encode-opt", $"level{l}-checksum-nosize", (input, _) => Zstd.Compress(input, new ZstdCompressionOptions { Level = l, Checksum = true, WriteContentSize = false }), "text-64k", "binary-1m", "runs-1m");
            Matrix.Add("zstd-encode-opt", $"level{l}-dict", (input, _) => Zstd.Compress(input, new ZstdCompressionOptions { Level = l, Dictionary = FormattedDictionary() }), "empty", "one-byte", "text-small", "text-64k", "binary-1m");
            Matrix.Add("zstd-encode-opt", $"level{l}-rawdict", (input, _) => Zstd.Compress(input, new ZstdCompressionOptions { Level = l, Dictionary = RawDictionary() }), "text-small", "text-64k", "binary-1m");
            Matrix.Add("zstd-encode-opt", $"level{l}-stream", (input, _) => Stream(input, new ZstdCompressionOptions { Level = l, ContentSize = input.Length }), "text-64k", "text-3m", "binary-1m", "runs-1m");
        }

        Matrix.Add("zstd-encode-opt", "level19-stream-unknown-size-dict", (input, _) => Stream(input, new ZstdCompressionOptions { Level = 19, Dictionary = RawDictionary(), Checksum = true }), "text-64k", "text-3m");
    }

    private static ZstdCompressionDictionary FormattedDictionary()
    {
        foreach ((string name, string data) in ZstdVectors.Dictionaries)
        {
            if (name == "dict16")
            {
                return ZstdCompressionDictionary.Parse(Convert.FromBase64String(data));
            }
        }

        throw new InvalidOperationException("missing dictionary vector");
    }

    private static ZstdCompressionDictionary RawDictionary() =>
        ZstdCompressionDictionary.FromContent(Inputs.Get("text-64k").AsSpan(0, 20000), 99);

    // Feeds the input in pieces of odd sizes through the streaming API with a small output buffer.
    private static byte[] Stream(ReadOnlySpan<byte> input, ZstdCompressionOptions options)
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
