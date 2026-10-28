using System.Buffers.Binary;
using System.Globalization;
using System.Diagnostics;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Zstd;

// Small-payload rows: payloads of a few hundred bytes to a few KiB, each compressed or decompressed on its own with a
// prepared dictionary and a reused encoder or decoder. Payload files are packed as repeated [u32 LE length][bytes].
//
//   SmallBench pack <manifest.tsv> <root> <game> <kind> <size> <count> <out>
//       Concatenates the game/kind payloads in manifest order and cuts them into <count> pieces of <size> bytes.
//   SmallBench rawdict <manifest.tsv> <root> <game> <kind> <size> <out>
//       Concatenates evenly spaced payloads (a raw dictionary for LZ4) up to <size> bytes.
//   SmallBench run <zstd|lz4> <level> <dictionary|-> <packed> <reference frames> [--seconds 3]
//       Compresses every payload (one reused encoder), decodes the reference tool's frames (one reused decoder), and prints
//       best/median/min payloads per second for each, the output size, and the bytes allocated per payload on both paths.
//       Ours are checked to round-trip through our own decoder first, and the reference frames must decode to the input.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: SmallBench pack|rawdict|run ...");
    return 2;
}

switch (args[0])
{
    case "pack": return Pack(args);
    case "rawdict": return RawDict(args);
    case "run": return Run(args);
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

static IEnumerable<string> Files(string manifest, string root, string game, string kind)
{
    foreach (string line in File.ReadLines(manifest))
    {
        string[] f = line.Split('\t');
        if (f.Length > 2 && f[0] == game && f[1] == kind)
        {
            yield return Path.Combine(root, f[2]);
        }
    }
}

static int Pack(string[] a)
{
    int size = int.Parse(a[5], CultureInfo.InvariantCulture);
    int count = int.Parse(a[6], CultureInfo.InvariantCulture);
    var pieces = new List<byte[]>();
    var carry = new List<byte>();
    foreach (string path in Files(a[1], a[2], a[3], a[4]))
    {
        carry.AddRange(File.ReadAllBytes(path));
        while (carry.Count >= size && pieces.Count < count)
        {
            pieces.Add(carry.GetRange(0, size).ToArray());
            carry.RemoveRange(0, size);
        }

        if (pieces.Count >= count)
        {
            break;
        }
    }

    using var o = new BinaryWriter(File.Create(a[7]));
    foreach (byte[] p in pieces)
    {
        o.Write((uint)p.Length);
        o.Write(p);
    }

    Console.WriteLine($"{a[3]}/{a[4]} {size} B x {pieces.Count}");
    return pieces.Count == 0 ? 1 : 0;
}

static int RawDict(string[] a)
{
    int size = int.Parse(a[5], CultureInfo.InvariantCulture);
    List<string> files = Files(a[1], a[2], a[3], a[4]).ToList();
    long total = files.Sum(f => new FileInfo(f).Length);
    long step = Math.Max(1, total / size);
    var dict = new List<byte>();
    for (int i = 0; i < files.Count && dict.Count < size; i++)
    {
        if (i % step == 0)
        {
            dict.AddRange(File.ReadAllBytes(files[i]));
        }
    }

    File.WriteAllBytes(a[6], dict.Take(size).ToArray());
    return 0;
}

static List<byte[]> Unpack(string path)
{
    byte[] raw = File.ReadAllBytes(path);
    var list = new List<byte[]>();
    for (int at = 0; at + 4 <= raw.Length;)
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
        list.Add(raw.AsSpan(at + 4, n).ToArray());
        at += 4 + n;
    }

    return list;
}

static int Run(string[] a)
{
    bool zstd = a[1] == "zstd";
    int level = int.Parse(a[2], CultureInfo.InvariantCulture);
    byte[]? dictBytes = a[3] == "-" ? null : File.ReadAllBytes(a[3]);
    List<byte[]> payloads = Unpack(a[4]);
    List<byte[]> refFrames = Unpack(a[5]);
    double seconds = 3;
    for (int i = 6; i + 1 < a.Length; i++)
    {
        if (a[i] == "--seconds")
        {
            seconds = double.Parse(a[i + 1], CultureInfo.InvariantCulture);
        }
    }

    byte[] output = new byte[1 << 20];
    byte[] back = new byte[1 << 20];
    Func<byte[], int> compress;
    Func<byte[], int> decompress;
    if (zstd)
    {
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = dictBytes is null ? null : ZstdCompressionDictionary.Parse(dictBytes) });
        var decoder = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = dictBytes is null ? null : ZstdDictionary.Parse(dictBytes) });
        compress = p => encoder.CompressFrame(p, output);
        decompress = f => Zstd.TryDecompress(decoder, f, back, out int w) ? w : -1;
    }
    else
    {
        Lz4Dictionary? dict = dictBytes is null ? null : new Lz4Dictionary(dictBytes);
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { Level = level, Dictionary = dict, ContentChecksum = false, IncludeContentSize = false });
        var decoder = new Lz4Decoder(new Lz4DecompressionOptions { Dictionary = dict });
        compress = p => encoder.TryCompressFrame(p, output, out int w) ? w : -1;
        decompress = f => Lz4.TryDecompress(decoder, f, back, out int w) ? w : -1;
    }

    long outBytes = 0;
    foreach (byte[] p in payloads)
    {
        int w = compress(p);
        outBytes += w;
        byte[] frame = output.AsSpan(0, w).ToArray();
        int d = decompress(frame);
        if (d != p.Length || !back.AsSpan(0, d).SequenceEqual(p))
        {
            Console.Error.WriteLine("ROUND TRIP MISMATCH");
            return 1;
        }
    }

    for (int i = 0; i < refFrames.Count; i++)
    {
        int d = decompress(refFrames[i]);
        if (d != payloads[i].Length || !back.AsSpan(0, d).SequenceEqual(payloads[i]))
        {
            Console.Error.WriteLine("REFERENCE FRAME DECODE MISMATCH at " + i);
            return 1;
        }
    }

    (double best, double median, double worst, double alloc) Time(Action pass)
    {
        pass();
        var times = new List<double>(2100);
        var clock = Stopwatch.StartNew();
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        while (times.Count < 5 || (clock.Elapsed.TotalSeconds < seconds && times.Count < 2000))
        {
            long t = Stopwatch.GetTimestamp();
            pass();
            times.Add(Stopwatch.GetElapsedTime(t).TotalSeconds);
        }

        // The pass times go into a list sized up front, so the delta is the codec path alone.
        long alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
        double perPass = (double)alloc / times.Count;
        times.Sort();
        return (times[0], times[times.Count / 2], times[^1], perPass);
    }

    var c = Time(() =>
    {
        foreach (byte[] p in payloads)
        {
            compress(p);
        }
    });
    var dd = Time(() =>
    {
        foreach (byte[] f in refFrames)
        {
            decompress(f);
        }
    });

    int n = payloads.Count;
    long inBytes = payloads.Sum(p => (long)p.Length);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"ours\t{a[1]}\tlevel={level}\tpayloads={n}\tin={inBytes}\tout={outBytes}\tcompress_best={n / c.best:F0}\tcompress_median={n / c.median:F0}\tcompress_min={n / c.worst:F0}\tdecompress_best={n / dd.best:F0}\tdecompress_median={n / dd.median:F0}\tdecompress_min={n / dd.worst:F0}\talloc_compress_per_payload={c.alloc / n:F2}\talloc_decompress_per_payload={dd.alloc / n:F2}"));
    return 0;
}
