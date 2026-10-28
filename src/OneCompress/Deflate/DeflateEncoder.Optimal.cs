using System.Numerics;

namespace RecursiveAI.OneCompress.Deflate;

public sealed partial class DeflateEncoder
{
    // Positions parsed together. The cost model is refreshed from the previous run's statistics, so shorter runs adapt faster and
    // longer ones spend less time on bookkeeping.
    private const int RunLength = 1 << 14;

    // Costs are in sixteenths of a bit.
    private const int CostUnit = 16;

    private OptimalState? _optimal;

    private sealed class OptimalState
    {
        public readonly DeflateBinaryTreeMatchFinder Finder = new();
        public readonly int[] MatchStart = new int[RunLength + 2];
        public int[] MatchLength = new int[RunLength * 4];
        public int[] MatchDistance = new int[RunLength * 4];
        public readonly int[] Cost = new int[RunLength + 1];
        public readonly uint[] Choice = new uint[RunLength + 1];
        public readonly int[] Path = new int[RunLength + 1];
        public readonly int[] LiteralCost = new int[256];
        public readonly int[] LengthCost = new int[DeflateTables.MaxMatchLength + 1];
        public readonly int[] DistanceSlotCost = new int[30];
        public readonly uint[] LiteralLengthStats = new uint[DeflateSequenceBuffer.LiteralLengthSymbolCount];
        public readonly uint[] DistanceStats = new uint[DeflateSequenceBuffer.DistanceSymbolCount];
        public readonly uint[] RunLiteralLength = new uint[DeflateSequenceBuffer.LiteralLengthSymbolCount];
        public readonly uint[] RunDistance = new uint[DeflateSequenceBuffer.DistanceSymbolCount];
        public readonly uint[] Blend = new uint[DeflateSequenceBuffer.LiteralLengthSymbolCount];
        public readonly uint[] BlendDistance = new uint[DeflateSequenceBuffer.DistanceSymbolCount];
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
            Array.Fill(LiteralCost, 8 * CostUnit + (CostUnit / 2));
            for (int length = DeflateTables.MinMatchLength; length <= DeflateTables.MaxMatchLength; length++)
            {
                int slot = DeflateSymbols.LengthSlot[length];
                LengthCost[length] = (7 + DeflateTables.LengthExtraBits[slot]) * CostUnit;
            }

            for (int slot = 0; slot < DistanceSlotCost.Length; slot++)
            {
                DistanceSlotCost[slot] = 5 * CostUnit;
            }
        }

        // Prices every symbol as -log2 of its share of the counts, smoothed so unseen symbols stay possible.
        public void SetCosts(ReadOnlySpan<uint> literalLength, ReadOnlySpan<uint> distance)
        {
            ulong total = 1;
            foreach (uint f in literalLength)
            {
                total += f;
            }

            int symbols = literalLength.Length;
            int logTotal = Log2Q8((uint)Math.Min(uint.MaxValue, (2 * total) + (uint)symbols));
            for (int b = 0; b < 256; b++)
            {
                LiteralCost[b] = SymbolCost(logTotal, literalLength[b]);
            }

            for (int length = DeflateTables.MinMatchLength; length <= DeflateTables.MaxMatchLength; length++)
            {
                int slot = DeflateSymbols.LengthSlot[length];
                LengthCost[length] = SymbolCost(logTotal, literalLength[257 + slot]) + (DeflateTables.LengthExtraBits[slot] * CostUnit);
            }

            ulong distanceTotal = 0;
            foreach (uint f in distance)
            {
                distanceTotal += f;
            }

            int logDistance = Log2Q8((uint)Math.Min(uint.MaxValue, (2 * distanceTotal) + (uint)distance.Length));
            for (int slot = 0; slot < DistanceSlotCost.Length; slot++)
            {
                DistanceSlotCost[slot] = SymbolCost(logDistance, distance[slot]);
            }
        }

        private static int SymbolCost(int logTotal, uint frequency)
        {
            int cost = (logTotal - Log2Q8((2 * frequency) + 1)) >> 4;
            return Math.Max(cost, 1);
        }

        // log2(x) with 8 fractional bits, by integer squaring, so the result is the same on every platform.
        public static int Log2Q8(uint x)
        {
            int exponent = BitOperations.Log2(x);
            ulong mantissa = (ulong)x << (31 - exponent);
            int fraction = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                mantissa = (mantissa * mantissa) >> 31;
                fraction <<= 1;
                if (mantissa >= (1UL << 32))
                {
                    fraction |= 1;
                    mantissa >>= 1;
                }
            }

            return (exponent << 8) | fraction;
        }
    }

    // Optimal parsing: for each run of positions, find every match, then choose the cheapest sequence of literals and matches by
    // dynamic programming over the cost model (a shortest path through the positions), and re-price from the choice and repeat.
    private void ParseOptimal(int limit)
    {
        OptimalState s = _optimal!;
        LevelParameters p = _parameters;
        byte[] window = _window;
        int fill = _fill;
        int position = _position;
        while (position < limit)
        {
            if (_sequences.Count > 0 && _sequences.Count + RunLength + 1 > _sequences.Capacity)
            {
                _position = position;
                CloseBlock(isFinal: false);
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
                    int found = s.Finder.Search(window, i, Math.Min(DeflateTables.MaxMatchLength, available), p.Chain, p.Nice, _oldest, s.ScratchLengths, s.ScratchDistances);
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
                            longLength = s.ScratchLengths[found - 1];
                            longDistance = s.ScratchDistances[found - 1];
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
                _sequences.AddMatchUnchecked(longLength, longDistance);
                s.RunLiteralLength[257 + DeflateSymbols.LengthSlot[longLength]]++;
                s.RunDistance[DeflateSymbols.DistanceSlot(longDistance)]++;
                int end = i + longLength;
                for (int q = i + 1; q < end; q++)
                {
                    if (fill - q >= DeflateBinaryTreeMatchFinder.RequiredLookahead)
                    {
                        s.Finder.Search(window, q, Math.Min(DeflateTables.MaxMatchLength, fill - q), p.Chain, p.Nice, _oldest, null, null);
                    }
                }

                position = end;
            }
            else
            {
                position = i;
            }

            // Next run's prices come from this run's choices, blended with what came before.
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

            if (_sequences.Count >= _checkAt)
            {
                _position = position;
                CheckBlock();
            }
        }

        _position = position;
    }

    // Chooses the path through positions runStart..runStart+n and appends it to the sequence buffer, counting the symbols used
    // into s.RunLiteralLength and s.RunDistance. With several passes the prices are refreshed from the previous pass's path.
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
                CountPath(s, window, runStart, steps, addToSequences: false);
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
        CountPath(s, window, runStart, steps, addToSequences: true);
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
                int slot = DeflateSymbols.DistanceSlot(distance);
                int baseCost = here + s.DistanceSlotCost[slot] + (DeflateTables.DistanceExtraBits[slot] * CostUnit);
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

    private void CountPath(OptimalState s, byte[] window, int runStart, int steps, bool addToSequences)
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
                if (addToSequences)
                {
                    _sequences.AddLiteralUnchecked(literal);
                }
            }
            else
            {
                int distance = (int)(choice >> 9);
                s.RunLiteralLength[257 + DeflateSymbols.LengthSlot[length]]++;
                s.RunDistance[DeflateSymbols.DistanceSlot(distance)]++;
                if (addToSequences)
                {
                    _sequences.AddMatchUnchecked(length, distance);
                }
            }

            at += length;
        }
    }
}
