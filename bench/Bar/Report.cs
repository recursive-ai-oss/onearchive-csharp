using System.Globalization;
using System.Text;

namespace RecursiveAI.Bar;

/// <summary>One result row: an implementation against a reference at one (codec, level, file).</summary>
internal sealed record Row(
    string Codec, int Level, string File, long InputBytes, string Impl, Role Role, string Ref, string RefVersion,
    int Threads, long OurSize, long RefSize, double OurCompressSeconds, double RefCompressSeconds,
    double OurDecompressSeconds, double RefDecompressSeconds, long OurRssBytes, long OurBaselineRssBytes, long RefRssBytes,
    string RefMethod)
{
    public const double SizeLimit = 1.01;
    public const double TimeLimit = 1.15;

    public double SizeRatio => (double)OurSize / RefSize;
    public double CompressTimeRatio => OurCompressSeconds / RefCompressSeconds;
    public double DecompressTimeRatio => OurDecompressSeconds / RefDecompressSeconds;
    public double OurCompressMBps => InputBytes / 1e6 / OurCompressSeconds;
    public double RefCompressMBps => InputBytes / 1e6 / RefCompressSeconds;
    public double OurDecompressMBps => InputBytes / 1e6 / OurDecompressSeconds;
    public double RefDecompressMBps => InputBytes / 1e6 / RefDecompressSeconds;

    /// <summary>"pass" or "FAIL" for gated rows, "-" for comparison rows.</summary>
    public string Verdict =>
        Role != Role.Ours ? "-"
        : SizeRatio <= SizeLimit && CompressTimeRatio <= TimeLimit && DecompressTimeRatio <= TimeLimit ? "pass" : "FAIL";
}

internal static class Report
{
    private static string F(double v, string fmt = "0.###") => v.ToString(fmt, CultureInfo.InvariantCulture);

    private static string Csv(string s) =>
        s.Contains(',', StringComparison.Ordinal) || s.Contains('"', StringComparison.Ordinal)
            ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    public static string ToCsv(IEnumerable<Row> rows, Machine m, LoadTrace load, string runId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("run,machine,cpu,cores,arch,sdk,runtime,work_files,load_before,load_during_max,load_during_mean,load_after," +
            "codec,level,file,input_bytes,impl,role,reference,reference_version,threads," +
            "our_size,ref_size,size_ratio,our_compress_mbps,ref_compress_mbps,compress_time_ratio," +
            "our_decompress_mbps,ref_decompress_mbps,decompress_time_ratio,our_peak_rss_mb,our_baseline_rss_mb,ref_peak_rss_mb,verdict,ref_method");
        foreach (var r in rows)
        {
            sb.AppendJoin(',',
                Csv(runId), Csv(m.Name), Csv(m.Cpu), m.Cores, m.Arch, Csv(m.Sdk), Csv(m.Runtime), Csv(m.WorkKind),
                F(load.Before, "0.00"), F(load.DuringMax, "0.00"), F(load.DuringMean, "0.00"), F(load.After, "0.00"),
                r.Codec, r.Level, Csv(r.File), r.InputBytes, r.Impl, r.Role, r.Ref, Csv(r.RefVersion), r.Threads,
                r.OurSize, r.RefSize, F(r.SizeRatio, "0.0000"),
                F(r.OurCompressMBps, "0.0"), F(r.RefCompressMBps, "0.0"), F(r.CompressTimeRatio, "0.000"),
                F(r.OurDecompressMBps, "0.0"), F(r.RefDecompressMBps, "0.0"), F(r.DecompressTimeRatio, "0.000"),
                F(r.OurRssBytes / 1048576.0, "0.0"), F(r.OurBaselineRssBytes / 1048576.0, "0.0"), F(r.RefRssBytes / 1048576.0, "0.0"),
                r.Verdict, Csv(r.RefMethod));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToMarkdown(IReadOnlyList<Row> rows, Machine m, LoadTrace load, string runId, IReadOnlyDictionary<string, string> versions)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Bar run {runId}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Machine: {m.Name}, {m.Cpu}, {m.Cores} cores, {m.Arch}, {m.Os}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- SDK {m.Sdk}, runtime {m.Runtime}; reference files on {m.WorkKind}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Load average (1 min): before {F(load.Before, "0.00")}, during max {F(load.DuringMax, "0.00")} / mean {F(load.DuringMean, "0.00")}, after {F(load.After, "0.00")} (the run itself contributes about 1)");
        foreach (var (k, v) in versions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Reference {k}: {v}");
        }

        sb.AppendLine("- Bar: size <= 1.01 x reference; compress and decompress time each <= 1.15 x reference. Comparison rows are reported, not gated.");
        sb.AppendLine();
        sb.AppendLine("| codec | lvl | file | impl | ref | size ratio | compress MB/s ours / ref (time x) | decompress MB/s ours / ref (time x) | peak MB ours (base) / ref | verdict |");
        sb.AppendLine("|---|---:|---|---|---|---:|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {r.Codec} | {r.Level} | {Path.GetFileName(r.File)} | {r.Impl} | {r.Ref} | {F(r.SizeRatio, "0.0000")} " +
                $"| {F(r.OurCompressMBps, "0.0")} / {F(r.RefCompressMBps, "0.0")} ({F(r.CompressTimeRatio, "0.00")}) " +
                $"| {F(r.OurDecompressMBps, "0.0")} / {F(r.RefDecompressMBps, "0.0")} ({F(r.DecompressTimeRatio, "0.00")}) " +
                $"| {F(r.OurRssBytes / 1048576.0, "0")} ({F(r.OurBaselineRssBytes / 1048576.0, "0")}) / {F(r.RefRssBytes / 1048576.0, "0")} | {r.Verdict} |");
        }

        return sb.ToString();
    }
}
