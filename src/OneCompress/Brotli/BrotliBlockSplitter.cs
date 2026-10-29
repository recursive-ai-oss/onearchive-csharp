namespace RecursiveAI.OneCompress.Brotli;

/// <summary>A division of a symbol sequence into blocks, each coded with one of a few block types (RFC 7932 section 6).</summary>
internal sealed class BrotliBlockSplit
{
    /// <summary>The number of block types, 1 to 256.</summary>
    internal int TypeCount = 1;

    /// <summary>The type of each block; types are numbered in order of first use.</summary>
    internal int[] Types = [0];

    /// <summary>The number of symbols in each block; they add up to the length of the sequence.</summary>
    internal int[] Lengths = [0];

    internal int BlockCount => Types.Length;

    internal static BrotliBlockSplit Single(int length) => new() { Lengths = [length] };
}

/// <summary>
/// Splits a sequence of symbols into blocks whose statistics differ, the way the block types of RFC 7932 section 6 want them.
/// </summary>
/// <remarks>
/// <para>The sequence is cut into equal segments (at least <c>minSegment</c> symbols, at most <c>maxSegments</c> of them). Segment
/// histograms are clustered (see <see cref="BrotliClusterer"/>) into candidate block types, and then refined a few times: each type's
/// histogram is rebuilt from its segments, and the best type for every segment is found by a Viterbi search over the sequence of
/// segments, where each cost is the bits the segment takes under the type's code and a change of type costs a fixed number of bits
/// (about what a block switch takes). The split is kept only if its estimated cost, headers and switches included, is below that of
/// coding the whole sequence with one code. Segment granularity makes the result a pure function of the symbols.</para>
/// </remarks>
internal static class BrotliBlockSplitter
{
    // About what a block switch costs: a block type code, a block count code and its extra bits.
    private const long SwitchCost = 14L << 16;

    internal static BrotliBlockSplit Split(ReadOnlySpan<ushort> symbols, int alphabetSize, int minSegment, int maxSegments, int pairwiseLimit)
    {
        int n = symbols.Length;
        if (n < 2 * minSegment)
        {
            return BrotliBlockSplit.Single(n);
        }

        int segmentLength = Math.Max(minSegment, (n + maxSegments - 1) / maxSegments);
        int m = (n + segmentLength - 1) / segmentLength;
        if (m < 2)
        {
            return BrotliBlockSplit.Single(n);
        }

        var histograms = new int[m][];
        for (int i = 0; i < m; i++)
        {
            histograms[i] = new int[alphabetSize];
            int from = i * segmentLength;
            int to = Math.Min(n, from + segmentLength);
            int[] h = histograms[i];
            for (int k = from; k < to; k++)
            {
                h[symbols[k]]++;
            }
        }

        int[] assignment = new int[m];
        int types = BrotliClusterer.Cluster(histograms, alphabetSize, 256, 12L << 16, assignment, out int[][] typeHistograms, pairwiseLimit);
        if (types == 1)
        {
            return BrotliBlockSplit.Single(n);
        }

        // Sparse segments, for fast costing.
        var segmentSymbols = new int[m][];
        for (int i = 0; i < m; i++)
        {
            int count = 0;
            for (int s = 0; s < alphabetSize; s++)
            {
                if (histograms[i][s] > 0)
                {
                    count++;
                }
            }

            segmentSymbols[i] = new int[count];
            count = 0;
            for (int s = 0; s < alphabetSize; s++)
            {
                if (histograms[i][s] > 0)
                {
                    segmentSymbols[i][count++] = s;
                }
            }
        }

        var bits = new int[types][];
        for (int t = 0; t < types; t++)
        {
            bits[t] = new int[alphabetSize];
        }

        var back = new int[m * types];
        var cost = new long[types];
        var next = new long[types];
        for (int iteration = 0; iteration < 4; iteration++)
        {
            for (int t = 0; t < types; t++)
            {
                int total = 0;
                foreach (int c in typeHistograms[t])
                {
                    total += c;
                }

                int logTotal = total > 0 ? BrotliEncodeTables.Log2((uint)total) : 0;
                int[] row = bits[t];
                int[] counts = typeHistograms[t];
                for (int s = 0; s < alphabetSize; s++)
                {
                    row[s] = counts[s] > 0 ? logTotal - BrotliEncodeTables.Log2((uint)counts[s]) : logTotal + (10 << 16);
                }
            }

            for (int i = 0; i < m; i++)
            {
                long minPrev = long.MaxValue;
                int argMin = 0;
                if (i > 0)
                {
                    for (int t = 0; t < types; t++)
                    {
                        if (cost[t] < minPrev)
                        {
                            minPrev = cost[t];
                            argMin = t;
                        }
                    }
                }

                for (int t = 0; t < types; t++)
                {
                    long segment = 0;
                    int[] row = bits[t];
                    int[] h = histograms[i];
                    foreach (int s in segmentSymbols[i])
                    {
                        segment += (long)h[s] * row[s];
                    }

                    if (i == 0)
                    {
                        next[t] = segment;
                    }
                    else if (cost[t] <= minPrev + SwitchCost)
                    {
                        next[t] = cost[t] + segment;
                        back[(i * types) + t] = t;
                    }
                    else
                    {
                        next[t] = minPrev + SwitchCost + segment;
                        back[(i * types) + t] = argMin;
                    }
                }

                (cost, next) = (next, cost);
            }

            int end = 0;
            for (int t = 1; t < types; t++)
            {
                if (cost[t] < cost[end])
                {
                    end = t;
                }
            }

            bool changed = false;
            for (int i = m - 1; i >= 0; i--)
            {
                if (assignment[i] != end)
                {
                    assignment[i] = end;
                    changed = true;
                }

                end = back[(i * types) + end];
            }

            for (int t = 0; t < types; t++)
            {
                Array.Clear(typeHistograms[t]);
            }

            for (int i = 0; i < m; i++)
            {
                int[] dst = typeHistograms[assignment[i]];
                foreach (int s in segmentSymbols[i])
                {
                    dst[s] += histograms[i][s];
                }
            }

            if (!changed && iteration > 0)
            {
                break;
            }
        }

        // Blocks are runs of segments with one type; types are renumbered in order of first use.
        var renumber = new int[types];
        Array.Fill(renumber, -1);
        int used = 0;
        var blockTypes = new List<int>();
        var blockLengths = new List<int>();
        for (int i = 0; i < m; i++)
        {
            int t = assignment[i];
            if (renumber[t] < 0)
            {
                renumber[t] = used++;
            }

            int length = Math.Min(n, (i + 1) * segmentLength) - (i * segmentLength);
            if (blockTypes.Count > 0 && blockTypes[^1] == renumber[t])
            {
                blockLengths[^1] += length;
            }
            else
            {
                blockTypes.Add(renumber[t]);
                blockLengths.Add(length);
            }
        }

        if (used == 1)
        {
            return BrotliBlockSplit.Single(n);
        }

        // Keep the split only when it is estimated to pay for its headers and switches.
        long splitCost = (blockTypes.Count - 1) * SwitchCost;
        var all = new int[alphabetSize];
        for (int t = 0; t < types; t++)
        {
            int[] h = typeHistograms[t];
            int nonZero = 0;
            for (int s = 0; s < alphabetSize; s++)
            {
                all[s] += h[s];
                if (h[s] > 0)
                {
                    nonZero++;
                }
            }

            if (nonZero > 0)
            {
                splitCost += BrotliEncodeTables.EntropyCost(h, out _) + BrotliClusterer.HeaderCost(nonZero);
            }
        }

        int allUsed = 0;
        foreach (int c in all)
        {
            if (c > 0)
            {
                allUsed++;
            }
        }

        long singleCost = BrotliEncodeTables.EntropyCost(all, out _) + BrotliClusterer.HeaderCost(allUsed);
        if (splitCost >= singleCost)
        {
            return BrotliBlockSplit.Single(n);
        }

        return new BrotliBlockSplit { TypeCount = used, Types = [.. blockTypes], Lengths = [.. blockLengths] };
    }
}
