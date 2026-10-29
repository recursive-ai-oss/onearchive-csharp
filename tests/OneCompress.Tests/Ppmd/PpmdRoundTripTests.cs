using System.Text;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.OneCompress.Tests;

public class PpmdRoundTripTests
{
    internal static byte[] Sample(int length, int seed)
    {
        // Text-like data with repetition, plus a stretch of random bytes, so that every model path (binary contexts, escapes, rescaling) runs.
        var rng = new Random(seed);
        string[] words = ["the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "PPMd", "context", "model", "\n", "0123456789"];
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(words[rng.Next(words.Length)]).Append(' ');
        }

        byte[] data = Encoding.ASCII.GetBytes(sb.ToString())[..length];
        for (int i = length / 3; i < length / 3 + Math.Min(length / 5, 4000); i++)
        {
            data[i] = (byte)rng.Next(256);
        }

        return data;
    }

    public static IEnumerable<object[]> Params() =>
    [
        [2, 1 << 11], [3, 1 << 16], [6, 1 << 20], [16, 1 << 22], [32, 1 << 20], [64, 1 << 24],
    ];

    [Theory]
    [MemberData(nameof(Params))]
    public void HRoundTrip(int order, int mem)
    {
        foreach (int len in new[] { 0, 1, 2, 100, 5000, 300000 })
        {
            byte[] data = Sample(len, len);
            byte[] c = PpmdH.Compress(data, new PpmdHOptions { Order = order, MemorySize = mem });
            byte[] d = PpmdH.Decompress(c, new PpmdHDecompressionOptions { Order = order, MemorySize = mem, OutputSize = len });
            Assert.Equal(data, d);
        }
    }

    [Theory]
    [MemberData(nameof(Params))]
    public void IRoundTrip(int order, int mem)
    {
        if (order > 16)
        {
            return;
        }

        mem = Math.Max(mem, PpmdIModel.MinMemorySize);

        foreach (var rm in new[] { PpmdRestoreMethod.Restart, PpmdRestoreMethod.CutOff })
        {
            foreach (int len in new[] { 0, 1, 2, 100, 5000, 300000 })
            {
                byte[] data = Sample(len, len);
                byte[] c = PpmdI.Compress(data, new PpmdIOptions { Order = order, MemorySize = mem, RestoreMethod = rm });
                byte[] d = PpmdI.Decompress(c, new PpmdIDecompressionOptions { Order = order, MemorySize = mem, RestoreMethod = rm, OutputSize = len });
                Assert.Equal(data, d);
            }
        }
    }
}
