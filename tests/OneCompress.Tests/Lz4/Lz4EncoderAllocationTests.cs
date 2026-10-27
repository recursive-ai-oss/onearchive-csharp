using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

// Run alone: another test thread allocating at the same moment would only blur the count.
[Collection("Lz4 allocation")]
public class Lz4EncoderAllocationTests
{
    // Runs `once` until batches of 1000 calls allocate nothing, then requires the next batches to allocate nothing too.
    private static void AssertSteadyStateAllocatesNothing(Action once)
    {
        long Batch()
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                once();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // The runtime's own tiered compilation allocates a little while it warms up, so wait for clean batches first.
        int clean = 0;
        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        while (clean < 4 || warmUp.ElapsedMilliseconds < 1500)
        {
            clean = Batch() == 0 ? clean + 1 : 0;
            Thread.Sleep(30);
            Assert.True(warmUp.ElapsedMilliseconds < 30_000, "allocations never settled");
        }

        var per = new List<long> { Batch(), Batch(), Batch(), Batch(), Batch() };
        Assert.True(per.Count(a => a == 0) >= 4 && per.Max() < 10_000, string.Join(",", per));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, true, false)]
    [InlineData(-3, true, true)]
    [InlineData(1, false, true)]
    [InlineData(4, true, true)]
    [InlineData(10, true, false)]
    public void ReusedEncoderAllocatesNothingForSmallPayloads(int level, bool independent, bool dictionary)
    {
        var encoder = new Lz4Encoder(new Lz4CompressionOptions
        {
            Level = level,
            BlocksIndependent = independent,
            Dictionary = dictionary ? Lz4EncoderTests.MakeDictionary(8000, 5) : null,
        });
        byte[][] payloads = [.. Enumerable.Range(0, 8).Select(i => Lz4EncoderTests.Text(40 + (i * 90), (ulong)(i + 100)))];
        byte[] output = new byte[2048];
        int next = 0;
        AssertSteadyStateAllocatesNothing(() =>
        {
            byte[] p = payloads[next++ & 7];
            if (!encoder.TryCompressFrame(p, output, out int w) || w == 0)
            {
                throw new InvalidOperationException();
            }
        });
    }

    [Fact]
    public void ReusedEncoderAllocatesNothingWhenStreamingPieces()
    {
        var encoder = new Lz4Encoder(new Lz4CompressionOptions { BlockSize = Lz4BlockSize.Size64KiB, BlocksIndependent = false, BlockChecksum = true });
        byte[] data = Lz4EncoderTests.Text(150_000, 3);
        byte[] output = new byte[300_000];
        AssertSteadyStateAllocatesNothing(() =>
        {
            encoder.Compress(data.AsSpan(0, 70_000), output, out _, out _, isFinalBlock: false);
            encoder.Flush(output, out _);
            encoder.Compress(data.AsSpan(70_000), output, out _, out _, isFinalBlock: true);
        });
    }

    [Fact]
    public void ReusedBlockCompressorAllocatesNothing()
    {
        foreach (int level in new[] { 1, 3, 12 })
        {
            Lz4BlockCompressor compressor = Lz4BlockCompressor.Create(level);
            compressor.SetDictionary(Lz4EncoderTests.MakeDictionary(4000));
            byte[] data = Lz4EncoderTests.Text(300, 7);
            byte[] output = new byte[1000];
            AssertSteadyStateAllocatesNothing(() => compressor.Compress(data, 0, output));
        }
    }
}
