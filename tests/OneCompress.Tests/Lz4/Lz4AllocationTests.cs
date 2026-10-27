using System.Buffers;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

public class Lz4AllocationTests
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

        // Any allocation per call would add at least 1000 bytes to every batch.
        var per = new List<long> { Batch(), Batch(), Batch(), Batch(), Batch() };
        Assert.True(per.Count(a => a == 0) >= 4 && per.Max() < 10_000, string.Join(",", per));
    }

    [Theory]
    [InlineData("payload2100-dict8k")]
    [InlineData("payload2101-dict8k-hc")]
    [InlineData("text300-1")]
    [InlineData("onebyte")]
    [InlineData("empty")]
    [InlineData("text100k-B4-BD-BX")]
    [InlineData("runs5m-B7")]
    [InlineData("runs5m-B7-BD")]
    [InlineData("random70k-B4-BX-BD")]
    [InlineData("text100k-legacy")]
    public void ReusedDecoderAllocatesNothingPerFrame(string name)
    {
        Lz4Vector v = Lz4TestData.Vector(name);
        byte[] compressed = v.Compressed;
        byte[] output = new byte[v.Size + 16];
        var decoder = new Lz4Decoder(v.Options());
        AssertSteadyStateAllocatesNothing(() =>
        {
            if (!Lz4.Lz4.TryDecompress(decoder, compressed, output, out int written) || written != v.Size)
            {
                throw new InvalidOperationException("wrong result");
            }
        });
        v.AssertMatches(output.AsSpan(0, v.Size));
    }

    [Theory]
    [InlineData("payload2100-dict8k", 17)]
    [InlineData("text100k-B4-BD-BX", 5000)]
    [InlineData("text100k-pipe", 3000)]
    [InlineData("concat-skippable", 50)]
    public void StreamingPathAllocatesNothingPerFrameEither(string name, int outputChunk)
    {
        // The output arrives in small pieces, so the window and the block buffer are used.
        Lz4Vector v = Lz4TestData.Vector(name);
        byte[] compressed = v.Compressed;
        var decoder = new Lz4Decoder(v.Options());
        byte[] chunk = new byte[outputChunk];
        AssertSteadyStateAllocatesNothing(() =>
        {
            decoder.Reset();
            int pos = 0;
            long total = 0;
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
                throw new InvalidOperationException("wrong result");
            }
        });
    }

    [Fact]
    public void RawBlockDecodeIntoAReusedBufferAllocatesNothing()
    {
        byte[] block = Lz4.Lz4.Decompress(Lz4TestData.Vector("text300-1").Compressed).Length > 0 ? ExtractFirstBlock(Lz4TestData.Vector("text300-1").Compressed) : [];
        byte[] output = new byte[1000];
        AssertSteadyStateAllocatesNothing(() =>
        {
            if (!Lz4BlockDecoder.TryDecode(block, output, 0, out int written) || written != 300)
            {
                throw new InvalidOperationException("wrong result");
            }
        });
    }

    // Frame: magic, FLG, BD, [content size], HC, then the first block's size and bytes.
    private static byte[] ExtractFirstBlock(byte[] frame)
    {
        int at = 4 + 2 + ((frame[4] & 0x08) != 0 ? 8 : 0) + 1;
        int size = BitConverter.ToInt32(frame, at);
        return frame[(at + 4)..(at + 4 + size)];
    }
}
