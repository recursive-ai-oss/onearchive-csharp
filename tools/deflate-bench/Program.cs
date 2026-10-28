using System.Buffers;
using System.Diagnostics;
using RecursiveAI.OneCompress.Deflate;

// DeflateBench enc <file> <level> [--format raw|zlib|gzip] [--stream] [--strategy name] [--reps N] [--seconds S]
//   Compresses the file in memory, N timed passes after 2 warm-up passes (or until S seconds), and prints:
//   size, best compress MB/s, and the best decompress MB/s of our decoder on our output.
//   --stream feeds 64 KiB pieces through the streaming encoder, as a stream wrapper would; the default is one call.
// DeflateBench dec <file.gz-or-zlib-or-raw> [--format ...] [--reps N]
//   Decompresses a stream written by another tool and prints the best MB/s.
#pragma warning disable ONECOMPRESS003
// DeflateBench ref <file> <reps> <tool> [args...]
//   Runs a reference tool with the file on standard input and its output in a scratch file, best of N wall times with the time of an
//   empty-ish run (process startup) subtracted, and prints the output size and MB/s of the input. The method is in docs/bench-method.md.
static int RunReference(string[] a)
{
    string file = a[1];
    int reps = int.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
    string scratch = Path.Combine(Environment.GetEnvironmentVariable("BENCH_TMP") ?? Path.GetTempPath(), "deflate-bench-" + Environment.ProcessId);
    Directory.CreateDirectory(scratch);
    string tiny = Path.Combine(scratch, "tiny");
    File.WriteAllBytes(tiny, new byte[16]);
    string output = Path.Combine(scratch, "out");
    double Once(string input)
    {
        var info = new ProcessStartInfo(a[3]) { RedirectStandardInput = true, RedirectStandardOutput = true };
        for (int i = 4; i < a.Length; i++)
        {
            info.ArgumentList.Add(a[i]);
        }

        long t = Stopwatch.GetTimestamp();
        using Process p = Process.Start(info)!;
        // Feed the input from another thread: the tool writes as it reads, and neither side may wait for the other's pipe to empty.
        Task feed = Task.Run(() =>
        {
            using FileStream src = File.OpenRead(input);
            src.CopyTo(p.StandardInput.BaseStream);
            p.StandardInput.Close();
        });
        using (FileStream dst = File.Create(output))
        {
            p.StandardOutput.BaseStream.CopyTo(dst);
        }

        feed.Wait();
        p.WaitForExit();
        double seconds = Stopwatch.GetElapsedTime(t).TotalSeconds;
        return p.ExitCode == 0 ? seconds : throw new InvalidOperationException("tool failed");
    }

    double startup = double.MaxValue;
    for (int i = 0; i < 5; i++)
    {
        startup = Math.Min(startup, Once(tiny));
    }

    double best = double.MaxValue;
    for (int i = 0; i < reps; i++)
    {
        best = Math.Min(best, Once(file));
    }

    long size = new FileInfo(output).Length;
    long input = new FileInfo(file).Length;
    Directory.Delete(scratch, true);
    Console.WriteLine($"ref size={size} seconds={best:F4} startup={startup:F4} MBps={input / 1e6 / (best - startup):F1}");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: DeflateBench enc|dec <file> [level] [--format raw|zlib|gzip] [--stream] [--strategy name] [--reps N] [--seconds S]");
    return 2;
}

if (args.Length < 2)
{
    return Usage();
}

if (args[0] == "ref")
{
    return RunReference(args);
}

string mode = args[0];
string path = args[1];
int level = 6;
DeflateFormat format = DeflateFormat.GZip;
bool stream = false;
DeflateStrategy strategy = DeflateStrategy.Default;
int reps = 5;
double seconds = 0;
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
        case "--seconds": seconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: return Usage();
    }
}

byte[] input = File.ReadAllBytes(path);
double mb = input.Length / 1e6;

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

double best = double.MaxValue;
var total = Stopwatch.StartNew();
for (int i = 0; i < reps || total.Elapsed.TotalSeconds < seconds; i++)
{
    long t = Stopwatch.GetTimestamp();
    size = CompressOnce();
    best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
}

Console.WriteLine($"enc level={level} format={format} {(stream ? "stream" : "oneshot")} strategy={strategy} in={input.Length} size={size} ratio={(double)size / input.Length:F4} comp_MBps={mb / best:F1}");
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

    double best = double.MaxValue;
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < reps || sw.Elapsed.TotalSeconds < seconds; i++)
    {
        long t = Stopwatch.GetTimestamp();
        n = Once();
        best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
    }

    if (original is not null && !original.AsSpan().SequenceEqual(buffer.AsSpan(0, n)))
    {
        Console.Error.WriteLine("ROUND TRIP MISMATCH");
        return 1;
    }

    Console.WriteLine($"dec out={n} dec_MBps={n / 1e6 / best:F1}");
    return 0;
}
