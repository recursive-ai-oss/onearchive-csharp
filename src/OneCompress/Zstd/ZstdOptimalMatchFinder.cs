using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The optimal-parsing strategies: <see cref="ZstdStrategy.BinaryTreeOptimal"/>, <see cref="ZstdStrategy.BinaryTreeUltra"/> and
/// <see cref="ZstdStrategy.BinaryTreeUltra2"/>. Matches come from a binary tree per hash value; the parse chooses, over a stretch of
/// up to 4096 positions, the cheapest way to code it as a path of literals and matches, where the price of every symbol is its
/// cost under the statistics of the data coded so far (a shortest-path search over positions, as in price-based LZ77 parsers).
/// </summary>
/// <remarks>
/// <para><b>Prices.</b> A symbol's price is <c>log2(total / count)</c> in 1/256 bit, plus its extra bits, from running counts of
/// literals, literal length codes, match length codes and offset codes. The counts of the first block start from the block's own
/// literal histogram and the predefined sequence distributions (RFC 8878 §3.1.1.3.2.2); every 4096 bytes parsed, and at the end of a
/// block, the counts so far in the block are added to half the counts of the blocks before it and the prices are rebuilt. For
/// <see cref="ZstdStrategy.BinaryTreeUltra2"/> the first block is parsed twice: a first pass with the starting prices only gathers
/// counts, and the real pass is priced with them. All arithmetic is integer, with a logarithm table (<see cref="ZstdCost"/>), so the
/// choice of path depends on the input and the parameters alone.</para>
/// <para><b>Path search.</b> A cell per position holds the cheapest known price of reaching it, the match or literal run that
/// ended there and the repeat offsets in force (a repeat-offset match is priced as a repeat code only if the path to it really
/// has that offset in the right place). From each cell the parse tries the three repeat offsets and every match the tree
/// reports (the longest match at each length found, so shorter lengths of the same match are tried too), and extends cell prices
/// by one literal. A match at least as long as the target length ends the search stretch at once and is taken. Otherwise the
/// stretch ends at the farthest cell any match reaches, the cheapest path to it is written out, and parsing resumes there; literals
/// at the end of that path stay pending for the next stretch rather than being forced into a match.</para>
/// <para><b>Tree.</b> Same structure as the binary-tree lazy2 finder's: <c>2^(ChainLog-1)</c> nodes with two links, one tree per hash,
/// every position entered exactly once, in order, the node that matches for the comparison limit taking over the children of the
/// one it replaces. A search visits at most <c>2^SearchLog</c> nodes. With a dictionary, the dictionary's hash chains are searched with
/// the attempts that are left.</para>
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdOptimalMatchFinder : ZstdMatchFinder
{
    // Positions one parse stretch looks ahead at most (the cells of the path search).
    private const int OptNum = 4096;
    private const int Infinite = 1 << 30;
    private const int CandidateCapacity = 8192;

    // The tree compares no more than this many bytes of two strings; a match that long is "long enough" for the tree's purposes and
    // is measured to its end only when it is going to be taken.
    private const int CompareLimit = 1024;

    // After this many bytes of a block are parsed the prices are rebuilt from the counts so far.
    private const int RefreshBytes = 4096;

    // The dictionary's hash chain is walked for this many times the level's attempts per position.
    private const int DictionaryAttemptsFactor = 16;

    // Matches of length 3 cost a little more than their symbols suggest: their far offsets evict useful repeat offsets and,
    // coded rarely, their symbols cost more than the counts predict. Two bits keeps them to the cases where they clearly pay.
    private const int ShortMatchPenalty = 2 * ZstdCost.Bit;

    // Marks a stored tree position that was entered with a shortened comparison length (see TreeWalk).
    private const uint TaintBit = 0x80000000u;
    private const uint PositionMask = 0x7FFFFFFFu;

    private const int Bit = ZstdCost.Bit;

    private struct Cell
    {
        public int Price;
        public int MatchLength;     // 0: reached by a literal
        public int LiteralLength;   // literal cell: run length; match cell: literals before the match
        public int From;            // match cell: the cell the match starts at
        public uint Offset;
        public ZstdRepeatOffsets Reps;
    }

    private uint[] _head = [];
    private uint[] _links = [];
    private int _hashLog;
    private int _linkLog;
    private int _linkGrowAt; // the first position that needs a larger table (int.MaxValue when whole)
    private int _linkSlots; // nodes the table holds now: a power of two, at most 1 << _linkLog
    private int _shift;
    private int _minMatch;
    private int _repeatMinMatch;
    private int _attempts;
    private int _sufficient;
    private int _compareLimit;
    private bool _prune;
    private bool _twoPass;
    private long _windowSize;
    private int _next;
    private bool _blockSeen;
    private ZstdCompressionDictionary? _dictionary;
    private ZstdChainDictionaryIndex? _dictionaryIndex;
    private int _dictionaryHashShift;
    private int _dictionaryHashLog;
    private int _dictionaryChainSize;

    private readonly Cell[] _cells = new Cell[OptNum + 16];
    private readonly int[] _candLength = new int[CandidateCapacity];
    private readonly int[] _candStart = new int[CandidateCapacity];
    private readonly int[] _candSource = new int[CandidateCapacity];
    private readonly uint[] _candOffset = new uint[CandidateCapacity];
    private int _candCount;
    private readonly int[] _pathLiterals = new int[OptNum + 16];
    private readonly int[] _pathLength = new int[OptNum + 16];
    private readonly uint[] _pathOffset = new uint[OptNum + 16];
    private ZstdSequenceStore? _scratchStore;

    // Statistics: running counts, the counts of the block being parsed, and the mix the current prices were made from.
    private readonly int[] _litStat = new int[256];
    private readonly int[] _llStat = new int[36];
    private readonly int[] _mlStat = new int[53];
    private readonly int[] _ofStat = new int[32];
    private readonly int[] _litCount = new int[256];
    private readonly int[] _llCount = new int[36];
    private readonly int[] _mlCount = new int[53];
    private readonly int[] _ofCount = new int[32];
    private readonly int[] _litMix = new int[256];
    private readonly int[] _llMix = new int[36];
    private readonly int[] _mlMix = new int[53];
    private readonly int[] _ofMix = new int[32];
    private bool _statsReady;
    private int _sinceHalved;
    private const int HalveBytes = 1 << 17;

    // Prices in 1/256 bit.
    private readonly int[] _litPrice = new int[256];
    private readonly int[] _llPrice = new int[36];
    private readonly int[] _mlPrice = new int[53];
    private readonly int[] _ofPrice = new int[32];
    private readonly int[] _llFull = new int[256];
    private readonly int[] _mlFull = new int[1024];

    /// <inheritdoc />
    public override void BeginFrame(ZstdCompressionParameters parameters, ZstdCompressionDictionary? dictionary)
    {
        if (parameters.Strategy < ZstdStrategy.BinaryTreeOptimal)
        {
            throw new ArgumentException("This match finder implements the BinaryTreeOptimal, BinaryTreeUltra and BinaryTreeUltra2 strategies.", nameof(parameters));
        }

        _prune = parameters.Strategy == ZstdStrategy.BinaryTreeOptimal;
        _twoPass = parameters.Strategy == ZstdStrategy.BinaryTreeUltra2;
        _minMatch = Math.Clamp(parameters.MinMatch, 3, 8);

        // Repeat-offset matches are cheap to code, so they are looked for down to four bytes (three when the level asks for three).
        _repeatMinMatch = Math.Min(_minMatch, 4);
        _shift = 64 - (8 * _minMatch);
        _hashLog = Math.Clamp(parameters.HashLog, 6, 26);
        _attempts = 1 << Math.Clamp(parameters.SearchLog, 0, 12);
        _sufficient = Math.Clamp(parameters.TargetLength, Math.Max(_minMatch, 8), OptNum - 1);
        _compareLimit = Math.Max(_sufficient, CompareLimit);
        _windowSize = parameters.WindowSize;
        _linkLog = Math.Clamp(parameters.ChainLog - 1, 4, 25);
        Prepare(ref _head, 1 << _hashLog);
        PrepareLinks();
        _next = 1;
        _blockSeen = false;
        _statsReady = false;
        _sinceHalved = 0;

        _dictionary = dictionary;
        _dictionaryIndex = null;
        if (dictionary is not null && dictionary.Content.Length >= 8)
        {
            _dictionaryHashLog = parameters.DictionaryHashLog != 0 ? Math.Clamp(parameters.DictionaryHashLog, 6, 26) : _hashLog;
            int chainLog = Math.Clamp(parameters.DictionaryChainLog != 0 ? parameters.DictionaryChainLog : parameters.ChainLog, 4, 25);
            _dictionaryChainSize = 1 << chainLog;
            _dictionaryHashShift = _shift;
            _dictionaryIndex = dictionary.GetChainIndex(_minMatch, _dictionaryHashLog, chainLog);
        }
    }

    private static void Prepare(ref uint[] table, int size)
    {
        if (table.Length < size)
        {
            table = new uint[size];
        }
        else
        {
            Array.Clear(table, 0, size);
        }
    }

    /// <inheritdoc />
    public override void Slide(int delta)
    {
        uint d = (uint)delta;
        _next = Math.Max(1, _next - delta);
        uint[] head = _head;
        int headSize = 1 << _hashLog;
        for (int i = 0; i < headSize; i++)
        {
            head[i] = Rebase(head[i], d);
        }

        // A node is stored at the slot of its position modulo the table size, so moving every position down by delta turns
        // the table around by delta slots as well. The table must be whole before it is turned.
        GrowLinks(1 << _linkLog);
        int slotCount = 1 << _linkLog;
        int rotation = (int)(d & (uint)(slotCount - 1));
        if (rotation != 0)
        {
            // Rotating left by `rotation` slots in place: reverse each part, then the whole.
            ReverseNodes(_links, 0, rotation);
            ReverseNodes(_links, rotation, slotCount);
            ReverseNodes(_links, 0, slotCount);
        }

        for (int i = 0; i < slotCount * 2; i++)
        {
            _links[i] = Rebase(_links[i], d);
        }
    }

    private static void ReverseNodes(uint[] links, int from, int to)
    {
        for (int i = from, j = to - 1; i < j; i++, j--)
        {
            (links[i * 2], links[j * 2]) = (links[j * 2], links[i * 2]);
            (links[(i * 2) + 1], links[(j * 2) + 1]) = (links[(j * 2) + 1], links[(i * 2) + 1]);
        }
    }

    // The tree table is allocated small and enlarged as positions reach its end, so a stream of unknown length that never fills the
    // window does not pay for the whole table. A node lives at the slot of its position modulo the full size; below the current
    // size that is the position itself, so growing keeps every node where it is and the output does not depend on the growth.
    private const int InitialLinkSlots = 1 << 16;

    private void PrepareLinks()
    {
        int slots = GrowTables ? Math.Min(1 << _linkLog, InitialLinkSlots) : 1 << _linkLog;
        if (_links.Length >= 2 * slots)
        {
            Array.Clear(_links, 0, 2 * slots);
        }
        else
        {
            _links = new uint[2 * slots];
        }

        SetLinkSlots(slots);
    }

    private void SetLinkSlots(int slots)
    {
        _linkSlots = slots;
        _linkGrowAt = slots < (1 << _linkLog) ? slots : int.MaxValue;
    }

    private void GrowLinks(int needSlots)
    {
        int full = 1 << _linkLog;
        if (needSlots > full)
        {
            needSlots = full;
        }

        if (needSlots <= _linkSlots)
        {
            return;
        }

        // Four times the old size, or the whole table once a quarter of it is needed or the step would pass half of it, so the
        // copies left behind stay a fraction of the final table.
        long step = BitOperations.RoundUpToPowerOf2((uint)Math.Max(needSlots, 4 * _linkSlots));
        int slots = needSlots > full / 4 || step > full / 2 ? full : (int)step;
        if (_links.Length >= 2 * slots)
        {
            // Memory left from a larger frame: it was cleared only up to the old size.
            Array.Clear(_links, 2 * _linkSlots, (2 * slots) - (2 * _linkSlots));
        }
        else
        {
            var grown = new uint[2 * slots];
            Array.Copy(_links, grown, 2 * _linkSlots);
            _links = grown;
        }

        SetLinkSlots(slots);
    }

    private static uint Rebase(uint stored, uint d)
    {
        uint position = stored & PositionMask;
        return position > d ? (position - d) | (stored & TaintBit) : 0;
    }

    /// <inheritdoc />
    public override void FindSequences(
        ReadOnlySpan<byte> window,
        int blockStart,
        int blockEnd,
        long windowStartPosition,
        ZstdRepeatOffsets repeatOffsets,
        ZstdSequenceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.BeginTracking(repeatOffsets);
        if ((uint)blockEnd > (uint)window.Length || blockStart < 0 || blockStart > blockEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(blockEnd));
        }

        if (!_statsReady)
        {
            InitialStatistics(window, blockStart, blockEnd);
        }

        BuildPrices(false);
        if (_twoPass && !_blockSeen && blockStart == 0 && windowStartPosition == 0 && blockEnd - blockStart >= 64)
        {
            // First pass: parse with the starting prices only to learn what this data costs, then start the tree over.
            _scratchStore ??= new ZstdSequenceStore(store.BlockSize);
            _scratchStore.Clear();
            Parse(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, _scratchStore);
            Array.Copy(_litCount, _litStat, 256);
            Array.Copy(_llCount, _llStat, 36);
            Array.Copy(_mlCount, _mlStat, 53);
            Array.Copy(_ofCount, _ofStat, 32);
            BuildPrices(false);
            Array.Clear(_head, 0, 1 << _hashLog);
            Array.Clear(_links, 0, 2 * _linkSlots);
            _next = 1;
        }

        Parse(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, store);
        _blockSeen = true;

        // Older blocks count half as much as the newest. A call may cover only the gap between two long matches, so the halving
        // happens once per block's worth of parsed bytes however the calls cut it; shorter calls just add their counts.
        _sinceHalved += blockEnd - blockStart;
        bool halve = _sinceHalved >= HalveBytes;
        if (halve)
        {
            _sinceHalved = 0;
        }

        Blend(_litStat, _litCount, halve);
        Blend(_llStat, _llCount, halve);
        Blend(_mlStat, _mlCount, halve);
        Blend(_ofStat, _ofCount, halve);
        _statsReady = true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The tree enters positions lazily: a search first enters every position since the last one entered. Priming moves that mark up
    /// to <paramref name="firstPosition"/>, so the positions before it are never entered and the next search enters the ones from there on.
    /// </remarks>
    public override void Prime(ReadOnlySpan<byte> window, int firstPosition, int endPosition)
    {
        _next = Math.Max(_next, Math.Max(firstPosition, 1));
    }

    private static void Blend(int[] stat, int[] count, bool halve)
    {
        for (int i = 0; i < stat.Length; i++)
        {
            stat[i] = (halve ? stat[i] >> 1 : stat[i]) + count[i];
        }
    }

    // RFC 8878 §3.1.1.3.2.2: the predefined distributions stand in for the sequence statistics before anything is known; the
    // literals start from the block's own histogram.
    private void InitialStatistics(ReadOnlySpan<byte> window, int blockStart, int blockEnd)
    {
        Array.Clear(_litStat);
        for (int i = blockStart; i < blockEnd; i++)
        {
            _litStat[window[i]]++;
        }

        FromDistribution(ZstdFormat.LiteralLengthDefaultDistribution, _llStat);
        FromDistribution(ZstdFormat.MatchLengthDefaultDistribution, _mlStat);
        FromDistribution(ZstdFormat.OffsetDefaultDistribution, _ofStat);

        // A dictionary in the Zstandard format starts the frame with its own tables, and the first block can code with them as
        // they stand, so they are what the symbols will cost.
        if (_dictionary is { HasEntropyTables: true } d)
        {
            ReadOnlySpan<byte> lengths = d.HuffmanTable!.CodeLengths;
            int maxBits = d.HuffmanTable.MaxCodeLength;
            for (int i = 0; i < 256; i++)
            {
                int len = i < lengths.Length ? lengths[i] : 0;
                _litStat[i] = len == 0 ? 1 : 1 << (maxBits - len);
            }

            FromCounts(d.LiteralLengthTable!.NormalizedCounts, _llStat);
            FromCounts(d.MatchLengthTable!.NormalizedCounts, _mlStat);
            FromCounts(d.OffsetTable!.NormalizedCounts, _ofStat);
        }
    }

    private static void FromCounts(ReadOnlySpan<short> normalized, int[] stat)
    {
        Array.Clear(stat);
        for (int i = 0; i < stat.Length; i++)
        {
            stat[i] = i < normalized.Length ? Math.Max((int)normalized[i], 1) : 1;
        }
    }

    private static void FromDistribution(short[] distribution, int[] stat)
    {
        Array.Clear(stat);
        for (int i = 0; i < distribution.Length; i++)
        {
            stat[i] = Math.Max((int)distribution[i], 1);
        }
    }

    private static void SymbolPrices(int[] stat, int[] price)
    {
        long total = 0;
        foreach (int c in stat)
        {
            total += c;
        }

        int logTotal = ZstdCost.Log2((uint)Math.Max(total, 1));
        for (int i = 0; i < stat.Length; i++)
        {
            price[i] = logTotal - ZstdCost.Log2((uint)Math.Max(stat[i], 1));
        }
    }

    private static void Mix(int[] stat, int[] count, int[] mix, int[] price)
    {
        for (int i = 0; i < stat.Length; i++)
        {
            mix[i] = (stat[i] >> 1) + count[i];
        }

        SymbolPrices(mix, price);
    }

    // Rebuilds the price tables from the statistics at the start of the block, with the block's counts so far added if asked.
    private void BuildPrices(bool withCounts)
    {
        if (withCounts)
        {
            Mix(_litStat, _litCount, _litMix, _litPrice);
            Mix(_llStat, _llCount, _llMix, _llPrice);
            Mix(_mlStat, _mlCount, _mlMix, _mlPrice);
            Mix(_ofStat, _ofCount, _ofMix, _ofPrice);
        }
        else
        {
            SymbolPrices(_litStat, _litPrice);
            SymbolPrices(_llStat, _llPrice);
            SymbolPrices(_mlStat, _mlPrice);
            SymbolPrices(_ofStat, _ofPrice);
        }

        for (int v = 0; v < _llFull.Length; v++)
        {
            int code = ZstdBlockWriter.LiteralLengthCode((uint)v);
            _llFull[v] = _llPrice[code] + (ZstdFormat.LiteralLengthBits[code] * Bit);
        }

        for (int v = 0; v < _mlFull.Length; v++)
        {
            int code = ZstdBlockWriter.MatchLengthCode((uint)v + 3);
            _mlFull[v] = _mlPrice[code] + (ZstdFormat.MatchLengthBits[code] * Bit);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int LiteralLengthPrice(int literals)
    {
        if ((uint)literals < (uint)_llFull.Length)
        {
            return _llFull[literals];
        }

        int code = ZstdBlockWriter.LiteralLengthCode((uint)literals);
        return _llPrice[code] + (ZstdFormat.LiteralLengthBits[code] * Bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int MatchLengthPrice(int length)
    {
        if ((uint)(length - 3) < (uint)_mlFull.Length)
        {
            return _mlFull[length - 3];
        }

        int code = ZstdBlockWriter.MatchLengthCode((uint)length);
        return _mlPrice[code] + (ZstdFormat.MatchLengthBits[code] * Bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int OffsetPrice(uint offsetValue)
    {
        int code = BitOperations.Log2(offsetValue);
        return _ofPrice[code] + (code * Bit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash(ReadOnlySpan<byte> window, int index) =>
        (int)(((Raw.ReadUInt64(window, index) << _shift) * 0x9E3779B185EBCA87UL) >> (64 - _hashLog));

    private void Parse(
        ReadOnlySpan<byte> window,
        int blockStart,
        int blockEnd,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        Array.Clear(_litCount);
        Array.Clear(_llCount);
        Array.Clear(_mlCount);
        Array.Clear(_ofCount);
        Cell[] cells = _cells;
        int limit = blockEnd - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        int refreshed = blockStart;
        while (ip <= limit)
        {
            int literals = ip - anchor;
            int longest = FindCandidates(window, ip, blockEnd, windowStartPosition, reps, literals);
            if (_candCount == 0)
            {
                ip++;
                continue;
            }

            if (longest >= _sufficient)
            {
                int best = LongestCandidate();
                Emit(window, ref anchor, literals, _candLength[best], _candOffset[best], ref reps, store);
                ip = anchor;
                continue;
            }

            cells[0].Price = LiteralLengthPrice(literals);
            cells[0].MatchLength = 0;
            cells[0].LiteralLength = literals;
            cells[0].Reps = reps;
            int last = 0;
            AddCandidates(0, ref last);

            int endCell;
            int finalLength = 0;
            uint finalOffset = 0;
            for (int cur = 1; ; cur++)
            {
                ref Cell cell = ref cells[cur];

                // Reaching this cell with one more literal may beat the match that reaches it.
                ref Cell previous = ref cells[cur - 1];
                int pending = previous.MatchLength > 0 ? 0 : previous.LiteralLength;
                int literalPrice = previous.Price + _litPrice[Raw.ByteAt(window, ip + cur - 1)] + LiteralLengthPrice(pending + 1) - LiteralLengthPrice(pending);
                if (literalPrice <= cell.Price)
                {
                    cell.Price = literalPrice;
                    cell.MatchLength = 0;
                    cell.LiteralLength = pending + 1;
                }

                if (cur == last)
                {
                    // The stretch ends here; literals at its end stay pending for the next one.
                    endCell = cur;
                    break;
                }

                if (cell.MatchLength > 0)
                {
                    ZstdRepeatOffsets r = cells[cell.From].Reps;
                    r.Apply(cell.Offset, (uint)cell.LiteralLength);
                    cell.Reps = r;
                }
                else
                {
                    cell.Reps = cells[cur - 1].Reps;
                }

                int p = ip + cur;
                if (p > limit)
                {
                    continue;
                }

                // A cell the next position can be reached from nearly as cheaply is not searched (the fast optimal level only).
                if (_prune && cells[cur + 1].Price <= cell.Price + (Bit / 2))
                {
                    continue;
                }

                int pendingLiterals = cell.MatchLength > 0 ? 0 : cell.LiteralLength;
                int longestHere = FindCandidates(window, p, blockEnd, windowStartPosition, cell.Reps, pendingLiterals);
                if (_candCount == 0)
                {
                    continue;
                }

                if (longestHere >= _sufficient || cur + longestHere >= OptNum)
                {
                    int best = LongestCandidate();
                    finalLength = _candLength[best];
                    finalOffset = _candOffset[best];
                    endCell = cur;
                    break;
                }

                AddCandidates(cur, ref last);
            }

            // Write the cheapest path to endCell, then the final long match if there is one.
            int n = 0;
            int pos = endCell;
            while (pos > 0)
            {
                ref Cell c = ref cells[pos];
                if (c.MatchLength == 0)
                {
                    pos--;
                    continue;
                }

                _pathLiterals[n] = c.LiteralLength;
                _pathLength[n] = c.MatchLength;
                _pathOffset[n] = c.Offset;
                n++;
                pos = c.From;
            }

            for (int i = n - 1; i >= 0; i--)
            {
                Emit(window, ref anchor, _pathLiterals[i], _pathLength[i], _pathOffset[i], ref reps, store);
            }

            if (finalLength > 0)
            {
                int pending = cells[endCell].MatchLength > 0 ? 0 : cells[endCell].LiteralLength;
                Emit(window, ref anchor, pending, finalLength, finalOffset, ref reps, store);
                ip = anchor;
            }
            else
            {
                ip += endCell;
            }

            if (anchor - refreshed >= RefreshBytes)
            {
                refreshed = anchor;
                BuildPrices(true);
            }
        }

        ReadOnlySpan<byte> tail = window[anchor..blockEnd];
        store.AppendLiterals(tail);
        foreach (byte b in tail)
        {
            _litCount[b]++;
        }

        store.SetTrackedEnd(reps.Offset1, reps.Offset2, reps.Offset3);
    }

    // The longest candidate (the first of equals: repeat offsets come first and cost least).
    private int LongestCandidate()
    {
        int best = 0;
        for (int i = 1; i < _candCount; i++)
        {
            if (_candLength[i] > _candLength[best])
            {
                best = i;
            }
        }

        return best;
    }

    private void Emit(ReadOnlySpan<byte> window, ref int anchor, int literals, int length, uint offset, ref ZstdRepeatOffsets reps, ZstdSequenceStore store)
    {
        uint value = reps.Apply(offset, (uint)literals);
        store.AppendTracked(window, anchor, (uint)literals, (uint)length, offset, value);
        for (int i = anchor; i < anchor + literals; i++)
        {
            _litCount[Raw.ByteAt(window, i)]++;
        }

        _llCount[ZstdBlockWriter.LiteralLengthCode((uint)literals)]++;
        _mlCount[ZstdBlockWriter.MatchLengthCode((uint)length)]++;
        _ofCount[BitOperations.Log2(value)]++;
        anchor += literals + length;
    }

    // Relaxes the cells ahead of cur with every candidate match. Cells past the old last one start out unreachable.
    private void AddCandidates(int cur, ref int last)
    {
        Cell[] cells = _cells;
        ref Cell from = ref cells[cur];
        int pending = from.MatchLength > 0 ? 0 : from.LiteralLength;
        int basePrice = from.Price + LiteralLengthPrice(0);
        ZstdRepeatOffsets reps = from.Reps;
        for (int k = 0; k < _candCount; k++)
        {
            int end = cur + _candLength[k];
            while (last < end)
            {
                cells[++last].Price = Infinite;
            }
        }

        for (int k = 0; k < _candCount; k++)
        {
            uint offset = _candOffset[k];
            ZstdRepeatOffsets r = reps;
            uint value = r.Apply(offset, (uint)pending);
            int fixedPrice = basePrice + OffsetPrice(value);
            int length = _candLength[k];
            for (int l = Math.Max(_candStart[k], 3); l <= length; l++)
            {
                int price = fixedPrice + MatchLengthPrice(l) + (l == 3 ? ShortMatchPenalty : 0);
                ref Cell target = ref cells[cur + l];
                if (price < target.Price)
                {
                    target.Price = price;
                    target.MatchLength = l;
                    target.LiteralLength = pending;
                    target.From = cur;
                    target.Offset = offset;
                }
            }
        }
    }

    private void AddCandidate(int length, uint offset, int source, int start)
    {
        if (_candCount < CandidateCapacity)
        {
            _candLength[_candCount] = length;
            _candOffset[_candCount] = offset;
            _candSource[_candCount] = source;
            _candStart[_candCount] = start;
            _candCount++;
        }
    }

    // Collects the matches at p into the candidate arrays: repeat offsets (source 0), then tree matches in increasing length
    // (source 1), then dictionary matches (source 2). Returns the longest length. p must not be smaller than any position searched before.
    // Bounds: p + 8 <= end <= window.Length, so eight-byte reads at p are inside the window.
    private int FindCandidates(ReadOnlySpan<byte> window, int p, int end, long windowStartPosition, in ZstdRepeatOffsets reps, int literals)
    {
        _candCount = 0;
        int longest;
        if (literals > 0)
        {
            longest = RepeatCandidate(window, p, end, windowStartPosition, reps.Offset1);
            longest = Math.Max(longest, RepeatCandidate(window, p, end, windowStartPosition, reps.Offset2));
            longest = Math.Max(longest, RepeatCandidate(window, p, end, windowStartPosition, reps.Offset3));
        }
        else
        {
            longest = RepeatCandidate(window, p, end, windowStartPosition, reps.Offset2);
            longest = Math.Max(longest, RepeatCandidate(window, p, end, windowStartPosition, reps.Offset3));
            longest = Math.Max(longest, RepeatCandidate(window, p, end, windowStartPosition, reps.Offset1 - 1));
        }

        CatchUp(window, p, end);
        int budget = _attempts;
        int bestTree = _minMatch - 1;
        TreeWalk(window, p, end, true, ref budget, ref bestTree);
        _next = p + 1;
        longest = Math.Max(longest, bestTree);
        if (_dictionaryIndex is not null && windowStartPosition == 0 && p <= _windowSize)
        {
            // The dictionary is a chain, not a tree, and its best match is often far down it: the frame's own tree takes nothing from
            // this budget, and the walk gets a multiple of the level's attempts. On a payload of a few KB this costs next to nothing.
            budget = _attempts * DictionaryAttemptsFactor;
            longest = Math.Max(longest, SearchDictionary(window, p, end, ref budget, bestTree));
        }

        if (longest >= _sufficient)
        {
            // The tree measured its matches only up to the comparison limit; one that is going to be taken is measured to its
            // end, so that it competes with the repeat-offset matches (measured in full) on its true length.
            for (int i = 0; i < _candCount; i++)
            {
                if (_candSource[i] == 1 && _candLength[i] >= _compareLimit)
                {
                    int length = _candLength[i];
                    uint offset = _candOffset[i];
                    if (offset <= (uint)p && p + length < end)
                    {
                        length += ZstdFastMatchFinder.Count(window, p - (int)offset + length, p + length, end);
                    }

                    _candLength[i] = length;
                    longest = Math.Max(longest, length);
                }
            }
        }

        return longest;
    }

    private int RepeatCandidate(ReadOnlySpan<byte> window, int p, int end, long windowStartPosition, uint rep)
    {
        if (rep == 0 || rep > _windowSize)
        {
            return 0;
        }

        for (int i = 0; i < _candCount; i++)
        {
            if (_candOffset[i] == rep)
            {
                return 0;
            }
        }

        int length;
        if (rep <= (uint)p)
        {
            int c = p - (int)rep;
            uint mask = _repeatMinMatch >= 4 ? 0xFFFFFFFFu : 0x00FFFFFFu;
            if (((Raw.ReadUInt32(window, c) ^ Raw.ReadUInt32(window, p)) & mask) != 0)
            {
                return 0;
            }

            length = ZstdFastMatchFinder.Count(window, c, p, end);
        }
        else
        {
            length = DictionaryRepeatLength(window, p, end, windowStartPosition, rep);
        }

        if (length < _repeatMinMatch)
        {
            return 0;
        }

        AddCandidate(length, rep, 0, _repeatMinMatch);
        return length;
    }

    // A repeat reaching before the frame's first byte lands in the dictionary, which sits right in front of it.
    private int DictionaryRepeatLength(ReadOnlySpan<byte> window, int p, int end, long windowStartPosition, uint rep)
    {
        if (_dictionary is null || windowStartPosition != 0 || p > _windowSize)
        {
            return 0;
        }

        ReadOnlySpan<byte> dictionary = _dictionary.ContentSpan;
        long back = rep - (uint)p;
        if (back > dictionary.Length)
        {
            return 0;
        }

        int d = dictionary.Length - (int)back;
        if (d + 4 > dictionary.Length || Raw.ReadUInt32(dictionary, d) != Raw.ReadUInt32(window, p))
        {
            return 0;
        }

        int length = 4 + ZstdFastMatchFinder.CountDictionary(dictionary, d + 4, window, p + 4, end);
        if (d + length == dictionary.Length && p + length < end)
        {
            // The match runs on from the dictionary into the start of the frame's data.
            length += ZstdFastMatchFinder.Count(window, 0, p + length, end);
        }

        return length;
    }

    private int SearchDictionary(ReadOnlySpan<byte> window, int p, int end, ref int budget, int best)
    {
        ZstdChainDictionaryIndex index = _dictionaryIndex!;
        ReadOnlySpan<byte> dictionary = _dictionary!.ContentSpan;
        uint[] chain = index.Chain;
        int mask = chain.Length - 1;
        int h = (int)(((Raw.ReadUInt64(window, p) << _dictionaryHashShift) * 0x9E3779B185EBCA87UL) >> (64 - _dictionaryHashLog));
        uint candidate = Raw.Get(index.Head, h);
        int low = Math.Max(dictionary.Length - _dictionaryChainSize, 0);
        int longest = 0;
        while (candidate != 0 && budget > 0)
        {
            budget--;
            int d = (int)candidate;
            int length = ZstdFastMatchFinder.CountDictionary(dictionary, d, window, p, end);
            if (d + length == dictionary.Length && p + length < end)
            {
                length += ZstdFastMatchFinder.Count(window, 0, p + length, end);
            }

            if (length >= _minMatch && length > best)
            {
                AddCandidate(length, (uint)(p + dictionary.Length - d), 2, best + 1);
                best = length;
                longest = length;
                if (p + length >= end)
                {
                    break;
                }
            }

            if (d <= low)
            {
                break;
            }

            candidate = Raw.Get(chain, d & mask);
        }

        return longest;
    }

    // Enters the positions between the last entered one and p into the tree without looking for matches.
    private void CatchUp(ReadOnlySpan<byte> window, int p, int end)
    {
        int next = _next;
        int unused = 0;
        while (next < p)
        {
            int budget = _attempts;
            TreeWalk(window, next, end, false, ref budget, ref unused);
            next++;
        }

        _next = next;
    }

    // Enters position p into the binary tree of its hash and, when record is set, adds the matches met on the way down as
    // candidates, in order of increasing length (each longer than all before it and than bestLength on entry). The tree orders the
    // strings starting at its positions; the new node takes the place of the old root and the nodes met are split into those below
    // and above it. When a comparison reaches the longest length the search will use (the end of the block or the comparison
    // limit) the new node takes over the children of the node compared.
    //
    // A node entered within the comparison limit of the end of a block was ordered by fewer bytes than the others, so what its
    // place in the tree implies about common prefixes does not hold; such nodes carry TaintBit in their stored value, and a walk
    // compares them from the first byte and forgets the prefix lengths it has relied on so far. A node that takes over a marked
    // node's children is marked as well.
    private void TreeWalk(ReadOnlySpan<byte> window, int p, int end, bool record, ref int budget, ref int bestLength)
    {
        if (p >= _linkGrowAt)
        {
            GrowLinks(p + 1);
        }

        uint[] head = _head;
        uint[] tree = _links;
        // Bound: p is below the table's current slot count (grown above), and so is every position c < p read from it.
        int mask = (1 << _linkLog) - 1;
        int h = Hash(window, p);
        int maxLength = Math.Min(end - p, _compareLimit);
        uint candidate = Raw.Get(head, h);
        Raw.Set(head, h, maxLength < _compareLimit ? (uint)p | TaintBit : (uint)p);
        int smallSlot = (p & mask) * 2;
        int largeSlot = smallSlot + 1;
        int low = Math.Max(p - mask - 1, 0);
        int smallLength = 0;
        int largeLength = 0;
        long windowSize = _windowSize;
        while (candidate != 0 && budget > 0)
        {
            int c = (int)(candidate & PositionMask);
            if ((long)p - c > windowSize)
            {
                break;
            }

            budget--;
            bool tainted = (candidate & TaintBit) != 0;
            int length = tainted ? 0 : Math.Min(smallLength, largeLength);
            length += ZstdFastMatchFinder.Count(window, c + length, p + length, p + maxLength);
            if (record && length > bestLength)
            {
                AddCandidate(length, (uint)(p - c), 1, bestLength + 1);
                bestLength = length;
            }

            if (c <= low)
            {
                // The node's slot has been reused for a newer position, so its children are not its own: stop here.
                break;
            }

            int node = (c & mask) * 2;
            if (length >= maxLength)
            {
                // The new node takes the old one's place and children. Those children were ordered against the old node, which is
                // only the same as ordering them against this one for the bytes the two have in common; if either was entered with a
                // shortened comparison, that is not all the bytes, so the new root is marked too.
                Raw.Set(tree, smallSlot, Raw.Get(tree, node));
                Raw.Set(tree, largeSlot, Raw.Get(tree, node + 1));
                if (tainted)
                {
                    Raw.Set(head, h, (uint)p | TaintBit);
                }

                return;
            }

            // Bounds: length < maxLength <= end - p, so both reads are inside the block.
            if (Raw.ByteAt(window, c + length) < Raw.ByteAt(window, p + length))
            {
                Raw.Set(tree, smallSlot, candidate);
                smallSlot = node + 1;
                candidate = Raw.Get(tree, smallSlot);
                smallLength = tainted ? 0 : length;
                if (tainted)
                {
                    largeLength = 0;
                }
            }
            else
            {
                Raw.Set(tree, largeSlot, candidate);
                largeSlot = node;
                candidate = Raw.Get(tree, largeSlot);
                largeLength = tainted ? 0 : length;
                if (tainted)
                {
                    smallLength = 0;
                }
            }
        }

        Raw.Set(tree, smallSlot, 0u);
        Raw.Set(tree, largeSlot, 0u);
    }
}
