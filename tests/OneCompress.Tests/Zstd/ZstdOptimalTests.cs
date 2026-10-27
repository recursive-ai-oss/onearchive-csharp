using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>The optimal-parsing levels (and the block splitting that goes with them): round trips and bounds.</summary>
public class ZstdOptimalTests
{
    public static TheoryData<int> OptimalLevels() => new() { 11, 13, 15, 16, 17, 18, 19, 20, 21, 22 };

    private static byte[] Mixed(int length, int seed)
    {
        // Text, then noise, then a repeating record structure: the statistics change from part to part, which is what block splitting is for.
        byte[] data = new byte[length];
        int third = length / 3;
        ZstdEncoderTests.Text(third, seed).CopyTo(data, 0);
        ZstdEncoderTests.RandomBytes(third, seed + 1).CopyTo(data, third);
        var random = new Random(seed + 2);
        for (int i = 2 * third; i < length; i++)
        {
            data[i] = (i % 32) < 28 ? (byte)(i / 32 % 7) : (byte)random.Next(256);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OptimalLevels))]
    public void RoundTripsAtEverySizeClass(int level)
    {
        foreach (int length in new[] { 0, 1, 7, 8, 100, 700, 5000, 16384, 20000, 100000, 140000, 300000 })
        {
            foreach (byte[] data in new[] { ZstdEncoderTests.Text(length, 5), Mixed(length, 6), new byte[length] })
            {
                byte[] frame = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
                Assert.True(Zstd.Zstd.Decompress(frame).AsSpan().SequenceEqual(data), $"level {level} length {length}");
            }
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(22)]
    public void IncompressibleDataStaysWithinTheBound(int level)
    {
        foreach (int length in new[] { 1000, 131072, 131073, 400000 })
        {
            byte[] data = ZstdEncoderTests.RandomBytes(length, 9);
            var options = new ZstdCompressionOptions { Level = level };
            byte[] frame = Zstd.Zstd.Compress(data, options);
            Assert.True(frame.Length <= ZstdEncoder.GetMaxCompressedLength(length));
            Assert.True(Zstd.Zstd.Decompress(frame).AsSpan().SequenceEqual(data));
            Assert.True(Zstd.Zstd.TryCompress(data, new byte[ZstdEncoder.GetMaxCompressedLength(length)], out int written, options));
            Assert.Equal(frame.Length, written);
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(19)]
    public void StreamingMatchesOneShot(int level)
    {
        byte[] data = Mixed(700_000, 11);
        var options = new ZstdCompressionOptions { Level = level, ContentSize = data.Length };
        byte[] expected = Zstd.Zstd.Compress(data, options);
        Assert.True(Zstd.Zstd.Decompress(expected).AsSpan().SequenceEqual(data));
        foreach (int piece in new[] { 1, 4999, 131072, 300000 })
        {
            var encoder = new ZstdEncoder(options);
            var output = new MemoryStream();
            byte[] buffer = new byte[20011];
            int position = 0;
            while (position < data.Length)
            {
                int n = Math.Min(Math.Max(piece, 1), data.Length - position);
                bool last = position + n == data.Length;
                int taken = 0;
                while (true)
                {
                    var status = encoder.Compress(data.AsSpan(position + taken, n - taken), buffer, out int consumed, out int written, last);
                    taken += consumed;
                    output.Write(buffer, 0, written);
                    if (status == System.Buffers.OperationStatus.Done)
                    {
                        break;
                    }
                }

                position += n;
            }

            Assert.True(expected.AsSpan().SequenceEqual(output.ToArray()), $"level {level} piece {piece}");
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(22)]
    public void DictionaryHelpsAndRoundTrips(int level)
    {
        byte[] text = ZstdEncoderTests.Text(60_000, 3);
        var dictionary = ZstdCompressionDictionary.FromContent(text.AsSpan(0, 30_000), 7);
        byte[] data = text.AsSpan(20_000, 25_000).ToArray();
        byte[] plain = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level });
        byte[] withDictionary = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
        Assert.True(withDictionary.Length < plain.Length);
        var decompression = new ZstdDecompressionOptions { Dictionary = ZstdDictionary.FromContent(text.AsSpan(0, 30_000), 7) };
        Assert.True(Zstd.Zstd.Decompress(withDictionary, decompression).AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void HigherLevelsDoNotLoseToLowerOnes()
    {
        byte[] data = Mixed(900_000, 21);
        int previous = int.MaxValue;
        foreach (int level in new[] { 12, 16, 19 })
        {
            int size = Zstd.Zstd.Compress(data, new ZstdCompressionOptions { Level = level }).Length;
            Assert.True(size <= previous * 1.005, $"level {level}: {size} after {previous}");
            previous = size;
        }
    }
}
