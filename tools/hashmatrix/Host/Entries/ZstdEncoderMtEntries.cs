using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The Zstandard encoder cutting its input into jobs, at levels 1 and 3, with and without long-distance matching, one-shot and
/// streamed. The thread count of the column (<see cref="MatrixContext.Threads"/>) is the number of workers; the bytes must be the
/// same for every count, so the threads column of the matrix has to equal the single-thread column.
/// </summary>
internal static class ZstdEncoderMtEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int level in new[] { 1, 3, 6, 12 })
        {
            int l = level;
            foreach (bool longDistance in new[] { false, true })
            {
                bool ldm = longDistance;
                string tag = ldm ? "ldm" : "plain";
                // 128 KiB is the smallest job; 300000 is rounded up to 384 KiB; auto is 1 MiB at these window sizes.
                Matrix.Add("zstd-encode", $"mt-{tag}-level{l}-job128k", (input, ctx) => Zstd.Compress(input, Make(l, ldm, 128 * 1024, ctx.Threads)), "text-3m", "binary-1m", "runs-1m", "random-1m", "zeros-4m");
                Matrix.Add("zstd-encode", $"mt-{tag}-level{l}-job300000", (input, ctx) => Zstd.Compress(input, Make(l, ldm, 300_000, ctx.Threads)), "text-3m", "binary-1m", "text-64k");
                Matrix.Add("zstd-encode", $"mt-{tag}-level{l}-auto-checksum-nosize", (input, ctx) => Zstd.Compress(input, Make(l, ldm, ZstdCompressionOptions.AutoJobSize, ctx.Threads, checksum: true, contentSize: false)), "text-3m", "binary-1m");
                Matrix.Add("zstd-encode", $"mt-{tag}-level{l}-stream", (input, ctx) => ZstdEncoderLdmEntries.Stream(input, Make(l, ldm, 256 * 1024, ctx.Threads, contentSize: true, size: input.Length)), "text-3m", "binary-1m");
                Matrix.Add("zstd-encode", $"mt-{tag}-level{l}-stream-unknown-size", (input, ctx) => ZstdEncoderLdmEntries.Stream(input, Make(l, ldm, 256 * 1024, ctx.Threads)), "text-3m", "binary-1m");
            }
        }

        // Four workers whatever the column asks for: a browser runs them one after another, and the bytes must not notice.
        Matrix.Add("zstd-encode", "mt-level3-job256k-fixed-workers4", (input, _) => Zstd.Compress(input, Make(3, false, 256 * 1024, 4)), "text-3m", "binary-1m");
        Matrix.Add("zstd-encode", "mt-ldm-level1-job256k-fixed-workers4", (input, _) => Zstd.Compress(input, Make(1, true, 256 * 1024, 4)), "text-3m", "binary-1m");
        Matrix.Add("zstd-encode", "mt-level3-job128k-overlap0", (input, ctx) => Zstd.Compress(input, new ZstdCompressionOptions { Level = 3, JobSize = 128 * 1024, OverlapSize = 0, Workers = ctx.Threads }), "text-3m", "binary-1m");
        Matrix.Add("zstd-encode", "mt-level3-job128k-rawdict", (input, ctx) => Zstd.Compress(input, new ZstdCompressionOptions { Level = 3, JobSize = 128 * 1024, Workers = ctx.Threads, Dictionary = ZstdCompressionDictionary.FromContent(Inputs.Get("text-64k").AsSpan(0, 20000), 99) }), "text-3m", "binary-1m");
    }

    private static ZstdCompressionOptions Make(int level, bool longDistance, int jobSize, int workers, bool checksum = false, bool contentSize = false, int size = 0) => new()
    {
        Level = level,
        LongDistanceMatching = longDistance,
        WindowLog = 21,
        JobSize = jobSize,
        Workers = workers,
        Checksum = checksum,
        WriteContentSize = !checksum,
        ContentSize = contentSize ? size : null,
    };
}
