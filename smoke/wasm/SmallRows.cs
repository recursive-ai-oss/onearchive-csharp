using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Smoke;

// Small-payload speed in the browser-wasm host: the same reused-encoder, reused-decoder, prepared-dictionary path as the
// native small-payload rows, on generated record-shaped payloads (no files, no reference tool: Node has neither). Prints
// payloads per second for ours only, a number to report rather than a gate.
//
//   node run.mjs <wwwroot> small [seconds]
internal static class SmallRows
{
    private static readonly string[] s_fields = ["pos", "vel", "hp", "tick", "owner", "kind", "state", "target", "seq", "flags"];

    public static int Run(string[] args)
    {
        double seconds = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 1;
        byte[] dictionary = Make(16 * 1024, 7);
        Console.WriteLine("codec\tlevel\tpayload_bytes\tpayloads\tout_bytes\tcompress_payloads_per_s\tdecompress_payloads_per_s");
        foreach (int size in new[] { 256, 1024, 4096, 16384 })
        {
            byte[] stream = Make(size * 400, 11);
            var payloads = new List<byte[]>();
            for (int i = 0; i + size <= stream.Length; i += size)
            {
                payloads.Add(stream.AsSpan(i, size).ToArray());
            }

            foreach (int level in new[] { 1, 3 })
            {
                var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = ZstdCompressionDictionary.Parse(dictionary) });
                var decoder = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(dictionary) });
                if (!Row("zstd", level, size, payloads, seconds, (p, o) => encoder.CompressFrame(p, o), (f, o) => Zstd.TryDecompress(decoder, f, o, out int w) ? w : -1))
                {
                    return 1;
                }
            }

            foreach (int level in new[] { -1, 1 })
            {
                var dict = new Lz4Dictionary(dictionary);
                var encoder = new Lz4Encoder(new Lz4CompressionOptions { Level = level, Dictionary = dict, ContentChecksum = false, IncludeContentSize = false });
                var decoder = new Lz4Decoder(new Lz4DecompressionOptions { Dictionary = dict });
                if (!Row("lz4", level, size, payloads, seconds, (p, o) => encoder.TryCompressFrame(p, o, out int w) ? w : -1, (f, o) => Lz4.TryDecompress(decoder, f, o, out int w) ? w : -1))
                {
                    return 1;
                }
            }
        }

        return 0;
    }

    private delegate int Transform(byte[] input, byte[] output);

    private static bool Row(string codec, int level, int size, List<byte[]> payloads, double seconds, Transform compress, Transform decompress)
    {
        byte[] output = new byte[1 << 20];
        byte[] back = new byte[1 << 20];
        var frames = new List<byte[]>();
        long outBytes = 0;
        foreach (byte[] p in payloads)
        {
            int w = compress(p, output);
            frames.Add(output.AsSpan(0, w).ToArray());
            outBytes += w;
        }

        for (int i = 0; i < frames.Count; i++)
        {
            int w = decompress(frames[i], back);
            if (w != payloads[i].Length || !back.AsSpan(0, w).SequenceEqual(payloads[i]))
            {
                Console.Error.WriteLine($"round trip mismatch: {codec} level {level} size {size}");
                return false;
            }
        }

        double c = Best(() => { foreach (byte[] p in payloads) { compress(p, output); } }, seconds);
        double d = Best(() => { foreach (byte[] f in frames) { decompress(f, back); } }, seconds);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{codec}\t{level}\t{size}\t{payloads.Count}\t{outBytes}\t{payloads.Count / c:F0}\t{payloads.Count / d:F0}"));
        return true;
    }

    private static double Best(Action pass, double seconds)
    {
        pass();
        double best = double.MaxValue;
        var clock = Stopwatch.StartNew();
        int runs = 0;
        while (runs < 3 || clock.Elapsed.TotalSeconds < seconds)
        {
            long t = Stopwatch.GetTimestamp();
            pass();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
            runs++;
        }

        return best;
    }

    // Record-shaped bytes: field names from a small vocabulary, counters that step, and values from a SplitMix64 stream with
    // a skew, so a dictionary of the same shape helps the way one trained on game state does.
    private static byte[] Make(int length, ulong seed)
    {
        var output = new byte[length];
        ulong state = seed;
        ulong Next()
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        int at = 0;
        uint counter = 0;
        Span<byte> record = stackalloc byte[16];
        while (at < length)
        {
            string name = s_fields[(int)(Next() % (ulong)s_fields.Length)];
            int n = Math.Min(length - at, 16);
            record.Clear();
            for (int i = 0; i < name.Length && i < 6; i++)
            {
                record[i] = (byte)name[i];
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record[8..], counter++);
            record[12] = (byte)(Next() & 0x0F);
            record[13] = (byte)(Next() & 0x3F);
            record.Slice(0, n).CopyTo(output.AsSpan(at));
            at += n;
        }

        return output;
    }
}
