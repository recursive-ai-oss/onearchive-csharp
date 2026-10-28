using System.Buffers;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Memory the encoder asks for. A streamed frame of unknown length at a high level used to reserve its whole window and
/// binary-tree tables up front (hundreds of megabytes, and the same again for a small dictionary's search index), which a
/// fuzz run with several instances could not afford.
/// </summary>
public class ZstdEncoderMemoryTests
{
    private static byte[] Text(int size, int seed)
    {
        string[] words = ["alpha ", "beta ", "gamma ", "delta ", "snapshot ", "entity ", "position ", "0123 ", "\n"];
        var random = new Random(seed);
        byte[] data = new byte[size];
        int p = 0;
        while (p < size)
        {
            string w = words[random.Next(words.Length)];
            for (int i = 0; i < w.Length && p < size; i++)
            {
                data[p++] = (byte)w[i];
            }

            if (random.Next(5) == 0 && p < size)
            {
                data[p++] = (byte)random.Next(256);
            }
        }

        return data;
    }

    private static byte[] Stream(ReadOnlySpan<byte> data, ZstdCompressionOptions options, int piece)
    {
        var encoder = new ZstdEncoder(options);
        var result = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int position = 0;
        while (true)
        {
            int n = Math.Min(piece, data.Length - position);
            bool last = position + n == data.Length;
            int taken = 0;
            while (true)
            {
                OperationStatus status = encoder.Compress(data.Slice(position + taken, n - taken), buffer, out int consumed, out int written, last);
                taken += consumed;
                result.Write(buffer, 0, written);
                if (status == OperationStatus.Done)
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

    private static long Allocated(Func<byte[]> work, out byte[] result)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        result = work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void UnknownSizeStreamAtLevel22WithADictionaryDoesNotReserveTheWholeWindow()
    {
        // The shape the round-trip fuzz target found: a streamed frame, no content size, level 22, a small dictionary.
        byte[] data = Text(200_000, 1);
        var dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(ZstdTestData.Path("dict4.zdict")));
        var options = new ZstdCompressionOptions { Level = 22, Dictionary = dictionary };

        long allocated = Allocated(() => Stream(data, options, 4096), out byte[] frame);

        // 640 MiB before the tables grew with the data; what remains is the hash table (128 MiB at this level) and the data-sized parts.
        Assert.True(allocated < 200L << 20, $"allocated {allocated >> 20} MiB");
        var decompression = new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(File.ReadAllBytes(ZstdTestData.Path("dict4.zdict"))), MaxWindowSize = 1L << 27 };
        Assert.Equal(data, Zstd.Zstd.Decompress(frame, decompression));
    }

    [Fact]
    public void GrowingTablesAndWindowDoNotChangeTheBytes()
    {
        // A hint far above the data picks the same parameters as no size at all but allocates every table whole, so the two must
        // agree byte for byte across the points where the window buffer (1 MiB) and the tree links (65,536 positions) grow.
        byte[] data = Text((5 << 19) + 1234, 2);
        foreach (int level in new[] { 17, 12, 5 })
        {
            byte[] grown = Stream(data, new ZstdCompressionOptions { Level = level }, 33_333);
            byte[] whole = Stream(data, new ZstdCompressionOptions { Level = level, SizeHint = 1L << 40 }, 33_333);
            Assert.Equal(whole, grown);
            Assert.Equal(data, Zstd.Zstd.Decompress(grown, new ZstdDecompressionOptions { MaxWindowSize = 1L << 27 }));
        }
    }

    [Fact]
    public void SizeHintSizesTheTablesWithoutStatingASize()
    {
        byte[] data = Text(30_000, 3);
        var hinted = new ZstdCompressionOptions { Level = 22, SizeHint = data.Length };

        long allocated = Allocated(() => Stream(data, hinted, 1000), out byte[] frame);

        Assert.True(allocated < 20L << 20, $"allocated {allocated >> 20} MiB");
        Assert.True(ZstdDecoder.TryReadFrameHeader(frame, out ZstdFrameHeader header));
        Assert.False(header.HasContentSize);
        Assert.Equal(data, Zstd.Zstd.Decompress(frame));

        // The estimate is not a limit: more data than estimated still round-trips.
        byte[] more = Text(700_000, 4);
        byte[] frame2 = Stream(more, new ZstdCompressionOptions { Level = 9, SizeHint = 1000 }, 50_000);
        Assert.Equal(more, Zstd.Zstd.Decompress(frame2));

        // A pledged size wins over the estimate.
        byte[] pledged = Stream(data, new ZstdCompressionOptions { Level = 3, ContentSize = data.Length, SizeHint = 5 }, 1000);
        Assert.Equal(Stream(data, new ZstdCompressionOptions { Level = 3, ContentSize = data.Length }, 1000), pledged);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdEncoder(new ZstdCompressionOptions { SizeHint = -1 }));
    }

    [Theory]
    [InlineData(22, 16 << 20, 4096)]
    [InlineData(19, 1 << 30, 4096)]
    [InlineData(3, 16 << 20, 4096)]
    [InlineData(1, 16 << 20, 100)]
    public void ADictionarysSearchIndexIsSizedByItsContent(int level, int contentSize, int dictionarySize)
    {
        ZstdCompressionParameters p = ZstdCompressionParameters.Resolve(level, contentSize, dictionarySize);

        int contentLog = Math.Max(System.Numerics.BitOperations.Log2((uint)dictionarySize - 1) + 1, 10);
        Assert.InRange(p.DictionaryHashLog, 6, contentLog + 4);
        Assert.InRange(p.DictionaryChainLog, 6, contentLog + 4);
    }
}
