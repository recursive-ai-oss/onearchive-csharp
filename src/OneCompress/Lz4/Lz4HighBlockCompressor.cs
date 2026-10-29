using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>Selects, at compile time, whether the high-compression chain entries carry a second link.</summary>
internal interface IChainMode
{
    static abstract bool Wide { get; }
}

internal readonly struct PlainChain : IChainMode
{
    public static bool Wide => false;
}

internal readonly struct WideChain : IChainMode
{
    public static bool Wide => true;
}

/// <summary>
/// The high-compression LZ4 match finder (levels 2 to 12): a hash chain over every position, searched to a depth that grows
/// with the level. Levels 2 to 9 parse lazily: at each position the longest match is found, and a longer one starting one
/// byte later replaces it (levels 3 to 9 also look two bytes ahead). Levels 10 to 12 choose the sequence of matches that makes
/// the block smallest, by a shortest-path search over the encoded size of each choice (a literal run grows by a byte, and by
/// one more at 15 and every 255 after; a match costs a token, two offset bytes and its length bytes) for as far ahead as the
/// level allows. The parameters of each level are in the encoder's documentation page.
/// </summary>
/// <remarks>
/// <para>The chain holds, for each position, the distance to the previous position with the same hash of four bytes; zero ends a
/// chain. Positions are kept in a running coordinate, as in <see cref="Lz4FastBlockCompressor"/>: starting a block without
/// history moves the origin forward so that earlier entries are further than 64 KiB away and are never followed.</para>
/// <para>A dictionary is placed in front of the first block and its chains are copied from a snapshot built once, so a block
/// with a dictionary costs a copy of the tables instead of re-indexing the dictionary.</para>
/// </remarks>
[Experimental("ONECOMPRESS002", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
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
    // Levels below 6 keep one 16-bit link per position; the others keep two (see Link), so the walk can read two candidates
    // from one entry. Only the table the level uses is allocated.
    private readonly ushort[] _chain;
    private readonly uint[] _chainWide;
    private readonly int _level;
    private readonly int _attempts;
    private readonly int _nice;
    private readonly bool _optimal;
    private readonly bool _lookahead;
    private readonly bool _wide;
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
    private uint[] _undoChain = [];
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
        _lookahead = level is >= 3 and <= 9;
        _wide = level >= 6;
        _chain = _wide ? [] : new ushort[ChainMask + 1];
        _chainWide = _wide ? new uint[ChainMask + 1] : [];
    }

    /// <summary>Gets the level.</summary>
    public int Level => _level;

    // Per level: how many chain candidates are examined at a position, the match length that is good enough to stop
    // searching (and, for the optimal levels, to stop planning), and whether matches are chosen optimally.
    private static (int Attempts, int Nice, bool Optimal) Parameters(int level) => level switch
    {
        2 => (3, 16, false),
        3 => (7, 256, false),
        4 => (12, 256, false),
        5 => (24, 256, false),
        6 => (44, 256, false),
        7 => (68, 256, false),
        8 => (96, 256, false),
        9 => (128, 256, false),
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
        for (int idx = Math.Max(0, _used - ChainMask - 1); idx < _used; idx++)
        {
            int slot = (int)((org + (uint)idx) & ChainMask);
            if (_wide)
            {
                uint entry = _chainWide[slot];
                if ((entry & 0xFFFF) > idx)
                {
                    _chainWide[slot] = 0;
                }
                else if ((entry >> 16) > idx)
                {
                    // The second link points before the window; the first one is still good.
                    _chainWide[slot] = entry & 0xFFFF;
                }
            }
            else if (_chain[slot] > idx)
            {
                _chain[slot] = 0;
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
            _undoChain = new uint[_undoHead.Length];
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
        for (int p = _next - 1; p >= dictLen; p--)
        {
            int h = Hash(Lz4EncRaw.Read32(_work, p));
            head[h] = _undoHead[p - dictLen];
            int slot = (int)((org + (uint)p) & ChainMask);
            if (_wide)
            {
                _chainWide[slot] = _undoChain[p - dictLen];
            }
            else
            {
                _chain[slot] = (ushort)_undoChain[p - dictLen];
            }
        }

        _next = dictLen;
        _used = dictLen;
        return written;
    }

    private void RestoreDictionary(Lz4PreparedDictionary dictionary)
    {
        Lz4PreparedDictionary.HighIndex snapshot = dictionary.HighSnapshot;
        uint[] head = snapshot.Head;
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
        if (_wide)
        {
            snapshot.ChainWide.AsSpan(0, dictionary.Content.Length).CopyTo(_chainWide);
        }
        else
        {
            snapshot.Chain.AsSpan(0, dictionary.Content.Length).CopyTo(_chain);
        }

        _next = dictionary.Content.Length;
    }

    // The same hash as Hash, as a native-width index.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint HashAt(Lz4In src, nint p) => (nint)((src.Read32(p) * 2654435761u) >> (32 - HashLog));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hash(uint fourBytes) => (int)((fourBytes * 2654435761u) >> (32 - HashLog));

    // Indexes every position from the first one not yet indexed up to, not including, target, remembering the values it
    // overwrites (see CompressParked).
    private void InsertRecording(ReadOnlySpan<byte> src, int target)
    {
        if (target <= _next)
        {
            return;
        }

        uint org = (uint)_origin;
        uint[] head = _head;
        for (int p = _next; p < target; p++)
        {
            int h = Hash(Lz4EncRaw.Read32(src, p));
            uint old = Lz4EncRaw.Get(head, h);
            int prev = (int)(old - org);
            int delta = p - prev;
            int slot = (int)((org + (uint)p) & ChainMask);
            bool linked = prev >= 0 && delta <= Lz4Format.MaxOffset;
            _undoHead[p - _undoBase] = old;
            if (_wide)
            {
                _undoChain[p - _undoBase] = Lz4EncRaw.Get(_chainWide, slot);
                Lz4EncRaw.Put(_chainWide, slot, linked ? Link(_chainWide, org, (uint)p, (uint)delta) : 0);
            }
            else
            {
                _undoChain[p - _undoBase] = Lz4EncRaw.Get(_chain, slot);
                Lz4EncRaw.Put(_chain, slot, linked ? (ushort)delta : (ushort)0);
            }

            Lz4EncRaw.Put(head, h, org + (uint)p);
        }

        _next = target;
    }

    // Indexes every position not yet indexed before `position`, then finds the longest match for src[position..] that is longer
    // than `best`, searching at most `attempts` candidates on the chain. Returns the length (or `best` unchanged when nothing
    // longer was found) and the offset of the match found.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Find(ReadOnlySpan<byte> src, int position, int matchLimit, int best, int attempts, int nice, ref int offset)
    {
        int from = _next;
        if (_recording)
        {
            InsertRecording(src, position);
            from = position;
        }

        long found = _wide
            ? FindChain<WideChain>(new Lz4In(src), new Lz4Table(_head), new Lz4Table16(_chain), new Lz4Table(_chainWide), (uint)_origin, from, position, matchLimit, best, attempts, nice, offset, !_recording)
            : FindChain<PlainChain>(new Lz4In(src), new Lz4Table(_head), new Lz4Table16(_chain), new Lz4Table(_chainWide), (uint)_origin, from, position, matchLimit, best, attempts, nice, offset, !_recording);
        int indexed = _recording ? position : position + 1;
        if (indexed > _next)
        {
            _next = indexed;
        }

        offset = (int)found;
        return (int)(found >> 32);
    }

    // The chain entry of a position whose first link is `delta` bytes back: that link in the low 16 bits and, in the high 16,
    // the distance to the position the first one links to (zero when either link is missing or further than 64 KiB). The
    // second link lets a search read two candidates from one entry, so the next entry's load starts a step sooner.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Link(uint[] chain, uint org, uint position, uint delta)
    {
        uint second = Lz4EncRaw.Get(chain, (int)((org + position - delta) & ChainMask)) & 0xFFFF;
        uint sum = delta + second;
        return delta | ((second != 0 && sum <= Lz4Format.MaxOffset ? sum : 0) << 16);
    }

    // Indexes a position: its chain link is the distance to the entry the table held for its hash, or zero when that entry is
    // further than 64 KiB back, before the window, or empty. The test is arithmetic rather than a branch: whether a link is
    // that old is as unpredictable as the data. With the wide chain the entry also carries the second link (see Link).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Index<TChain>(Lz4Table head, Lz4Table16 chain, Lz4Table wide, uint org, nint hash, uint previous, nint position)
        where TChain : struct, IChainMode
    {
        uint here = org + (uint)position;
        uint delta = here - previous;
        uint valid = (delta - 1) < (uint)Lz4Format.MaxOffset ? 1u : 0u;
        uint first = delta & (0u - valid);
        if (TChain.Wide)
        {
            uint entry = first;
            if (first != 0)
            {
                uint second = wide.Get((nint)((here - first) & ChainMask)) & 0xFFFF;
                uint sum = first + second;
                entry |= (second != 0 && sum <= Lz4Format.MaxOffset ? sum : 0) << 16;
            }

            wide.Put((nint)(here & ChainMask), entry);
        }
        else
        {
            chain.Put((nint)(here & ChainMask), (ushort)first);
        }

        head.Put(hash, here);
    }

    // Looks at one candidate for the match at ip: when it agrees with ip on the first four bytes and on the four bytes ending
    // at ip + best (which any match longer than `best` must), measures it, and keeps it if it is longer. Returns true when
    // the match is long enough that the search should stop.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Probe(Lz4In src, nint ip, nint cand, nint distance, nint matchLimit, nint nice, uint first, ref nint best, ref nint found, ref uint tail)
    {
        if (src.Read32(cand + best - (Lz4Format.MinMatch - 1)) == tail && src.Read32(cand) == first)
        {
            nint length = Lz4Format.MinMatch + src.CommonLength(cand + Lz4Format.MinMatch, ip + Lz4Format.MinMatch, matchLimit);
            if (length > best)
            {
                best = length;
                found = distance;
                if (best >= nice || ip + best >= matchLimit)
                {
                    return true;
                }

                tail = src.Read32(ip + best - (Lz4Format.MinMatch - 1));
            }
        }

        return false;
    }

    // Walks the chain that starts at `entry` (the table's value for ip's hash before ip was indexed) and returns the longest
    // match for src[ip..] that is longer than `best`: its length in the high 32 bits and its distance in the low 32 (`found`
    // when nothing longer exists). Candidates are positions in the window, negative or further than 64 KiB back when the entry
    // is stale or empty.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Walk(Lz4In src, Lz4Table16 chain, uint org, nint ip, nint matchLimit, uint entry, nint best, nint found, nint attempts, nint nice)
    {
        uint first = src.Read32(ip);
        nint cand = (int)(entry - org);
        nint slotBase = (nint)(org & ChainMask);
        // A candidate longer than `best` agrees with ip on the first four bytes and on the four bytes ending at ip + best.
        uint tail = src.Read32(ip + best - (Lz4Format.MinMatch - 1));
        while (attempts-- > 0)
        {
            // The candidate must lie 1 to 65535 bytes back; older entries, entries from before the window and empty slots all fail.
            nint distance = ip - cand;
            if ((nuint)(distance - 1) >= Lz4Format.MaxOffset)
            {
                break;
            }

            if (Probe(src, ip, cand, distance, matchLimit, nice, first, ref best, ref found, ref tail))
            {
                break;
            }

            nint delta = chain.Get((cand + slotBase) & ChainMask);
            if (delta == 0)
            {
                break;
            }

            cand -= delta;
        }

        return (best << 32) | (uint)found;
    }

    // Walk for the wide chain: one entry yields two candidates, the one it belongs to and, when the entry's first link
    // exists, the one after it, and the third candidate is reached through the second link without waiting for the second
    // one's own entry. It visits the same candidates in the same order as Walk.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long WalkWide(Lz4In src, Lz4Table chain, uint org, nint ip, nint matchLimit, uint entry, nint best, nint found, nint attempts, nint nice)
    {
        uint first = src.Read32(ip);
        nint cand = (int)(entry - org);
        nint slotBase = (nint)(org & ChainMask);
        uint tail = src.Read32(ip + best - (Lz4Format.MinMatch - 1));
        while (attempts > 0)
        {
            nint distance = ip - cand;
            if ((nuint)(distance - 1) >= Lz4Format.MaxOffset)
            {
                break;
            }

            uint links = chain.Get((cand + slotBase) & ChainMask);
            if (Probe(src, ip, cand, distance, matchLimit, nice, first, ref best, ref found, ref tail))
            {
                break;
            }

            nint next = (nint)(links & 0xFFFF);
            if (next == 0 || --attempts <= 0)
            {
                break;
            }

            nint nextDistance = distance + next;
            if ((nuint)(nextDistance - 1) >= Lz4Format.MaxOffset || Probe(src, ip, cand - next, nextDistance, matchLimit, nice, first, ref best, ref found, ref tail))
            {
                break;
            }

            nint skip = (nint)(links >> 16);
            if (skip == 0)
            {
                break;
            }

            attempts--;
            cand -= skip;
        }

        return (best << 32) | (uint)found;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long WalkChain<TChain>(Lz4In src, Lz4Table16 chain, Lz4Table wide, uint org, nint ip, nint matchLimit, uint entry, nint best, nint found, nint attempts, nint nice)
        where TChain : struct, IChainMode =>
        TChain.Wide
            ? WalkWide(src, wide, org, ip, matchLimit, entry, best, found, attempts, nice)
            : Walk(src, chain, org, ip, matchLimit, entry, best, found, attempts, nice);

    // Finds the longest match for src[ip..] that is longer than `best`, in a method of its own so that its few live values stay
    // in registers instead of sharing the parser's. Positions before ip are indexed first, in order, so that the chains the
    // search follows include every one of them, and ip itself is indexed with the hash already in hand when `indexSelf` is
    // set (the search only follows links that lead backwards). Returns the length in the high 32 bits and the offset in the low
    // 32 (the offset passed in when nothing longer is found). Every index below is a position inside the window or a
    // hash/mask bounded by the table sizes.
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long FindChain<TChain>(Lz4In src, Lz4Table head, Lz4Table16 chain, Lz4Table wide, uint org, nint from, nint ip, nint matchLimit, int best, int attempts, int nice, int offset, bool indexSelf)
        where TChain : struct, IChainMode
    {
        for (nint p = from; p < ip; p++)
        {
            nint h = HashAt(src, p);
            Index<TChain>(head, chain, wide, org, h, head.Get(h), p);
        }

        nint hip = HashAt(src, ip);
        uint entry = head.Get(hip);
        if (indexSelf)
        {
            Index<TChain>(head, chain, wide, org, hip, entry, ip);
        }

        if (ip + best >= matchLimit)
        {
            return ((long)best << 32) | (uint)offset;
        }

        // The next two positions are searched soon, and their head slots are scattered over a table that does not fit the
        // first-level cache; asking for them now hides the wait.
        head.Prefetch(HashAt(src, ip + 1));
        head.Prefetch(HashAt(src, ip + 2));
        return WalkChain<TChain>(src, chain, wide, org, ip, matchLimit, entry, best, offset, attempts, nice);
    }

    // Searches forward from ip, one position at a time and indexing each, for the first position whose longest match reaches
    // at least four bytes (Walk with nothing found so far), up to and including the last position a match may start at.
    // Returns that match (length in the high 32 bits, distance in the low 32) and its position in `position`; a length of
    // three means none was found (and position is then past the last one). The loop is the parser's hot path on data that
    // matches poorly, so it makes no call per position.
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Scan<TChain>(Lz4In src, Lz4Table head, Lz4Table16 chain, Lz4Table wide, uint org, nint from, nint ip, nint matchLimit, int attempts, int nice, out nint position)
        where TChain : struct, IChainMode
    {
        for (nint p = from; p < ip; p++)
        {
            nint h = HashAt(src, p);
            Index<TChain>(head, chain, wide, org, h, head.Get(h), p);
        }

        // The last position a match may start at is twelve bytes before the end of the block, and four bytes are read two
        // positions on, so every read stays inside the block.
        nint last = matchLimit - (Lz4Format.MatchFindLimit - Lz4Format.LastLiterals);
        while (ip <= last)
        {
            nint h0 = HashAt(src, ip);
            head.Prefetch(HashAt(src, ip + 2));
            uint entry = head.Get(h0);
            Index<TChain>(head, chain, wide, org, h0, entry, ip);
            long result = WalkChain<TChain>(src, chain, wide, org, ip, matchLimit, entry, Lz4Format.MinMatch - 1, 0, attempts, nice);
            if ((result >> 32) >= Lz4Format.MinMatch)
            {
                position = ip;
                return result;
            }

            ip++;
        }

        position = ip;
        return (long)(Lz4Format.MinMatch - 1) << 32;
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
            int offset;
            int length;
            if (_recording)
            {
                offset = 0;
                length = Find(src, ip, matchLimit, Lz4Format.MinMatch - 1, _attempts, _nice, ref offset);
                if (length < Lz4Format.MinMatch)
                {
                    ip++;
                    continue;
                }
            }
            else
            {
                nint at;
                long found = _wide
                    ? Scan<WideChain>(new Lz4In(src), new Lz4Table(_head), new Lz4Table16(_chain), new Lz4Table(_chainWide), (uint)_origin, _next, ip, matchLimit, _attempts, _nice, out at)
                    : Scan<PlainChain>(new Lz4In(src), new Lz4Table(_head), new Lz4Table16(_chain), new Lz4Table(_chainWide), (uint)_origin, _next, ip, matchLimit, _attempts, _nice, out at);
                if ((found >> 32) < Lz4Format.MinMatch)
                {
                    _next = (int)at;
                    break;
                }

                ip = (int)at;
                _next = ip + 1;
                length = (int)(found >> 32);
                offset = (int)found;
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

                int offset2 = 0;
                int length2 = Find(src, ip + 1, matchLimit, length, _attempts, _nice, ref offset2);
                if (length2 <= length)
                {
                    // Two bytes on, a match that ends further than two literals' cost beyond this one also wins.
                    if (_lookahead && ip + 2 <= mfLimit)
                    {
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
