using System.Globalization;
using System.Text.RegularExpressions;

namespace RecursiveAI.Bar;

/// <summary>Settings shared by every measurement.</summary>
internal sealed record BarConfig(string ToolsBin, string WorkDir, int Runs, int RefSeconds, IReadOnlyDictionary<string, string> Pins);

/// <summary>What a reference tool measured for one (level, file).</summary>
internal sealed record RefResult(
    string Ref, string Version, long Size, double CompressSeconds, double DecompressSeconds, long PeakRssBytes, string Method);

/// <summary>A pinned reference tool for one codec.</summary>
internal abstract class Reference(string id, string codec, string tool, int minLevel, int maxLevel)
{
    public string Id { get; } = id;
    public string Codec { get; } = codec;
    public string Tool { get; } = tool;
    public bool Supports(int level) => level >= minLevel && level <= maxLevel;
    public abstract string Version(BarConfig cfg);
    public abstract RefResult Measure(string file, int level, int threads, BarConfig cfg);

    protected string Bin(BarConfig cfg) => Path.Combine(cfg.ToolsBin, Tool);

    protected static double Best(int runs, Func<double> run)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            best = Math.Min(best, run());
        }

        return best;
    }
}

/// <summary>
/// A reference with its own in-memory benchmark mode (zstd -b, lz4 -b): the tool reports the
/// compressed size and the best compression and decompression speeds itself.
/// </summary>
internal sealed class BenchModeReference(string id, string codec, string tool, int minLevel, int maxLevel)
    : Reference(id, codec, tool, minLevel, maxLevel)
{
    private static readonly Regex Result = new(
        @"->\s*(\d+)\s*\(\s*(?:x)?[\d.]+%?\)\s*,?\s*([\d.]+)\s*MB/s\s*,?\s*([\d.]+)\s*MB/s", RegexOptions.CultureInvariant);

    public override string Version(BarConfig cfg) => Proc.FirstLine(Bin(cfg), "--version");

    public override RefResult Measure(string file, int level, int threads, BarConfig cfg)
    {
        string cmd(int seconds) =>
            $"{Proc.Q(Bin(cfg))} -b{level} -e{level} -i{seconds} -T{threads} {Proc.Q(file)} 2>&1 | tr '\\r' '\\n'";
        var r = Proc.Sh(cmd(cfg.RefSeconds));
        var matches = Result.Matches(r.Stdout);
        if (r.ExitCode != 0 || matches.Count == 0)
        {
            throw new InvalidOperationException($"{Id}: cannot parse benchmark output: {r.Stdout}");
        }

        var m = matches[^1];
        long size = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        double cmbs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        double dmbs = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        long inBytes = new FileInfo(file).Length;
        // Peak memory comes from a separate one-second run under the time utility; it covers compression and decompression together.
        long rss = Proc.Sh(cmd(1), measureMemory: true).PeakRssBytes;
        return new RefResult(Id, Version(cfg), size, inBytes / (cmbs * 1e6), inBytes / (dmbs * 1e6), rss,
            $"{Tool} -b (in-memory, -i{cfg.RefSeconds}, -T{threads})");
    }
}

/// <summary>
/// A command-line reference timed on files in the work directory, with the process start-up cost
/// (the same command on an empty input) subtracted. The compressed size is corrected for container framing.
/// </summary>
internal sealed class CliReference(
    string id, string codec, string tool, int minLevel, int maxLevel,
    Func<int, int, string> compressArgs, string decompressArgs, string versionArgs, bool gzipFraming)
    : Reference(id, codec, tool, minLevel, maxLevel)
{
    public override string Version(BarConfig cfg) =>
        versionArgs.StartsWith("pin:", StringComparison.Ordinal)
            ? versionArgs[4..] + " " + cfg.Pins.GetValueOrDefault(versionArgs[4..], "unknown") + " (pinned; the tool has no version flag)"
            : Proc.FirstLine(Bin(cfg), versionArgs);

    public override RefResult Measure(string file, int level, int threads, BarConfig cfg)
    {
        string bin = Proc.Q(Bin(cfg));
        string empty = Path.Combine(cfg.WorkDir, "empty");
        string emptyZ = Path.Combine(cfg.WorkDir, "empty.cmp");
        string z = Path.Combine(cfg.WorkDir, "ref.cmp");
        string back = Path.Combine(cfg.WorkDir, "ref.out");
        File.WriteAllBytes(empty, []);

        string comp(string i, string o) => $"{bin} {compressArgs(level, threads)} < {Proc.Q(i)} > {Proc.Q(o)}";
        string decomp(string i, string o) => $"{bin} {decompressArgs} < {Proc.Q(i)} > {Proc.Q(o)}";
        void run(string cmd)
        {
            var r = Proc.Sh(cmd);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException($"{Id}: '{cmd}' failed: {r.Stderr}");
            }
        }

        double timed(string cmd)
        {
            var r = Proc.Sh(cmd);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException($"{Id}: '{cmd}' failed: {r.Stderr}");
            }

            return r.Seconds;
        }

        // Start-up cost: the same commands on empty input.
        run(comp(empty, emptyZ));
        long emptySize = new FileInfo(emptyZ).Length;
        double startC = Best(cfg.Runs, () => timed(comp(empty, emptyZ)));
        double startD = Best(cfg.Runs, () => timed(decomp(emptyZ, back)));

        run(comp(file, z)); // warm-up, and leaves the compressed file for the decompression runs
        double tc = Best(cfg.Runs, () => timed(comp(file, z)));
        long size = new FileInfo(z).Length;
        run(decomp(z, back));
        double td = Best(cfg.Runs, () => timed(decomp(z, back)));
        if (new FileInfo(back).Length != new FileInfo(file).Length)
        {
            throw new InvalidOperationException($"{Id}: round trip changed the length");
        }

        long rssC = Proc.Sh(comp(file, z), measureMemory: true).PeakRssBytes;
        long rssD = Proc.Sh(decomp(z, back), measureMemory: true).PeakRssBytes;

        // gzip framing is 10 header + 8 trailer bytes around the deflate stream; zlib's is 2 + 4.
        long overhead = gzipFraming ? emptySize - 2 : 0;
        long adjusted = Codec switch
        {
            "deflate" when gzipFraming => size - overhead,
            "zlib" when gzipFraming => size - overhead + 6,
            _ => size,
        };

        return new RefResult(Id, Version(cfg), adjusted, Math.Max(tc - startC, 1e-6), Math.Max(td - startD, 1e-6),
            Math.Max(rssC, rssD), $"{Tool} CLI, stdin to stdout, start-up subtracted, best of {cfg.Runs}");
    }
}

internal static class References
{
    public static IReadOnlyList<Reference> All { get; } =
    [
        new BenchModeReference("zstd", "zstd", "zstd", 1, 22),
        new BenchModeReference("lz4", "lz4", "lz4", 1, 12),
        // Deflate family: zlib-ng for streaming, libdeflate for one-shot. Both write gzip, so the
        // raw-deflate and zlib codecs compare sizes after removing the gzip framing.
        ..GzipRefs("deflate"),
        ..GzipRefs("zlib"),
        ..GzipRefs("gzip"),
        new CliReference("brotli", "brotli", "brotli", 0, 11, (l, _) => $"-q {l} -c", "-d -c", "--version", false),
        new CliReference("xz", "xz", "xz", 0, 9, (l, _) => $"-{l} -T1 -c", "-d -c", "--version", false),
        new CliReference("bzip2", "bzip2", "bzip2", 1, 9, (l, _) => $"-{l} -c", "-d -c", "--help", false),
        new CliReference("lzip", "lzip", "lzip", 0, 9, (l, _) => $"-{l} -c", "-d -c", "--version", false),
    ];

    private static IEnumerable<Reference> GzipRefs(string codec)
    {
        yield return new CliReference("zlib-ng", codec, "minigzip-ng", 1, 9, (l, _) => $"-{l}", "-d", "pin:zlib-ng", true);
        yield return new CliReference("libdeflate", codec, "libdeflate-gzip", 1, 12, (l, _) => $"-{l} -c", "-d -c", "-V", true);
    }

    public static IEnumerable<Reference> For(string codec, int level) =>
        All.Where(r => r.Codec == codec && r.Supports(level));
}
