using RecursiveAI.OneCompress.Ppmd;
using SC = SharpCompress.Compressors;

namespace RecursiveAI.ManagedBench;

/// <summary>PPMd rows: variant I (the zip method) at order 6 with 16 MiB of model memory, the only variant the managed library writes and reads.</summary>
internal static class PpmdRows
{
    private const int Order = 6;
    private const int MemoryMiB = 16;

    public static void Add(List<Row> rows)
    {
        rows.Add(new Row
        {
            Id = "ppmd-i-compress/o6m16", Data = "silesia.tar@16", Runs = 2, MakePacked = null,
            Verify = (d, n) => PpmdI.Decompress(d.AsSpan(0, n), IDecode(16 << 20)),
            Cands =
            [
                new Cand("ours", "main", Kind.Ours, c => PpmdI.TryCompress(c.Src, c.Dst, out int n, IOptions) ? n : -1),
                new Cand("SharpCompress", "1.0.0", Kind.Managed, c => Io.Write(c, s => new SC.PPMd.PpmdStream(new SC.PPMd.PpmdProperties(MemoryMiB << 20, Order), s, true))),
            ],
        });
        rows.Add(new Row
        {
            Id = "ppmd-i-decode/o6m16", Data = "silesia.tar@16", Decode = true, Runs = 2,
            MakePacked = c => PpmdI.Compress(c.Src, IOptions),
            Cands =
            [
                new Cand("ours", "main", Kind.Ours, c => PpmdI.Decompress(c.Packed, IDecode(c.Src.Length)).AsSpan().TryCopyTo(c.Dst) ? c.Src.Length : -1),
                new Cand("SharpCompress", "1.0.0", Kind.Managed, c => Io.Read(c, s => new SC.PPMd.PpmdStream(new SC.PPMd.PpmdProperties(MemoryMiB << 20, Order), s, false))),
            ],
        });
    }

    private static readonly PpmdIOptions IOptions = new() { Order = Order, MemorySize = MemoryMiB << 20, RestoreMethod = PpmdRestoreMethod.Restart };

    private static PpmdIDecompressionOptions IDecode(long outputSize) =>
        new() { Order = Order, MemorySize = MemoryMiB << 20, RestoreMethod = PpmdRestoreMethod.Restart, OutputSize = outputSize };
}
