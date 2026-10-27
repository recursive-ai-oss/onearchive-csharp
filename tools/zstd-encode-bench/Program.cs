using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

// Times and checks the Zstandard encoder, memory to memory and warm.
//
//   ZstdEncodeBench big <file> <level> [--seconds 4] [--dict path] [--checksum] [--out frame.zst] [--chunk N]
//       Compresses the file as one frame (or in N-byte blocks, each a frame, like `zstd -B`) repeatedly with one encoder,
//       prints the size, the best compression time as MB/s and the best time of our decoder on our output.
//       The frame from the last pass is written to --out so the zstd tool can decode it.
//   ZstdEncodeBench small <dictionary> <dir> <level> [--seconds 4] [--limit N]
//       Compresses every file in the directory, the first N in name order (a payload each) with one encoder that has the dictionary loaded and prints
//       payloads per second, total bytes and the compressed total. The first pass is checked by decoding.
//
// Compare with `zstd -b<level> -i<seconds> file` and `zstd -b<level> -D dict files...`.
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: ZstdEncodeBench big <file> <level> | small <dictionary> <dir> <level>");
    return 2;
}

double seconds = 4;
string? dictPath = null;
string? outPath = null;
bool checksum = false;
int chunk = 0;
int limit = int.MaxValue;
string? perFile = null;
bool longMatching = false;
int windowLog = 0;
int workers = 1;
int jobSize = 0;
int overlap = -1;
bool once = false;
for (int i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--once": once = true; break;
        case "--long": longMatching = true; windowLog = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--window": windowLog = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--workers": workers = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--job": jobSize = args[++i] == "auto" ? ZstdCompressionOptions.AutoJobSize : int.Parse(args[i], CultureInfo.InvariantCulture); break;
        case "--overlap": overlap = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--dict": dictPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--checksum": checksum = true; break;
        case "--chunk": chunk = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--perfile": perFile = args[++i]; break;
        case "--limit": limit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}

ZstdCompressionOptions Options(int level, ZstdCompressionDictionary? dictionary = null) => new()
{
    Level = level,
    Checksum = checksum,
    Dictionary = dictionary,
    LongDistanceMatching = longMatching,
    WindowLog = windowLog,
    Workers = workers,
    JobSize = jobSize,
    OverlapSize = overlap,
};

double Best(Action run) => BestOf(run, out _, out _);

// Runs once to warm up, then repeatedly for the given number of seconds; returns the best time and reports the median and worst.
double BestOf(Action run, out double median, out double worst)
{
    if (!once)
    {
        run();
    }

    var times = new List<double>();
    var total = Stopwatch.StartNew();
    while (!once && (total.Elapsed.TotalSeconds < seconds || times.Count < 3) || times.Count == 0)
    {
        long t = Stopwatch.GetTimestamp();
        run();
        times.Add(Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    times.Sort();
    median = times[times.Count / 2];
    worst = times[^1];
    return times[0];
}

if (args[0] == "split")
{
    // Match finding and block writing timed apart (one frame, 128 KiB blocks, no window sliding: the whole file is the window).
    int level = int.Parse(args[2], CultureInfo.InvariantCulture);
    byte[] data = File.ReadAllBytes(args[1]);
    var parameters = ZstdCompressionParameters.Resolve(level, data.Length, 0);
    var finder = new ZstdFastMatchFinder();
    var writer = new ZstdBlockWriter();
    var store = new ZstdSequenceStore();
    byte[] target = new byte[(1 << 17) + 4096];
    double findTime = 0, writeTime = 0, best = double.MaxValue;
    long total = 0;
    var clock = Stopwatch.StartNew();
    int passes = 0;
    while (clock.Elapsed.TotalSeconds < seconds || findTime == 0)
    {
        passes++;
        double f = 0, w = 0;
        total = 0;
        finder.BeginFrame(parameters, null);
        writer.BeginFrame(null, parameters.CompressLiterals);
        for (int start = 0; start < data.Length; start += 1 << 17)
        {
            int end = Math.Min(data.Length, start + (1 << 17));
            store.Clear();
            long t0 = Stopwatch.GetTimestamp();
            finder.FindSequences(data, start, end, 0, writer.RepeatOffsets, store);
            long t1 = Stopwatch.GetTimestamp();
            total += writer.WriteBlock(data.AsSpan(start, end - start), store, false, target);
            long t2 = Stopwatch.GetTimestamp();
            f += Stopwatch.GetElapsedTime(t0, t1).TotalSeconds;
            w += Stopwatch.GetElapsedTime(t1, t2).TotalSeconds;
        }

        if (f + w < best)
        {
            best = f + w;
            findTime = f;
            writeTime = w;
        }
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"split level={level} compressed={total} find={data.Length / findTime / 1e6:F0} MB/s write={data.Length / writeTime / 1e6:F0} MB/s total={data.Length / best / 1e6:F0} MB/s"));
    return 0;
}

if (args[0] == "genfar")
{
    // genfar <enwik8> <silesia.tar> <out> [--size N]: a large input whose repeats lie far apart (about 96 MB), beyond any level's
    // own window and inside a 2^27 window. Three 32 MB pieces of the corpora come round again and again, each time with one
    // byte changed in every 64 KiB, so the repeats are long but never exact.
    long size = 1_200_000_000;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "--size")
        {
            size = long.Parse(args[++i], CultureInfo.InvariantCulture);
        }
    }

    const int PieceSize = 32 << 20;
    byte[][] pieces = new byte[3][];
    using (var a = File.OpenRead(args[1]))
    using (var b = File.OpenRead(args[2]))
    {
        pieces[0] = new byte[PieceSize];
        pieces[1] = new byte[PieceSize];
        pieces[2] = new byte[PieceSize];
        a.ReadExactly(pieces[0]);
        b.ReadExactly(pieces[1]);
        b.Seek(64L << 20, SeekOrigin.Begin);
        b.ReadExactly(pieces[2]);
    }

    var rng = new Random(12345);
    using var output = File.Create(args[3]);
    long written = 0;
    for (int round = 0; written < size; round++)
    {
        byte[] piece = pieces[round % 3];
        if (round >= 3)
        {
            for (int offset = 0; offset < PieceSize; offset += 65536)
            {
                piece[offset + rng.Next(65536)] ^= (byte)(1 + rng.Next(255));
            }
        }

        int n = (int)Math.Min(PieceSize, size - written);
        output.Write(piece, 0, n);
        written += n;
    }

    Console.WriteLine($"genfar {written} bytes");
    return 0;
}

if (args[0] == "stream")
{
    // stream <file> <out.zst> <level> [options]: the streaming encoder, 1 MiB pieces from file to file; prints size and time.
    long start = Stopwatch.GetTimestamp();
    var encoder = new ZstdEncoder(Options(int.Parse(args[3], CultureInfo.InvariantCulture)));
    using var input = File.OpenRead(args[1]);
    using var sink = File.Create(args[2]);
    byte[] piece = new byte[1 << 20];
    byte[] buffer = new byte[1 << 20];
    long total = 0;
    long compressedTotal = 0;
    while (true)
    {
        int n = input.Read(piece, 0, piece.Length);
        bool final = n == 0;
        int consumed = 0;
        System.Buffers.OperationStatus status;
        do
        {
            status = encoder.Compress(piece.AsSpan(consumed, n - consumed), buffer, out int taken, out int written, final);
            consumed += taken;
            sink.Write(buffer, 0, written);
            compressedTotal += written;
        }
        while (status == System.Buffers.OperationStatus.DestinationTooSmall || consumed < n);
        total += n;
        if (final)
        {
            break;
        }
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stream size={total} compressed={compressedTotal} seconds={Stopwatch.GetElapsedTime(start).TotalSeconds:F1}"));
    return 0;
}

if (args[0] == "verify")
{
    // verify <file.zst> <original>: our decoder, window up to 2^30, compared with the original.
    var decoder = new ZstdDecoder(new ZstdDecompressionOptions { MaxOutputSize = long.MaxValue, MaxWindowSize = 1L << 30 });
    byte[] frame = File.ReadAllBytes(args[1]);
    byte[] original = File.ReadAllBytes(args[2]);
    byte[] restored = new byte[original.Length];
    bool good = Zstd.TryDecompress(decoder, frame, restored, out int produced) && produced == original.Length && restored.AsSpan().SequenceEqual(original);
    Console.WriteLine(good ? "verify ok" : "verify MISMATCH");
    return good ? 0 : 1;
}

if (args[0] == "big")
{
    int level = int.Parse(args[2], CultureInfo.InvariantCulture);
    byte[] data = File.ReadAllBytes(args[1]);
    ZstdCompressionDictionary? dictionary = dictPath is null ? null : ZstdCompressionDictionary.Parse(File.ReadAllBytes(dictPath));
    var encoder = new ZstdEncoder(Options(level, dictionary));
    int piece = chunk == 0 ? data.Length : chunk;
    int pieces = Math.Max(1, (data.Length + piece - 1) / piece);
    byte[] output = new byte[(pieces * (ZstdEncoder.GetMaxCompressedLength(piece) + 8)) + 64];
    int[] sizes = new int[pieces];

    void Compress()
    {
        int p = 0;
        for (int k = 0; k < pieces; k++)
        {
            int start = k * piece;
            int n = Math.Min(piece, data.Length - start);
            int w = encoder.CompressFrame(data.AsSpan(start, n), output.AsSpan(p));
            sizes[k] = w;
            p += w;
        }
    }

    double compressTime = BestOf(Compress, out double compressMedian, out double compressWorst);
    long compressed = sizes.Sum(s => (long)s);
    var decoder = new ZstdDecoder(new ZstdDecompressionOptions
    {
        Dictionary = dictPath is null ? null : ZstdDictionary.Parse(File.ReadAllBytes(dictPath)),
        MaxOutputSize = long.MaxValue,
        MaxWindowSize = 1L << 30,
    });
    byte[] restored = new byte[data.Length];
    bool ok = true;

    void Decompress()
    {
        int p = 0;
        int q = 0;
        for (int k = 0; k < pieces; k++)
        {
            int start = k * piece;
            int n = Math.Min(piece, data.Length - start);
            if (!Zstd.TryDecompress(decoder, output.AsSpan(p, sizes[k]), restored.AsSpan(q, n), out int w) || w != n)
            {
                throw new InvalidOperationException("decode failed");
            }

            p += sizes[k];
            q += n;
        }
    }

    double decompressTime = BestOf(Decompress, out double decompressMedian, out double decompressWorst);
    ok = restored.AsSpan().SequenceEqual(data);
    if (outPath is not null)
    {
        File.WriteAllBytes(outPath, output.AsSpan(0, (int)compressed).ToArray());
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"big\t{Path.GetFileName(args[1])}\tlevel={level}\tsize={data.Length}\tcompressed={compressed}\tratio={(double)data.Length / compressed:F4}\tcompress={data.Length / compressTime / 1e6:F0} MB/s\tcompress_median={data.Length / compressMedian / 1e6:F0}\tcompress_min={data.Length / compressWorst / 1e6:F0}\tdecompress={data.Length / decompressTime / 1e6:F0} MB/s\tdecompress_median={data.Length / decompressMedian / 1e6:F0}\tdecompress_min={data.Length / decompressWorst / 1e6:F0}\troundtrip={(ok ? "ok" : "MISMATCH")}"));
    return ok ? 0 : 1;
}

{
    int level = int.Parse(args[3], CultureInfo.InvariantCulture);
    var dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(args[1]));
    var payloads = Directory.GetFiles(args[2]).Order(StringComparer.Ordinal).Take(limit).Select(File.ReadAllBytes).ToList();
    var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
    var decoder = new ZstdDecoder(new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(File.ReadAllBytes(args[1])) });
    byte[] output = new byte[1 << 20];
    byte[] check = new byte[1 << 20];
    long total = 0;
    long compressed = 0;

    void Pass(bool verify)
    {
        total = 0;
        compressed = 0;
        foreach (byte[] p in payloads)
        {
            int w = encoder.CompressFrame(p, output);
            total += p.Length;
            compressed += w;
            if (verify)
            {
                if (!Zstd.TryDecompress(decoder, output.AsSpan(0, w), check, out int d) || !check.AsSpan(0, d).SequenceEqual(p))
                {
                    throw new InvalidOperationException("round trip failed");
                }
            }
        }
    }

    Pass(true);
    if (perFile is not null)
    {
        var sizes = new List<string>();
        foreach (byte[] p in payloads)
        {
            sizes.Add(encoder.CompressFrame(p, output).ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllLines(perFile, sizes);
    }

    double best = Best(() => Pass(false));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"small\tlevel={level}\tpayloads={payloads.Count}\tbytes={total}\tcompressed={compressed}\tratio={(double)total / compressed:F4}\tpayloads/s={payloads.Count / best:F0}\tMB/s={total / best / 1e6:F0}"));
    return 0;
}
