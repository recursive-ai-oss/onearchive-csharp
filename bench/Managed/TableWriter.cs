using System.Globalization;
using System.Text;

namespace RecursiveAI.ManagedBench;

/// <summary>Turns the detail rows into one line per bench row: ours against the fastest managed library.</summary>
internal static class TableWriter
{
    private sealed record Rec(string Row, string Data, string Kind, string Lib, string Version, string Role, long BytesIn, long BytesOut, double Mbps, string Load, string Note);

    public static int Run(string detailPath, string outPath)
    {
        var recs = new List<Rec>();
        foreach (var line in File.ReadLines(detailPath).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 13) continue;
            recs.Add(new Rec(f[0], f[1], f[2], f[3], f[4], f[5], long.Parse(f[6], CultureInfo.InvariantCulture), long.Parse(f[7], CultureInfo.InvariantCulture),
                double.Parse(f[9], CultureInfo.InvariantCulture), $"{f[11]}-{f[12]}", f.Length > 13 ? f[13] : ""));
        }

        var tsv = new StringBuilder("row\tdata\tours_mbps\tbest_managed\tbest_managed_mbps\tours_over_managed\tsize_delta_vs_managed\tnative_info\thost_load\tother_managed\tours_bytes\tmanaged_bytes\n");
        var md = new StringBuilder("| Row | Data | Ours MB/s | Fastest managed (MB/s) | Ours / managed | Size vs managed | Native, information (MB/s) | Load before-after | Other managed (MB/s) |\n|---|---|---:|---|---:|---:|---|---|---|\n");
        foreach (var g in recs.GroupBy(r => r.Row))
        {
            var ours = g.FirstOrDefault(r => r.Role == "ours");
            if (ours is null) continue;
            var managed = g.Where(r => r.Role == "managed" && r.Note.Length == 0).OrderByDescending(r => r.Mbps).ToList();
            var failed = g.Where(r => r.Role == "managed" && r.Note.Length > 0).ToList();
            var native = g.Where(r => r.Role == "native" && r.Note.Length == 0).ToList();
            bool compress = ours.Kind == "compress";

            string best = "none exists", bestMbps = "", ratio = "", size = "", ob = "", mb = "";
            if (managed.Count > 0)
            {
                var b = managed[0];
                best = $"{b.Lib} {b.Version}";
                bestMbps = b.Mbps.ToString("F1", CultureInfo.InvariantCulture);
                ratio = (ours.Mbps / b.Mbps).ToString("F2", CultureInfo.InvariantCulture);
                if (compress)
                {
                    size = ((double)ours.BytesOut / b.BytesOut - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture);
                }

                ob = ours.BytesOut.ToString(CultureInfo.InvariantCulture);
                mb = b.BytesOut.ToString(CultureInfo.InvariantCulture);
            }

            string nat = string.Join("; ", native.Select(n => $"{n.Lib}: {n.Mbps.ToString("F1", CultureInfo.InvariantCulture)}" + (compress ? $" ({((double)n.BytesOut / ours.BytesOut - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture)} size vs ours)" : "")));
            string other = string.Join("; ", managed.Skip(1).Select(m => $"{m.Lib}: {m.Mbps.ToString("F1", CultureInfo.InvariantCulture)}" + (compress ? $" ({((double)m.BytesOut / ours.BytesOut - 1).ToString("+0.0%;-0.0%;0.0%", CultureInfo.InvariantCulture)})" : "")))
                + (failed.Count > 0 ? "; failed: " + string.Join(", ", failed.Select(x => $"{x.Lib} ({x.Note})")) : "");
            string mbpsOurs = ours.Mbps.ToString("F1", CultureInfo.InvariantCulture);
            tsv.Append(string.Join('\t', g.Key, ours.Data, mbpsOurs, best, bestMbps, ratio, size, nat, ours.Load, other, ob, mb)).Append('\n');
            md.Append(CultureInfo.InvariantCulture, $"| {g.Key} | {ours.Data} | {mbpsOurs} | {best}{(bestMbps.Length > 0 ? " (" + bestMbps + ")" : "")} | {ratio} | {size} | {nat} | {ours.Load} | {other} |\n");
        }

        File.WriteAllText(outPath, tsv.ToString());
        Console.Write(md);
        return 0;
    }
}
