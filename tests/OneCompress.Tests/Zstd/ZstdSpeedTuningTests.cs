using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The helpers and level settings that trade a little size for speed: they must keep every stream decodable.</summary>
public class ZstdSpeedTuningTests
{
    private static readonly ZstdDecompressionOptions Big = new() { MaxWindowSize = 1L << 30, MaxOutputSize = 1L << 31 };

    [Fact]
    public void CommonPrefix16CountsTheEqualLeadingBytes()
    {
        var random = new Random(7);
        byte[] data = new byte[4096];
        random.NextBytes(data);
        for (int equal = 0; equal <= 16; equal++)
        {
            for (int trial = 0; trial < 50; trial++)
            {
                int a = random.Next(0, 2000);
                int b = a + 16 + random.Next(0, 2000);
                data.AsSpan(a, 16).CopyTo(data.AsSpan(b, 16));
                if (equal < 16)
                {
                    data[b + equal] = (byte)(data[a + equal] ^ (1 << random.Next(8)));
                }

                Assert.Equal(equal, Raw.CommonPrefix16(data, a, b));
            }
        }
    }

    // Counts the blocks of a frame written with a known content size.
    private static int CountBlocks(byte[] frame)
    {
        int p = 4;
        int descriptor = frame[p++];
        bool single = (descriptor & 32) != 0;
        if (!single)
        {
            p++;
        }

        p += new[] { 0, 1, 2, 4 }[descriptor & 3];
        int field = descriptor >> 6;
        p += field == 0 ? (single ? 1 : 0) : field == 1 ? 2 : field == 2 ? 4 : 8;
        int blocks = 0;
        while (true)
        {
            int header = frame[p] | (frame[p + 1] << 8) | (frame[p + 2] << 16);
            blocks++;
            int type = (header >> 1) & 3;
            p += 3 + (type == 1 ? 1 : header >> 3);
            if ((header & 1) != 0)
            {
                return blocks;
            }
        }
    }

    // Every 64 KiB the byte alphabet changes, so half a block's literals are coded better apart from the other half's.
    private static byte[] ShiftingAlphabets(int total, int seed)
    {
        var random = new Random(seed);
        byte[] data = new byte[total];
        for (int i = 0; i < total; i++)
        {
            int segment = i / 65536;
            data[i] = (byte)(((segment & 1) == 0 ? 'a' : 'p') + random.Next(12));
        }

        return data;
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public void LongDistanceMatchingCutsBlocksWhereTheStatisticsChange(int level)
    {
        byte[] data = ShiftingAlphabets(1_500_000, 11);
        byte[] plain = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level, WindowLog = 24 });
        byte[] ldm = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level, WindowLog = 24, LongDistanceMatching = true });
        Assert.Equal(data, Zstd.Zstd.Decompress(plain, Big));
        Assert.Equal(data, Zstd.Zstd.Decompress(ldm, Big));
        Assert.True(CountBlocks(ldm) > CountBlocks(plain), $"{CountBlocks(ldm)} blocks against {CountBlocks(plain)}");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(9)]
    public void LazyLevelsRoundTripTextAndBinary(int level)
    {
        byte[] data = [.. ZstdEncoderTests.Text(400_000, 3), .. ZstdEncoderTests.RandomBytes(100_000, 4), .. ZstdEncoderTests.Text(400_000, 5)];
        byte[] packed = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
        Assert.Equal(data, Zstd.Zstd.Decompress(packed, Big));
    }
}
