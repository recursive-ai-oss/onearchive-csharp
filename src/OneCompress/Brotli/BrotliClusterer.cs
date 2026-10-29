namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Groups histograms whose symbol statistics are alike, so that one prefix code can serve the whole group. The encoder uses it twice:
/// to merge the segments of a block split into a few block types, and to map the literal and distance contexts onto prefix codes.
/// </summary>
/// <remarks>
/// <para>The cost of a histogram is its entropy, the bits an ideal code for it would take; merging two histograms costs the entropy of
/// the sum minus the entropies of the parts. A merge is worth it when that increase is less than what a separate prefix code would cost to
/// describe (<see cref="HeaderCost"/>, an estimate of 4 bits per used symbol plus 20). Up to <see cref="PairwiseLimit"/> histograms the
/// cheapest pair is merged repeatedly (agglomerative clustering); beyond that, histograms are taken in order of size and each joins the
/// nearest group or starts one, followed by two rounds of moving every histogram to the group that codes it best. All arithmetic is
/// integer (1/65536-bit units), and every tie goes to the lower index.</para>
/// </remarks>
internal static class BrotliClusterer
{
    internal const int PairwiseLimit = 160;

    /// <summary>Estimated cost in 1/65536-bit units of describing a prefix code with the given number of used symbols.</summary>
    internal static long HeaderCost(int usedSymbols) => ((long)((4 * usedSymbols) + 20)) << 16;

    private sealed class Group
    {
        internal int[] Counts = [];
        internal int Total;
        internal long SumXLogX; // sum of count * log2(count)
        internal int[] Used = [];
        internal int UsedCount;
        internal bool Alive = true;

        internal long Cost => BrotliEncodeTables.XLog2X(Total) - SumXLogX;
    }

    /// <summary>Clusters histograms.</summary>
    /// <param name="histograms">The histograms; a null or empty one takes no part and is assigned group 0.</param>
    /// <param name="alphabetSize">The number of symbols in each histogram.</param>
    /// <param name="maxGroups">The most groups to produce.</param>
    /// <param name="mergeBonus">Extra cost in 1/65536-bit units a merge may add and still be taken, on top of the header estimate.</param>
    /// <param name="assignment">Receives, for each histogram, its group, numbered in order of first use.</param>
    /// <param name="groupHistograms">Receives the sum of the histograms of each group.</param>
    /// <param name="pairwiseLimit">The most histograms clustered by repeated pair merging; more are clustered sequentially.</param>
    /// <returns>The number of groups (at least 1).</returns>
    internal static int Cluster(int[]?[] histograms, int alphabetSize, int maxGroups, long mergeBonus, int[] assignment, out int[][] groupHistograms, int pairwiseLimit = PairwiseLimit)
    {
        var groups = new List<Group>();
        var owner = new int[histograms.Length];
        Array.Fill(owner, -1);
        for (int i = 0; i < histograms.Length; i++)
        {
            int[]? h = histograms[i];
            if (h is null)
            {
                continue;
            }

            Group g = MakeGroup(h, alphabetSize);
            if (g.Total > 0)
            {
                owner[i] = groups.Count;
                groups.Add(g);
            }
        }

        if (groups.Count == 0)
        {
            Array.Clear(assignment);
            groupHistograms = [new int[alphabetSize]];
            return 1;
        }

        if (groups.Count <= pairwiseLimit)
        {
            Pairwise(groups, owner, maxGroups, mergeBonus, alphabetSize);
        }
        else
        {
            Sequential(histograms, groups, owner, alphabetSize, maxGroups, mergeBonus);
        }

        // Number the surviving groups in order of first use and gather their histograms.
        var number = new int[groups.Count];
        Array.Fill(number, -1);
        int count = 0;
        for (int i = 0; i < histograms.Length; i++)
        {
            if (owner[i] < 0)
            {
                continue;
            }

            int g = owner[i];
            if (number[g] < 0)
            {
                number[g] = count++;
            }
        }

        groupHistograms = new int[count][];
        for (int k = 0; k < count; k++)
        {
            groupHistograms[k] = new int[alphabetSize];
        }

        for (int i = 0; i < histograms.Length; i++)
        {
            if (owner[i] < 0)
            {
                assignment[i] = 0;
                continue;
            }

            int k = number[owner[i]];
            assignment[i] = k;
            int[] src = histograms[i]!;
            int[] dst = groupHistograms[k];
            for (int s = 0; s < alphabetSize; s++)
            {
                dst[s] += src[s];
            }
        }

        return count;
    }

    private static Group MakeGroup(int[] h, int alphabetSize)
    {
        var g = new Group { Counts = (int[])h.Clone() };
        Rebuild(g, alphabetSize);
        return g;
    }

    private static void Rebuild(Group g, int alphabetSize)
    {
        long sum = 0;
        int total = 0;
        int used = 0;
        if (g.Used.Length < alphabetSize)
        {
            g.Used = new int[alphabetSize];
        }

        for (int s = 0; s < alphabetSize; s++)
        {
            int c = g.Counts[s];
            if (c > 0)
            {
                total += c;
                sum += BrotliEncodeTables.XLog2X(c);
                g.Used[used++] = s;
            }
        }

        g.Total = total;
        g.SumXLogX = sum;
        g.UsedCount = used;
    }

    // The increase in cost when the two groups share a code.
    private static long MergeDelta(Group a, Group b)
    {
        Group small = a.UsedCount <= b.UsedCount ? a : b;
        Group large = ReferenceEquals(small, a) ? b : a;
        long overlap = 0;
        for (int k = 0; k < small.UsedCount; k++)
        {
            int s = small.Used[k];
            int other = large.Counts[s];
            if (other > 0)
            {
                int mine = small.Counts[s];
                overlap += BrotliEncodeTables.XLog2X(mine + other) - BrotliEncodeTables.XLog2X(mine) - BrotliEncodeTables.XLog2X(other);
            }
        }

        // cost(union) = XLog2X(Ta + Tb) - (Sa + Sb + overlap)
        long union = BrotliEncodeTables.XLog2X(a.Total + b.Total) - (a.SumXLogX + b.SumXLogX + overlap);
        return union - a.Cost - b.Cost;
    }

    private static void Pairwise(List<Group> groups, int[] owner, int maxGroups, long bonus, int alphabetSize)
    {
        int n = groups.Count;
        long[] delta = new long[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                delta[(i * n) + j] = MergeDelta(groups[i], groups[j]);
            }
        }

        int alive = n;
        while (alive > 1)
        {
            long best = long.MaxValue;
            long bestRaw = 0;
            int bi = -1;
            int bj = -1;
            for (int i = 0; i < n; i++)
            {
                if (!groups[i].Alive)
                {
                    continue;
                }

                for (int j = i + 1; j < n; j++)
                {
                    if (!groups[j].Alive)
                    {
                        continue;
                    }

                    long d = delta[(i * n) + j];
                    long margin = d - HeaderCost(Math.Min(groups[i].UsedCount, groups[j].UsedCount)) - bonus;
                    if (margin < best)
                    {
                        best = margin;
                        bestRaw = d;
                        bi = i;
                        bj = j;
                    }
                }
            }

            _ = bestRaw;
            if (best >= 0 && alive <= maxGroups)
            {
                break;
            }

            Group a = groups[bi];
            Group b = groups[bj];
            for (int s = 0; s < alphabetSize; s++)
            {
                a.Counts[s] += b.Counts[s];
            }

            Rebuild(a, alphabetSize);
            b.Alive = false;
            alive--;
            for (int k = 0; k < owner.Length; k++)
            {
                if (owner[k] == bj)
                {
                    owner[k] = bi;
                }
            }

            for (int k = 0; k < n; k++)
            {
                if (k != bi && groups[k].Alive)
                {
                    delta[(Math.Min(k, bi) * n) + Math.Max(k, bi)] = MergeDelta(groups[Math.Min(k, bi)], groups[Math.Max(k, bi)]);
                }
            }
        }
    }

    private static void Sequential(int[]?[] histograms, List<Group> items, int[] owner, int alphabetSize, int maxGroups, long bonus)
    {
        // Largest first (ties by index), so that the groups are seeded by the histograms that matter most.
        int n = items.Count;
        long[] keys = new long[n];
        for (int i = 0; i < n; i++)
        {
            keys[i] = ((long)(int.MaxValue - items[i].Total) << 32) | (uint)i;
        }

        Array.Sort(keys);
        var groups = new List<Group>();
        var itemGroup = new int[n];
        for (int r = 0; r < n; r++)
        {
            int item = (int)(keys[r] & 0xFFFFFFFF);
            Group h = items[item];
            long bestDelta = long.MaxValue;
            int best = -1;
            for (int g = 0; g < groups.Count; g++)
            {
                long d = MergeDelta(groups[g], h);
                if (d < bestDelta)
                {
                    bestDelta = d;
                    best = g;
                }
            }

            if (best >= 0 && (groups.Count >= maxGroups || bestDelta < HeaderCost(h.UsedCount) + h.Cost + bonus))
            {
                Group g = groups[best];
                for (int s = 0; s < alphabetSize; s++)
                {
                    g.Counts[s] += h.Counts[s];
                }

                Rebuild(g, alphabetSize);
                itemGroup[item] = best;
            }
            else
            {
                Group g = new() { Counts = (int[])h.Counts.Clone() };
                Rebuild(g, alphabetSize);
                itemGroup[item] = groups.Count;
                groups.Add(g);
            }
        }

        // Two rounds of moving each histogram to the group whose code is cheapest for it.
        for (int round = 0; round < 2; round++)
        {
            var logTotal = new int[groups.Count];
            for (int g = 0; g < groups.Count; g++)
            {
                logTotal[g] = groups[g].Total > 0 ? BrotliEncodeTables.Log2((uint)groups[g].Total) : 0;
            }

            bool changed = false;
            for (int item = 0; item < n; item++)
            {
                Group h = items[item];
                long bestCost = long.MaxValue;
                int best = itemGroup[item];
                for (int g = 0; g < groups.Count; g++)
                {
                    if (groups[g].Total == 0)
                    {
                        continue;
                    }

                    long cost = 0;
                    int[] counts = groups[g].Counts;
                    for (int k = 0; k < h.UsedCount; k++)
                    {
                        int s = h.Used[k];
                        int c = counts[s];
                        long bits = c > 0 ? logTotal[g] - BrotliEncodeTables.Log2((uint)c) : logTotal[g] + (8 << 16);
                        cost += h.Counts[s] * bits;
                    }

                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = g;
                    }
                }

                if (best != itemGroup[item])
                {
                    itemGroup[item] = best;
                    changed = true;
                }
            }

            for (int g = 0; g < groups.Count; g++)
            {
                Array.Clear(groups[g].Counts);
            }

            for (int item = 0; item < n; item++)
            {
                int[] counts = groups[itemGroup[item]].Counts;
                int[] src = items[item].Counts;
                for (int s = 0; s < alphabetSize; s++)
                {
                    counts[s] += src[s];
                }
            }

            for (int g = 0; g < groups.Count; g++)
            {
                Rebuild(groups[g], alphabetSize);
            }

            if (!changed)
            {
                break;
            }
        }

        // Report the result through owner: groups are numbered by their index in the list built here.
        int index = 0;
        for (int i = 0; i < histograms.Length; i++)
        {
            if (owner[i] >= 0)
            {
                owner[i] = itemGroup[owner[i]];
                index++;
            }
        }

        _ = index;
        items.Clear();
        items.AddRange(groups);
    }
}
