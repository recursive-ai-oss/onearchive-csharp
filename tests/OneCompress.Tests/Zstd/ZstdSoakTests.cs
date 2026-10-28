using System.Diagnostics;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Long-lived use: encoders and dictionaries that stay alive while many frames pass through them at every level, window size
/// and reuse pattern must not make the process grow. The default run is short enough for every build; set
/// <c>ONEARCHIVE_SOAK_FRAMES</c> for a longer one.
/// </summary>
// Run alone: the heap size it watches is process-wide, so other tests allocating at the same time would look like growth.
[CollectionDefinition("Zstd soak", DisableParallelization = true)]
[Collection("Zstd soak")]
public class ZstdSoakTests(Xunit.ITestOutputHelper output)
{
    private static byte[] Text(int seed, int length)
    {
        // Compressible, deterministic data: words drawn from a small vocabulary with an LCG.
        string[] words = ["alpha ", "beta ", "gamma ", "delta ", "snapshot ", "entity ", "position ", "tick ", "{\"id\":", "0000", "ff"];
        var sb = new System.Text.StringBuilder(length + 16);
        uint s = (uint)seed * 2654435761u + 1;
        while (sb.Length < length)
        {
            s = (s * 1664525) + 1013904223;
            sb.Append(words[(s >> 16) % (uint)words.Length]);
            if ((s & 0x300) == 0)
            {
                sb.Append((s >> 8) & 0xffff);
            }
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    [Fact]
    public void LongLivedEncodersAndDictionariesStayBounded()
    {
        int frames = int.TryParse(Environment.GetEnvironmentVariable("ONEARCHIVE_SOAK_FRAMES"), out int n) ? n : 400;
        var dictionaries = new ZstdCompressionDictionary?[]
        {
            null,
            ZstdCompressionDictionary.FromContent(Text(1, 100_000), 7),
            ZstdCompressionDictionary.FromContent(Text(2, 6_000), 8),
        };
        var sizes = frames > 1000 ? new[] { 0, 1, 100, 3_000, 70_000, 300_000, 1_500_000 } : new[] { 0, 1, 100, 3_000, 70_000, 200_000 };
        var windowLogs = new[] { 0, 0, 0, 10, 14, 17, 20, 23 };
        var rng = new Random(12345);
        ZstdEncoder? held = null;
        byte[] output1 = new byte[ZstdEncoder.GetMaxCompressedLength(2_000_000) + 1024];
        var samples = new List<(int Frame, long Heap, long Working)>();
                var clock = Stopwatch.StartNew();

        for (int i = 0; i < frames; i++)
        {
            int level = rng.Next(-5, 23);
            int size = sizes[rng.Next(sizes.Length)];
            if (level >= 19)
            {
                size = Math.Min(size, 300_000);
            }

            var options = new ZstdCompressionOptions
            {
                Level = level,
                WindowLog = windowLogs[rng.Next(windowLogs.Length)],
                Dictionary = dictionaries[rng.Next(dictionaries.Length)],
                Checksum = rng.Next(2) == 0,
                LongDistanceMatching = rng.Next(8) == 0,
            };
            byte[] data = Text(i % 17, size);
            byte[] frame;
            switch (rng.Next(3))
            {
                case 0:
                    frame = Zstd.Zstd.Compress(data, options);
                    break;
                case 1:
                    // A fresh streaming encoder per frame, left for the collector.
                    frame = Streamed(new ZstdEncoder(options), data, output1);
                    break;
                default:
                    // One encoder kept for many frames with changing nothing: the long-lived pattern.
                    held = rng.Next(40) == 0 || held is null ? new ZstdEncoder(options) : held;
                    frame = Streamed(held, data, output1);
                    break;
            }

            Assert.True(frame.Length > 0);
            if (i % 50 == 49 || i == frames - 1)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long heap = GC.GetTotalMemory(false);
                long working = Process.GetCurrentProcess().WorkingSet64;
                samples.Add((i + 1, heap, working));
            }
        }

        foreach ((int f, long h, long w) in samples.Where((_, k) => k % 4 == 0))
        {
            output.WriteLine($"frame {f}: heap {h >> 20} MiB, working set {w >> 20} MiB");
        }

        foreach (ZstdCompressionDictionary? d in dictionaries)
        {
            if (d is not null)
            {
                output.WriteLine($"dictionary {d.Id}: {d.CachedIndexBytes >> 20} MiB of tables kept");
                Assert.True(d.CachedIndexBytes <= ZstdCompressionDictionary.IndexBudget + (512L << 20));
            }
        }

        // A kept encoder or dictionary holds tables for the largest setting it has run, so the heap moves with what is alive at the
        // moment; what must not happen is a climb. Compare the peak of the last third of the run with that of the first third.
        int third = samples.Count / 3;
        long early = samples.Take(third).Max(static x => x.Heap);
        long late = samples.Skip(samples.Count - third).Max(static x => x.Heap);
        long lateWorking = samples.Skip(samples.Count - third).Max(static x => x.Working);
        output.WriteLine($"{frames} frames in {clock.Elapsed.TotalSeconds:F1} s; heap peak first third {early >> 20} MiB, last third {late >> 20} MiB; working set peak {lateWorking >> 20} MiB");
        Assert.True(late <= early + (128L << 20), $"heap grew from {early >> 20} to {late >> 20} MiB");
        Assert.True(late < (1L << 30), $"heap of {late >> 20} MiB after collection");
    }

    private static byte[] Streamed(ZstdEncoder encoder, byte[] data, byte[] buffer)
    {
        encoder.Reset();
        var result = new MemoryStream();
        int position = 0;
        while (true)
        {
            int n = Math.Min(40_000, data.Length - position);
            bool last = position + n == data.Length;
            int taken = 0;
            while (true)
            {
                var status = encoder.Compress(data.AsSpan(position + taken, n - taken), buffer, out int consumed, out int written, last);
                taken += consumed;
                result.Write(buffer, 0, written);
                if (status == System.Buffers.OperationStatus.Done)
                {
                    break;
                }
            }

            position += n;
            if (last)
            {
                return result.ToArray();
            }
        }
    }
}
