using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// The high-compression LZ4 match finder (levels 2 to 12): a hash chain over every position, searched to a depth that grows
/// with the level. Levels 2 to 6 parse lazily: at each position the longest match is found, and a longer one starting one
/// byte later replaces it (levels 3 to 6 also look two bytes ahead). Levels 7 to 12 choose the sequence of matches that makes the block smallest, by a shortest-path search
/// over the encoded size of each choice (a literal run grows by a byte, and by one more at 15 and every 255 after;
/// a match costs a token, two offset bytes and its length bytes) for as far ahead as the level allows. The parameters of
/// each level are in the encoder's documentation page.
/// </summary>
/// <remarks>
/// <para>The chain holds, for each position, the distance to the previous position with the same hash of four bytes; zero ends a
/// chain. Positions are kept in a running coordinate, as in <see cref="Lz4FastBlockCompressor"/>: starting a block without
/// history moves the origin forward so that earlier entries are further than 64 KiB away and are never followed.</para>
/// <para>A dictionary is placed in front of the first block and its chains are copied from a snapshot built once, so a block
/// with a dictionary costs a copy of the tables instead of re-indexing the dictionary.</para>
/// </remarks>
public sealed class Lz4HighBlockCompressor : Lz4BlockCompressor
{
    internal const int HashLog = 16;
    private const int ChainMask = 0xFFFF;
    private const long InitialOrigin = 1 << 16;
    private const int OptWindow = 4096;
    private const int OptMargin = 8;
    private const int MinSequence = Lz4Format.MinMatch;
    private const int Infinity = int.MaxValue / 2;

    private readonly uint[] _head = new uint[1 << HashLog];
    private readonly ushort[] _chain = new ushort[ChainMask + 1];
    private readonly int _level;
    private readonly int _attempts;
    private readonly int _nice;
    private readonly bool _optimal;
    private readonly bool _lookahead;
    private OptState[]? _opt;
    private int[]? _plan;
    private byte[] _work = [];
    private long _origin = InitialOrigin;
    private int _used;
    private int _next;
    private bool _scrubPending;
    private Lz4PreparedDictionary? _dictionary;

    // Small blocks with a dictionary keep the dictionary's index in place between calls (see CompressParked): the dictionary
    // whose index the tables hold, and the values each insertion overwrote, so the block's own entries can be taken back out.
    private Lz4PreparedDictionary? _parked;
    private uint[] _undoHead = [];
    private ushort[] _undoChain = [];
    private bool _recording;
    private int _undoBase;

    /// <summary>Creates a high-compression compressor.</summary>
    /// <param name="level">The level, from 2 to 12.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 2 to 12.</exception>
    public Lz4HighBlockCompressor(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 12);
        _level = level;
        (_attempts, _nice, _optimal) = Parameters(level);
        _lookahead = level is >= 3 and <= 6;
    }

    /// <summary>Gets the level.</summary>
    public int Level => _level;

    // Per level: how many chain candidates are examined at a position, the match length that is good enough to stop
    // searching (and, for the optimal levels, to stop planning), and whether matches are chosen optimally.
    private static (int Attempts, int Nice, bool Optimal) Parameters(int level) => level switch
    {
        2 => (3, 16, false),
        3 => (7, 256, false),
        4 => (14, 256, false),
        5 => (28, 256, false),
        6 => (56, 256, false),
        7 => (32, 128, true),
        8 => (36, 128, true),
        9 => (40, 128, true),
        10 => (56, 256, true),
        11 => (100, 256, true),
        _ => (128, 256, true),
    };

    /// <inheritdoc />
    public override void SetDictionary(Lz4Dictionary? dictionary) =>
        _dictionary = dictionary is null || dictionary.Length < 8 ? null : Lz4PreparedDictionary.For(dictionary);

    /// <inheritdoc />
    public override void Reset()
    {
        // When the tables hold only a dictionary's index there is no history to forget, and keeping the index spares the next
        // block (a new frame with the same dictionary, typically) rebuilding it.
        if (_parked is null)
        {
            Forget();
        }
    }

    private void Forget()
    {
        // The origin stays a multiple of 64 KiB so that a dictionary snapshot's chain can be copied as it is.
        _origin = (_origin + _used + ChainMask + 1 + ChainMask) & ~(long)ChainMask;
        _used = 0;
        _next = 0;
        _scrubPending = false;
        _parked = null;
    }

    /// <inheritdoc />
    public override void Rebase(int removed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removed);
        if (_parked is not null)
        {
            // The tables hold only a dictionary's index and nothing of the window is indexed. Starting afresh, as a block that
            // continues from here does anyway, keeps the history searchable; clearing the flag alone left the dictionary's
            // coordinates standing in for the window's, so the next block could not see the one before it.
            Forget();
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(removed, _used);
        _origin += removed;
        _used -= removed;
        _next = Math.Max(0, _next - removed);
        Scrub();
    }

    // Forgets every link that points before the window's first byte (into dropped bytes or a dictionary that was in front of
    // the window). The searches rely on a link being either inside the window or further than 64 KiB away.
    private void Scrub()
    {
        _scrubPending = false;
        uint org = (uint)_origin;
        uint[] head = _head;
        for (int i = 0; i < head.Length; i++)
        {
            if ((int)(head[i] - org) < 0)
            {
                head[i] = 0;
            }
        }

        // Only the last 64 KiB of positions still have their own chain slot.
        ushort[] chain = _chain;
        for (int idx = Math.Max(0, _used - ChainMask - 1); idx < _used; idx++)
        {
            int slot = (int)((org + (uint)idx) & ChainMask);
            int delta = chain[slot];
            if (delta > idx)
            {
                chain[slot] = 0;
            }
        }
    }

    /// <inheritdoc />
    public override int Compress(ReadOnlySpan<byte> window, int historyLength, Span<byte> destination)
    {
        CheckArguments(window, historyLength, destination);
        if (historyLength == 0 && _dictionary is { } parkable && window.Length <= MaxParkedInput)
        {
            return CompressParked(window, parkable, destination);
        }

        if (historyLength == 0 || _parked is not null)
        {
            // A block without history starts afresh; so does one that continues a block whose index was taken apart.
            Forget();
        }

        if (_origin + window.Length + (3 * (ChainMask + 1)) + (_dictionary?.Content.Length ?? 0) >= int.MaxValue)
        {
            Array.Clear(_head);
            _origin = InitialOrigin;
            _next = 0;
        }

        if (historyLength > 0 && _scrubPending)
        {
            Scrub();
        }

        Lz4PreparedDictionary? dictionary = historyLength == 0 ? _dictionary : null;
        if (dictionary is null)
        {
            _used = window.Length;
            return Parse(window, historyLength, destination);
        }

        // Dictionary first, then the input, in one buffer; the dictionary's index is restored from its snapshot.
        int dictLen = dictionary.Content.Length;
        int total = dictLen + window.Length;
        if (_work.Length < total)
        {
            _work = new byte[Math.Max(total, _work.Length * 2)];
        }

        dictionary.Content.CopyTo(_work, 0);
        window.CopyTo(_work.AsSpan(dictLen));
        RestoreDictionary(dictionary);
        _used = total;
        int written = Parse(_work.AsSpan(0, total), dictLen, destination);

        // From here on coordinates refer to the caller's window, which starts where the dictionary ended.
        _origin += dictLen;
        _used = window.Length;
        _next = Math.Max(0, _next - dictLen);
        _scrubPending = true;
        return written;
    }

    // Blocks up to this size use the in-place dictionary index.
    private const int MaxParkedInput = ChainMask + 1;

    // A block with a dictionary, without history, small enough that undoing its insertions is cheaper than rebuilding the
    // dictionary's index: the tables keep the dictionary's entries between calls. The block is appended to the dictionary in
    // one buffer, compressed as usual while every insertion records the head and chain values it replaced, and afterwards the
    // insertions are undone in reverse order, which leaves the tables exactly as the dictionary alone left them. A fresh
    // compressor and a reused one therefore see the same index at every step, and the output cannot depend on earlier blocks.
    private int CompressParked(ReadOnlySpan<byte> window, Lz4PreparedDictionary dictionary, Span<byte> destination)
    {
        int dictLen = dictionary.Content.Length;
        int total = dictLen + window.Length;
        if (_work.Length < total)
        {
            _work = new byte[Math.Max(total, _work.Length * 2)];
            _parked = null;
        }

        if (!ReferenceEquals(_parked, dictionary))
        {
            Forget();
            if (_origin + (3 * (ChainMask + 1)) + dictLen >= int.MaxValue)
            {
                Array.Clear(_head);
                _origin = InitialOrigin;
            }

            dictionary.Content.CopyTo(_work, 0);
            RestoreDictionary(dictionary);
            _parked = dictionary;
        }

        if (_undoHead.Length < window.Length)
        {
            _undoHead = new uint[Math.Max(window.Length, 256)];
            _undoChain = new ushort[_undoHead.Length];
        }

        window.CopyTo(_work.AsSpan(dictLen));
        _used = total;
        _next = dictLen;
        _undoBase = dictLen;
        _recording = true;
        int written = Parse(_work.AsSpan(0, total), dictLen, destination);
        _recording = false;

        // Undo, newest first: a position's old head value is what the next position with the same hash saw as its predecessor.
        uint org = (uint)_origin;
        uint[] head = _head;
        ushort[] chain = _chain;
        for (int p = _next - 1; p >= dictLen; p--)
        {
            int h = Hash(Lz4EncRaw.Read32(_work, p));
            head[h] = _undoHead[p - dictLen];
            chain[(int)((org + (uint)p) & ChainMask)] = _undoChain[p - dictLen];
        }

        _next = dictLen;
        _used = dictLen;
        return written;
    }

    private void RestoreDictionary(Lz4PreparedDictionary dictionary)
    {
        Lz4PreparedDictionary.HighIndex snapshot = dictionary.HighSnapshot;
        uint[] head = snapshot.Head;
        ushort[] chain = snapshot.Chain;
        uint org = (uint)_origin;
        uint[] mine = _head;
        for (int i = 0; i < head.Length; i++)
        {
            uint e = head[i];
            if (e != 0)
            {
                mine[i] = org + e - 1;
            }
        }

        // The origin is a multiple of 64 KiB, so a dictionary position's slot is its index.
        chain.AsSpan(0, dictionary.Content.Length).CopyTo(_chain);
        _next = dictionary.Content.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hash(uint fourBytes) => (int)((fourBytes * 2654435761u) >> (32 - HashLog));

    // Indexes every position from the first one not yet indexed up to, not including, target.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InsertUpTo(ReadOnlySpan<byte> src, int target)
    {
        uint org = (uint)_origin;
        uint[] head = _head;
        ushort[] chain = _chain;
        bool record = _recording;
        for (int p = _next; p < target; p++)
        {
            int h = Hash(Lz4EncRaw.Read32(src, p));
            uint old = Lz4EncRaw.Get(head, h);
            int prev = (int)(old - org);
            int delta = p - prev;
            int slot = (int)((org + (uint)p) & ChainMask);
            if (record)
            {
                _undoHead[p - _undoBase] = old;
                _undoChain[p - _undoBase] = Lz4EncRaw.Get(chain, slot);
            }

            Lz4EncRaw.Put(chain, slot, (prev >= 0 && delta <= Lz4Format.MaxOffset) ? (ushort)delta : (ushort)0);
            Lz4EncRaw.Put(head, h, org + (uint)p);
        }

        if (target > _next)
        {
            _next = target;
        }
    }

    // The longest match for src[ip..] that is longer than `best`, searching at most `attempts` candidates on the chain.
    // Returns the length (or `best` unchanged when nothing longer was found) and the offset of the match found.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(ReadOnlySpan<byte> src, int position, int matchLimit, int best, int attempts, int nice, ref int offset)
    {
        if (position + best >= matchLimit)
        {
            return best;
        }

        // Native-width positions keep the address arithmetic free of sign extensions; every index below is a position inside
        // the window or a hash/mask bounded by the table sizes.
        uint org = (uint)_origin;
        var head = new Lz4Table(_head);
        var chain = new Lz4Table16(_chain);
        nint ip = position;
        uint first = Lz4EncRaw.Read32(src, ip);
        uint candidate = head.Get(Hash(first));
        byte tail = Lz4EncRaw.Get(src, ip + best);
        while (attempts-- > 0)
        {
            // The candidate must lie 1 to 65535 bytes back; older entries, entries from before the window and empty slots all fail.
            nint distance = ip - (int)(candidate - org);
            if ((nuint)(distance - 1) >= Lz4Format.MaxOffset)
            {
                break;
            }

            nint idx = ip - distance;
            if (Lz4EncRaw.Get(src, idx + best) == tail && Lz4EncRaw.Read32(src, idx) == first)
            {
                int length = Lz4Format.MinMatch + Lz4EncRaw.CommonLength(src, (int)idx + Lz4Format.MinMatch, position + Lz4Format.MinMatch, matchLimit);
                if (length > best)
                {
                    best = length;
                    offset = (int)distance;
                    if (best >= nice || position + best >= matchLimit)
                    {
                        break;
                    }

                    tail = Lz4EncRaw.Get(src, ip + best);
                }
            }

            ushort delta = chain.Get((nint)(candidate & ChainMask));
            if (delta == 0)
            {
                break;
            }

            candidate -= delta;
        }

        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Parse(ReadOnlySpan<byte> src, int start, Span<byte> dst)
    {
        // The window is exactly [history][input]; when a dictionary stands in as history `dst` indexes still start at zero.
        int n = src.Length;
        int mfLimit = n - Lz4Format.MatchFindLimit;
        int matchLimit = n - Lz4Format.LastLiterals;
        int op = 0;
        int anchor = start;
        int ip = start;

        if (_optimal)
        {
            _opt ??= new OptState[OptWindow + OptMargin + 1];
        }

        while (ip <= mfLimit)
        {
            InsertUpTo(src, ip);
            int offset = 0;
            int length = Find(src, ip, matchLimit, Lz4Format.MinMatch - 1, _attempts, _nice, ref offset);
            if (length < Lz4Format.MinMatch)
            {
                ip++;
                continue;
            }

            if (_optimal)
            {
                (ip, anchor, op) = Optimal(src, dst, ip, anchor, op, length, offset, mfLimit, matchLimit);
                continue;
            }

            // Lazy: a match starting one byte later and longer than this one wins, and the byte before it becomes a literal.
            while (length < _nice)
            {
                if (ip + 1 > mfLimit)
                {
                    break;
                }

                InsertUpTo(src, ip + 1);
                int offset2 = 0;
                int length2 = Find(src, ip + 1, matchLimit, length, _attempts, _nice, ref offset2);
                if (length2 <= length)
                {
                    // Two bytes on, a match that ends further than two literals' cost beyond this one also wins.
                    if (_lookahead && ip + 2 <= mfLimit)
                    {
                        InsertUpTo(src, ip + 2);
                        int offset3 = 0;
                        int length3 = Find(src, ip + 2, matchLimit, length + 1, _attempts, _nice, ref offset3);
                        if (length3 > length + 1)
                        {
                            ip += 2;
                            length = length3;
                            offset = offset3;
                            continue;
                        }
                    }

                    break;
                }

                ip++;
                length = length2;
                offset = offset2;
            }

            // Extend backwards over pending literals (the match may start earlier than the hash chain showed).
            int source = ip - offset;
            while (ip > anchor && source > 0 && Lz4EncRaw.Get(src, ip - 1) == Lz4EncRaw.Get(src, source - 1))
            {
                ip--;
                source--;
                length++;
            }

            op = Lz4BlockWriter.Sequence(dst, op, src, anchor, ip - anchor, offset, length);
            ip += length;
            anchor = ip;
        }

        return Lz4BlockWriter.LastLiterals(dst, op, src, anchor, n - anchor);
    }

    private struct OptState
    {
        public int Price;     // bytes needed to encode everything before this position along the best path found
        public int Offset;    // offset of the match that ends here (0 when reached by a literal)
        public int Length;    // length of the match that ends here (0 when reached by a literal)
        public int Literals;  // literals pending after this position along that path
    }

    // Bytes a literal adds to a run that already holds `run` literals: itself, plus a length byte when the run reaches 15,
    // 270, 525, ...
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LiteralPrice(int run) => run + 1 >= 15 && (run + 1 - 15) % 255 == 0 ? 2 : 1;

    // Bytes of a match of `length` after its literals: token, offset, and length bytes beyond the 15 the token holds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int MatchPrice(int length) => length - Lz4Format.MinMatch >= 15 ? 3 + ((length - Lz4Format.MinMatch - 15) / 255) + 1 : 3;

    // Plans the cheapest sequence of literals and matches over the stretch that starts at ip, given the longest match there,
    // and writes it. Returns the new position, anchor and output position.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (int Ip, int Anchor, int Op) Optimal(ReadOnlySpan<byte> src, Span<byte> dst, int ip, int anchor, int op, int firstLength, int firstOffset, int mfLimit, int matchLimit)
    {
        OptState[] opt = _opt!;
        opt[0] = new OptState { Price = 0, Literals = ip - anchor };
        int last = 0;
        int tailAt = -1;
        int tailLength = 0;
        int tailOffset = 0;
        for (int cur = 0; cur < last || cur == 0; cur++)
        {
            OptState here = opt[cur];
            int length = 0;
            int offset = 0;
            if (cur == 0)
            {
                length = firstLength;
                offset = firstOffset;
            }
            else
            {
                // One more literal reaches cur + 1 (cur < last here, so that slot exists).
                int literalPrice = here.Price + LiteralPrice(here.Literals);
                if (literalPrice < opt[cur + 1].Price)
                {
                    opt[cur + 1] = new OptState { Price = literalPrice, Literals = here.Literals + 1 };
                }

                // A match starting here can be replaced by the same match starting one byte later (shorter by one, and never
                // dearer) when cur + 1 is already no dearer to reach, and a four-byte match here is no better than what
                // reaches cur + 4 already. Then the search would find nothing the plan does not have.
                bool dominated = opt[cur + 1].Price <= here.Price && cur + MinSequence <= last && opt[cur + MinSequence].Price <= here.Price + MatchPrice(Lz4Format.MinMatch);
                if (!dominated && ip + cur <= mfLimit)
                {
                    InsertUpTo(src, ip + cur);
                    length = Find(src, ip + cur, matchLimit, Lz4Format.MinMatch - 1, _attempts, _nice, ref offset);
                }
            }

            if (length >= Lz4Format.MinMatch && (length >= _nice || cur + length >= OptWindow))
            {
                // Long enough to take as it is: the plan ends with this match.
                tailAt = cur;
                tailLength = length;
                tailOffset = offset;
                break;
            }

            // Every length from the shortest up is available at the found offset, and offsets cost the same.
            for (int len = Lz4Format.MinMatch; len <= length; len++)
            {
                int pos = cur + len;
                while (last < pos)
                {
                    opt[++last].Price = Infinity;
                }

                int price = here.Price + MatchPrice(len);
                if (price < opt[pos].Price)
                {
                    opt[pos] = new OptState { Price = price, Offset = offset, Length = len };
                }
            }

            if (cur == 0)
            {
                // The first position's literal transition, now that the slot exists.
                int literalPrice = here.Price + LiteralPrice(here.Literals);
                if (literalPrice < opt[1].Price)
                {
                    opt[1] = new OptState { Price = literalPrice, Literals = here.Literals + 1 };
                }
            }
        }

        // Walk back from the end of the plan, collecting the matches, then write them front to back.
        int end = tailAt >= 0 ? tailAt : last;
        int[] plan = _plan ??= new int[3 * ((OptWindow / Lz4Format.MinMatch) + 2)];
        int count = 0;
        for (int pos = end; pos > 0;)
        {
            OptState s = opt[pos];
            if (s.Length == 0)
            {
                pos--;
                continue;
            }

            pos -= s.Length;
            plan[count++] = pos;
            plan[count++] = s.Length;
            plan[count++] = s.Offset;
        }

        int newIp = ip + end;
        for (int i = count; i > 0; i -= 3)
        {
            int start = ip + plan[i - 3];
            op = Lz4BlockWriter.Sequence(dst, op, src, anchor, start - anchor, plan[i - 1], plan[i - 2]);
            anchor = start + plan[i - 2];
        }

        if (tailAt >= 0)
        {
            int start = ip + tailAt;
            op = Lz4BlockWriter.Sequence(dst, op, src, anchor, start - anchor, tailOffset, tailLength);
            anchor = start + tailLength;
            newIp = anchor;
        }

        return (newIp, anchor, op);
    }
}
