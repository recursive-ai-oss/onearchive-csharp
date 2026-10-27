using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class ZstdAllocationTests
{
    [Theory]
    [InlineData("dict16-l3-p2100")]
    [InlineData("dict4-l19-p2101")]
    [InlineData("dict112-l3-p2101")]
    [InlineData("text300-l3")]
    [InlineData("onebyte")]
    public void ReusedDecoderWithADictionaryAllocatesNothingPerFrame(string name)
    {
        ZstdVector v = ZstdTestData.Vector(name);
        byte[] compressed = v.Compressed;
        byte[] output = new byte[v.Size + 16];
        var decoder = new ZstdDecoder(v.Options());

        // The runtime's own tiered compilation allocates a little while it warms up, so run until a batch is clean,
        // then require the next batches to be clean too.
        long Batch()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                if (!Zstd.Zstd.TryDecompress(decoder, compressed, output, out int written) || written != v.Size)
                {
                    throw new InvalidOperationException("wrong result");
                }
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        int clean = 0;
        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        while (clean < 4 || warmUp.ElapsedMilliseconds < 1500)
        {
            clean = Batch() == 0 ? clean + 1 : 0;
            Thread.Sleep(30);
            Assert.True(warmUp.ElapsedMilliseconds < 30_000, "allocations never settled");
        }

        // Any allocation per frame would add at least 1000 bytes to every batch. The runtime itself occasionally
        // allocates some thousand bytes on the calling thread once (tiered compilation bookkeeping), which is not ours.
        var per = new List<long> { Batch(), Batch(), Batch(), Batch(), Batch() };
        Assert.True(per.Count(a => a == 0) >= 4 && per.Max() < 10_000, string.Join(",", per));
        v.AssertMatches(output.AsSpan(0, v.Size));
    }

    [Fact]
    public void StreamingPathAllocatesNothingPerFrameEither()
    {
        // The frame carries no content size and the output arrives in small pieces, so the window and the
        // block buffer are used.
        ZstdVector v = ZstdTestData.Vector("text300k-nosize-pipe");
        byte[] compressed = v.Compressed;
        var decoder = new ZstdDecoder();
        byte[] chunk = new byte[10_000];

        void DecodeOnce()
        {
            decoder.Reset();
            int pos = 0;
            int total = 0;
            while (true)
            {
                OperationStatus s = decoder.Decompress(compressed.AsSpan(pos, Math.Min(5000, compressed.Length - pos)), chunk, out int c, out int w);
                pos += c;
                total += w;
                if (s == OperationStatus.Done && pos == compressed.Length)
                {
                    break;
                }
            }

            if (total != v.Size)
            {
                throw new InvalidOperationException("wrong size");
            }
        }

        for (int i = 0; i < 5; i++)
        {
            DecodeOnce();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            DecodeOnce();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
