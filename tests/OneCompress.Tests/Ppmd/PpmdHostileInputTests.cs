using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.OneCompress.Tests;

public class PpmdHostileInputTests
{
    private static readonly byte[] s_data = PpmdRoundTripTests.Sample(6000, 5);

    private static void Decode(bool variantI, byte[] stream, long size)
    {
        try
        {
            if (variantI)
            {
                PpmdI.Decompress(stream, new PpmdIDecompressionOptions { Order = 6, MemorySize = 1 << 20, OutputSize = size, MaxOutputSize = 1 << 20 });
            }
            else
            {
                PpmdH.Decompress(stream, new PpmdHDecompressionOptions { Order = 6, MemorySize = 1 << 20, OutputSize = size, MaxOutputSize = 1 << 20 });
            }
        }
        catch (CompressionException)
        {
        }
    }

    private static byte[] Encode(bool variantI) => variantI
        ? PpmdI.Compress(s_data, new PpmdIOptions { Order = 6, MemorySize = 1 << 20 })
        : PpmdH.Compress(s_data, new PpmdHOptions { Order = 6, MemorySize = 1 << 20 });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncationAtEveryOffsetIsTypedOrShort(bool variantI)
    {
        byte[] c = Encode(variantI);
        for (int n = 0; n < c.Length; n++)
        {
            Decode(variantI, c.AsSpan(0, n).ToArray(), s_data.Length);
        }

        // A stream cut short cannot produce all of the declared output.
        Assert.ThrowsAny<CompressionException>(() => Decode2(variantI, c.AsSpan(0, c.Length / 2).ToArray()));
    }

    private static void Decode2(bool variantI, byte[] stream)
    {
        if (variantI)
        {
            PpmdI.Decompress(stream, new PpmdIDecompressionOptions { Order = 6, MemorySize = 1 << 20, OutputSize = s_data.Length });
        }
        else
        {
            PpmdH.Decompress(stream, new PpmdHDecompressionOptions { Order = 6, MemorySize = 1 << 20, OutputSize = s_data.Length });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BitFlipsAreTypedOrWrongButNeverCrash(bool variantI)
    {
        byte[] c = Encode(variantI);
        var rng = new Random(11);
        for (int i = 0; i < 1500; i++)
        {
            byte[] copy = (byte[])c.Clone();
            copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
            Decode(variantI, copy, s_data.Length);
        }

        byte[] garbage = new byte[3000];
        for (int i = 0; i < 50; i++)
        {
            rng.NextBytes(garbage);
            Decode(variantI, garbage, 100_000);
        }
    }

    [Fact]
    public void ModelMemoryAndOutputLimitsAreChecked()
    {
        byte[] c = Encode(false);
        Assert.Throws<CompressionLimitException>(() => PpmdH.Decompress(c, new PpmdHDecompressionOptions { Order = 6, MemorySize = 64 << 20, MaxMemorySize = 1 << 20 }));
        Assert.Throws<CompressionLimitException>(() => PpmdH.Decompress(c, new PpmdHDecompressionOptions { Order = 6, MemorySize = 1 << 20, OutputSize = 1000, MaxOutputSize = 10 }));
        byte[] ci = Encode(true);
        Assert.Throws<CompressionLimitException>(() => PpmdI.Decompress(ci, new PpmdIDecompressionOptions { Order = 6, MemorySize = 64 << 20, MaxMemorySize = 1 << 20 }));
        Assert.Throws<CompressionFormatException>(() => PpmdH.Decompress(c, new PpmdHDecompressionOptions { Order = 1, MemorySize = 1 << 20 }));
    }
}
