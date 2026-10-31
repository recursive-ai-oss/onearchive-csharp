namespace RecursiveAI.OneCompress.Brotli;

internal sealed partial class BrotliEncoderCore
{
    // Costs in the shortest-path parse are in 1/256 bit.
    private const int CostBit = 256;
    
    // Path costs are 64-bit: a block of a million literals at 8 bits is 2^31 in these units, which does not fit in an int.
    private const long Infinite = long.MaxValue / 4;

    private bool _collecting;

    // Match candidates of every position of the block, as found by the binary tree: lists of (length, distance, bytes covered, dictionary).
    private int[] _candStart = [];
    private int[] _candLength = [];
    private int[] _candDistance = [];
    private int[] _candCovered = [];
    private bool[] _candDictionary = [];
    private int _candCount;

    // The best path to every position: its cost, the position it came from, the match that led here (length 0 for a literal), the literals
    // since the last match, and the four last distances.
    private long[] _nodeCost = [];
    private int[] _nodePrevious = [];
    private int[] _nodeLength = [];
    private int[] _nodeDistance = [];
    private int[] _nodeCovered = [];
    private bool[] _nodeDictionary = [];
    private int[] _nodeInsert = [];
    private int[] _nodeRing = [];

    private readonly int[] _literalCost = new int[256];

    // Literal prices per context (RFC 7932 section 7.1): a literal's context depends only on the two bytes before it, which are input
    // whatever path is taken, so the price of a literal at a position is known before the path is. _literalContext holds each position's
    // context of the block; the prices are the 64 contexts of 256 bytes each, in 1/256 bit. Without literal contexts all of them are 0.
    private readonly int[] _contextCost = new int[64 * 256];
    private byte[] _literalContext = [];
    private readonly int[] _commandCost = new int[704];
    private readonly int[] _distanceCost = new int[BrotliDistanceCoding.AlphabetSize(0, 0)];

    private void AddCandidate(int length, int distance, int covered, bool dictionary = false)
    {
        if (_candCount == _candLength.Length)
        {
            int size = Math.Max(1024, _candCount * 2);
            Array.Resize(ref _candLength, size);
            Array.Resize(ref _candDistance, size);
            Array.Resize(ref _candCovered, size);
            Array.Resize(ref _candDictionary, size);
        }

        _candLength[_candCount] = length;
        _candDistance[_candCount] = distance;
        _candCovered[_candCount] = covered;
        _candDictionary[_candCount] = dictionary;
        _candCount++;
    }

    // ----- Cost model -----

    private static int CostOf(int total, int count, int unseenBits)
    {
        int logTotal = BrotliEncodeTables.Log2((uint)Math.Max(total, 1));
        int bits16 = count > 0 ? logTotal - BrotliEncodeTables.Log2((uint)count) : logTotal + (unseenBits << 16);
        return Math.Max(CostBit / 8, bits16 >> 8);
    }

    // Before any parse has been made: literals cost what the block's byte histogram says, the rest a plausible constant.
    private void SetPriorCosts(int start, int end)
    {
        var histogram = new int[256];
        for (int p = start; p < end; p++)
        {
            histogram[_buffer[p]]++;
        }

        for (int b = 0; b < 256; b++)
        {
            _literalCost[b] = CostOf(end - start, histogram[b], 3);
            _contextCost[b] = _literalCost[b];
        }

        Array.Clear(_literalContext, 0, end - start);
        Array.Fill(_commandCost, 9 * CostBit);
        for (int s = 0; s < _distanceCost.Length; s++)
        {
            _distanceCost[s] = s < 16 ? 4 * CostBit : 6 * CostBit;
        }
    }

    // After a parse: costs from the symbol statistics of its commands, with unseen symbols priced above the seen ones.
    private void SetCostsFromCommands(int start)
    {
        var lit = new int[256];
        var cmd = new int[704];
        var dist = new int[_distanceCost.Length];
        int[]? contextCounts = _plan.LiteralContexts ? new int[64 * 256] : null;
        Span<int> ring = stackalloc int[4];
        _ring.AsSpan(0, 4).CopyTo(ring);
        int pos = start;
        int litTotal = 0;
        int cmdTotal = 0;
        int distTotal = 0;
        for (int j = 0; j < _commands.Count; j++)
        {
            for (int k = 0; k < _commands.Insert[j]; k++)
            {
                lit[_buffer[pos + k]]++;
                litTotal++;
            }

            if (contextCounts != null)
            {
                CountLiteralContexts(contextCounts, pos, _commands.Insert[j]);
            }

            pos += _commands.Insert[j] + _commands.Covered[j];
            int ic = BrotliEncodeTables.InsertCode(_commands.Insert[j]);
            if (_commands.Copy[j] == 0)
            {
                cmd[BrotliEncodeTables.CommandSymbol(ic, 0, BrotliEncodeTables.CanBeImplicit(ic, 0))]++;
                cmdTotal++;
                continue;
            }

            int cc = BrotliEncodeTables.CopyCode(_commands.Copy[j]);
            int d = _commands.Distance[j];
            int code = _commands.IsDictionary[j] ? -1 : BrotliDistanceCoding.ShortCode(d, ring);
            bool implicitDistance = code == 0 && BrotliEncodeTables.CanBeImplicit(ic, cc);
            cmd[BrotliEncodeTables.CommandSymbol(ic, cc, implicitDistance)]++;
            cmdTotal++;
            if (!implicitDistance)
            {
                int sym = code >= 0 ? code : Math.Min(BrotliDistanceCoding.Symbol(d, 0, 0, out _, out _), dist.Length - 1);
                dist[sym]++;
                distTotal++;
            }

            if (!_commands.IsDictionary[j] && d != ring[0])
            {
                ring[3] = ring[2];
                ring[2] = ring[1];
                ring[1] = ring[0];
                ring[0] = d;
            }
        }

        for (int b = 0; b < 256; b++)
        {
            _literalCost[b] = CostOf(litTotal, lit[b], 4);
            _contextCost[b] = _literalCost[b];
        }

        if (contextCounts != null && litTotal > 0)
        {
            SetContextCosts(contextCounts, lit, litTotal);
        }

        for (int s = 0; s < 704; s++)
        {
            _commandCost[s] = CostOf(cmdTotal, cmd[s], 3);
        }

        for (int s = 0; s < dist.Length; s++)
        {
            _distanceCost[s] = CostOf(distTotal, dist[s], 3);
        }
    }

    // RFC 7932 section 7.1: the UTF8 context mode, which is the mode the writer picks for almost all of the literals this parse codes, so
    // literals are priced in the contexts they will be coded in.
    private static int ContextOf(int p1, int p2) => (BrotliContextTables.Lut0[p1] | BrotliContextTables.Lut1[p2]) & 63;

    // Counts the literals of one command by context and byte.
    private void CountLiteralContexts(int[] contextCounts, int pos, int count)
    {
        for (int k = 0; k < count; k++)
        {
            int at = pos + k;
            int p1 = at > 0 ? _buffer[at - 1] : 0;
            int p2 = at > 1 ? _buffer[at - 2] : 0;
            contextCounts[(ContextOf(p1, p2) << 8) | _buffer[at]]++;
        }
    }

    // Prices each byte in each context from that context's counts, blended with the block-wide counts so that a context seen only a few
    // times does not look cheaper than it is. The weight of the block-wide counts is large because the writer clusters the 64 contexts
    // into a few codes, which smooths them toward each other.
    private void SetContextCosts(int[] contextCounts, int[] blockCounts, int blockTotal)
    {
        const int Blend = 1024;
        long blockDenominator = blockTotal + 256L;
        for (int c = 0; c < 64; c++)
        {
            ReadOnlySpan<int> h = contextCounts.AsSpan(c << 8, 256);
            int total = 0;
            foreach (int v in h)
            {
                total += v;
            }

            long logDenominator = Log2Long((total + (long)Blend) * blockDenominator);
            for (int b = 0; b < 256; b++)
            {
                long numerator = ((long)h[b] * blockDenominator) + ((long)Blend * (blockCounts[b] + 1));
                int bits16 = (int)(logDenominator - Log2Long(numerator));
                _contextCost[(c << 8) + b] = Math.Max(CostBit / 8, bits16 >> 8);
            }
        }
    }

    // log2(x) in 16.16 fixed point for a positive 64-bit x.
    private static long Log2Long(long x)
    {
        int shift = Math.Max(0, 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)x) - 32);
        return BrotliEncodeTables.Log2((uint)(x >> shift)) + ((long)shift << 16);
    }

    // The context of every position of the block.
    private void FillLiteralContexts(int start, int end)
    {
        for (int pos = start; pos < end; pos++)
        {
            int p1 = pos > 0 ? _buffer[pos - 1] : 0;
            int p2 = pos > 1 ? _buffer[pos - 2] : 0;
            _literalContext[pos - start] = (byte)ContextOf(p1, p2);
        }
    }

    // The cost of the command that inserts insertLength literals and copies, with the extra bits and the distance symbol.
    private int CommandPrice(int insertLength, int copyLength, int distance, bool dictionary, ReadOnlySpan<int> ring)
    {
        int ic = BrotliEncodeTables.InsertCode(insertLength);
        int cc = BrotliEncodeTables.CopyCode(copyLength);
        int code = dictionary ? -1 : BrotliDistanceCoding.ShortCode(distance, ring);
        bool implicitDistance = code == 0 && BrotliEncodeTables.CanBeImplicit(ic, cc);
        int price = _commandCost[BrotliEncodeTables.CommandSymbol(ic, cc, implicitDistance)]
            + ((BrotliCodes.InsertExtra[ic] + BrotliCodes.CopyExtra[cc]) * CostBit);
        if (!implicitDistance)
        {
            if (code >= 0)
            {
                price += _distanceCost[code];
            }
            else
            {
                int sym = BrotliDistanceCoding.Symbol(distance, 0, 0, out int extraBits, out _);
                price += _distanceCost[Math.Min(sym, _distanceCost.Length - 1)] + (extraBits * CostBit);
            }
        }

        return price;
    }

    // ----- The parse -----

    private void EnsureNodes(int n)
    {
        if (_nodeCost.Length >= n + 1)
        {
            return;
        }

        int size = n + 1;
        _nodeCost = new long[size];
        _nodePrevious = new int[size];
        _nodeLength = new int[size];
        _nodeDistance = new int[size];
        _nodeCovered = new int[size];
        _nodeDictionary = new bool[size];
        _nodeInsert = new int[size];
        _nodeRing = new int[size * 4];
        _candStart = new int[size + 1];
        _literalContext = new byte[size];
    }

    private void ParseOptimal(int start, int end)
    {
        int n = end - start;
        EnsureNodes(n);
        CollectCandidates(start, end);
        SetPriorCosts(start, end);
        for (int pass = 0; pass < _plan.OptimalPasses; pass++)
        {
            FindBestPath(start, end);
            _commands.Clear();
            EmitPath(start, end);
            if (pass + 1 < _plan.OptimalPasses)
            {
                SetCostsFromCommands(start);
                if (_plan.LiteralContexts)
                {
                    FillLiteralContexts(start, end);
                }
            }
        }
    }

    private void CollectCandidates(int start, int end)
    {
        _collecting = true;
        _candCount = 0;
        var unused = new Match();
        for (int pos = start; pos < end; pos++)
        {
            _candStart[pos - start] = _candCount;
            if (pos + 4 > end)
            {
                continue;
            }

            HashUpTo(pos);
            bool enter = _hashedUpTo == pos && pos + _plan.NiceLength <= end;
            TreeInsert(pos, _plan.ChainDepth, true, enter, ref unused);
            if (enter)
            {
                _hashedUpTo = pos + 1;
            }

            // As in the greedy parse, a long match found by the tree makes a dictionary word unlikely to pay.
            bool searchWords = _plan.Dictionary && (_candCount == _candStart[pos - start] || _candLength[_candCount - 1] < 12);
            if (searchWords)
            {
                int maxDistance = (int)Math.Min(_window, _streamBase + pos);
                int hits = BrotliDictionaryIndex.Instance.Search(_buffer.AsSpan(0, end), pos, end - pos, _plan.DictionaryTransforms, _hits, _transformed);
                for (int i = 0; i < hits; i++)
                {
                    AddCandidate(_hits[i].WordLength, maxDistance + 1 + _hits[i].WordId, _hits[i].Covered, true);
                }
            }
        }

        _candStart[end - start] = _candCount;
        _collecting = false;
    }

    private void FindBestPath(int start, int end)
    {
        int n = end - start;
        ReadOnlySpan<byte> buffer = _buffer;
        Array.Fill(_nodeCost, Infinite, 0, n + 1);
        _nodeCost[0] = 0;
        _nodeInsert[0] = 0;
        _ring.AsSpan(0, 4).CopyTo(_nodeRing);
        Span<int> ring = stackalloc int[4];
        for (int i = 0; i < n; i++)
        {
            long cost = _nodeCost[i];
            if (cost >= Infinite)
            {
                continue;
            }

            int pos = start + i;
            _nodeRing.AsSpan(i * 4, 4).CopyTo(ring);
            int insert = _nodeInsert[i];
            int maxLength = end - pos;
            int maxDistance = (int)Math.Min(_window, _streamBase + pos);

            // A very long match is taken whole and the positions inside it are not visited.
            int first = _candStart[i];
            int last = _candStart[i + 1];
            int longest = last - 1;
            while (longest >= first && _candDictionary[longest])
            {
                longest--;
            }

            if (longest >= first && _candLength[longest] >= _plan.NiceLength)
            {
                int d = _candDistance[longest];
                int full = CommonLength(pos - d, pos, maxLength);
                Relax(i, i + full, cost, insert, full, d, full, false, ring);
                i += full - 1;
                continue;
            }

            // A literal.
            long literalCost = cost + _contextCost[(_literalContext[i] << 8) | buffer[pos]];
            if (literalCost < _nodeCost[i + 1])
            {
                _nodeCost[i + 1] = literalCost;
                _nodePrevious[i + 1] = i;
                _nodeLength[i + 1] = 0;
                _nodeInsert[i + 1] = insert + 1;
                ring.CopyTo(_nodeRing.AsSpan((i + 1) * 4, 4));
            }

            // The recent distances, any length from 2.
            for (int r = 0; r < 4; r++)
            {
                int d = ring[r];
                if (d > maxDistance || maxLength < 2)
                {
                    continue;
                }

                int len = CommonLength(pos - d, pos, maxLength);
                for (int l = Math.Max(2, len - 6); l <= len; l++)
                {
                    Relax(i, i + l, cost, insert, l, d, l, false, ring);
                }
            }

            // The distances the short codes 4 to 15 reach: the last two distances plus or minus up to three.
            for (int r = 0; r < 2; r++)
            {
                for (int delta = -3; delta <= 3; delta++)
                {
                    int d = ring[r] + delta;
                    if (delta == 0 || d < 1 || d > maxDistance || maxLength < 2)
                    {
                        continue;
                    }

                    int len = CommonLength(pos - d, pos, maxLength);
                    for (int l = Math.Max(2, len - 6); l <= len; l++)
                    {
                        Relax(i, i + l, cost, insert, l, d, l, false, ring);
                    }
                }
            }

            // The tree's candidates: for each length the nearest distance among the candidates at least that long.
            int minDistance = int.MaxValue;
            for (int k = last - 1; k >= first; k--)
            {
                if (_candDictionary[k])
                {
                    Relax(i, i + _candCovered[k], cost, insert, _candLength[k], _candDistance[k], _candCovered[k], true, ring);
                    continue;
                }

                minDistance = Math.Min(minDistance, _candDistance[k]);
                int below = 3;
                for (int j = k - 1; j >= first; j--)
                {
                    if (!_candDictionary[j])
                    {
                        below = _candLength[j];
                        break;
                    }
                }

                int top = Math.Min(_candLength[k], maxLength);
                int bottom = Math.Max(below + 1, top - 10);
                for (int l = Math.Max(bottom, 4); l <= top; l++)
                {
                    Relax(i, i + l, cost, insert, l, minDistance, l, false, ring);
                }
            }
        }
    }

    private void Relax(int from, int to, long cost, int insert, int length, int distance, int covered, bool dictionary, ReadOnlySpan<int> ring)
    {
        if (to > _nodeCost.Length - 1 || to <= from)
        {
            return;
        }

        long total = cost + CommandPrice(insert, length, distance, dictionary, ring);
        if (total >= _nodeCost[to])
        {
            return;
        }

        _nodeCost[to] = total;
        _nodePrevious[to] = from;
        _nodeLength[to] = length;
        _nodeDistance[to] = distance;
        _nodeCovered[to] = covered;
        _nodeDictionary[to] = dictionary;
        _nodeInsert[to] = 0;
        Span<int> next = _nodeRing.AsSpan(to * 4, 4);
        ring.CopyTo(next);
        if (!dictionary && distance != ring[0])
        {
            next[3] = ring[2];
            next[2] = ring[1];
            next[1] = ring[0];
            next[0] = distance;
        }
    }

    // Reads the best path back from the end of the block into commands.
    private void EmitPath(int start, int end)
    {
        int n = end - start;
        var steps = new List<int>();
        for (int i = n; i > 0; i = _nodePrevious[i])
        {
            if (_nodeLength[i] > 0)
            {
                steps.Add(i);
            }
        }

        int at = 0;
        for (int s = steps.Count - 1; s >= 0; s--)
        {
            int to = steps[s];
            int from = _nodePrevious[to];
            _commands.Add(from - at, _nodeLength[to], _nodeDistance[to], _nodeCovered[to], _nodeDictionary[to]);
            at = to;
        }

        if (at < n)
        {
            _commands.Add(n - at, 0, 0, 0, false);
        }
    }
}
