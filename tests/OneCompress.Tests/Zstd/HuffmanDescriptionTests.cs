using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Huffman tree descriptions written by the encoder read back to the weights they were made from.</summary>
public class HuffmanDescriptionTests
{
    [Fact]
    public void RandomTreesDescribeThemselvesExactly()
    {
        var random = new Random(12345);
        var table = new HuffmanEncodingTable();
        var reader = new HuffmanDecodingTable();
        byte[] description = new byte[256];
        int compressed = 0;
        for (int round = 0; round < 20000; round++)
        {
            var histogram = new uint[256];
            int maxSymbol = round % 7 == 0 ? 255 : random.Next(1, 256);
            int kind = random.Next(4);
            int present = kind == 0 ? 2 : random.Next(2, Math.Min(maxSymbol + 1, 256) + 1);
            for (int i = 0; i < present; i++)
            {
                int symbol = i == 0 ? maxSymbol : random.Next(maxSymbol + 1);
                uint count = kind switch
                {
                    0 => (uint)(1 + random.Next(1000)),
                    1 => (uint)(1 + random.Next(4)),
                    2 => (uint)(1 << random.Next(14)),
                    _ => (uint)(1 + (random.NextDouble() * random.NextDouble() * 50000)),
                };
                histogram[symbol] += count;
            }

            if (!table.Build(histogram, maxSymbol))
            {
                continue;
            }

            int size = table.WriteDescription(description);
            Assert.True(size > 0, $"round {round}");
            if (description[0] < 128)
            {
                compressed++;
            }

            reader.ReadDescription(description.AsSpan(0, size), 0, buildTable: false);
            ReadOnlySpan<byte> weights = reader.Weights;
            int n = table.SymbolCount - 1;
            Assert.Equal(n, weights.Length);
            ReadOnlySpan<byte> lengths = table.CodeLengths;
            for (int s = 0; s < n; s++)
            {
                int expected = lengths[s] == 0 ? 0 : table.MaxCodeLength + 1 - lengths[s];
                Assert.True(expected == weights[s], $"round {round}, symbol {s}");
            }
        }

        Assert.True(compressed > 1000, $"{compressed} compressed descriptions");
    }
}
