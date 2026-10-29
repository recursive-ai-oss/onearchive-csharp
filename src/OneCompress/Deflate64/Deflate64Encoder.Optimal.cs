using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Deflate64;

public sealed partial class Deflate64Encoder
{
    // Positions parsed together. The price list is refreshed from the previous run's choices, so shorter runs adapt faster and
    // longer ones spend less time on bookkeeping.
    private const int RunLength = 1 << 12;

    // Prices are in sixteenths of a bit.
    private const int CostUnit = 16;

    // The shortest-path search prices lengths 3 to 257, which have symbols of their own; a longer match (symbol 285 with 16 extra
    // bits) is taken whole when the finder reaches this length, see ParseOptimal.
    private const int MaxPricedLength = 257;

    private OptimalState? _optimal;

    private sealed class OptimalState
    {
        public readonly DeflateBinaryTreeMatchFinder Finder = new(16, HistorySize, 16);
        public readonly int[] MatchStart = new int[RunLength + 2];
        public int[] MatchLength = new int[RunLength * 4];
        public int[] MatchDistance = new int[RunLength * 4];
        public readonly int[] Cost = new int[RunLength + 1];
        public readonly uint[] Choice = new uint[RunLength + 1];
        public readonly int[] Path = new int[RunLength + 1];
        public readonly int[] LiteralCost = new int[256];
        public readonly int[] LengthCost = new int[MaxPricedLength + 1];
        public readonly int[] DistanceSlotCost = new int[Deflate64Tables.DistanceSymbolCount];
        public readonly uint[] LiteralLengthStats = new uint[286];
        public readonly uint[] DistanceStats = new uint[Deflate64Tables.DistanceSymbolCount];
        public readonly uint[] RunLiteralLength = new uint[286];
        public readonly uint[] RunDistance = new uint[Deflate64Tables.DistanceSymbolCount];
        public readonly uint[] Blend = new uint[286];
        public readonly uint[] BlendDistance = new uint[Deflate64Tables.DistanceSymbolCount];
        public readonly int[] ScratchLengths = new int[DeflateBinaryTreeMatchFinder.MaxMatches];
        public readonly int[] ScratchDistances = new int[DeflateBinaryTreeMatchFinder.MaxMatches];

        public void Reset()
        {
            Finder.Reset();
            Array.Clear(LiteralLengthStats);
            Array.Clear(DistanceStats);
            SetStartingCosts();
        }

        // Before any statistics exist: the costs of the fixed Huffman codes (RFC 1951 section 3.2.6).
        private void SetStartingCosts()
        {
            Array.Fill(LiteralCost, (8 * CostUnit) + (CostUnit / 2));
            for (int length = Deflate64Tables.MinMatchLength; length <= MaxPricedLength; length++)
            {
                LengthCost[length] = (7 + Deflate64Tables.LengthExtraBits[LengthSlot(length)]) * CostUnit;
            }

            Array.Fill(DistanceSlotCost, 5 * CostUnit);
        }

        // Prices every symbol as -log2 of its share of the counts, smoothed so unseen symbols stay possible.
        public void SetCosts(ReadOnlySpan<uint> literalLength, ReadOnlySpan<uint> distance)
        {
            ulong total = 1;
            foreach (uint f in literalLength)
            {
                total += f;
            }

            int logTotal = DeflateCostMath.Log2Q8((uint)Math.Min(uint.MaxValue, (2 * total) + (uint)literalLength.Length));
            for (int b = 0; b < 256; b++)
            {
                LiteralCost[b] = SymbolCost(logTotal, literalLength[b]);
            }

            for (int length = Deflate64Tables.MinMatchLength; length <= MaxPricedLength; length++)
            {
                int slot = LengthSlot(length);
                LengthCost[length] = SymbolCost(logTotal, literalLength[257 + slot]) + (Deflate64Tables.LengthExtraBits[slot] * CostUnit);
            }

            ulong distanceTotal = 0;
            foreach (uint f in distance)
            {
                distanceTotal += f;
            }

            int logDistance = DeflateCostMath.Log2Q8((uint)Math.Min(uint.MaxValue, (2 * distanceTotal) + (uint)distance.Length));
            for (int slot = 0; slot < DistanceSlotCost.Length; slot++)
            {
                DistanceSlotCost[slot] = SymbolCost(logDistance, distance[slot]);
            }
        }

        private static int SymbolCost(int logTotal, uint frequency)
        {
            int cost = (logTotal - DeflateCostMath.Log2Q8((2 * frequency) + 1)) >> 4;
            return Math.Max(cost, 1);
        }
    }

    // Optimal parsing, as in the Deflate encoder: for each run of positions find every match, choose the cheapest sequence of
    // literals and matches by dynamic programming over the price list (a shortest path through the positions), re-price from the
    // choice and repeat. The 64 KiB window and the Deflate64 symbols are the only differences.
    private void ParseOptimal(int limit)
    {
        OptimalState s = _optimal!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        int fill = _fill;
        int position = _position;
        while (position < limit)
        {
            // A run adds at most RunLength tokens; close the block first when they might not fit.
            if (_tokenCount > 0 && _tokenCount + RunLength + 1 > TokenCapacity)
            {
                _position = position;
                CloseBlock(final: false);
            }

            int runStart = position;
            int runLimit = Math.Min(limit, runStart + RunLength);
            int matchCount = 0;
            int i = runStart;
            int longLength = 0;
            int longDistance = 0;
            while (i < runLimit)
            {
                s.MatchStart[i - runStart] = matchCount;
                int available = fill - i;
                if (available >= DeflateBinaryTreeMatchFinder.RequiredLookahead)
                {
                    int found = s.Finder.Search(window, i, Math.Min(MaxPricedLength, available), p.Chain, p.Nice, 0, s.ScratchLengths, s.ScratchDistances);
                    if (found > 0)
                    {
                        if (matchCount + found > s.MatchLength.Length)
                        {
                            Array.Resize(ref s.MatchLength, s.MatchLength.Length * 2);
                            Array.Resize(ref s.MatchDistance, s.MatchDistance.Length * 2);
                        }

                        Array.Copy(s.ScratchLengths, 0, s.MatchLength, matchCount, found);
                        Array.Copy(s.ScratchDistances, 0, s.MatchDistance, matchCount, found);
                        matchCount += found;
                        if (s.ScratchLengths[found - 1] >= p.Nice)
                        {
                            // The search stops comparing at the nice length; see how far this match really goes.
                            int reached = s.ScratchLengths[found - 1];
                            longDistance = s.ScratchDistances[found - 1];
                            int most = Math.Min(Deflate64Tables.MaxMatchLength, available);
                            longLength = reached + DeflateEncRaw.MatchLength(window, i - longDistance + reached, i + reached, most - reached);
                            break;
                        }
                    }
                }

                i++;
            }

            int n = i - runStart;
            if (longLength == 0)
            {
                s.MatchStart[n] = matchCount;
            }

            if (n > 0)
            {
                EmitOptimalRun(s, window, runStart, n, p.Passes);
            }

            if (longLength != 0)
            {
                PutMatch(longLength, longDistance);
                s.RunLiteralLength[257 + LengthSlot(longLength)]++;
                s.RunDistance[DistanceSlot(longDistance)]++;
                int end = i + longLength;
                for (int q = i + 1; q < end; q++)
                {
                    if (fill - q >= DeflateBinaryTreeMatchFinder.RequiredLookahead)
                    {
                        s.Finder.Search(window, q, Math.Min(MaxPricedLength, fill - q), p.Chain, p.Nice, 0, null, null);
                    }
                }

                position = end;
            }
            else
            {
                position = i;
            }

            // The next run's prices come from this run's choices, blended with what came before.
            for (int k = 0; k < s.LiteralLengthStats.Length; k++)
            {
                s.LiteralLengthStats[k] = (s.LiteralLengthStats[k] >> 1) + s.RunLiteralLength[k];
            }

            for (int k = 0; k < s.DistanceStats.Length; k++)
            {
                s.DistanceStats[k] = (s.DistanceStats[k] >> 1) + s.RunDistance[k];
            }

            s.SetCosts(s.LiteralLengthStats, s.DistanceStats);
            Array.Clear(s.RunLiteralLength);
            Array.Clear(s.RunDistance);
            _position = position;
            CheckBlock();
        }

        _position = position;
    }

    // Chooses the path through positions runStart..runStart+n and appends it to the token arrays, counting the symbols used into
    // RunLiteralLength and RunDistance. With several passes the prices are refreshed from the previous pass's path.
    private void EmitOptimalRun(OptimalState s, byte[] window, int runStart, int n, int passes)
    {
        int steps = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            FindCheapestPath(s, window, runStart, n);
            steps = TracePath(s, n);
            if (pass + 1 < passes)
            {
                Array.Clear(s.RunLiteralLength);
                Array.Clear(s.RunDistance);
                CountPath(s, window, runStart, steps, addTokens: false);
                for (int k = 0; k < s.Blend.Length; k++)
                {
                    s.Blend[k] = (s.LiteralLengthStats[k] >> 2) + s.RunLiteralLength[k];
                }

                for (int k = 0; k < s.BlendDistance.Length; k++)
                {
                    s.BlendDistance[k] = (s.DistanceStats[k] >> 2) + s.RunDistance[k];
                }

                s.SetCosts(s.Blend, s.BlendDistance);
            }
        }

        Array.Clear(s.RunLiteralLength);
        Array.Clear(s.RunDistance);
        CountPath(s, window, runStart, steps, addTokens: true);
    }

    private static void FindCheapestPath(OptimalState s, byte[] window, int runStart, int n)
    {
        int[] cost = s.Cost;
        uint[] choice = s.Choice;
        int[] literalCost = s.LiteralCost;
        int[] lengthCost = s.LengthCost;
        cost[0] = 0;
        for (int k = 1; k <= n; k++)
        {
            cost[k] = int.MaxValue;
        }

        for (int j = 0; j < n; j++)
        {
            int here = cost[j];
            int viaLiteral = here + literalCost[window[runStart + j]];
            if (viaLiteral < cost[j + 1])
            {
                cost[j + 1] = viaLiteral;
                choice[j + 1] = 1;
            }

            int first = s.MatchStart[j];
            int last = s.MatchStart[j + 1];
            int room = n - j;
            int previousLength = 2;
            for (int m = first; m < last; m++)
            {
                int length = s.MatchLength[m];
                int distance = s.MatchDistance[m];
                int slot = DistanceSlot(distance);
                int baseCost = here + s.DistanceSlotCost[slot] + (Deflate64Tables.DistanceExtraBits[slot] * CostUnit);
                int top = Math.Min(length, room);
                for (int l = previousLength + 1; l <= top; l++)
                {
                    int total = baseCost + lengthCost[l];
                    if (total < cost[j + l])
                    {
                        cost[j + l] = total;
                        choice[j + l] = (uint)l | ((uint)distance << 9);
                    }
                }

                if (length >= room)
                {
                    break;
                }

                previousLength = length;
            }
        }
    }

    // Walks the choices back from the end; Path[0] is the last step.
    private static int TracePath(OptimalState s, int n)
    {
        int steps = 0;
        int node = n;
        while (node > 0)
        {
            uint choice = s.Choice[node];
            s.Path[steps++] = (int)choice;
            node -= (int)(choice & 0x1FF);
        }

        return steps;
    }

    private void CountPath(OptimalState s, byte[] window, int runStart, int steps, bool addTokens)
    {
        int at = runStart;
        for (int k = steps - 1; k >= 0; k--)
        {
            uint choice = (uint)s.Path[k];
            int length = (int)(choice & 0x1FF);
            if (length == 1)
            {
                byte literal = window[at];
                s.RunLiteralLength[literal]++;
                if (addTokens)
                {
                    PutLiteral(literal);
                }
            }
            else
            {
                int distance = (int)(choice >> 9);
                s.RunLiteralLength[257 + LengthSlot(length)]++;
                s.RunDistance[DistanceSlot(distance)]++;
                if (addTokens)
                {
                    PutMatch(length, distance);
                }
            }

            at += length;
        }
    }
}
