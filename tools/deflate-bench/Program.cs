using System.Buffers;
using System.Diagnostics;
using RecursiveAI.OneCompress.Deflate;

// DeflateBench enc <file> <level> [--format raw|zlib|gzip] [--stream] [--strategy name] [--reps N] [--seconds S]
//   Compresses the file in memory, N timed passes after 2 warm-up passes (or until S seconds), and prints:
//   size, best compress MB/s, and the best decompress MB/s of our decoder on our output.
//   --stream feeds 64 KiB pieces through the streaming encoder, as a stream wrapper would; the default is one call.
//   --out <file> also writes our compressed bytes, so a reference decoder can be timed on them.
//   Rates are printed as best, median and slowest of the timed passes.
// DeflateBench dec <file.gz-or-zlib-or-raw> [--format ...] [--reps N]
//   Decompresses a stream written by another tool and prints the best MB/s.
// DeflateBench bz2dec <file.bz2> [--reps N]
//   Decompresses a bzip2 stream and prints the best MB/s (the bzip2 decoder bar rows).
static int Usage()
{
    Console.Error.WriteLine("usage: DeflateBench enc|dec <file> [level] [--format raw|zlib|gzip] [--stream] [--strategy name] [--reps N] [--seconds S]");
    return 2;
}

if (args.Length < 2)
{
    return Usage();
}

string mode = args[0];
string path = args[1];
int level = 6;
DeflateFormat format = DeflateFormat.GZip;
bool stream = false;
DeflateStrategy strategy = DeflateStrategy.Default;
int reps = 5;
double seconds = 0;
string? outPath = null;
int next = 2;
if (mode == "enc")
{
    level = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    next = 3;
}

for (int i = next; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--format": format = Enum.Parse<DeflateFormat>(args[++i], true); break;
        case "--stream": stream = true; break;
        case "--strategy": strategy = Enum.Parse<DeflateStrategy>(args[++i], true); break;
        case "--reps": reps = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--out": outPath = args[++i]; break;
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: return Usage();
    }
}

byte[] input = File.ReadAllBytes(path);
double mb = input.Length / 1e6;

if (mode == "bz2dec")
{
    return Bz2Decode(input, reps, seconds);
}

if (mode == "dec")
{
    return Decode(input, format, reps, seconds, expectedSize: -1);
}

var options = new DeflateOptions { Level = level, Strategy = strategy };
var encoder = new DeflateEncoder(format, options);
byte[] output = new byte[DeflateEncoder.GetMaxCompressedLength(format, input.Length, options)];
int size = 0;

int CompressOnce()
{
    if (!stream)
    {
        return OneShot();
    }

    encoder.Reset();
    int written = 0;
    int offset = 0;
    const int Piece = 65536;
    while (true)
    {
        int take = Math.Min(Piece, input.Length - offset);
        bool last = offset + take == input.Length;
        encoder.Compress(input.AsSpan(offset, take), output.AsSpan(written), out int consumed, out int w, last ? DeflateFlush.Finish : DeflateFlush.None);
        written += w;
        offset += consumed;
        if (last)
        {
            return written;
        }
    }
}

int OneShot()
{
    bool ok = format switch
    {
        DeflateFormat.Raw => Deflate.TryCompress(input, output, out int n, options) && (size = n) >= 0,
        DeflateFormat.ZLib => ZLib.TryCompress(input, output, out int n2, options) && (size = n2) >= 0,
        _ => GZip.TryCompress(input, output, out int n3, options) && (size = n3) >= 0,
    };
    return ok ? size : throw new InvalidOperationException("output too small");
}

for (int i = 0; i < 2; i++)
{
    size = CompressOnce();
}

var times = new List<double>();
var total = Stopwatch.StartNew();
for (int i = 0; i < reps || total.Elapsed.TotalSeconds < seconds; i++)
{
    long t = Stopwatch.GetTimestamp();
    size = CompressOnce();
    times.Add(Stopwatch.GetElapsedTime(t).TotalSeconds);
}

var (best, med, worst) = Stats(times);

Console.WriteLine($"enc level={level} format={format} {(stream ? "stream" : "oneshot")} strategy={strategy} in={input.Length} size={size} ratio={(double)size / input.Length:F4} comp_MBps={mb / best:F1} comp_MBps_med={mb / med:F1} comp_MBps_min={mb / worst:F1}");
if (outPath is not null)
{
    File.WriteAllBytes(outPath, output.AsSpan(0, size).ToArray());
}

if (Environment.GetEnvironmentVariable("BENCH_NODECODE") is not null)
{
    return 0;
}

byte[] compressed = output.AsSpan(0, size).ToArray();
return Decode(compressed, format, reps, seconds, input.Length, input);

static int Decode(byte[] compressed, DeflateFormat format, int reps, double seconds, long expectedSize, byte[]? original = null)
{
    var decoder = new DeflateDecoder(format);
    byte[] buffer = new byte[Math.Max(expectedSize, compressed.Length * 4L) > int.MaxValue ? int.MaxValue : (int)Math.Max(expectedSize, compressed.Length * 4L)];
    int Once()
    {
        decoder.Reset();
        int total = 0;
        int consumed = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            OperationStatus s = decoder.Decompress(compressed.AsSpan(consumed), buffer.AsSpan(total), out int c, out int w);
            consumed += c;
            total += w;
            if (s == OperationStatus.Done)
            {
                return total;
            }
        }
    }

    int n = 0;
    for (int i = 0; i < 2; i++)
    {
        n = Once();
    }

    var times = new List<double>();
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < reps || sw.Elapsed.TotalSeconds < seconds; i++)
    {
        long t = Stopwatch.GetTimestamp();
        n = Once();
        times.Add(Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    var (best, med, worst) = Stats(times);

    if (original is not null && !original.AsSpan().SequenceEqual(buffer.AsSpan(0, n)))
    {
        Console.Error.WriteLine("ROUND TRIP MISMATCH");
        return 1;
    }

    Console.WriteLine($"dec out={n} dec_MBps={n / 1e6 / best:F1} dec_MBps_med={n / 1e6 / med:F1} dec_MBps_min={n / 1e6 / worst:F1}");
    return 0;
}

static int Bz2Decode(byte[] compressed, int reps, double seconds)
{
    var opts = new RecursiveAI.OneCompress.BZip2.BZip2DecompressionOptions();
    byte[] result = RecursiveAI.OneCompress.BZip2.BZip2.Decompress(compressed, opts);
    byte[] buffer = new byte[result.Length];
    var times = new List<double>();
    var sw = Stopwatch.StartNew();
    int n = 0;
    for (int i = 0; i < reps || sw.Elapsed.TotalSeconds < seconds; i++)
    {
        long t = Stopwatch.GetTimestamp();
        if (!RecursiveAI.OneCompress.BZip2.BZip2.TryDecompress(compressed, buffer, out n, opts))
        {
            throw new InvalidOperationException("bzip2 decode failed");
        }

        times.Add(Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    var (best, med, worst) = Stats(times);
    Console.WriteLine($"dec out={n} dec_MBps={n / 1e6 / best:F1} dec_MBps_med={n / 1e6 / med:F1} dec_MBps_min={n / 1e6 / worst:F1}");
    return 0;
}

static (double Best, double Median, double Worst) Stats(List<double> times)
{
    times.Sort();
    return (times[0], times[times.Count / 2], times[^1]);
}
