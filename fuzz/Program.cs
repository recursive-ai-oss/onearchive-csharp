using System.Globalization;
using RecursiveAI.Fuzz;

// A small in-process mutational driver: it mutates seed files with a seeded generator, runs a
// target on each result, and saves any input that makes the target throw an unexpected exception.
// The same targets can be wrapped by a coverage-guided fuzzer later.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: fuzz list | afl <target> | replay <target> <file>... | <target> [--seeds <dir>] [--out <dir>] [--iterations <n>] [--seconds <n>] [--rng <n>]");
    Console.Error.WriteLine("targets: " + string.Join(", ", FuzzTargets.All.Select(static t => t.Name)));
    return 2;
}

// Coverage-guided mode: `fuzz afl <target>` reads each input from stdin under afl-fuzz. The library assembly
// must be instrumented first (see README.md). Expected rejections return quietly; anything else is left
// unhandled so the process aborts and the fuzzer records the input as a crash.
if (args.Length == 2 && args[0] == "afl")
{
    // The instrumented library must not run before SharpFuzz has mapped the coverage buffer, so the target (and with
    // it every static initializer of the registry) is only touched from inside the callback.
    string aflName = args[1];
    FuzzTarget? aflTarget = null;
    SharpFuzz.Fuzzer.OutOfProcess.Run(stream =>
    {
        aflTarget ??= FuzzTargets.All.FirstOrDefault(t => t.Name == aflName)
            ?? throw new ArgumentException($"unknown target '{aflName}'");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        try
        {
            aflTarget.Run(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        catch (Exception ex) when (aflTarget.IsExpected(ex))
        {
        }
        catch (Exception ex) when (LogUnexpected(ex))
        {
        }
    });
    return 0;
}

// `fuzz replay <target> <file>...` runs the target once per file, unmodified, and prints what happened.
if (args.Length >= 3 && args[0] == "replay")
{
    FuzzTarget? replayTarget = FuzzTargets.All.FirstOrDefault(t => t.Name == args[1]);
    if (replayTarget is null)
    {
        Console.Error.WriteLine($"unknown target '{args[1]}'");
        return 2;
    }

    int failed = 0;
    foreach (string file in args.Skip(2))
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            replayTarget.Run(File.ReadAllBytes(file));
            Console.WriteLine($"{file}: ok in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (replayTarget.IsExpected(ex))
        {
            Console.WriteLine($"{file}: rejected ({ex.GetType().Name}) in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine($"{file}: FINDING {ex.GetType().Name}: {ex.Message}");
        }
    }

    return failed == 0 ? 0 : 1;
}

if (args[0] == "list")
{
    foreach (FuzzTarget t in FuzzTargets.All)
    {
        Console.WriteLine(t.Name);
    }

    return 0;
}

FuzzTarget? target = FuzzTargets.All.FirstOrDefault(t => t.Name == args[0]);
if (target is null)
{
    Console.Error.WriteLine($"unknown target '{args[0]}'");
    return 2;
}

string? seedDir = null;
string outDir = "findings";
int iterations = 10_000;
int rngSeed = 1;
double seconds = 0;
for (int i = 1; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--seeds": seedDir = args[i + 1]; break;
        case "--out": outDir = args[i + 1]; break;
        case "--iterations": iterations = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--seconds": seconds = double.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        case "--rng": rngSeed = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
    }
}

var seeds = new List<byte[]>();
if (seedDir is not null)
{
    foreach (string f in Directory.EnumerateFiles(seedDir).Order(StringComparer.Ordinal))
    {
        seeds.Add(File.ReadAllBytes(f));
    }
}

if (seeds.Count == 0)
{
    seeds.Add([]);
}

var rng = new Random(rngSeed);
int findings = 0;
var clock = System.Diagnostics.Stopwatch.StartNew();
long executed = 0;

// With --seconds the run is bounded by time (and --iterations is ignored); otherwise by iterations.
for (int n = 0; seconds > 0 ? clock.Elapsed.TotalSeconds < seconds : n < iterations; n++)
{
    executed++;
    byte[] input = Mutate(seeds[rng.Next(seeds.Count)], rng, seeds);
    try
    {
        target.Run(input);
    }
    catch (Exception ex) when (target.IsExpected(ex))
    {
        // Documented rejection of malformed input.
    }
    catch (Exception ex)
    {
        findings++;
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"{target.Name}-{n}.bin");
        File.WriteAllBytes(path, input);
        Console.Error.WriteLine($"finding: {ex.GetType().Name}: {ex.Message} -> {path}");
    }
}

Console.WriteLine($"{target.Name}: {executed} runs in {clock.Elapsed.TotalSeconds:F0} s ({executed / Math.Max(1, clock.Elapsed.TotalSeconds):F0}/s), {findings} findings");
return findings == 0 ? 0 : 1;

static byte[] Mutate(byte[] seed, Random rng, List<byte[]> seeds)
{
    var data = new List<byte>(seed);
    int edits = 1 + rng.Next(4);
    for (int e = 0; e < edits; e++)
    {
        switch (rng.Next(9))
        {
            case 4 when data.Count > 1:
                {
                    // Cut the tail off.
                    int keep = rng.Next(data.Count);
                    data.RemoveRange(keep, data.Count - keep);
                    break;
                }

            case 5 when data.Count > 0:
                {
                    // Overwrite a short run with 0x00 or 0xFF, which often makes sizes and counts extreme.
                    int at = rng.Next(data.Count);
                    byte fill = rng.Next(2) == 0 ? (byte)0 : (byte)0xFF;
                    for (int k = at; k < Math.Min(data.Count, at + 1 + rng.Next(8)); k++)
                    {
                        data[k] = fill;
                    }

                    break;
                }

            case 6:
                {
                    // Splice a piece of another seed in, mixing the structure of two frames.
                    byte[] other = seeds[rng.Next(seeds.Count)];
                    if (other.Length > 0)
                    {
                        int from = rng.Next(other.Length);
                        int count = Math.Min(other.Length - from, 1 + rng.Next(64));
                        data.InsertRange(rng.Next(data.Count + 1), other.AsSpan(from, count).ToArray());
                    }

                    break;
                }

            case 7 when data.Count > 0:
                {
                    // Duplicate a piece in place.
                    int at = rng.Next(data.Count);
                    int count = Math.Min(data.Count - at, 1 + rng.Next(32));
                    data.InsertRange(at, data.GetRange(at, count));
                    break;
                }

            case 8 when data.Count > 1:
                data[rng.Next(data.Count)] += (byte)(rng.Next(2) == 0 ? 1 : 255);
                break;
            case 0 when data.Count > 0:
                data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8));
                break;
            case 1 when data.Count > 0:
                data[rng.Next(data.Count)] = (byte)rng.Next(256);
                break;
            case 2 when data.Count > 0:
                {
                    int at = rng.Next(data.Count);
                    data.RemoveRange(at, Math.Min(1 + rng.Next(8), data.Count - at));
                }

                break;
            default:
                data.Insert(rng.Next(data.Count + 1), (byte)rng.Next(256));
                break;
        }
    }

    return [.. data];
}

// The fuzzer keeps only the input of a crash, so the exception text goes to a file named by ONEARCHIVE_FUZZ_ERRLOG for triage.
// Always returns false: the exception still ends the process.
static bool LogUnexpected(Exception ex)
{
    if (Environment.GetEnvironmentVariable("ONEARCHIVE_FUZZ_ERRLOG") is { Length: > 0 } path)
    {
        File.AppendAllText(path, ex + Environment.NewLine + "----" + Environment.NewLine);
    }

    return false;
}
