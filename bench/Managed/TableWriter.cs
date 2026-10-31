using System.Globalization;
using System.Text;

namespace RecursiveAI.ManagedBench;

/// <summary>
/// Turns the detail rows into tables. The first has one line per bench row: ours (at the row's level) against the fastest managed
/// library. The second decides each managed library the size-matched way: a row is met when some level of ours writes at most 1% more
/// bytes at no less than 0.87 of the library's speed.
/// </summary>
internal static class TableWriter
{
    private const double SpeedFloor = 0.87;
    private const double SizeCeiling = 1.01;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Rec(string Row, string Data, string Kind, string Lib, string Version, string Role, long BytesIn, long BytesOut, double Mbps, string Load, string Note);

    private static string F1(double v) => v.ToString("F1", Inv);
    private static string F2(double v) => v.ToString("F2", Inv);
    private static string Pct(double v) => v.ToString("+0.0%;-0.0%;0.0%", Inv);

    public static int Run(string detailPath, string outPath)
    {
        var recs = new List<Rec>();
        foreach (var line in File.ReadLines(detailPath).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 13) continue;
            recs.Add(new Rec(f[0], f[1], f[2], f[3], f[4], f[5], long.Parse(f[6], Inv), long.Parse(f[7], Inv),
                double.Parse(f[9], Inv), $"{f[11]}-{f[12]}", f.Length > 13 ? f[13] : ""));
        }

        var tsv = new StringBuilder("row\tdata\tours_level\tours_mbps\tbest_managed\tbest_managed_mbps\tours_over_managed\tsize_delta_vs_managed\tmet\tmatched\tnative_info\thost_load\tother_managed\tours_bytes\tmanaged_bytes\n");
        var md = new StringBuilder("| Row | Data | Ours MB/s | Fastest managed (MB/s) | Ours / managed | Size vs managed | Met | Native, information (MB/s) | Load before-after | Other managed (MB/s) |\n|---|---|---:|---|---:|---:|---|---|---|---|\n");
        var md2 = new StringBuilder("| Row | Data | Managed library (MB/s, bytes) | Met by (size vs it, speed vs it) | Nearest: smaller side is too slow | Nearest: faster side is too big |\n|---|---|---|---|---|---|\n");
        foreach (var g in recs.GroupBy(r => r.Row))
        {
            var ours = g.FirstOrDefault(r => r.Role == "ours" && r.Note.Length == 0);
            if (ours is null) continue;
            var all = g.Where(r => (r.Role == "ours" || r.Role == "sweep") && r.Note.Length == 0).ToList();
            var managed = g.Where(r => r.Role == "managed" && r.Note.Length == 0).OrderByDescending(r => r.Mbps).ToList();
            var failed = g.Where(r => r.Role == "managed" && r.Note.Length > 0).ToList();
            var native = g.Where(r => r.Role == "native" && r.Note.Length == 0).ToList();
            bool compress = ours.Kind == "compress";

            string best = "none exists", bestMbps = "", ratio = "", size = "", ob = "", mb = "";
            if (managed.Count > 0)
            {
                var b = managed[0];
                best = $"{b.Lib} {b.Version}";
                bestMbps = F1(b.Mbps);
                ratio = F2(ours.Mbps / b.Mbps);
                if (compress) size = Pct((double)ours.BytesOut / b.BytesOut - 1);
                ob = ours.BytesOut.ToString(Inv);
                mb = b.BytesOut.ToString(Inv);
            }

            // Size-matched decisions, one per managed library.
            var unmet = new List<string>();
            var matched = new List<string>();
            foreach (var m in managed)
            {
                if (!compress)
                {
                    bool ok = ours.Mbps >= SpeedFloor * m.Mbps;
                    if (!ok) unmet.Add(m.Lib);
                    matched.Add($"{m.Lib}: {(ok ? "met" : "not met")} {F2(ours.Mbps / m.Mbps)}x");
                    continue;
                }

                var meets = all.Where(c => c.BytesOut <= SizeCeiling * m.BytesOut && c.Mbps >= SpeedFloor * m.Mbps).OrderBy(c => c.BytesOut).ThenByDescending(c => c.Mbps).ToList();
                string Desc(Rec c) => $"{c.Version} {Pct((double)c.BytesOut / m.BytesOut - 1)} size, {F2(c.Mbps / m.Mbps)}x";
                string metBy = meets.Count > 0 ? Desc(meets[0]) : "not met";
                var slow = all.Where(c => c.BytesOut <= SizeCeiling * m.BytesOut).OrderByDescending(c => c.Mbps).FirstOrDefault();
                var big = all.Where(c => c.Mbps >= SpeedFloor * m.Mbps).OrderBy(c => c.BytesOut).FirstOrDefault();
                if (meets.Count == 0) unmet.Add(m.Lib);
                matched.Add($"{m.Lib} {m.Version}: {metBy}");
                md2.Append(Inv, $"| {g.Key} | {ours.Data} | {m.Lib} {m.Version} ({F1(m.Mbps)}, {m.BytesOut}) | {metBy} | {(meets.Count == 0 && slow is not null ? Desc(slow) : "")} | {(meets.Count == 0 && big is not null ? Desc(big) : "")} |\n");
            }

            string met = managed.Count == 0 ? "n/a (no managed)" : unmet.Count == 0 ? "met" : "NOT MET: " + string.Join(", ", unmet);

            string nat = string.Join("; ", native.Select(n => $"{n.Lib}: {F1(n.Mbps)}" + (compress ? $" ({Pct((double)n.BytesOut / ours.BytesOut - 1)} size vs ours)" : "")));
            string other = string.Join("; ", managed.Skip(1).Select(m => $"{m.Lib}: {F1(m.Mbps)}" + (compress ? $" ({Pct((double)m.BytesOut / ours.BytesOut - 1)})" : "")))
                + (failed.Count > 0 ? "; failed: " + string.Join(", ", failed.Select(x => $"{x.Lib} ({x.Note})")) : "");
            string mbpsOurs = F1(ours.Mbps);
            tsv.Append(string.Join('\t', g.Key, ours.Data, ours.Version, mbpsOurs, best, bestMbps, ratio, size, met, string.Join(" | ", matched), nat, ours.Load, other, ob, mb)).Append('\n');
            md.Append(Inv, $"| {g.Key} | {ours.Data} | {mbpsOurs} | {best}{(bestMbps.Length > 0 ? " (" + bestMbps + ")" : "")} | {ratio} | {size} | {met} | {nat} | {ours.Load} | {other} |\n");
        }

        File.WriteAllText(outPath, tsv.ToString());
        Console.Write(md);
        Console.WriteLine();
        Console.WriteLine("Size-matched rows (met = some level of ours within +1% size at 0.87 of the library's speed or better):");
        Console.WriteLine();
        Console.Write(md2);
        return 0;
    }
}
