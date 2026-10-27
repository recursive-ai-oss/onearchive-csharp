using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Round trips shaped to reach each specialised decoder loop: blocks with few sequences, matches whose offset is 1-15
/// (overlapping copies), matches into a dictionary, sequences wider than 56 bits, short inputs where the fast loops stop
/// near the start of the stream, and dictionary Huffman tables used by short literal runs.
/// </summary>
public class ZstdDecoderPathTests
{
    private static byte[] Periodic(int length, int period, int seed)
    {
        byte[] unit = ZstdEncoderTests.RandomBytes(period, seed);
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = unit[i % period];
        }

        return data;
    }

    private static void RoundTrip(byte[] data, int level, ZstdCompressionDictionary? cd = null, ZstdDictionary? dd = null)
    {
        byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level, Dictionary = cd });
        byte[] back = Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { Dictionary = dd });
        Assert.True(data.AsSpan().SequenceEqual(back), $"level {level}, length {data.Length}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(15)]
    public void OverlappingOffsetsRoundTrip(int level)
    {
        // Offsets 1..40 cover the pattern-fill, the 8..15 inline copy and the plain wide copy.
        for (int period = 1; period <= 40; period++)
        {
            foreach (int length in new[] { 20, 150, 3000, 70000 })
            {
                byte[] data = Periodic(length, period, period);
                if (length > 20)
                {
                    // A few literals in the middle make several sequences with different offsets.
                    data[length / 2] ^= 0x55;
                }

                RoundTrip(data, level);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(15)]
    public void SmallAndNearStartBlocksRoundTrip(int level)
    {
        for (int length = 0; length <= 400; length++)
        {
            byte[] text = ZstdEncoderTests.Text(length, length);
            RoundTrip(text, level);
            if (length >= 8)
            {
                // Match at the very start of the block and a literal tail of every small length.
                byte[] rep = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    rep[i] = (byte)(i < length / 2 ? i * 7 : (i - length / 2) * 7);
                }

                RoundTrip(rep, level);
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(15)]
    public void WideSequencesRoundTrip(int level)
    {
        // A match of a megabyte-scale offset with a long literal run and a long match: the sequence needs more than 56 bits
        // of stream when the offset code, match length code and literal length code are all large.
        byte[] a = ZstdEncoderTests.RandomBytes(3_000_000, 77);
        byte[] data = new byte[a.Length * 2 + 70_000];
        a.CopyTo(data, 0);
        ZstdEncoderTests.RandomBytes(70_000, 78).CopyTo(data, a.Length);
        a.CopyTo(data, a.Length + 70_000);
        byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level, WindowLog = 23 });
        byte[] back = Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { MaxWindowSize = 1 << 24 });
        Assert.True(data.AsSpan().SequenceEqual(back));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(15)]
    public void DictionaryMatchesAndPairTableRoundTrip(int level)
    {
        var samples = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < 600; i++)
        {
            samples.Add(JsonLike(i, 150 + (i % 7) * 40));
        }

        byte[] dictBytes = ZstdDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 4096 }, TestContext.Current.CancellationToken);
        ZstdCompressionDictionary cd = ZstdCompressionDictionary.Parse(dictBytes);
        ZstdDictionary dd = ZstdDictionary.Parse(dictBytes);
        for (int i = 600; i < 700; i++)
        {
            RoundTrip(JsonLike(i, 100 + (i % 11) * 300), level, cd, dd);
        }

        // Larger payloads: several literal sections that reuse the dictionary's Huffman table, with offsets reaching into it.
        for (int i = 0; i < 6; i++)
        {
            byte[] big = new byte[20000 + i * 5000];
            for (int p = 0; p < big.Length;)
            {
                byte[] piece = JsonLike(700 + p, 120);
                int n = Math.Min(piece.Length, big.Length - p);
                piece.AsSpan(0, n).CopyTo(big.AsSpan(p));
                p += n;
            }

            RoundTrip(big, level, cd, dd);
        }
    }

    private static byte[] JsonLike(int i, int length)
    {
        string[] names = ["lantern", "brick", "card", "player", "chest", "door"];
        var sb = new System.Text.StringBuilder();
        uint x = (uint)(i * 2654435761u + 12345);
        while (sb.Length < length)
        {
            x = (x * 1664525u) + 1013904223u;
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"{{\"id\":{x % 5000},\"kind\":\"{names[(x >> 8) % (uint)names.Length]}\",\"pos\":[{(x >> 12) % 256},{(x >> 20) % 64}],\"tick\":{1000 + (i * 3)}}},");
        }

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }
}
