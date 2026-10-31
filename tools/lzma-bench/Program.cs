using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneArchive;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;

// LzmaBench enc xz|lzip <file> <level> [--reps N] [--out <file>]
//   Compresses the file in memory with one call, at xz preset / lzip level <level>, and prints the size and the best, median and
//   slowest compress rate in MB/s. A first pass shorter than 8 s is a warm-up and is not counted; a longer one counts as a rep.
//   With --out the compressed bytes are written, and the output is always decoded and compared with the input (untimed).
// LzmaBench dec xz|lzip <file.xz|file.lz> [--reps N]
//   Decompresses a stream written by any tool into a preallocated buffer and prints the best, median and slowest rate.
// LzmaBench sevenz <file> <level> --out <file>
//   Writes the file as a one-entry 7z archive (LZMA2, solid, header compressed) at the given level and prints the size.
static int Usage()
{
    Console.Error.WriteLine("usage: LzmaBench enc xz|lzip <file> <level> [--reps N] [--out f] | dec xz|lzip <file> [--reps N] | sevenz <file> <level> --out f");
    return 2;
}

if (args.Length < 3)
{
    return Usage();
}

string mode = args[0];
int reps = 5;
string? outPath = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i] == "--reps")
    {
        reps = int.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--out")
    {
        outPath = args[++i];
    }
}

switch (mode)
{
    case "enc" when args.Length >= 4:
        return Encode(args[1], File.ReadAllBytes(args[2]), int.Parse(args[3], CultureInfo.InvariantCulture), reps, outPath);
    case "dec":
        return Decode(args[1], File.ReadAllBytes(args[2]), reps);
    case "sevenz" when args.Length >= 3 && outPath is not null:
        return SevenZ(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), outPath);
    default:
        return Usage();
}

static byte[] Compress(string codec, byte[] input, int level, byte[] buffer, out int size)
{
    bool ok = codec == "xz"
        ? Xz.TryCompress(input, buffer, out size, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = level } })
        : Lzip.TryCompress(input, buffer, out size, new LzipOptions { Level = level });
    if (!ok)
    {
        throw new InvalidOperationException("output buffer too small");
    }

    return buffer;
}

static int Encode(string codec, byte[] input, int level, int reps, string? outPath)
{
    byte[] buffer = new byte[input.Length + (input.Length >> 3) + 65536];
    var times = new List<double>();
    int size = 0;
    for (int i = 0; i <= reps; i++)
    {
        long t = Stopwatch.GetTimestamp();
        Compress(codec, input, level, buffer, out size);
        double s = Stopwatch.GetElapsedTime(t).TotalSeconds;
        if (i > 0 || s >= 8 || reps == 0)
        {
            times.Add(s);
        }

        if (i == 0 && s >= 8 && reps > 0)
        {
            reps--;
        }
    }

    times.Sort();
    double best = times[0], med = times[times.Count / 2], worst = times[^1];
    if (outPath is not null)
    {
        File.WriteAllBytes(outPath, buffer.AsSpan(0, size).ToArray());
        byte[] back = codec == "xz" ? Xz.Decompress(buffer.AsSpan(0, size)) : Lzip.Decompress(buffer.AsSpan(0, size));
        if (!back.AsSpan().SequenceEqual(input))
        {
            Console.Error.WriteLine("ROUND TRIP MISMATCH");
            return 1;
        }
    }

    double mb = input.Length / 1e6;
    Console.WriteLine($"enc size={size} comp_MBps={mb / best:F2} comp_MBps_med={mb / med:F2} comp_MBps_min={mb / worst:F2}");
    return 0;
}

static int Decode(string codec, byte[] packed, int reps)
{
    byte[] first = codec == "xz"
        ? Xz.Decompress(packed, new XzDecompressionOptions { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 })
        : Lzip.Decompress(packed, new LzipDecompressionOptions { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 });
    byte[] buffer = new byte[first.Length];
    var times = new List<double>();
    int n = 0;
    for (int i = 0; i <= reps; i++)
    {
        long t = Stopwatch.GetTimestamp();
        bool ok = codec == "xz"
            ? Xz.TryDecompress(packed, buffer, out n, new XzDecompressionOptions { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 })
            : Lzip.TryDecompress(packed, buffer, out n, new LzipDecompressionOptions { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 });
        double s = Stopwatch.GetElapsedTime(t).TotalSeconds;
        if (!ok || n != first.Length)
        {
            throw new InvalidOperationException("decode failed");
        }

        if (i > 0)
        {
            times.Add(s);
        }
    }

    times.Sort();
    double best = times[0], med = times[times.Count / 2], worst = times[^1];
    Console.WriteLine($"dec out={n} dec_MBps={n / 1e6 / best:F1} dec_MBps_med={n / 1e6 / med:F1} dec_MBps_min={n / 1e6 / worst:F1}");
    return 0;
}

static int SevenZ(string path, int level, string outPath)
{
    byte[] data = File.ReadAllBytes(path);
    using (var file = File.Create(outPath))
    using (var writer = new SevenZipWriter(file, new SevenZipWriterOptions { Level = level }))
    {
        writer.WriteEntry(new ArchiveEntry { Name = Path.GetFileName(path), Kind = ArchiveEntryKind.File, Size = data.Length }, data);
        writer.Finish();
    }

    Console.WriteLine($"size={new FileInfo(outPath).Length}");
    return 0;
}
