using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace RecursiveAI.ManagedBench;

/// <summary>One implementation in a row: our own, a managed library, or a native one shown for information.</summary>
internal sealed record Cand(string Lib, string Version, Kind Kind, Func<Ctx, int> Run);

internal enum Kind { Ours, Managed, Native }

/// <summary>Shared buffers of one row. Compression rows write into <see cref="Dst"/>; decode rows read <see cref="Packed"/>.</summary>
internal sealed class Ctx
{
    public byte[] Src = [];
    public byte[] Dst = [];
    public byte[] Packed = [];
}

/// <summary>A measured row: what is compressed or decoded, with whom, and how the result is checked.</summary>
internal sealed class Row
{
    public required string Id { get; init; }
    public required string Data { get; init; }        // slice name, see Slices
    public bool Decode { get; init; }
    public required Func<Ctx, byte[]>? MakePacked { get; init; }   // decode rows: the stream every decoder reads
    public required List<Cand> Cands { get; init; }
    /// <summary>Compression rows: decodes the output with our decoder to prove the candidate's stream is real.</summary>
    public Func<byte[], int, byte[]>? Verify { get; init; }
    public int Runs { get; init; } = 3;
}

internal static class Program
{
    private static readonly string Corpora = Environment.GetEnvironmentVariable("MANAGED_BENCH_CORPORA")
        ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", "onearchive", "corpora");

    /// <summary>A candidate that runs longer than this is recorded as timed out and the run stops (exit code 3); rerun the remaining rows with --only.</summary>
    private static readonly int TimeoutSeconds = int.Parse(Environment.GetEnvironmentVariable("MANAGED_BENCH_TIMEOUT") ?? "300", CultureInfo.InvariantCulture);

    public static int Main(string[] args)
    {
        string outPath = "managed-detail.tsv";
        string? only = null;
        bool list = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": outPath = args[++i]; break;
                case "--only": only = args[++i]; break;
                case "--list": list = true; break;
                case "train": return Training.Run(args[1], Corpora);
                case "table": return TableWriter.Run(args[1], args[2]);
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 2;
            }
        }

        var rows = Rows.All(Corpora);
        if (list)
        {
            foreach (var r in rows) Console.WriteLine(r.Id);
            return 0;
        }

        var filter = only?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var fresh = !File.Exists(outPath);
        using var tsv = new StreamWriter(outPath, append: true, new UTF8Encoding(false));
        if (fresh) tsv.WriteLine("row\tdata\tkind\tlib\tversion\trole\tbytes_in\tbytes_out\tbest_s\tmbps\truns\tload_before\tload_after");

        Ctx? ctx = null;
        string? ctxData = null;
        foreach (var row in rows)
        {
            if (filter is not null && !filter.Any(f => row.Id == f || row.Id.StartsWith(f + "/", StringComparison.Ordinal))) continue;
            if (ctxData != row.Data)
            {
                ctx = null;
                GC.Collect();
                ctx = new Ctx { Src = Slices.Load(Corpora, row.Data) };
                ctx.Dst = new byte[ctx.Src.Length + (ctx.Src.Length >> 2) + (4 << 20)];
                ctxData = row.Data;
            }

            ctx!.Packed = row.MakePacked is null ? [] : row.MakePacked(ctx);
            string loadBefore = Load();
            var best = new double[row.Cands.Count];
            var size = new int[row.Cands.Count];
            Array.Fill(best, double.MaxValue);
            var failed = new string?[row.Cands.Count];

            void Emit()
            {
                string loadAfter = Load();
                for (int c = 0; c < row.Cands.Count; c++)
                {
                    var cand = row.Cands[c];
                    string role = cand.Kind.ToString().ToLowerInvariant();
                    string line;
                    if (failed[c] is null && best[c] == double.MaxValue) failed[c] = "not run";
                    if (failed[c] is not null)
                    {
                        line = $"{row.Id}\t{row.Data}\t{(row.Decode ? "decode" : "compress")}\t{cand.Lib}\t{cand.Version}\t{role}\t{ctx.Src.Length}\t-1\t0\t0\t0\t{loadBefore}\t{loadAfter}\tFAILED: {failed[c]}";
                    }
                    else
                    {
                        double mbps = ctx.Src.Length / 1e6 / best[c];
                        line = string.Create(CultureInfo.InvariantCulture, $"{row.Id}\t{row.Data}\t{(row.Decode ? "decode" : "compress")}\t{cand.Lib}\t{cand.Version}\t{role}\t{ctx.Src.Length}\t{size[c]}\t{best[c]:F4}\t{mbps:F2}\t{row.Runs}\t{loadBefore}\t{loadAfter}");
                    }

                    tsv.WriteLine(line);
                    Console.WriteLine(line.Replace('\t', ' '));
                }
                tsv.Flush();
            }

            // Interleaved: every round runs every candidate once, so a busy host hits all of them alike.
            // The first round of each candidate is also checked, and is not timed when it is the only one.
            for (int round = 0; round < row.Runs + 1; round++)
            {
                for (int c = 0; c < row.Cands.Count; c++)
                {
                    if (failed[c] is not null) continue;
                    var cand = row.Cands[c];
                    try
                    {
                        GC.Collect();
                        int cc = c;
                        using var watchdog = new Timer(_ =>
                        {
                            failed[cc] = $"timed out after {TimeoutSeconds} s";
                            Emit();
                            Environment.Exit(3);
                        }, null, TimeoutSeconds * 1000, Timeout.Infinite);
                        var sw = Stopwatch.StartNew();
                        int n = cand.Run(ctx);
                        sw.Stop();
                        if (round == 0)
                        {
                            size[c] = n;
                            string? bad = Check(row, ctx, n);
                            if (bad is not null) failed[c] = bad;
                            continue; // round 0 is a warm-up (JIT, tiering, page faults)
                        }

                        best[c] = Math.Min(best[c], sw.Elapsed.TotalSeconds);
                    }
                    catch (Exception e)
                    {
                        failed[c] = e.GetType().Name + ": " + e.Message.Split('\n')[0];
                    }
                }
            }

            Emit();
            tsv.Flush();
        }

        return 0;
    }

    private static string? Check(Row row, Ctx ctx, int n)
    {
        if (row.Decode)
        {
            if (n != ctx.Src.Length) return $"decoded {n} bytes, expected {ctx.Src.Length}";
            return ctx.Dst.AsSpan(0, n).SequenceEqual(ctx.Src) ? null : "decoded bytes differ";
        }

        if (n <= 0) return "no output";
        if (row.Verify is null) return null;
        try
        {
            var back = row.Verify(ctx.Dst, n);
            return back.AsSpan().SequenceEqual(ctx.Src) ? null : "round trip through our decoder differs";
        }
        catch (Exception e)
        {
            return "our decoder rejected the stream: " + e.GetType().Name;
        }
    }

    private static string Load()
    {
        try { return File.ReadAllText("/proc/loadavg").Split(' ')[0]; }
        catch (IOException) { return "?"; }
    }
}

/// <summary>Input slices. Several rows share a slice, and slow codecs use small ones.</summary>
internal static class Slices
{
    public static byte[] Load(string corpora, string name)
    {
        if (name == "silesia.tar") return File.ReadAllBytes(Path.Combine(corpora, "silesia.tar"));
        if (name == "enwik8") return File.ReadAllBytes(Path.Combine(corpora, "enwik8"));
        // "silesia.tar@16" / "enwik8@16": four equally spaced pieces of the given total MiB, joined.
        int at = name.IndexOf('@', StringComparison.Ordinal);
        string file = name[..at];
        int total = int.Parse(name[(at + 1)..], CultureInfo.InvariantCulture) << 20;
        using var f = File.OpenRead(Path.Combine(corpora, file));
        const int Pieces = 4;
        int piece = total / Pieces;
        var buf = new byte[total];
        for (int i = 0; i < Pieces; i++)
        {
            long pos = (f.Length - piece) * i / (Pieces - 1);
            f.Position = pos;
            f.ReadExactly(buf, i * piece, piece);
        }

        return buf;
    }
}

/// <summary>Writes into a caller-provided array.</summary>
internal sealed class Sink(byte[] buffer) : Stream
{
    public int Pos;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Pos;
    public override long Position { get => Pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) { Buffer.BlockCopy(b, o, buffer, Pos, c); Pos += c; }
    public override void Write(ReadOnlySpan<byte> b) { b.CopyTo(buffer.AsSpan(Pos)); Pos += b.Length; }
}

internal static class Io
{
    /// <summary>Compresses through a stream wrapper, in 256 KiB writes, the way a caller copying a file would.</summary>
    public static int Write(Ctx c, Func<Stream, Stream> make, int length = -1)
    {
        var sink = new Sink(c.Dst);
        int len = length < 0 ? c.Src.Length : length;
        using (var s = make(sink))
        {
            for (int p = 0; p < len; p += 1 << 18)
            {
                s.Write(c.Src, p, Math.Min(1 << 18, len - p));
            }
        }

        return sink.Pos;
    }

    /// <summary>Decodes through a stream wrapper into <see cref="Ctx.Dst"/>.</summary>
    public static int Read(Ctx c, Func<Stream, Stream> make)
    {
        using var ms = new MemoryStream(c.Packed, writable: false);
        using var s = make(ms);
        int total = 0, n;
        while (total < c.Dst.Length && (n = s.Read(c.Dst, total, Math.Min(1 << 20, c.Dst.Length - total))) > 0) total += n;
        return total;
    }

    public static int Copy(byte[] data, byte[] dst)
    {
        Buffer.BlockCopy(data, 0, dst, 0, data.Length);
        return data.Length;
    }
}

/// <summary>Runs a reference tool as a black box over a file in RAM; used only to make decode inputs.</summary>
internal static class Tool
{
    public static byte[] Pipe(string exe, string args, byte[] input)
    {
        var psi = new ProcessStartInfo(exe, args) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var feed = Task.Run(() => { p.StandardInput.BaseStream.Write(input); p.StandardInput.Close(); });
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        feed.Wait();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{exe} exited with {p.ExitCode}");
        return ms.ToArray();
    }
}
