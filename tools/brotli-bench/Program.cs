using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Brotli;

// BrotliBench <file> <quality> [--reps N] [--window W] [--tool <path-to-brotli>] [--out <file>]
//   Compresses the file in memory with one call and prints the size and the best and median rate in MB/s. With --tool the same file
//   is compressed by the brotli command-line tool (file to file, timed end to end, with its start-up time measured on an empty file
//   and subtracted) and its size and rates are printed on a second line. With --out our bytes are written for decoding elsewhere.
#pragma warning disable ONECOMPRESS008
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: BrotliBench <file> <quality> [--reps N] [--window W] [--tool path] [--out file]");
    return 2;
}

byte[] data = File.ReadAllBytes(args[0]);
int quality = int.Parse(args[1], CultureInfo.InvariantCulture);
int reps = 3;
int window = 22;
string? tool = null;
string? outPath = null;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--reps": reps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--window": window = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--tool": tool = args[++i]; break;
        case "--out": outPath = args[++i]; break;
    }
}

double mb = data.Length / 1e6;
var options = new BrotliOptions { Quality = quality, WindowBits = window };
byte[] output = [];
var times = new List<double>();
for (int r = 0; r < reps; r++)
{
    long t0 = Stopwatch.GetTimestamp();
    output = Brotli.Compress(data, options);
    times.Add(Stopwatch.GetElapsedTime(t0).TotalSeconds);
}

if (outPath is not null)
{
    File.WriteAllBytes(outPath, output);
}

times.Sort();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"ours  q{quality} {Path.GetFileName(args[0])} size {output.Length} ratio {(double)data.Length / output.Length:F4} best {mb / times[0]:F2} MB/s median {mb / times[times.Count / 2]:F2} MB/s"));

if (tool is not null)
{
    string tmp = Path.Combine(Path.GetTempPath(), "brotlibench-" + Environment.ProcessId + ".br");
    string empty = Path.Combine(Path.GetTempPath(), "brotlibench-" + Environment.ProcessId + ".empty");
    File.WriteAllBytes(empty, []);
    double Run(string input)
    {
        long t0 = Stopwatch.GetTimestamp();
        using var p = Process.Start(new ProcessStartInfo(tool, ["-f", "-q", quality.ToString(CultureInfo.InvariantCulture), "-w", window.ToString(CultureInfo.InvariantCulture), "-o", tmp, input]))!;
        p.WaitForExit();
        return Stopwatch.GetElapsedTime(t0).TotalSeconds;
    }

    double startup = double.MaxValue;
    for (int r = 0; r < 5; r++)
    {
        startup = Math.Min(startup, Run(empty));
    }

    var tt = new List<double>();
    for (int r = 0; r < reps; r++)
    {
        tt.Add(Run(args[0]) - startup);
    }

    long size = new FileInfo(tmp).Length;
    File.Delete(tmp);
    File.Delete(empty);
    tt.Sort();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"tool  q{quality} {Path.GetFileName(args[0])} size {size} ratio {(double)data.Length / size:F4} best {mb / tt[0]:F2} MB/s median {mb / tt[tt.Count / 2]:F2} MB/s  (ours/tool size {(double)output.Length / size:F4}, speed {tt[0] / times[0]:F2})"));
}

return 0;
