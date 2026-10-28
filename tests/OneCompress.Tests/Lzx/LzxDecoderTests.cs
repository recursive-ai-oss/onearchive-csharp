using RecursiveAI.OneCompress.Lzx;

namespace RecursiveAI.OneCompress.Tests.Lzx;

public class LzxDecoderTests
{
    internal static byte[] Sample(int length, int seed, bool withCalls = false)
    {
        var rng = new Random(seed);
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "lorem ", "ipsum ", "dolor ", "sit ", "amet\n", "consectetur "];
        var data = new List<byte>(length);
        while (data.Count < length)
        {
            int kind = rng.Next(10);
            if (kind < 6)
            {
                data.AddRange(System.Text.Encoding.ASCII.GetBytes(words[rng.Next(words.Length)]));
            }
            else if (kind < 8)
            {
                int n = rng.Next(1, 40);
                byte b = (byte)rng.Next(256);
                for (int i = 0; i < n; i++)
                {
                    data.Add(b);
                }
            }
            else if (kind < 9 && withCalls)
            {
                data.Add(0xE8);
                data.AddRange(BitConverter.GetBytes(rng.Next(-5000, 60000)));
            }
            else
            {
                int n = rng.Next(1, 30);
                for (int i = 0; i < n; i++)
                {
                    data.Add((byte)rng.Next(256));
                }
            }
        }

        return [.. data.Take(length)];
    }

    internal static byte[] Concat(List<byte[]> frames) => [.. frames.SelectMany(f => f)];

    [Theory]
    [InlineData(15, 100_000, 50_000)]
    [InlineData(16, 200_000, 70_000)]
    [InlineData(18, 300_000, 30_000)]
    [InlineData(21, 500_000, 120_000)]
    [InlineData(16, 32768, 32768)]
    [InlineData(16, 32769, 100)]
    [InlineData(16, 1, 100)]
    public void RoundTripsEveryBlockKind(int windowBits, int length, int blockSize)
    {
        byte[] data = Sample(length, windowBits + length);
        var settings = new LzxTestEncoder.Settings
        {
            WindowBits = windowBits,
            BlockSize = blockSize,
            Kinds = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Uncompressed, LzxTestEncoder.Kind.Aligned],
        };
        List<byte[]> frames = LzxTestEncoder.Encode(data, settings);
        Assert.Equal(data, RecursiveAI.OneCompress.Lzx.Lzx.Decompress(Concat(frames), windowBits, data.Length));
    }

    [Fact]
    public void RoundTripsWithCallTranslation()
    {
        byte[] data = Sample(200_000, 7, withCalls: true);
        var settings = new LzxTestEncoder.Settings { WindowBits = 17, TranslationSize = 150_000 };
        Assert.Equal(data, RecursiveAI.OneCompress.Lzx.Lzx.Decompress(Concat(LzxTestEncoder.Encode(data, settings)), 17, data.Length));
    }

    [Fact]
    public void FrameByFrameMatchesOneCall()
    {
        byte[] data = Sample(100_000, 3);
        List<byte[]> frames = LzxTestEncoder.Encode(data, new LzxTestEncoder.Settings { WindowBits = 16 });
        var decoder = new LzxDecoder(16);
        var output = new byte[data.Length];
        for (int i = 0; i < frames.Count; i++)
        {
            int n = Math.Min(32768, data.Length - (i * 32768));
            decoder.DecodeFrame(frames[i], output.AsSpan(i * 32768, n), out int consumed);
            Assert.Equal(frames[i].Length, consumed);
        }

        Assert.Equal(data, output);
    }
}
