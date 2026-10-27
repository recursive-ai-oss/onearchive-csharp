using System.Globalization;
using RecursiveAI.Fuzz;

// A small in-process mutational driver: it mutates seed files with a seeded generator, runs a
// target on each result, and saves any input that makes the target throw an unexpected exception.
// The same targets can be wrapped by a coverage-guided fuzzer later.
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: fuzz <target> [--seeds <dir>] [--out <dir>] [--iterations <n>] [--rng <n>]");
    Console.Error.WriteLine("targets: " + string.Join(", ", FuzzTargets.All.Select(static t => t.Name)));
    return 2;
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
for (int i = 1; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--seeds": seedDir = args[i + 1]; break;
        case "--out": outDir = args[i + 1]; break;
        case "--iterations": iterations = int.Parse(args[i + 1], CultureInfo.InvariantCulture); break;
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
for (int n = 0; n < iterations; n++)
{
    byte[] input = Mutate(seeds[rng.Next(seeds.Count)], rng);
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

Console.WriteLine($"{target.Name}: {iterations} runs, {findings} findings");
return findings == 0 ? 0 : 1;

static byte[] Mutate(byte[] seed, Random rng)
{
    var data = new List<byte>(seed);
    int edits = 1 + rng.Next(4);
    for (int e = 0; e < edits; e++)
    {
        switch (rng.Next(4))
        {
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
