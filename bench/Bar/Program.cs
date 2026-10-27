using System.Diagnostics;
using System.Globalization;
using RecursiveAI.Bar;

// Bar runner. See docs/bench-method.md.
//
//   Bar run  --codec deflate,zstd [--levels 1,6] --files a,b [--impl name] [--runs 5] [--ref-seconds 3]
//            [--threads N] [--out dir] [--work dir] [--tools dir] [--pins file]
//   Bar list                                   registered implementations and references
//   Bar one  --phase c|d|v ...                 (internal) one measurement in a child process
if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: Bar run --codec c1,c2 --files f1,f2 [--levels 1,6] [--impl name] [--runs 5] [--ref-seconds 3] [--threads N] [--out dir] [--work dir] [--tools dir] [--pins file]");
    Console.WriteLine("       Bar list");
    return 0;
}

var opt = Options.Parse(args[1..]);
switch (args[0])
{
    case "list":
        foreach (var c in Registry.All)
        {
            Console.WriteLine($"ours-side: {c.Codec,-8} {c.Impl,-12} {c.Role,-10} levels {string.Join(',', c.Levels)}");
        }

        foreach (var r in References.All)
        {
            Console.WriteLine($"reference: {r.Codec,-8} {r.Id,-10} tool {r.Tool}");
        }

        return 0;
    case "one":
        return Child.Run(opt);
    case "run":
        return Runner.Run(opt);
    default:
        Console.Error.WriteLine("unknown command " + args[0]);
        return 2;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _v = new(StringComparer.Ordinal);

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= a.Length)
            {
                throw new ArgumentException("bad argument " + a[i]);
            }

            o._v[a[i][2..]] = a[++i];
        }

        return o;
    }

    public string? Get(string k) => _v.GetValueOrDefault(k);
    public string Req(string k) => Get(k) ?? throw new ArgumentException("missing --" + k);
    public int Int(string k, int d) => Get(k) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : d;
    public string[] List(string k) => Get(k)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
}

/// <summary>The measurement child: best-of-N timing of one phase in a fresh process, so the parent can read its peak memory.</summary>
internal static class Child
{
    public static int Run(Options o)
    {
        var codec = Registry.Find(o.Req("codec"), o.Req("impl")) ?? throw new ArgumentException("unknown implementation");
        int level = o.Int("level", 1);
        int threads = o.Int("threads", 1);
        int runs = o.Int("runs", 5);
        string phase = o.Req("phase");
        byte[] input = File.ReadAllBytes(o.Req("file"));
        string compPath = o.Req("comp");

        switch (phase)
        {
            case "c":
            {
                byte[] dst = new byte[codec.MaxCompressedLength(input.Length)];
                int size = 0;
                // Two warm-up runs let the JIT reach its final tier before timing.
                for (int i = 0; i < 2; i++)
                {
                    size = codec.Compress(input, dst, level, threads);
                }

                double best = double.MaxValue;
                for (int i = 0; i < runs; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    size = codec.Compress(input, dst, level, threads);
                    best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalSeconds);
                }

                File.WriteAllBytes(compPath, dst.AsSpan(0, size).ToArray());
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"size={size} seconds={best:R}"));
                return 0;
            }

            case "d":
            {
                byte[] comp = File.ReadAllBytes(compPath);
                byte[] dst = new byte[input.Length];
                double best = double.MaxValue;
                for (int i = 0; i < runs + 2; i++)
                {
                    long t = Stopwatch.GetTimestamp();
                    int n = codec.Decompress(comp, dst);
                    double s = Stopwatch.GetElapsedTime(t).TotalSeconds;
                    if (n != input.Length)
                    {
                        throw new InvalidOperationException("decompressed length differs");
                    }

                    if (i >= 2)
                    {
                        best = Math.Min(best, s);
                    }
                }

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"size={comp.Length} seconds={best:R}"));
                return 0;
            }

            case "v":
            {
                byte[] comp = File.ReadAllBytes(compPath);
                byte[] dst = new byte[input.Length];
                int n = codec.Decompress(comp, dst);
                if (n != input.Length || !dst.AsSpan().SequenceEqual(input))
                {
                    Console.Error.WriteLine("round trip mismatch");
                    return 1;
                }

                Console.WriteLine("verified");
                return 0;
            }

            default:
                throw new ArgumentException("unknown phase " + phase);
        }
    }
}

internal static class Runner
{
    private sealed record Ours(long Size, double CSeconds, double DSeconds, long Rss, long BaselineRss);

    public static int Run(Options o)
    {
        string home = Environment.GetEnvironmentVariable("ONEARCHIVE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive");
        string work = o.Get("work") ?? (OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm/onearchive-bar" : Path.Combine(home, "u1", "work"));
        Directory.CreateDirectory(work);
        string outDir = o.Get("out") ?? Path.Combine(Environment.CurrentDirectory, "bar-results");
        Directory.CreateDirectory(outDir);
        var cfg = new BarConfig(
            o.Get("tools") ?? Path.Combine(home, "tools", "bin"), work, o.Int("runs", 5), o.Int("ref-seconds", 3), LoadPins(o.Get("pins")));
        int maxThreads = o.Int("threads", 8);
        string[] codecs = o.List("codec");
        string[] files = o.List("files");
        string? implFilter = o.Get("impl");
        if (codecs.Length == 0 || files.Length == 0)
        {
            Console.Error.WriteLine("--codec and --files are required");
            return 2;
        }

        var machine = EnvInfo.Describe(work);
        string runId = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        using var load = new LoadTrace();
        var rows = new List<Row>();
        var versions = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var refCache = new Dictionary<string, RefResult>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();

        foreach (string codecId in codecs)
        {
            foreach (var impl in Registry.All.Where(c => c.Codec == codecId && (implFilter is null || c.Impl == implFilter)))
            {
                int[] levels = o.Get("levels") is null ? [.. impl.Levels] : [.. o.List("levels").Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
                foreach (string file in files)
                {
                    foreach (int level in levels)
                    {
                        int threads = impl.IsMultithreaded(level) ? maxThreads : 1;
                        var refs = References.For(codecId, level).ToList();
                        if (refs.Count == 0)
                        {
                            Console.Error.WriteLine($"skip {codecId} level {level}: no reference supports it");
                            continue;
                        }

                        var ours = MeasureOurs(impl, level, threads, file, cfg);
                        foreach (var r in refs)
                        {
                            string key = $"{r.Id}|{codecId}|{level}|{threads}|{file}";
                            if (!refCache.TryGetValue(key, out var rr))
                            {
                                rr = r.Measure(file, level, threads, cfg);
                                refCache[key] = rr;
                            }

                            versions[r.Id] = rr.Version;
                            var row = new Row(codecId, level, file, new FileInfo(file).Length, impl.Impl, impl.Role, r.Id, rr.Version, threads,
                                ours.Size, rr.Size, ours.CSeconds, rr.CompressSeconds, ours.DSeconds, rr.DecompressSeconds,
                                ours.Rss, ours.BaselineRss, rr.PeakRssBytes, rr.Method);
                            rows.Add(row);
                            Console.WriteLine($"[{EnvInfo.Elapsed(total)}] {codecId} L{level} {Path.GetFileName(file)} {impl.Impl} vs {r.Id}: size x{row.SizeRatio:0.0000}, compress x{row.CompressTimeRatio:0.00}, decompress x{row.DecompressTimeRatio:0.00} {row.Verdict}");
                        }
                    }
                }
            }
        }

        load.Finish();
        File.WriteAllText(Path.Combine(outDir, $"bar-{runId}.csv"), Report.ToCsv(rows, machine, load, runId));
        File.WriteAllText(Path.Combine(outDir, $"bar-{runId}.md"), Report.ToMarkdown(rows, machine, load, runId, versions));
        Console.WriteLine($"wrote {Path.Combine(outDir, $"bar-{runId}.csv")} and .md ({rows.Count} rows)");
        return 0;
    }

    private static Ours MeasureOurs(IBarCodec impl, int level, int threads, string file, BarConfig cfg)
    {
        var (host, dll) = EnvInfo.Self();
        string comp = Path.Combine(cfg.WorkDir, "ours.cmp");
        string empty = Path.Combine(cfg.WorkDir, "empty");
        File.WriteAllBytes(empty, []);

        string child(string phase, string f) =>
            $"{Proc.Q(host)} {Proc.Q(dll)} one --phase {phase} --codec {impl.Codec} --impl {impl.Impl} --level {level} --threads {threads} " +
            $"--runs {cfg.Runs} --file {Proc.Q(f)} --comp {Proc.Q(comp)}";

        (string Out, long Rss) go(string cmd)
        {
            var r = Proc.Sh(cmd, measureMemory: true);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException($"{impl.Impl} {impl.Codec} level {level}: {r.Stdout}{r.Stderr}");
            }

            return (r.Stdout, r.PeakRssBytes);
        }

        static (long Size, double Seconds) parse(string s)
        {
            var kv = s.Trim().Split(' ').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            return (long.Parse(kv["size"], CultureInfo.InvariantCulture), double.Parse(kv["seconds"], CultureInfo.InvariantCulture));
        }

        var baseline = go(child("c", empty)); // runtime and codec start-up memory with no data
        var c = go(child("c", file));
        var (size, cs) = parse(c.Out);
        var d = go(child("d", file));
        var (_, ds) = parse(d.Out);
        go(child("v", file));
        return new Ours(size, cs, ds, Math.Max(c.Rss, d.Rss), baseline.Rss);
    }

    private static Dictionary<string, string> LoadPins(string? path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        path ??= Path.Combine(AppContext.BaseDirectory, "pins.tsv");
        if (!File.Exists(path))
        {
            return d;
        }

        foreach (string line in File.ReadLines(path))
        {
            string[] f = line.Split('\t');
            if (f.Length >= 2 && !line.StartsWith('#'))
            {
                d.TryAdd(f[0], f[1]);
            }
        }

        return d;
    }
}
