using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class FseCostTableTests
{
    // The price of a histogram under a built table (which keeps a per-symbol price table) must equal the price under the same
    // distribution priced symbol by symbol, including when the histogram has symbols the table cannot code.
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    public void BuiltTablePricesLikeTheDistribution(int accuracyLog)
    {
        var random = new Random(accuracyLog);
        for (int round = 0; round < 300; round++)
        {
            int tableMax = 3 + random.Next(Math.Min(40, (1 << accuracyLog) / 2 - 4));
            var counts = new uint[tableMax + 1];
            uint total = 0;
            for (int s = 0; s <= tableMax; s++)
            {
                counts[s] = (uint)(random.Next(3) == 0 ? 0 : 1 + random.Next(300));
                total += counts[s];
            }

            counts[0] += 2;
            counts[tableMax] += 3;
            total += 5;
            var normalized = new short[tableMax + 1];
            FseEncodingTable.Normalize(normalized, accuracyLog, counts, total, tableMax);
            var table = new FseEncodingTable();
            table.Build(normalized, accuracyLog);

            for (int probe = 0; probe < 6; probe++)
            {
                // Histograms inside the table's range, some beyond it, some touching symbols with no state.
                int max = probe < 4 ? random.Next(tableMax + 1) : tableMax + 1 + random.Next(10);
                var histogram = new uint[max + 1];
                for (int s = 0; s <= max; s++)
                {
                    histogram[s] = (uint)(random.Next(3) == 0 ? 0 : random.Next(500));
                }

                Assert.Equal(
                    FseEncodingTable.Cost(normalized, accuracyLog, histogram, max),
                    table.Cost(histogram, max));
            }

            // Rebuilding the same table object with another distribution must not keep the old prices.
            var other = new short[tableMax + 1];
            var shifted = (uint[])counts.Clone();
            shifted[random.Next(tableMax + 1)] += 40;
            FseEncodingTable.Normalize(other, accuracyLog, shifted, total + 40, tableMax);
            table.Build(other, accuracyLog);
            Assert.Equal(
                FseEncodingTable.Cost(other, accuracyLog, counts, tableMax),
                table.Cost(counts, tableMax));
        }
    }
}
