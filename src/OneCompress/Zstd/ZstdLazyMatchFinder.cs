using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The greedy, lazy, lazy2 and binary-tree lazy2 strategies. Every position is entered into a hash table keyed on
/// <see cref="ZstdCompressionParameters.MinMatch"/> bytes. Greedy, lazy and lazy2 keep, for each hash, a short row of the newest
/// positions with one tag byte per entry, so a search compares sixteen tags at once and touches the data only for entries that
/// probably match; binary-tree lazy2 keeps a binary search tree per hash, ordered by the bytes that follow. A search visits at most
/// <c>1 &lt;&lt; SearchLog</c> candidates and keeps the match with the best gain. The strategies differ in how the parse uses the
/// search: greedy takes the first match found; lazy and lazy2 look one or two positions ahead and defer a match when a later
/// one is clearly better (the classic lazy evaluation of LZ77 parsers, as in deflate's levels 4 to 9).
/// </summary>
/// <remarks>
/// <para>The gain of a match is <c>4 * length - bits(offset)</c> with <c>bits</c> the integer base-2 logarithm, and a repeat-offset
/// match costs one bit: a cheap integer stand-in for what the match saves in the entropy-coded output. A later match must beat the
/// current one by a margin (<see cref="StepMargin"/> per position of delay) to replace it. Everything is integer arithmetic, so
/// the sequences depend only on the input and the parameters.</para>
/// <para>Tables hold window positions as <see langword="uint"/> and 0 means empty, so the first byte of a window is never a
/// candidate. A position is entered only when eight bytes follow it inside the block being parsed; the entry is made by the next
/// search that reaches it, so blocks continue each other seamlessly. A dictionary's search chains are built once per parameter set
/// (see <see cref="ZstdCompressionDictionary"/>); a search that exhausts the frame's own candidates continues into them with the
/// attempts it has left. The binary-tree strategy's own positions form a tree; the dictionary side is searched as chains.</para>
/// <para>The optimal strategies above <see cref="ZstdStrategy.BinaryTreeLazy2"/> have their own finder, <see cref="ZstdOptimalMatchFinder"/>.</para>
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdLazyMatchFinder : ZstdMatchFinder
{
    // The parse stops looking ahead once a match is this long: a longer one found a position later could add little.
    private const int LongEnough = 128;

    // Gain a match found one (two) positions later must have over the current match to replace it.
    private const int StepMargin = 4;
    private const int StepMargin2 = 7;

    // A match at the first repeat offset costs about one bit instead of the log of its offset.
    private const int RepeatCost = 1;

    // With nothing found the step grows by one for every 2^SkipShift bytes since the last match.
    private const int SkipShift = 8;

    // The greedy strategy steps faster over stretches with no match, and looks only at the newest repeat offset at each position: it has no
    // lookahead to recover from a poor choice, and its levels are the ones that trade a little size for speed.
    private const int GreedySkipShift = 6;

    // Positions the parse skips over (inside a match, or before a later search) are entered into the tree without looking for matches,
    // and that is most of the tree's work: a match is usually a repeat of text that is already in the tree. So when more than
    // TreeDenseTail positions lie between the last one entered and the next search, only every TreeInsertStride-th of them is entered
    // (the last TreeDenseTail always are), and these insertions walk at most TreeInsertAttempts nodes. A later match into skipped text
    // starts at most TreeInsertStride - 1 bytes late and is extended backwards over the literals before it. About a quarter less time
    // for 0.2% to 0.5% more output at levels 13 to 15.
    private const int TreeInsertStride = 8;
    private const int TreeDenseTail = 16;

    // A level that searches at most 16 nodes spends relatively more of its time on these insertions, so it keeps fewer positions dense.
    private const int ShallowAttempts = 16;
    private const int TreeDenseTailShallow = 8;
    private const int TreeInsertAttempts = 16;

    // Sources under 1 MiB are small enough to keep every position: the saving there is little and the loss counts more.
    private const int ThinInsertsMinWindowLog = 21;
    private bool _thinInserts;

    // Marks a stored tree position that was entered with a shortened comparison length (see TreeWalk).
    private const uint TaintBit = 0x80000000u;
    private const uint PositionMask = 0x7FFFFFFFu;

    private byte[] _tags = [];
    private uint[] _slots = [];
    private byte[] _heads = [];
    private int _rowLog;
    private int _rowBits;
    private int _rowMask;
    private uint[] _head = [];
    private uint[] _links = [];
    private uint[] _scratch = [];
    private int _hashLog;
    private int _linkLog;
    private int _shift;
    private int _minMatch;
    private int _attempts;
    private int _depth;
    private int _nice;
    private bool _tree;
    private long _windowSize;
    private int _next;
    private bool _flat;
    private bool _warmRows;
    private int _warmed;
    private int _stride = 1;
    private const int WarmDistance = 8;

    // Positions inside a long match are entered every InsertStride positions, except the last DenseTail before the next search, which
    // are all entered (see InsertStride).
    private const int DenseTail = 16;

    // Only tables of 2^19 entries and up (2 MiB of positions) are far enough from the cache for the entering of positions to matter.
    private const int StrideMinHashLog = 19;

    // Tables of 2^20 entries (4 MiB of positions) and up are too big for the cache, and loading rows ahead pays; below that it costs more than it saves.
    private const int WarmMinHashLog = 20;

    // The tree is walked by following links from node to node, each at an unrelated place in tables and a window far larger than
    // the cache, so a walk waits for one memory round trip per node. Three hints start those loads early (they change no value):
    // the head slot of a position HeadWarmDistance ahead (its hash needs only the window), the first node and its link pair of a
    // position RootWarmDistance ahead (their address is in the head slot warmed earlier), and each node's link pair before the
    // window bytes compared with it are loaded, so the two misses overlap. Tables under 2^18 entries (1 MiB) stay in the cache
    // and are left alone.
    private const int HeadWarmDistance = 16;
    private const int RootWarmDistance = 8;
    private const int TreeWarmMinHashLog = 18;
    private bool _warmTree;
    private bool _touchTree;
    private uint _touchSink;
    private int _rowShift;
    private int _tagShift;
    private ZstdCompressionDictionary? _dictionary;
    private ZstdChainDictionaryIndex? _dictionaryIndex;
    private int _dictionaryHashShift;
    private int _dictionaryHashLog;
    private int _dictionaryChainSize;

    /// <inheritdoc />
    public override void BeginFrame(ZstdCompressionParameters parameters, ZstdCompressionDictionary? dictionary)
    {
        if (parameters.Strategy < ZstdStrategy.Greedy)
        {
            throw new ArgumentException("This match finder implements the Greedy, Lazy, Lazy2 and BinaryTreeLazy2 strategies.", nameof(parameters));
        }

        _tree = parameters.Strategy >= ZstdStrategy.BinaryTreeLazy2;
        _thinInserts = parameters.WindowLog >= ThinInsertsMinWindowLog;
        _depth = parameters.Strategy switch
        {
            ZstdStrategy.Greedy => 0,
            ZstdStrategy.Lazy => 1,
            _ => 2,
        };
        _minMatch = Math.Clamp(parameters.MinMatch, 3, 8);
        _shift = 64 - (8 * _minMatch);
        _hashLog = Math.Clamp(parameters.HashLog, 6, 26);
        _attempts = 1 << Math.Clamp(parameters.SearchLog, 0, 12);
        _nice = 256;
        _windowSize = parameters.WindowSize;
        _next = 1;
        if (_tree)
        {
            _linkLog = Math.Clamp(parameters.ChainLog - 1, 4, 25);
            Prepare(ref _head, 1 << _hashLog);
            Prepare(ref _links, 2 << _linkLog);
        }
        else
        {
            // Rows of 16, 32 or 64 entries, as wide as the search is deep; the table holds 2^hashLog entries in all.
            _rowLog = Math.Clamp(parameters.SearchLog, 4, 6);
            _rowMask = (1 << _rowLog) - 1;
            _rowBits = Math.Max(_hashLog - _rowLog, 1);
            _rowShift = 64 - _rowBits;
            _tagShift = 56 - _rowBits;
            _attempts = Math.Min(_attempts, 1 << _rowLog);
            int rows = 1 << _rowBits;
            Prepare(ref _tags, rows << _rowLog);
            Prepare(ref _slots, rows << _rowLog);
            Prepare(ref _heads, rows);
        }

        _dictionary = dictionary;
        _dictionaryIndex = null;
        _flat = !_tree && dictionary is null && _minMatch >= 4;
        _warmed = 0;
        _stride = !_tree && dictionary is null && _minMatch >= 4 && _rowLog == 4 && _hashLog >= StrideMinHashLog ? Math.Max(InsertStride, 1) : 1;
        _warmTree = Raw.CanWarm && _tree && _hashLog >= TreeWarmMinHashLog;
        _touchTree = Raw.CanTouch && _tree && _hashLog >= TreeWarmMinHashLog;
        _warmRows = Raw.CanWarm && !_tree && _hashLog >= WarmMinHashLog;
        if (dictionary is not null && dictionary.Content.Length >= 8)
        {
            _dictionaryHashLog = parameters.DictionaryHashLog != 0 ? Math.Clamp(parameters.DictionaryHashLog, 6, 26) : _hashLog;
            int chainLog = Math.Clamp(parameters.DictionaryChainLog != 0 ? parameters.DictionaryChainLog : parameters.ChainLog, 4, 25);
            _dictionaryChainSize = 1 << chainLog;
            _dictionaryHashShift = _shift;
            _dictionaryIndex = dictionary.GetChainIndex(_minMatch, _dictionaryHashLog, chainLog);
        }
    }

    /// <summary>
    /// How many positions apart the positions skipped over by a match are entered, once more than 16 lie between the last one entered and
    /// the next search; the last 16 are always all entered. A long match is usually a repeat of text that is already in the table, and every
    /// position entered costs a write to a row far from the cache, so entering one in four loses almost no match and saves most of that
    /// work. 1 enters every position. Applies to row strategies with 16-entry rows, no dictionary and a table of 2^19 entries or more;
    /// set before <see cref="BeginFrame"/>.
    /// </summary>
    internal int InsertStride { get; set; } = 4;

    private static void Prepare(ref byte[] table, int size)
    {
        if (table.Length < size)
        {
            table = new byte[size];
        }
        else
        {
            Array.Clear(table, 0, size);
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
    /// <remarks>
    /// The rows and the tree enter positions lazily: a search first enters every position since the last one entered. Priming therefore
    /// moves that mark up to <paramref name="firstPosition"/>, and the next search enters the positions from there on, with the
    /// block's own end as the bound as for any position. Positions before the mark are never entered.
    /// </remarks>
    public override void Prime(ReadOnlySpan<byte> window, int firstPosition, int endPosition)
    {
        _next = Math.Max(_next, Math.Max(firstPosition, 1));
    }

    /// <inheritdoc />
    public override void Slide(int delta)
    {
        uint d = (uint)delta;
        _next = Math.Max(1, _next - delta);
        _warmed = Math.Max(0, _warmed - delta);
        if (!_tree)
        {
            // Rows are rings of positions; only the positions change.
            uint[] slots = _slots;
            int count = 1 << (_rowBits + _rowLog);
            for (int i = 0; i < count; i++)
            {
                slots[i] = Rebase(slots[i], d);
            }

            return;
        }

        uint[] head = _head;
        int headSize = 1 << _hashLog;
        for (int i = 0; i < headSize; i++)
        {
            head[i] = Rebase(head[i], d);
        }

        // A node is stored at the slot of its position modulo the table size, so moving every position down by delta turns
        // the table around by delta slots as well.
        int slotCount = 1 << _linkLog;
        int size = slotCount * 2;
        if (_scratch.Length < size)
        {
            _scratch = new uint[size];
        }

        Array.Copy(_links, _scratch, size);
        int rotation = (int)(d & (uint)(slotCount - 1));
        for (int i = 0; i < slotCount; i++)
        {
            int source = ((i + rotation) & (slotCount - 1)) * 2;
            _links[i * 2] = Rebase(_scratch[source], d);
            _links[(i * 2) + 1] = Rebase(_scratch[source + 1], d);
        }
    }

    // Moves a stored position down by d; positions that fall off the start become empty. The tree's taint mark is kept.
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

        ZstdRepeatOffsets reps = repeatOffsets;
        int limit = blockEnd - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        while (ip <= limit)
        {
            int length = _flat
                ? SearchFlat(window, ip, blockEnd, anchor, reps, out uint offset, out int gain)
                : Search(window, ip, blockEnd, windowStartPosition, anchor, reps, out offset, out gain);
            int start = ip;
            if (_depth == 0 && ip < limit)
            {
                // Greedy parsing has no lookahead, but a repeat of the last offset one byte on is nearly free and worth taking.
                int repeatLength = length;
                uint repeatOffset = offset;
                int repeatGain = length == 0 ? int.MinValue : gain;
                int before = repeatGain;
                Repeat(window, ip + 1, blockEnd, windowStartPosition, reps.Offset1, ref repeatLength, ref repeatOffset, ref repeatGain);
                if (repeatGain != before)
                {
                    start = ip + 1;
                    length = repeatLength;
                    offset = repeatOffset;
                    gain = repeatGain;
                }
            }

            if (length == 0)
            {
                ip += 1 + ((ip - anchor) >> (_depth == 0 ? GreedySkipShift : SkipShift));
                continue;
            }

            while (_depth > 0 && length < LongEnough)
            {
                int p1 = start + 1;
                if (p1 > limit)
                {
                    break;
                }

                int length1 = _flat
                    ? SearchFlat(window, p1, blockEnd, anchor, reps, out uint offset1, out int gain1)
                    : Search(window, p1, blockEnd, windowStartPosition, anchor, reps, out offset1, out gain1);
                if (length1 > 0 && gain1 > gain + StepMargin)
                {
                    start = p1;
                    length = length1;
                    offset = offset1;
                    gain = gain1;
                    continue;
                }

                if (_depth >= 2)
                {
                    int p2 = start + 2;
                    if (p2 > limit)
                    {
                        break;
                    }

                    int length2 = _flat
                        ? SearchFlat(window, p2, blockEnd, anchor, reps, out uint offset2, out int gain2)
                        : Search(window, p2, blockEnd, windowStartPosition, anchor, reps, out offset2, out gain2);
                    if (length2 > 0 && gain2 > gain + StepMargin2)
                    {
                        start = p2;
                        length = length2;
                        offset = offset2;
                        gain = gain2;
                        continue;
                    }
                }

                break;
            }

            // Extend backwards over literals that the match also covers (not into the dictionary, whose bytes are not in the window).
            int source = start - (int)offset;
            while (start > anchor && source > 0 && Raw.ByteAt(window, start - 1) == Raw.ByteAt(window, source - 1))
            {
                start--;
                source--;
                length++;
            }

            uint literalLength = (uint)(start - anchor);
            uint offsetValue = reps.Apply(offset, literalLength);
            store.AppendTracked(window, anchor, literalLength, (uint)length, offset, offsetValue);
            store.SetTrackedEnd(reps.Offset1, reps.Offset2, reps.Offset3);
            ip = start + length;
            anchor = ip;
        }

        store.AppendLiterals(window[anchor..blockEnd]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Hash(ReadOnlySpan<byte> window, int index) =>
        (int)(((Raw.ReadUInt64(window, index) << _shift) * 0x9E3779B185EBCA87UL) >> (64 - _hashLog));

    // Search for the common case of the row strategies without a dictionary. It finds exactly what Search finds, with less work per
    // position: the best match lives in locals rather than in variables passed by reference, the eight bytes at p are read once for
    // the hash and the repeat-offset checks, the table shifts are precomputed, and there are no dictionary or tree branches. Rows far
    // larger than the cache are loaded a few positions ahead (see WarmRowsAhead).
    // Bounds: p + 8 <= end <= window.Length, so eight-byte reads at p are inside the window; every candidate position is below p.
    private int SearchFlat(ReadOnlySpan<byte> window, int p, int end, int anchor, in ZstdRepeatOffsets reps, out uint offset, out int gain)
    {
        ulong word = Raw.ReadUInt64(window, p);
        uint first = (uint)word;
        long windowSize = _windowSize;
        uint repeatLimit = (uint)Math.Min(windowSize, p);

        // Repeat offsets in the order Search tries them (RFC 8878 section 3.1.1.5 for what the codes mean when no literals precede p).
        uint rep0, rep1, rep2;
        if (p > anchor)
        {
            rep0 = reps.Offset1;
            rep1 = reps.Offset2;
            rep2 = reps.Offset3;
        }
        else
        {
            rep0 = reps.Offset2;
            rep1 = reps.Offset3;
            rep2 = reps.Offset1 - 1;
        }

        int bestLength = RepeatLengthFlat(window, p, first, rep0, repeatLimit, end);
        uint bestOffset = bestLength > 0 ? rep0 : 0;
        int bestGain = bestLength > 0 ? (4 * bestLength) - RepeatCost : int.MinValue;
        int length;
        if (_depth > 0)
        {
            length = RepeatLengthFlat(window, p, first, rep1, repeatLimit, end);
            if (length > 0 && (4 * length) - RepeatCost > bestGain)
            {
                bestGain = (4 * length) - RepeatCost;
                bestLength = length;
                bestOffset = rep1;
            }

            length = RepeatLengthFlat(window, p, first, rep2, repeatLimit, end);
            if (length > 0 && (4 * length) - RepeatCost > bestGain)
            {
                bestGain = (4 * length) - RepeatCost;
                bestLength = length;
                bestOffset = rep2;
            }
        }

        if (_warmRows)
        {
            WarmRowsAhead(window, p, end);
        }

        CatchUpRows(window, p);
        ulong hash = (word << _shift) * 0x9E3779B185EBCA87UL;
        int row = (int)(hash >> _rowShift);
        byte tag = (byte)(hash >> _tagShift);
        int rowBase = row << _rowLog;
        int head = Raw.Get(_heads, row);
        int size = _rowMask + 1;
        ulong equal = 0;
        for (int i = 0; i < size; i += 16)
        {
            equal |= (ulong)Raw.EqualMask16(_tags, rowBase + i, tag) << i;
        }

        // Bit 0 of the rotated mask is the head, the newest entry.
        ulong all = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
        ulong mask = ((equal >> head) | (equal << ((size - head) & 63))) & all;
        int searched = Math.Max(bestLength, _minMatch - 1);
        if (mask != 0 && p + searched < end)
        {
            // Entries run from the newest to the oldest, so the first one that is empty (0) or beyond the window ends the search.
            uint oldest = (uint)Math.Max(p - windowSize, 1);
            uint[] slots = _slots;
            int rowMask = _rowMask;
            int remaining = _attempts;
            uint want = Raw.ReadUInt32(window, p + searched - 3);
            do
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                uint candidate = Raw.Get(slots, rowBase + ((head + k) & rowMask));
                if (candidate < oldest)
                {
                    break;
                }

                int c = (int)candidate;

                // A match longer than `searched` agrees with p on the four bytes that end at index `searched`; most candidates fail this.
                if (Raw.ReadUInt32(window, c + searched - 3) == want)
                {
                    length = ZstdFastMatchFinder.Count(window, c, p, end);
                    if (length > searched)
                    {
                        uint distance = (uint)(p - c);
                        int g = (4 * length) - BitOperations.Log2(distance);
                        if (g > bestGain)
                        {
                            bestGain = g;
                            bestLength = length;
                            bestOffset = distance;
                            searched = length;
                            if (p + searched >= end)
                            {
                                break;
                            }

                            want = Raw.ReadUInt32(window, p + searched - 3);
                        }
                    }
                }
            }
            while (mask != 0 && --remaining > 0);
        }

        InsertRow(hash, p);
        _next = p + 1;
        offset = bestOffset;
        gain = bestGain;
        return bestLength;
    }

    // The length of the repeat-offset match at p for offset rep, or 0: the offset must be between 1 and the limit (the smaller of the
    // window and p; with no dictionary a repeat cannot reach before the first byte) and the bytes must agree on the first four.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RepeatLengthFlat(ReadOnlySpan<byte> window, int p, uint first, uint rep, uint limit, int end)
    {
        if (rep - 1 >= limit)
        {
            return 0;
        }

        int c = p - (int)rep;
        if (Raw.ReadUInt32(window, c) != first)
        {
            return 0;
        }

        return 4 + ZstdFastMatchFinder.Count(window, c + 4, p + 4, end);
    }

    // The best match at p, or 0. Positions up to p are entered into the tables; p must not be smaller than any position searched before.
    // Bounds: p + 8 <= end <= window.Length, so eight-byte reads at p are inside the window.
    private int Search(
        ReadOnlySpan<byte> window,
        int p,
        int end,
        long windowStartPosition,
        int anchor,
        in ZstdRepeatOffsets reps,
        out uint offset,
        out int gain)
    {
        int bestLength = 0;
        uint bestOffset = 0;
        int bestGain = int.MinValue;
        long windowSize = _windowSize;

        // Repeat offsets cost almost nothing to code. With literals before the match the three codes mean the three offsets; with none
        // they mean the second, the third and the first minus one (RFC 8878 section 3.1.1.5).
        if (p > anchor)
        {
            Repeat(window, p, end, windowStartPosition, reps.Offset1, ref bestLength, ref bestOffset, ref bestGain);
            Repeat(window, p, end, windowStartPosition, reps.Offset2, ref bestLength, ref bestOffset, ref bestGain);
            Repeat(window, p, end, windowStartPosition, reps.Offset3, ref bestLength, ref bestOffset, ref bestGain);
        }
        else
        {
            Repeat(window, p, end, windowStartPosition, reps.Offset2, ref bestLength, ref bestOffset, ref bestGain);
            Repeat(window, p, end, windowStartPosition, reps.Offset3, ref bestLength, ref bestOffset, ref bestGain);
            Repeat(window, p, end, windowStartPosition, reps.Offset1 - 1, ref bestLength, ref bestOffset, ref bestGain);
        }

        int budget = _attempts;
        int minimum = _minMatch;
        if (!_tree)
        {
            CatchUpRows(window, p);
            SearchRow(window, p, end, ref budget, ref bestLength, ref bestOffset, ref bestGain);
            _next = p + 1;
        }
        else
        {
            CatchUpTree(window, p, end);
            TreeWalk(window, p, end, true, ref budget, ref bestLength, ref bestOffset, ref bestGain);
            _next = p + 1;
            if (bestLength > 0 && bestLength >= Math.Min(_nice, end - p) && bestOffset <= (uint)p)
            {
                // The tree compares no further than the nice length; the parse wants the whole match.
                int extra = ZstdFastMatchFinder.Count(window, p - (int)bestOffset + bestLength, p + bestLength, end);
                if (extra > 0)
                {
                    bestLength += extra;
                    bestGain += 4 * extra;
                }
            }
        }

        if (_dictionaryIndex is not null && budget > 0 && windowStartPosition == 0 && p <= windowSize)
        {
            SearchDictionary(window, p, end, ref budget, ref bestLength, ref bestOffset, ref bestGain);
        }

        offset = bestOffset;
        gain = bestGain;
        return bestLength;
    }

    // A repeat-offset match at p. A repeat reaching before the frame's first byte lands in the dictionary, which sits right in front of it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Repeat(ReadOnlySpan<byte> window, int p, int end, long windowStartPosition, uint rep, ref int bestLength, ref uint bestOffset, ref int bestGain)
    {
        // Most positions have no repeat match: reject those without leaving the caller.
        if (rep == 0 || rep > _windowSize || (rep <= (uint)p && Raw.ReadUInt32(window, p - (int)rep) != Raw.ReadUInt32(window, p)))
        {
            return;
        }

        RepeatLength(window, p, end, windowStartPosition, rep, ref bestLength, ref bestOffset, ref bestGain);
    }

    private void RepeatLength(ReadOnlySpan<byte> window, int p, int end, long windowStartPosition, uint rep, ref int bestLength, ref uint bestOffset, ref int bestGain)
    {
        if (rep == 0 || rep > _windowSize)
        {
            return;
        }

        int length;
        if (rep <= (uint)p)
        {
            int c = p - (int)rep;
            if (Raw.ReadUInt32(window, c) != Raw.ReadUInt32(window, p))
            {
                return;
            }

            length = 4 + ZstdFastMatchFinder.Count(window, c + 4, p + 4, end);
        }
        else
        {
            length = DictionaryRepeatLength(window, p, end, windowStartPosition, rep);
            if (length == 0)
            {
                return;
            }
        }

        int g = (4 * length) - RepeatCost;
        if (g > bestGain)
        {
            bestGain = g;
            bestLength = length;
            bestOffset = rep;
        }
    }

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

    private void SearchDictionary(ReadOnlySpan<byte> window, int p, int end, ref int budget, ref int bestLength, ref uint bestOffset, ref int bestGain)
    {
        ZstdChainDictionaryIndex index = _dictionaryIndex!;
        ReadOnlySpan<byte> dictionary = _dictionary!.ContentSpan;
        uint[] chain = index.Chain;
        int mask = chain.Length - 1;
        int h = (int)(((Raw.ReadUInt64(window, p) << _dictionaryHashShift) * 0x9E3779B185EBCA87UL) >> (64 - _dictionaryHashLog));
        uint candidate = Raw.Get(index.Head, h);
        int low = Math.Max(dictionary.Length - _dictionaryChainSize, 0);
        while (candidate != 0 && budget > 0)
        {
            budget--;
            int d = (int)candidate;
            int length = ZstdFastMatchFinder.CountDictionary(dictionary, d, window, p, end);
            if (d + length == dictionary.Length && p + length < end)
            {
                length += ZstdFastMatchFinder.Count(window, 0, p + length, end);
            }

            if (length >= _minMatch)
            {
                uint distance = (uint)(p + dictionary.Length - d);
                int g = (4 * length) - BitOperations.Log2(distance);
                if (g > bestGain)
                {
                    bestGain = g;
                    bestLength = length;
                    bestOffset = distance;
                }
            }

            if (d <= low)
            {
                break;
            }

            candidate = Raw.Get(chain, d & mask);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong RowHash(ReadOnlySpan<byte> window, int index) => (Raw.ReadUInt64(window, index) << _shift) * 0x9E3779B185EBCA87UL;

    // Enters position q into its row: the newest entry goes in front of the older ones (the row is a ring, its head moves down by one).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertRow(ulong hash, int q)
    {
        int row = (int)(hash >> (64 - _rowBits));
        int slot = (row << _rowLog) + ((Raw.Get(_heads, row) - 1) & _rowMask);
        Raw.Set(_heads, row, (byte)(slot & _rowMask));
        Raw.Set(_tags, slot, (byte)(hash >> (56 - _rowBits)));
        Raw.Set(_slots, slot, (uint)q);
    }

    // Enters the positions between the last entered one and p: those skipped over by a match or a step.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CatchUpRows(ReadOnlySpan<byte> window, int p)
    {
        int q = _next;
        if (q >= p)
        {
            return;
        }

        byte[] heads = _heads;
        byte[] tags = _tags;
        uint[] slots = _slots;
        int shift = _shift;
        int rowShift = _rowShift;
        int tagShift = _tagShift;
        int rowLog = _rowLog;
        int rowMask = _rowMask;
        int stride = _stride;
        do
        {
            ulong hash = (Raw.ReadUInt64(window, q) << shift) * 0x9E3779B185EBCA87UL;
            int row = (int)(hash >> rowShift);
            int slot = (row << rowLog) + ((Raw.Get(heads, row) - 1) & rowMask);
            Raw.Set(heads, row, (byte)(slot & rowMask));
            Raw.Set(tags, slot, (byte)(hash >> tagShift));
            Raw.Set(slots, slot, (uint)q);
        }
        while ((q += p - q > DenseTail ? stride : 1) < p);
    }

    // Brings the table lines of the rows of the next few positions into the cache before they are searched or filled in: the rows are
    // at random places in tables far larger than the cache, so a row first touched at its search costs the whole memory latency.
    // Positions are warmed once, in order, WarmDistance ahead of the one being handled.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WarmRowsAhead(ReadOnlySpan<byte> window, int p, int end)
    {
        int to = Math.Min(p + WarmDistance, end - 8);
        int q = Math.Max(_warmed, Math.Max(_next, p - 32));
        for (; q <= to; q++)
        {
            int row = (int)(((Raw.ReadUInt64(window, q) << _shift) * 0x9E3779B185EBCA87UL) >> _rowShift);
            int at = row << _rowLog;
            Raw.Warm(_tags, at);
            Raw.Warm(_heads, row);
            Raw.Warm(_slots, at);
            Raw.Warm(_slots, at + _rowMask);
        }

        _warmed = q;
    }

    // Visits the entries of p's row whose tag equals p's, newest first, and enters p. The tags are a filter: one byte per entry, compared
    // sixteen at a time, so a search touches the window only for entries that probably match. The mask of equal tags is the same
    // whether it is computed with vectors or without.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SearchRow(ReadOnlySpan<byte> window, int p, int end, ref int budget, ref int bestLength, ref uint bestOffset, ref int bestGain)
    {
        ulong hash = RowHash(window, p);
        int row = (int)(hash >> (64 - _rowBits));
        byte tag = (byte)(hash >> (56 - _rowBits));
        int rowBase = row << _rowLog;
        int head = Raw.Get(_heads, row);
        int size = _rowMask + 1;
        ulong equal = 0;
        for (int i = 0; i < size; i += 16)
        {
            equal |= (ulong)Raw.EqualMask16(_tags, rowBase + i, tag) << i;
        }

        // Bit 0 of the rotated mask is the head, the newest entry.
        ulong all = size == 64 ? ulong.MaxValue : (1UL << size) - 1;
        ulong mask = ((equal >> head) | (equal << ((size - head) & 63))) & all;
        uint[] slots = _slots;
        int searched = Math.Max(bestLength, _minMatch - 1);

        // Entries run from the newest to the oldest, so the first one that is empty (0) or beyond the window ends the search.
        uint oldest = (uint)Math.Max(p - _windowSize, 1);
        int remaining = budget;
        if (p + searched < end)
        {
            while (mask != 0 && remaining > 0)
            {
                int k = BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                uint candidate = Raw.Get(slots, rowBase + ((head + k) & _rowMask));
                if (candidate < oldest)
                {
                    break;
                }

                remaining--;
                int c = (int)candidate;

                // A match longer than `searched` agrees with p on the four bytes that end at index `searched`; most candidates fail this.
                if (searched >= 3
                    ? Raw.ReadUInt32(window, c + searched - 3) == Raw.ReadUInt32(window, p + searched - 3)
                    : Raw.ByteAt(window, c + searched) == Raw.ByteAt(window, p + searched))
                {
                    int length = ZstdFastMatchFinder.Count(window, c, p, end);
                    if (length > searched)
                    {
                        uint distance = (uint)(p - c);
                        int g = (4 * length) - BitOperations.Log2(distance);
                        if (g > bestGain)
                        {
                            bestGain = g;
                            bestLength = length;
                            bestOffset = distance;
                            searched = length;
                            if (p + searched >= end)
                            {
                                break;
                            }
                        }
                    }
                }
            }
        }

        budget = remaining;
        InsertRow(hash, p);
    }

    // Enters the positions between the last entered one and p into the tree without looking for matches.
    private void CatchUpTree(ReadOnlySpan<byte> window, int p, int end)
    {
        int next = _next;
        int insertAttempts = _thinInserts ? Math.Min(_attempts, TreeInsertAttempts) : _attempts;
        int stride = _thinInserts ? TreeInsertStride : 1;
        int denseTail = _attempts <= ShallowAttempts ? TreeDenseTailShallow : TreeDenseTail;
        while (next < p)
        {
            InsertWalk(window, next, end, insertAttempts);
            next += p - next > denseTail ? stride : 1;
        }

        _next = next;
    }

    // Enters position p into the binary tree of its hash and, when record is set, reports the matches met on the way down in
    // order of increasing length. The tree orders the strings starting at its positions; the new node takes the place of the
    // old root and the nodes met are split into those below and above it. When a comparison reaches the longest length the
    // search will use (the end of the block or the nice length) the new node takes over the children of the node compared.
    //
    // A node entered within the nice length of the end of a block was ordered by fewer bytes than the others, so what its
    // place in the tree implies about common prefixes does not hold; such nodes carry TaintBit in their stored value, and a walk
    // compares them from the first byte and forgets the prefix lengths it has relied on so far. A node that takes over a marked
    // node's children is marked as well.
    private void TreeWalk(ReadOnlySpan<byte> window, int p, int end, bool record, ref int budget, ref int bestLength, ref uint bestOffset, ref int bestGain)
    {
        uint[] head = _head;
        uint[] tree = _links;
        int mask = (1 << _linkLog) - 1;
        int h = Hash(window, p);
        int maxLength = Math.Min(end - p, _nice);
        if (_touchTree)
        {
            // Same positions as the prefetch below, for targets that have none: a plain load, kept in a field so it stays.
            // Bounds: as below, and a table index is a hash or a masked position.
            uint touched = 0;
            if (p + HeadWarmDistance <= end - 8)
            {
                touched = Raw.Touch(head, Hash(window, p + HeadWarmDistance));
            }

            if (p + RootWarmDistance <= end - 8)
            {
                int root = (int)(Raw.Get(head, Hash(window, p + RootWarmDistance)) & PositionMask);
                if (root != 0)
                {
                    touched += Raw.Touch(tree, (root & mask) * 2);
                }
            }

            _touchSink = touched;
        }

        if (_warmTree)
        {
            // Bounds: the hash reads eight bytes, so q + 8 <= end; a stored position is inside the window and below p.
            if (p + HeadWarmDistance <= end - 8)
            {
                Raw.Warm(head, Hash(window, p + HeadWarmDistance));
            }

            if (p + RootWarmDistance <= end - 8)
            {
                int root = (int)(Raw.Get(head, Hash(window, p + RootWarmDistance)) & PositionMask);
                if (root != 0)
                {
                    Raw.Warm(tree, (root & mask) * 2);
                    Raw.WarmAt(window, root);
                }
            }
        }

        uint candidate = Raw.Get(head, h);
        Raw.Set(head, h, maxLength < _nice ? (uint)p | TaintBit : (uint)p);
        int smallSlot = (p & mask) * 2;
        int largeSlot = smallSlot + 1;
        int low = Math.Max(p - mask - 1, 0);
        int smallLength = 0;
        int largeLength = 0;
        int minimum = _minMatch;
        long windowSize = _windowSize;
        while (candidate != 0 && budget > 0)
        {
            int c = (int)(candidate & PositionMask);
            if ((long)p - c > windowSize)
            {
                break;
            }

            budget--;
            if (_warmTree)
            {
                Raw.Warm(tree, (c & mask) * 2);
            }

            bool tainted = (candidate & TaintBit) != 0;
            int length = tainted ? 0 : Math.Min(smallLength, largeLength);
            length += ZstdFastMatchFinder.Count(window, c + length, p + length, p + maxLength);
            if (record && length >= minimum)
            {
                uint distance = (uint)(p - c);
                int g = (4 * length) - BitOperations.Log2(distance);
                if (g > bestGain)
                {
                    bestGain = g;
                    bestLength = length;
                    bestOffset = distance;
                }
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

    // TreeWalk without the search: enters position p and reports nothing, so no result is kept in memory between nodes.
    private void InsertWalk(ReadOnlySpan<byte> window, int p, int end, int budget)
    {
        uint[] head = _head;
        uint[] tree = _links;
        int mask = (1 << _linkLog) - 1;
        int h = Hash(window, p);
        int maxLength = Math.Min(end - p, _nice);
        if (_touchTree)
        {
            // Same positions as the prefetch below, for targets that have none: a plain load, kept in a field so it stays.
            // Bounds: as below, and a table index is a hash or a masked position.
            uint touched = 0;
            if (p + HeadWarmDistance <= end - 8)
            {
                touched = Raw.Touch(head, Hash(window, p + HeadWarmDistance));
            }

            if (p + RootWarmDistance <= end - 8)
            {
                int root = (int)(Raw.Get(head, Hash(window, p + RootWarmDistance)) & PositionMask);
                if (root != 0)
                {
                    touched += Raw.Touch(tree, (root & mask) * 2);
                }
            }

            _touchSink = touched;
        }

        if (_warmTree)
        {
            // Bounds: the hash reads eight bytes, so q + 8 <= end; a stored position is inside the window and below p.
            if (p + HeadWarmDistance <= end - 8)
            {
                Raw.Warm(head, Hash(window, p + HeadWarmDistance));
            }

            if (p + RootWarmDistance <= end - 8)
            {
                int root = (int)(Raw.Get(head, Hash(window, p + RootWarmDistance)) & PositionMask);
                if (root != 0)
                {
                    Raw.Warm(tree, (root & mask) * 2);
                    Raw.WarmAt(window, root);
                }
            }
        }

        uint candidate = Raw.Get(head, h);
        Raw.Set(head, h, maxLength < _nice ? (uint)p | TaintBit : (uint)p);
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
            if (_warmTree)
            {
                Raw.Warm(tree, (c & mask) * 2);
            }

            bool tainted = (candidate & TaintBit) != 0;
            int length = tainted ? 0 : Math.Min(smallLength, largeLength);
            length += ZstdFastMatchFinder.Count(window, c + length, p + length, p + maxLength);
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
