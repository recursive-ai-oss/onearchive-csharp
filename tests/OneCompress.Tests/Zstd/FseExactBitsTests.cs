using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class FseExactBitsTests
{
    // Builds a table from a random histogram over symbols 0..maxSymbol and returns it with the histogram.
    private static FseEncodingTable Table(Random random, int maxSymbol, int accuracyLog, out uint[] histogram)
    {
        histogram = new uint[maxSymbol + 1];
        uint total = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            histogram[s] = (uint)(random.Next(4) == 0 ? 0 : 1 + random.Next(1 + random.Next(200)));
            total += histogram[s];
        }

        histogram[0] += 1;
        histogram[maxSymbol] += 1;
        total += 2;
        var normalized = new short[maxSymbol + 1];
        FseEncodingTable.Normalize(normalized, accuracyLog, histogram, total, maxSymbol);
        var table = new FseEncodingTable();
        table.Build(normalized, accuracyLog);
        return table;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SeveralTablesAtOnceCountTheSameBitsAsOneAtATime(int seed)
    {
        var random = new Random(seed);
        foreach (int length in new[] { 1, 2, 3, 7, 64, 500, 1500 })
        {
            var tables = new FseEncodingTable[6];
            var codes = new byte[3][];
            var histograms = new uint[6][];
            for (int k = 0; k < 3; k++)
            {
                int maxSymbol = 5 + random.Next(40);
                tables[2 * k] = Table(random, maxSymbol, 6 + random.Next(4), out histograms[2 * k]);
                tables[(2 * k) + 1] = Table(random, maxSymbol, 6 + random.Next(4), out histograms[(2 * k) + 1]);
                codes[k] = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    // Symbols both tables of the pair can code.
                    int s;
                    do
                    {
                        s = random.Next(maxSymbol + 1);
                    }
                    while (histograms[2 * k][s] == 0 || histograms[(2 * k) + 1][s] == 0);
                    codes[k][i] = (byte)s;
                }
            }

            var six = new int[6];
            FseEncodingTable.ExactBits(codes[0], codes[1], codes[2], tables, six);
            for (int j = 0; j < 6; j++)
            {
                Assert.Equal(tables[j].ExactBits(codes[j / 2]), six[j]);
            }

            FseEncodingTable.ExactBits(codes[0], codes[1], codes[2], tables[1], tables[3], tables[5], out int a, out int b, out int c);
            Assert.Equal([six[1], six[3], six[5]], [a, b, c]);
        }
    }
}
