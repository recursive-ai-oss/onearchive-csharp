using System.Buffers;
using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Deflate, zlib and gzip compression: every level, every strategy, streaming with sync and full flushes, and preset dictionaries.
/// The compressed bytes must not depend on the platform, vector support or runtime.
/// </summary>
internal static class DeflateEncoderEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        for (int level = 0; level <= 12; level++)
        {
            int l = level;
            Matrix.Add("deflate-enc", $"gzip-level{l}", (input, _) => GZip.Compress(input, new DeflateOptions { Level = l }));
            if (l is 0 or 1 or 4 or 6 or 9 or 10 or 12)
            {
                Matrix.Add("deflate-enc", $"raw-level{l}", (input, _) => Deflate.Compress(input, new DeflateOptions { Level = l }));
                Matrix.Add("deflate-enc", $"zlib-level{l}", (input, _) => ZLib.Compress(input, new DeflateOptions { Level = l }));
            }
        }

        foreach (int level in new[] { 1, 6, 11 })
        {
            int l = level;
            foreach (DeflateStrategy strategy in new[] { DeflateStrategy.HuffmanOnly, DeflateStrategy.RunLength, DeflateStrategy.FixedCodes })
            {
                DeflateStrategy s = strategy;
                Matrix.Add("deflate-enc", $"raw-level{l}-{s}", (input, _) => Deflate.Compress(input, new DeflateOptions { Level = l, Strategy = s }));
            }

            // Streaming: odd-sized writes with a sync flush and a full flush between them, drained through a small buffer.
            Matrix.Add("deflate-enc", $"raw-level{l}-flushes", (input, _) => Streamed(input, l, DeflateFormat.Raw, default));
            Matrix.Add("deflate-enc", $"zlib-level{l}-flushes", (input, _) => Streamed(input, l, DeflateFormat.ZLib, default));

            // A preset dictionary: the first 20000 bytes of the 64 KiB text input.
            Matrix.Add("deflate-enc", $"raw-level{l}-dict", (input, _) => Deflate.Compress(input, new DeflateOptions { Level = l, Dictionary = Dictionary() }));
            Matrix.Add("deflate-enc", $"zlib-level{l}-dict", (input, _) => ZLib.Compress(input, new DeflateOptions { Level = l, Dictionary = Dictionary() }));
            Matrix.Add("deflate-enc", $"zlib-level{l}-dict-flushes", (input, _) => Streamed(input, l, DeflateFormat.ZLib, Dictionary()));
        }

        Matrix.Add("deflate-enc", "gzip-header-fields", (input, _) => GZip.Compress(input, new DeflateOptions
        {
            Level = 6,
            GZipFileName = "matrix.txt",
            GZipComment = "comment",
            GZipExtra = new byte[] { 65, 66, 2, 0, 1, 2 },
            GZipModificationTime = 1_700_000_000,
            GZipOperatingSystem = 3,
            GZipIsText = true,
            GZipHeaderCrc = true,
        }));
    }

    private static byte[] Dictionary() => Inputs.Get("text-64k").AsSpan(0, 20000).ToArray();

    private static byte[] Streamed(ReadOnlySpan<byte> input, int level, DeflateFormat format, byte[] dictionary)
    {
        var encoder = new DeflateEncoder(format, new DeflateOptions { Level = level, Dictionary = dictionary });
        var output = new List<byte>();
        byte[] buffer = new byte[777];
        int[] pieces = [1, 4999, 10000, 33333, 70001, 250000];
        int offset = 0;
        int step = 0;
        while (true)
        {
            int take = Math.Min(pieces[step % pieces.Length], input.Length - offset);
            bool last = offset + take == input.Length;
            DeflateFlush mode = last ? DeflateFlush.Finish : step % 3 == 1 ? DeflateFlush.Sync : step % 3 == 2 ? DeflateFlush.Full : DeflateFlush.None;
            ReadOnlySpan<byte> source = input.Slice(offset, take);
            while (true)
            {
                OperationStatus status = encoder.Compress(source, buffer, out int consumed, out int written, mode);
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
        }
    }
}
