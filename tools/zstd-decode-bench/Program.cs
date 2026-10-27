using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

// Times the Zstandard decoder, memory to memory and warm.
//
//   ZstdDecodeBench big <file.zst> [--seconds 4] [--window-mib 2048]
//       Decodes the whole frame repeatedly into a buffer that is reused, and prints the best time as MB/s of output.
//       Two rows: "oneshot" (the output buffer holds the whole frame) and "stream" (a 128 KiB output buffer, so
//       the sliding window is used, as in a ZstdStream).
//   ZstdDecodeBench small <dictionary> <dir of .zst frames> [--seconds 4]
//       Decodes every frame in the directory with one decoder that has the dictionary loaded, and prints the
//       best pass as frames per second and MB/s of output.
//
// Compare with `zstd -b<level> -i<seconds> file` (decompression speed) and `zstd -b<level> -D dict files...`.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ZstdDecodeBench big <file.zst> | small <dictionary> <dir>");
    return 2;
}

double seconds = 4;
long windowMiB = 2048;
for (int i = 2; i + 1 < args.Length; i++)
{
    if (args[i] == "--seconds")
    {
        seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--window-mib")
    {
        windowMiB = long.Parse(args[i + 1], CultureInfo.InvariantCulture);
    }
}

if (args[0] == "big")
{
    byte[] compressed = File.ReadAllBytes(args[1]);
    var options = new ZstdDecompressionOptions { MaxWindowSize = windowMiB << 20, MaxOutputSize = long.MaxValue };
    var decoder = new ZstdDecoder(options);
    ZstdDecoder.TryReadFrameHeader(compressed, out ZstdFrameHeader header);
    long size = header.HasContentSize ? (long)header.ContentSize : Zstd.Decompress(compressed, options).Length;
    byte[] whole = new byte[size];
    byte[] chunk = new byte[128 * 1024];

    double Best(Action run)
    {
        run();
        double best = double.MaxValue;
        var total = Stopwatch.StartNew();
        while (total.Elapsed.TotalSeconds < seconds)
        {
            long t = Stopwatch.GetTimestamp();
            run();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
        }

        return best;
    }

    double oneshot = Best(() =>
    {
        if (!Zstd.TryDecompress(decoder, compressed, whole, out int w) || w != size)
        {
            throw new InvalidOperationException("decode failed");
        }
    });
    double stream = Best(() =>
    {
        decoder.Reset();
        int pos = 0;
        long total = 0;
        while (true)
        {
            System.Buffers.OperationStatus s = decoder.Decompress(compressed.AsSpan(pos), chunk, out int c, out int w);
            pos += c;
            total += w;
            if (s == System.Buffers.OperationStatus.Done && pos == compressed.Length)
            {
                break;
            }
        }

        if (total != size)
        {
            throw new InvalidOperationException("decode failed");
        }
    });
    Console.WriteLine($"big\t{Path.GetFileName(args[1])}\tsize={size}\tcompressed={compressed.Length}\toneshot={size / oneshot / 1e6:F0} MB/s\tstream={size / stream / 1e6:F0} MB/s");
    return 0;
}

{
    var dictionary = ZstdDictionary.Parse(File.ReadAllBytes(args[1]));
    var frames = Directory.GetFiles(args[2], "*.zst").Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
    var decoder = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = dictionary });
    byte[] output = new byte[1 << 20];
    long bytes = 0;

    void Pass()
    {
        bytes = 0;
        foreach (byte[] f in frames)
        {
            if (!Zstd.TryDecompress(decoder, f, output, out int w))
            {
                throw new InvalidOperationException("decode failed");
            }

            bytes += w;
        }
    }

    Pass();
    double best = double.MaxValue;
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < seconds)
    {
        long t = Stopwatch.GetTimestamp();
        Pass();
        best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    Console.WriteLine($"small\tframes={frames.Count}\toutput={bytes}\t{frames.Count / best:F0} frames/s\t{bytes / best / 1e6:F0} MB/s");
    return 0;
}
