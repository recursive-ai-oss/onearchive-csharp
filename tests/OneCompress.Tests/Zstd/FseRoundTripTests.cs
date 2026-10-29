using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

public class FseRoundTripTests
{
    // Codes a symbol sequence with the encoder table (last symbol first, RFC 8878 4.1.1) and decodes it with the decoding
    // table built from the same counts; the result must be the input.
    private static string RoundTrip(short[] counts, int accuracyLog, byte[] symbols)
    {
        var encoder = new FseEncodingTable();
        encoder.Build(counts, accuracyLog);
        var decoder = new FseDecodingTable();
        decoder.Build(counts, accuracyLog);
        int n = symbols.Length;
        var bits = new uint[n];
        var bitCounts = new int[n];
        int state = encoder.InitialState(symbols[n - 1]);
        for (int i = n - 2; i >= 0; i--)
        {
            state = encoder.Encode(state, symbols[i], out bits[i], out bitCounts[i]);
        }

        // The decoder starts from the encoder's final state and reads the bits of each step as it moves on.
        int decodeState = state - (1 << accuracyLog);
        var entries = decoder.Entries;
        for (int i = 0; i < n; i++)
        {
            FseEntry e = entries[decodeState];
            if (e.Symbol != symbols[i])
            {
                return $"symbol {i}: decoded {e.Symbol}, expected {symbols[i]}";
            }

            if (i < n - 1)
            {
                if (e.NumBits != bitCounts[i])
                {
                    return $"symbol {i}: decoder reads {e.NumBits} bits, encoder wrote {bitCounts[i]}";
                }

                decodeState = e.Baseline + (int)bits[i];
            }
        }

        return string.Empty;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RandomTablesWithManyRareSymbolsRoundTrip(int seed)
    {
        var random = new Random(seed);
        for (int iteration = 0; iteration < 3000; iteration++)
        {
            int accuracyLog = random.Next(5, 10);
            int maxSymbol = random.Next(2, 53);
            var histogram = new uint[maxSymbol + 1];
            uint total = 0;
            for (int s = 0; s <= maxSymbol; s++)
            {
                // A heavy head and a long tail of rare symbols, like the match-length codes of real data.
                histogram[s] = (uint)(random.Next(3) == 0 ? 0 : random.Next(4) == 0 ? 1 + random.Next(2000) : 1 + random.Next(4));
                total += histogram[s];
            }

            histogram[0] += 1;
            histogram[maxSymbol] += 1;
            total += 2;
            int distinct = histogram.Count(c => c != 0);
            if (distinct > (1 << accuracyLog))
            {
                continue;
            }

            var counts = new short[maxSymbol + 1];
            FseEncodingTable.Normalize(counts, accuracyLog, histogram, total, maxSymbol);
            var present = Enumerable.Range(0, maxSymbol + 1).Where(s => counts[s] != 0).Select(s => (byte)s).ToArray();
            var symbols = new byte[400];
            for (int i = 0; i < symbols.Length; i++)
            {
                symbols[i] = present[random.Next(present.Length)];
            }

            string problem = RoundTrip(counts, accuracyLog, symbols);
            Assert.True(problem.Length == 0, $"iteration {iteration}, accuracy log {accuracyLog}, counts {string.Join(',', counts)}: {problem}");
        }
    }
}
