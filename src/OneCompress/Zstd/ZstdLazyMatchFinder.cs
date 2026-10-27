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
/// <para>Strategies above <see cref="ZstdStrategy.BinaryTreeLazy2"/> are accepted and parsed as binary-tree lazy2 until their
/// optimal parsers exist.</para>
/// </remarks>
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
            _attempts = Math.Min(_attempts, 1 << _rowLog);
            int rows = 1 << _rowBits;
            Prepare(ref _tags, rows << _rowLog);
            Prepare(ref _slots, rows << _rowLog);
            Prepare(ref _heads, rows);
        }

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
            int length = Search(window, ip, blockEnd, windowStartPosition, anchor, reps, out uint offset, out int gain);
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
                ip += 1 + ((ip - anchor) >> SkipShift);
                continue;
            }

            while (_depth > 0 && length < LongEnough)
            {
                int p1 = start + 1;
                if (p1 > limit)
                {
                    break;
                }

                int length1 = Search(window, p1, blockEnd, windowStartPosition, anchor, reps, out uint offset1, out int gain1);
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

                    int length2 = Search(window, p2, blockEnd, windowStartPosition, anchor, reps, out uint offset2, out int gain2);
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
        for (int q = _next; q < p; q++)
        {
            InsertRow(RowHash(window, q), q);
        }
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
        int length = 0;
        uint offset = 0;
        int gain = 0;
        while (next < p)
        {
            int budget = _attempts;
            TreeWalk(window, next, end, false, ref budget, ref length, ref offset, ref gain);
            next++;
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
}
