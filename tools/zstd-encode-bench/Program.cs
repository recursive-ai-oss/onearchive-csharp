using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

// Times and checks the Zstandard encoder, memory to memory and warm.
//
//   ZstdEncodeBench big <file> <level> [--seconds 4] [--dict path] [--checksum] [--out frame.zst] [--chunk N]
//       Compresses the file as one frame (or in N-byte blocks, each a frame, like `zstd -B`) repeatedly with one encoder,
//       prints the size, the best compression time as MB/s and the best time of our decoder on our output.
//       The frame from the last pass is written to --out so the zstd tool can decode it.
//   ZstdEncodeBench small <dictionary> <dir> <level> [--seconds 4]
//       Compresses every file in the directory (a payload each) with one encoder that has the dictionary loaded and prints
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
for (int i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--dict": dictPath = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--checksum": checksum = true; break;
        case "--chunk": chunk = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
    }
}

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

if (args[0] == "big")
{
    int level = int.Parse(args[2], CultureInfo.InvariantCulture);
    byte[] data = File.ReadAllBytes(args[1]);
    ZstdCompressionDictionary? dictionary = dictPath is null ? null : ZstdCompressionDictionary.Parse(File.ReadAllBytes(dictPath));
    var options = new ZstdCompressionOptions { Level = level, Checksum = checksum, Dictionary = dictionary };
    var encoder = new ZstdEncoder(options);
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

    double compressTime = Best(Compress);
    long compressed = sizes.Sum(s => (long)s);
    var decoder = new ZstdDecoder(new ZstdDecompressionOptions
    {
        Dictionary = dictPath is null ? null : ZstdDictionary.Parse(File.ReadAllBytes(dictPath)),
        MaxOutputSize = long.MaxValue,
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

    double decompressTime = Best(Decompress);
    ok = restored.AsSpan().SequenceEqual(data);
    if (outPath is not null)
    {
        File.WriteAllBytes(outPath, output.AsSpan(0, (int)compressed).ToArray());
    }

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"big\t{Path.GetFileName(args[1])}\tlevel={level}\tsize={data.Length}\tcompressed={compressed}\tratio={(double)data.Length / compressed:F4}\tcompress={data.Length / compressTime / 1e6:F0} MB/s\tdecompress={data.Length / decompressTime / 1e6:F0} MB/s\troundtrip={(ok ? "ok" : "MISMATCH")}"));
    return ok ? 0 : 1;
}

{
    int level = int.Parse(args[3], CultureInfo.InvariantCulture);
    var dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(args[1]));
    var payloads = Directory.GetFiles(args[2]).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToList();
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
    double best = Best(() => Pass(false));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"small\tlevel={level}\tpayloads={payloads.Count}\tbytes={total}\tcompressed={compressed}\tratio={(double)total / compressed:F4}\tpayloads/s={payloads.Count / best:F0}\tMB/s={total / best / 1e6:F0}"));
    return 0;
}
