using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

// Run alone: another test thread allocating at the same moment would only blur the count.
[Collection("Lz4 allocation")]
public class Lz4BlockEncoderAllocationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(-3)]
    [InlineData(9)]
    public void ReusedBlockEncoderAllocatesNothing(int level)
    {
        var encoder = new Lz4BlockEncoder(new Lz4CompressionOptions { Level = level, Dictionary = new Lz4Dictionary(new byte[3000]) });
        byte[] data = new byte[256];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 13 * (i % 5));
        }

        byte[] buffer = new byte[Lz4BlockEncoder.GetMaxPrefixedLength(data.Length)];
        byte[] back = new byte[data.Length];
        for (int i = 0; i < 20000; i++)
        {
            encoder.TryCompressPrefixed(data, buffer, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            encoder.TryCompressPrefixed(data, buffer, out int written);
            Lz4PrefixedBlock.TryDecompress(buffer.AsSpan(0, written), back, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
