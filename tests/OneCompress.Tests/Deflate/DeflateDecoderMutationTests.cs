using System.IO.Compression;
using RecursiveAI.OneCompress.Deflate;
using DeflateCodec = RecursiveAI.OneCompress.Deflate.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Damaged streams through the decoder's fast loop: every outcome must be a typed error or output that the BCL decoder agrees with.
/// </summary>
public class DeflateDecoderMutationTests
{
    private static byte[] Runs(int seed, int size)
    {
        // Short distances (2 to 7) and runs, which take the loop's pattern-copy paths.
        var r = new Random(seed);
        var data = new byte[size];
        int at = 0;
        while (at < size)
        {
            int period = 1 + r.Next(9);
            var unit = new byte[period];
            r.NextBytes(unit);
            int repeats = 1 + r.Next(40);
            for (int i = 0; i < repeats * period && at < size; i++)
            {
                data[at++] = unit[i % period];
            }
        }

        return data;
    }

    private static byte[]? BclDecode(byte[] compressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var stream = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    [Fact]
    public void DamagedStreamsGiveTypedErrorsOrAgreeWithTheBcl()
    {
        byte[][] inputs = [DeflateTestData.Text(3, 60_000), Runs(4, 60_000), DeflateTestData.RandomBytes(5, 20_000)];
        var random = new Random(77);
        foreach (byte[] data in inputs)
        {
            foreach (int level in new[] { 1, 4, 9 })
            {
                byte[] good = DeflateCodec.Compress(data, new DeflateOptions { Level = level });
                for (int round = 0; round < 300; round++)
                {
                    byte[] bad = (byte[])good.Clone();
                    int flips = 1 + random.Next(3);
                    for (int f = 0; f < flips; f++)
                    {
                        bad[random.Next(bad.Length)] ^= (byte)(1 << random.Next(8));
                    }

                    if (round % 7 == 0)
                    {
                        bad = bad[..random.Next(bad.Length)];
                    }

                    byte[]? ours = null;
                    try
                    {
                        ours = DeflateCodec.Decompress(bad);
                    }
                    catch (CompressionException)
                    {
                    }

                    byte[]? theirs = BclDecode(bad);
                    if (ours is not null && theirs is not null)
                    {
                        Assert.True(ours.AsSpan().SequenceEqual(theirs), $"level {level} round {round}: outputs differ");
                    }
                }
            }
        }
    }
}
