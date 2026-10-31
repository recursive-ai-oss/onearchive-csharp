using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The fast and double-fast strategies: a hash table maps the bytes at a position to the last position that had the same
/// hash, and a match is taken when the candidate really matches. The fast strategy keeps one table; the double-fast strategy
/// keeps a table keyed on eight bytes and one keyed on <see cref="ZstdCompressionParameters.MinMatch"/> bytes, so it finds long
/// matches that a short hash would overwrite. After a match, the next repeat offset is tried straight away.
/// </summary>
/// <remarks>
/// Positions are stored as window indexes with 0 meaning empty, so the first byte of a window is never a candidate. The hash is a
/// fixed multiplicative hash of the low bytes of an eight-byte read, the same on every platform. After
/// <see cref="ZstdCompressionParameters.TargetLength"/> acceleration (negative levels) fewer positions are tried; the search also
/// speeds up the longer it goes without finding a match.
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdFastMatchFinder : ZstdMatchFinder
{
    private const ulong HashPrime = 0x9E3779B185EBCA87UL;

    // The search steps ahead one byte further each time this many bytes pass without a match.
    private const int SearchStrengthShift = 7;

    private uint[] _primary = [];
    private uint[] _secondary = [];
    private int _primaryLog;
    private int _secondaryLog;
    private int _dictionaryPrimaryLog;
    private int _dictionarySecondaryLog;
    private int _minMatch;
    private int _stride;
    private bool _double;
    private long _windowSize;
    private ZstdCompressionDictionary? _dictionary;
    private ZstdDictionaryIndex? _dictionaryIndex;

    // How to read a dictionary table entry (see ZstdDictionaryIndex.Tagged): the bits that must equal the tag, and the position bits.
    private uint _tagMask;
    private uint _positionMask = uint.MaxValue;

    internal static int Hash(ReadOnlySpan<byte> data, int index, int minMatch, int log) =>
        HashValue(Raw.ReadUInt64(data, index), 64 - (8 * minMatch), log);

    /// <summary>The tag stored beside a dictionary position: a hash of the four bytes there, in the top eight bits.</summary>
    internal static uint Tag(uint fourBytes) => (fourBytes * 0x9E3779B1u) & 0xFF00_0000u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashValue(ulong value, int shift, int log) => (int)(((value << shift) * HashPrime) >> (64 - log));

    /// <inheritdoc />
    public override void BeginFrame(ZstdCompressionParameters parameters, ZstdCompressionDictionary? dictionary)
    {
        if (parameters.Strategy is not (ZstdStrategy.Fast or ZstdStrategy.DoubleFast))
        {
            throw new ArgumentException("This match finder implements the Fast and DoubleFast strategies.", nameof(parameters));
        }

        _double = parameters.Strategy == ZstdStrategy.DoubleFast;
        _minMatch = Math.Clamp(parameters.MinMatch, 4, 8);
        _primaryLog = Math.Clamp(parameters.HashLog, 6, 26);
        _secondaryLog = Math.Clamp(parameters.ChainLog, 6, 26);
        _windowSize = parameters.WindowSize;
        _stride = parameters.TargetLength + (parameters.TargetLength == 0 ? 1 : 0) + 1;
        Prepare(ref _primary, 1 << _primaryLog);
        if (_double)
        {
            Prepare(ref _secondary, 1 << _secondaryLog);
        }

        _dictionary = dictionary;
        _dictionaryPrimaryLog = parameters.DictionaryHashLog != 0 ? Math.Clamp(parameters.DictionaryHashLog, 6, 26) : _primaryLog;
        _dictionarySecondaryLog = parameters.DictionaryChainLog != 0 ? Math.Clamp(parameters.DictionaryChainLog, 6, 26) : _secondaryLog;
        _dictionaryIndex = dictionary?.GetIndex(_minMatch, _dictionaryPrimaryLog, _dictionarySecondaryLog, _double);
        bool tagged = _dictionaryIndex?.Tagged ?? false;
        _tagMask = tagged ? 0xFF00_0000u : 0;
        _positionMask = tagged ? 0x00FF_FFFFu : uint.MaxValue;
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
        SlideTable(_primary, 1 << _primaryLog, delta);
        if (_double)
        {
            SlideTable(_secondary, 1 << _secondaryLog, delta);
        }
    }

    /// <inheritdoc />
    public override void Prime(ReadOnlySpan<byte> window, int firstPosition, int endPosition)
    {
        // Bounds: i + 8 <= window.Length for every read; table indexes come from the hash.
        int limit = Math.Min(endPosition, window.Length - 7);
        int shift = 64 - (8 * _minMatch);
        for (int i = Math.Max(firstPosition, 1); i < limit; i++)
        {
            ulong value = Raw.ReadUInt64(window, i);
            if (_double)
            {
                Raw.Set(_primary, HashValue(value, 0, _primaryLog), (uint)i);
                Raw.Set(_secondary, HashValue(value, shift, _secondaryLog), (uint)i);
            }
            else
            {
                Raw.Set(_primary, HashValue(value, shift, _primaryLog), (uint)i);
            }
        }
    }

    private static void SlideTable(uint[] table, int size, int delta)
    {
        uint d = (uint)delta;
        for (int i = 0; i < size; i++)
        {
            uint e = table[i];
            table[i] = e > d ? e - d : 0;
        }
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

        if (_double && _dictionaryIndex is null)
        {
            FindDoubleNoDictionary(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, store);
        }
        else if (_double)
        {
            FindDouble(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, store);
        }
        else if (_dictionaryIndex is not null)
        {
            FindSingleWithDictionary(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, store);
        }
        else
        {
            FindSingle(window, blockStart, blockEnd, windowStartPosition, repeatOffsets, store);
        }
    }

    // Number of bytes, at most max, that are equal going backward from a[ai - 1] and b[bi - 1]; max is at most ai and bi. The first eight
    // bytes are compared in one step, and the count read off the first difference, so that the usual short extension (zero to three
    // bytes) does not cost a mispredicted branch per byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CountBackward(ReadOnlySpan<byte> a, int ai, ReadOnlySpan<byte> b, int bi, int max)
    {
        if (ai >= 8 && bi >= 8)
        {
            // Bounds: ai >= 8 and bi >= 8, so both eight-byte reads end at or before a[ai] and b[bi].
            ulong x = Raw.ReadUInt64(a, ai - 8) ^ Raw.ReadUInt64(b, bi - 8);
            int n8 = BitOperations.LeadingZeroCount(x) >> 3;
            if (n8 < 8 || max <= 8)
            {
                return Math.Min(n8, max);
            }

            return 8 + CountBackwardLong(a, ai - 8, b, bi - 8, max - 8);
        }

        return CountBackwardLong(a, ai, b, bi, max);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountBackwardLong(ReadOnlySpan<byte> a, int ai, ReadOnlySpan<byte> b, int bi, int max)
    {
        // Bounds: n + 8 <= max <= ai and bi, so both eight-byte reads end at or before a[ai] and b[bi].
        int n = 0;
        while (n + 8 <= max)
        {
            ulong x = Raw.ReadUInt64(a, ai - n - 8) ^ Raw.ReadUInt64(b, bi - n - 8);
            if (x != 0)
            {
                return n + (BitOperations.LeadingZeroCount(x) >> 3);
            }

            n += 8;
        }

        while (n < max && Raw.ByteAt(a, ai - n - 1) == Raw.ByteAt(b, bi - n - 1))
        {
            n++;
        }

        return n;
    }

    // Length of the common prefix of window[a..] and window[b..], where a < b, looking no further than window[limit].
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Count(ReadOnlySpan<byte> window, int a, int b, int limit)
    {
        // Bounds: b + n <= limit <= window.Length for every access, and a < b.
        int max = limit - b;
        int n = 0;
        while (n + 8 <= max)
        {
            ulong x = Raw.ReadUInt64(window, a + n) ^ Raw.ReadUInt64(window, b + n);
            if (x != 0)
            {
                return n + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            n += 8;
        }

        while (n < max && Raw.ByteAt(window, a + n) == Raw.ByteAt(window, b + n))
        {
            n++;
        }

        return n;
    }

    // As Count with native-width positions. Most matches end within the first eight bytes this compares, so that word is inline and
    // anything longer goes to a call; keeping the loop out of the search methods leaves them room for their values in registers.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Count(ReadOnlySpan<byte> window, nint a, nint b, int limit)
    {
        // Bounds: b + 8 <= limit <= window.Length for the first reads, and a < b.
        if (b + 8 <= limit)
        {
            ulong x = Raw.ReadUInt64(window, a) ^ Raw.ReadUInt64(window, b);
            if (x != 0)
            {
                return BitOperations.TrailingZeroCount(x) >> 3;
            }

            return 8 + CountFrom(window, a + 8, b + 8, limit);
        }

        return CountFrom(window, a, b, limit);
    }

    // As Count, but the first sixteen bytes are compared in one step: a match usually ends within them, and finding the end from a
    // mask takes one branch where a loop of eight-byte steps takes one or two that are hard to predict.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountWide(ReadOnlySpan<byte> window, nint a, nint b, int limit)
    {
        // Bounds: b + 16 <= limit <= window.Length for the vector read, and a < b.
        if (b + 16 <= limit)
        {
            int equal = Raw.CommonPrefix16(window, a, b);
            if (equal < 16)
            {
                return equal;
            }

            return 16 + CountFrom(window, a + 16, b + 16, limit);
        }

        return CountFrom(window, a, b, limit);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountFrom(ReadOnlySpan<byte> window, nint a, nint b, int limit)
    {
        // Bounds: b + n <= limit <= window.Length for every access, and a < b.
        nint max = limit - b;
        nint n = 0;
        while (n + 16 <= max)
        {
            int equal = Raw.CommonPrefix16(window, a + n, b + n);
            if (equal < 16)
            {
                return (int)n + equal;
            }

            n += 16;
        }

        if (n + 8 <= max)
        {
            ulong x = Raw.ReadUInt64(window, a + n) ^ Raw.ReadUInt64(window, b + n);
            if (x != 0)
            {
                return (int)n + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            n += 8;
        }

        while (n < max && Raw.ByteAt(window, (int)(a + n)) == Raw.ByteAt(window, (int)(b + n)))
        {
            n++;
        }

        return (int)n;
    }

    internal static int CountDictionary(ReadOnlySpan<byte> dictionary, int d, ReadOnlySpan<byte> window, int b, int limit)
    {
        int max = Math.Min(limit - b, dictionary.Length - d);
        int n = 0;
        while (n + 8 <= max)
        {
            ulong x = Raw.ReadUInt64(dictionary, d + n) ^ Raw.ReadUInt64(window, b + n);
            if (x != 0)
            {
                return n + (BitOperations.TrailingZeroCount(x) >> 3);
            }

            n += 8;
        }

        while (n < max && Raw.ByteAt(dictionary, d + n) == Raw.ByteAt(window, b + n))
        {
            n++;
        }

        return n;
    }

    // The quick test in front of DictionaryRepeatLength, small enough to inline into the search loops: once a block has used a repeat
    // offset that reaches into the dictionary, every later position asks about it, and nearly all of them fail here. True when
    // DictionaryRepeatLength can find a match of at least four bytes (rep > ip, and the frame has not left its first window).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DictionaryRepeatMayMatch(ReadOnlySpan<byte> window, int ip, int end, long windowStartPosition, uint rep)
    {
        ReadOnlySpan<byte> dictionary = _dictionary!.ContentSpan;
        long d = dictionary.Length + (long)ip - rep;
        return windowStartPosition == 0
            && ip <= _windowSize
            && d >= 0
            && d + 4 <= dictionary.Length
            && ip + 4 <= end
            && Raw.ReadUInt32(dictionary, (int)d) == Raw.ReadUInt32(window, ip);
    }

    // A repeat offset that reaches back before the first byte of the frame lands in the dictionary content, which sits right before it.
    // Returns the match length at window position ip (4 or more), or 0 if there is no such match.
    private int DictionaryRepeatLength(ReadOnlySpan<byte> window, int ip, int end, long windowStartPosition, uint rep)
    {
        if (_dictionary is null || windowStartPosition != 0 || rep <= (uint)ip || ip > _windowSize)
        {
            return 0;
        }

        ReadOnlySpan<byte> dictionary = _dictionary.ContentSpan;
        long back = rep - (uint)ip;
        if (back > dictionary.Length)
        {
            return 0;
        }

        int d = dictionary.Length - (int)back;
        if (d + 4 > dictionary.Length || ip + 4 > end || Raw.ReadUInt32(dictionary, d) != Raw.ReadUInt32(window, ip))
        {
            return 0;
        }

        int length = 4 + CountDictionary(dictionary, d + 4, window, ip + 4, end);
        if (d + length == dictionary.Length && ip + length < end)
        {
            // The match runs past the end of the dictionary into the start of the frame's own data.
            length += Count(window, 0, ip + length, end);
        }

        return length;
    }

    // A match into the dictionary content at window position ip. Offsets into the dictionary are valid while the bytes produced so far
    // do not exceed the window size (RFC 8878 §3.1.1.1.2). The table probe is inlined into the search loops because it fails at
    // nearly every position; only a probe that finds matching bytes goes on to measure and extend the match.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryDictionary(
        ReadOnlySpan<byte> window,
        int ip,
        int anchor,
        int end,
        long windowStartPosition,
        uint[] indexTable,
        int tableLog,
        int minMatch,
        bool requireEight,
        out int matchStart,
        out int matchLength,
        out uint offset)
    {
        matchStart = 0;
        matchLength = 0;
        offset = 0;
        ReadOnlySpan<byte> dictionary = _dictionary!.ContentSpan;
        ulong value = Raw.ReadUInt64(window, ip);
        uint entry = indexTable[HashValue(value, 64 - (8 * minMatch), tableLog)];
        int d = (int)(entry & _positionMask);
        if (d == 0 || ((entry ^ Tag((uint)value)) & _tagMask) != 0)
        {
            return false;
        }

        // Table entries are positions with at least eight bytes after them, so reading eight bytes is in range.
        if (requireEight ? Raw.ReadUInt64(dictionary, d) != Raw.ReadUInt64(window, ip) : Raw.ReadUInt32(dictionary, d) != Raw.ReadUInt32(window, ip))
        {
            return false;
        }

        return ExtendDictionaryMatch(dictionary, d, window, ip, anchor, end, windowStartPosition, requireEight, out matchStart, out matchLength, out offset);
    }

    private bool ExtendDictionaryMatch(
        ReadOnlySpan<byte> dictionary,
        int d,
        ReadOnlySpan<byte> window,
        int ip,
        int anchor,
        int end,
        long windowStartPosition,
        bool requireEight,
        out int matchStart,
        out int matchLength,
        out uint offset)
    {
        matchStart = 0;
        matchLength = 0;
        offset = 0;
        long produced = windowStartPosition + ip;
        if (produced > _windowSize)
        {
            return false;
        }

        int length = (requireEight ? 8 : 4) + CountDictionary(dictionary, d + (requireEight ? 8 : 4), window, ip + (requireEight ? 8 : 4), end);
        if (d + length == dictionary.Length && windowStartPosition == 0 && ip + length < end)
        {
            length += Count(window, 0, ip + length, end);
        }

        int start = ip;
        int back = Math.Min(start - anchor, d);
        if (back > 0)
        {
            back = CountBackward(window, start, dictionary, d, back);
            start -= back;
            d -= back;
            length += back;
        }

        long distance = windowStartPosition + start + dictionary.Length - d;
        if (distance > int.MaxValue)
        {
            return false;
        }

        matchStart = start;
        matchLength = length;
        offset = (uint)distance;
        return true;
    }

    private void FindSingle(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        // As FindDoubleNoDictionary: the table is a span walked with native-width positions, and the repeat offsets live in memory.
        // The search itself is FindMatch. Bounds: the table indexes below are a 64-bit product shifted down to the table's log, below
        // its length, and the positions indexed are at most end - 8, so their eight-byte reads are in the window.
        Span<uint> table = _primary;
        int hashShift = 64 - _primaryLog;
        // (v << cut) * P is v * (P << cut): the hash of the low bytes is one multiplication.
        ulong multiplier = HashPrime << (64 - (8 * _minMatch));
        long windowSize = _windowSize;
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        nint limit = end - 8;
        int stride = _stride;
        nint reach = (nint)Math.Min(windowSize, int.MaxValue);
        nint ip = Math.Max(blockStart, 1);
        nint anchor = blockStart;
        while (ip + 1 <= limit)
        {
            nint matchStart = FindMatch(window, ip, end, rep1, stride, table, hashShift, multiplier, windowSize, reach, out uint offset);
            if (matchStart < 0)
            {
                break;
            }

            int matchLength = 4 + CountWide(window, matchStart - (nint)offset + 4, matchStart + 4, end);

            // Extend backward over bytes that also match (only for matches inside the window).
            nint source = matchStart - (nint)offset;
            nint back = Math.Min(matchStart - anchor, source);
            int extra = CountBackward(window, (int)matchStart, window, (int)source, (int)back);
            matchStart -= extra;
            matchLength += extra;

            EmitLocal(window, store, (int)anchor, (int)matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            // Index two positions inside the match and the one just before its end, then look for an immediate repeat.
            nint a = matchStart + 2;
            if (a <= limit)
            {
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, a) * multiplier) >> hashShift), (uint)a);
            }

            nint b = ip - 2;
            if (b <= limit && b > 0)
            {
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, b) * multiplier) >> hashShift), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && (nuint)ip >= second && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (nint)second))
                {
                    length = 4 + CountWide(window, ip - (nint)second + 4, ip + 4, end);
                }
                else
                {
                    break;
                }

                EmitLocal(window, store, (int)ip, (int)ip, length, second, ref rep1, ref rep2, ref rep3);
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, ip) * multiplier) >> hashShift), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[(int)anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
    }

    // The fast strategy's search, apart from the code that emits matches so that its few values stay in registers. Starting at
    // `ip` with the base step, it hashes two neighbouring positions per round, writes both into the table, and tests in this order
    // the repeat offset at ip + step, the candidate of ip and the candidate of ip + 1. Returns the position of the first match
    // (its offset in `offset`; the caller measures its length), or -1 when the block has no more positions to try. The step grows by
    // one every SearchStrengthShift-sized stretch without a match.
    //
    // The rounds run while ip + step <= end - 8, which keeps every read inside the window with no further test: the eight bytes at
    // ip + step are read once, tested against the repeat offset and carried into the next round as the bytes at its ip. The few
    // rounds closer to the end go to FindMatchNearEnd, which tests each read. A candidate is read before its distance is checked: the
    // candidate is a position stored earlier, so the read is inside the window, and a candidate whose bytes differ is the usual case.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint FindMatch(
        ReadOnlySpan<byte> window,
        nint ip,
        int end,
        uint repeatOffset,
        int stride,
        Span<uint> table,
        int hashShift,
        ulong multiplier,
        long windowSize,
        nint reach,
        out uint offset)
    {
        // Bounds: see above; table indexes are a 64-bit product shifted down to the table's log, below its length. The tables, shift,
        // multiplier and reach are the caller's, worked out once per block rather than once per match.
        // A repeat offset beyond the window is never used; one that can't be reached from any position tests as never matching.
        nuint rep = repeatOffset <= windowSize ? repeatOffset : uint.MaxValue;
        nint limit = end - 8;
        int step = stride;
        nint nextStep = ip + (1 << SearchStrengthShift);
        while (true)
        {
            nint roundsEnd = limit - step + 1;
            if (ip >= roundsEnd)
            {
                break;
            }

            nint phaseEnd = Math.Min(nextStep, roundsEnd);
            ulong v0 = Raw.ReadUInt64(window, ip);
            do
            {
                nint ip1 = ip + 1;
                nint ip2 = ip + step;
                ulong v1 = Raw.ReadUInt64(window, ip1);
                nuint h0 = (nuint)((v0 * multiplier) >> hashShift);
                nuint h1 = (nuint)((v1 * multiplier) >> hashShift);
                nint candidate0 = (nint)Raw.Get(table, h0);
                nint candidate1 = (nint)Raw.Get(table, h1);
                Raw.Set(table, h0, (uint)ip);
                Raw.Set(table, h1, (uint)ip1);
                ulong v2 = Raw.ReadUInt64(window, ip2);

                if ((nuint)ip2 >= rep && (uint)v2 == Raw.ReadUInt32(window, ip2 - (nint)rep))
                {
                    offset = (uint)rep;
                    return ip2;
                }

                if (Raw.ReadUInt32(window, candidate0) == (uint)v0 && ip - candidate0 <= reach)
                {
                    offset = (uint)(ip - candidate0);
                    return ip;
                }

                if (Raw.ReadUInt32(window, candidate1) == (uint)v1 && ip1 - candidate1 <= reach)
                {
                    offset = (uint)(ip1 - candidate1);
                    return ip1;
                }

                ip = ip2;
                v0 = v2;
            }
            while (ip < phaseEnd);

            if (ip >= nextStep)
            {
                step++;
                nextStep += 1 << SearchStrengthShift;
            }
        }

        return FindMatchNearEnd(window, ip, end, repeatOffset, step, nextStep, out offset);
    }

    // The rounds of FindMatch that are too close to the end of the block for its unchecked reads: the same search with each read tested.
    private nint FindMatchNearEnd(ReadOnlySpan<byte> window, nint ip, int end, uint repeatOffset, int step, nint nextStep, out uint offset)
    {
        // Bounds: ip + 1 <= end - 8, so both eight-byte reads are in the window; table indexes as in FindMatch.
        Span<uint> table = _primary;
        int hashShift = 64 - _primaryLog;
        ulong multiplier = HashPrime << (64 - (8 * _minMatch));
        long windowSize = _windowSize;
        nint reach = (nint)Math.Min(windowSize, int.MaxValue);
        nint limit = end - 8;
        while (ip + 1 <= limit)
        {
            nint ip1 = ip + 1;
            nint ip2 = ip + step;
            ulong v0 = Raw.ReadUInt64(window, ip);
            ulong v1 = Raw.ReadUInt64(window, ip1);
            nuint h0 = (nuint)((v0 * multiplier) >> hashShift);
            nuint h1 = (nuint)((v1 * multiplier) >> hashShift);
            nint candidate0 = (nint)Raw.Get(table, h0);
            nint candidate1 = (nint)Raw.Get(table, h1);
            Raw.Set(table, h0, (uint)ip);
            Raw.Set(table, h1, (uint)ip1);

            if (ip2 + 4 <= end && (nuint)ip2 >= repeatOffset && repeatOffset <= windowSize && Raw.ReadUInt32(window, ip2) == Raw.ReadUInt32(window, ip2 - (nint)repeatOffset))
            {
                offset = repeatOffset;
                return ip2;
            }

            if (ip - candidate0 <= reach && Raw.ReadUInt32(window, candidate0) == (uint)v0)
            {
                offset = (uint)(ip - candidate0);
                return ip;
            }

            if (ip1 - candidate1 <= reach && Raw.ReadUInt32(window, candidate1) == (uint)v1)
            {
                offset = (uint)(ip1 - candidate1);
                return ip1;
            }

            ip = ip2;
            if (ip >= nextStep)
            {
                step++;
                nextStep += 1 << SearchStrengthShift;
            }
        }

        offset = 0;
        return -1;
    }

    // With a dictionary attached the search looks at one position at a time: the repeat offset, the table of the data so far,
    // then the dictionary's table. The dictionary's content is the more valuable history, and its matches are worth looking for
    // at every position. The search itself is SearchWithDictionary, apart from the code that emits matches so that its few values
    // stay in registers.
    private void FindSingleWithDictionary(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        // Bounds: the table indexes below are a 64-bit product shifted down to the table's log, below its length, and the positions
        // indexed are at most end - 8, so their eight-byte reads are in the window.
        Span<uint> table = _primary;
        int hashShift = 64 - _primaryLog;
        // (v << cut) * P is v * (P << cut): the hash of the low bytes is one multiplication.
        ulong multiplier = HashPrime << (64 - (8 * _minMatch));
        long windowSize = _windowSize;
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        int baseStep = _stride >> 1;
        uint[] dictionaryTable = _dictionaryIndex!.Primary;
        int dictionaryShift = 64 - _dictionaryPrimaryLog;
        ReadOnlySpan<byte> dictionaryContent = _dictionary!.ContentSpan;
        uint tagMask = _tagMask, positionMask = _positionMask;
        nint limit = end - 8;
        nint ip = Math.Max(blockStart, 1);
        nint anchor = blockStart;
        while (ip <= limit)
        {
            nint matchStart = SearchWithDictionary(window, ip, anchor, end, windowStartPosition, rep1, baseStep, table, hashShift, multiplier, windowSize, dictionaryTable, dictionaryShift, dictionaryContent, tagMask, positionMask, out int matchLength, out uint offset);
            if (matchStart < 0)
            {
                break;
            }

            nint source = matchStart - (nint)offset;
            nint back = Math.Min(matchStart - anchor, source);
            if (back > 0)
            {
                int extra = CountBackward(window, (int)matchStart, window, (int)source, (int)back);
                matchStart -= extra;
                matchLength += extra;
            }

            EmitLocal(window, store, (int)anchor, (int)matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            nint a = matchStart + 2;
            if (a <= limit)
            {
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, a) * multiplier) >> hashShift), (uint)a);
            }

            nint b = ip - 2;
            if (b <= limit && b > 0)
            {
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, b) * multiplier) >> hashShift), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (nint)second))
                {
                    length = 4 + CountWide(window, ip - (nint)second + 4, ip + 4, end);
                }
                else if (second <= (uint)ip || !DictionaryRepeatMayMatch(window, (int)ip, end, windowStartPosition, second) || (length = DictionaryRepeatLength(window, (int)ip, end, windowStartPosition, second)) == 0)
                {
                    break;
                }

                EmitLocal(window, store, (int)ip, (int)ip, length, second, ref rep1, ref rep2, ref rep3);
                Raw.Set(table, (nuint)((Raw.ReadUInt64(window, ip) * multiplier) >> hashShift), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[(int)anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
    }

    // The search of FindSingleWithDictionary. From `ip` it looks at one position at a time and writes it into the table, then tests in
    // this order the first repeat offset at the next position (inside the data, else reaching into the dictionary), the table's
    // candidate, and the dictionary table's candidate. Returns the position of the first match (its length counted forward and its
    // offset in the outs; the caller extends it backward), or -1 when the block has no more positions to try. A miss steps ahead
    // by the base step plus one for every 256 bytes since the last match.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint SearchWithDictionary(
        ReadOnlySpan<byte> window,
        nint ip,
        nint anchor,
        int end,
        long windowStartPosition,
        uint repeatOffset,
        int baseStep,
        Span<uint> table,
        int hashShift,
        ulong multiplier,
        long windowSize,
        uint[] dictionaryTable,
        int dictionaryShift,
        ReadOnlySpan<byte> dictionaryContent,
        uint tagMask,
        uint positionMask,
        out int matchLength,
        out uint offset)
    {
        // Bounds: ip <= end - 8 so the eight-byte read at ip is inside the window and so is the four-byte read at ip + 1; a table
        // index is a product shifted down to the table's log (the search table's, or the dictionary index's), below its length; a
        // search table entry is a position stored earlier, and a dictionary entry is a content position with at least eight bytes
        // after it. A repeat offset beyond the window never matches inside the data.
        nuint inWindow = repeatOffset <= windowSize ? repeatOffset : nuint.MaxValue;

        // The positions whose repeat offset lands in the dictionary's content (RFC 8878 §3.1.1.1.2: with at least four bytes left
        // there, and no further back than the window reaches) form one range, tested with one comparison: the distance from its start,
        // as an unsigned number, within its span. The range is empty after the frame's first window, when the offset is too small to
        // leave the data, and when the offset is longer than the content; it then starts at -1, which no position is.
        nint repeatLow = -1;
        nuint repeatSpan = 0;
        if (windowStartPosition == 0)
        {
            long low = Math.Max((long)repeatOffset - dictionaryContent.Length, 0);
            long high = Math.Min(Math.Min((long)repeatOffset - 4, windowSize), int.MaxValue);
            if (high >= low)
            {
                repeatLow = (nint)low;
                repeatSpan = (nuint)(high - low);
            }
        }

        nint limit = end - 8;
        while (ip <= limit)
        {
            ulong value = Raw.ReadUInt64(window, ip);
            uint low = (uint)value;
            ulong product = value * multiplier;
            nuint h = (nuint)(product >> hashShift);
            nint candidate = (nint)Raw.Get(table, h);
            Raw.Set(table, h, (uint)ip);

            nint ip1 = ip + 1;
            if ((nuint)ip1 >= inWindow && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (nint)inWindow))
            {
                offset = repeatOffset;
                matchLength = 4 + CountWide(window, ip1 - (nint)inWindow + 4, ip1 + 4, end);
                return ip1;
            }

            if ((nuint)(ip1 - repeatLow) <= repeatSpan
                && Raw.ReadUInt32(dictionaryContent, dictionaryContent.Length + ip1 - (nint)repeatOffset) == Raw.ReadUInt32(window, ip1))
            {
                int length = DictionaryRepeatLength(window, (int)ip1, end, windowStartPosition, repeatOffset);
                if (length > 0)
                {
                    offset = repeatOffset;
                    matchLength = length;
                    return ip1;
                }
            }

            if (candidate != 0 && ip - candidate <= windowSize && Raw.ReadUInt32(window, candidate) == low)
            {
                offset = (uint)(ip - candidate);
                matchLength = 4 + CountWide(window, candidate + 4, ip + 4, end);
                return ip;
            }

            uint entry = Raw.Get(dictionaryTable, (nuint)(product >> dictionaryShift));
            nint d = (nint)(entry & positionMask);
            if (d != 0 && ((entry ^ Tag(low)) & tagMask) == 0 && Raw.ReadUInt32(dictionaryContent, d) == low)
            {
                if (ExtendDictionaryMatch(dictionaryContent, (int)d, window, (int)ip, (int)anchor, end, windowStartPosition, false, out int extendedStart, out int extendedLength, out uint extendedOffset))
                {
                    offset = extendedOffset;
                    matchLength = extendedLength;
                    return extendedStart;
                }
            }

            ip += baseStep + ((ip - anchor) >> 8);
        }

        matchLength = 0;
        offset = 0;
        return -1;
    }

    // The double-fast strategy with a dictionary attached. The search itself is SearchDoubleWithDictionary, apart from the code that
    // emits matches so that its few values stay in registers.
    private void FindDouble(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        // Bounds: table indexes below are a 64-bit product shifted down to the table's log, below its length (BeginFrame allocates at
        // least 1 << log entries); positions indexed are at most end - 8, so their eight-byte reads are in the window.
        Span<uint> longTable = _primary;
        Span<uint> shortTable = _secondary;
        int longShift = 64 - _primaryLog;
        int shortShift = 64 - _secondaryLog;
        // The short hash multiplies the low bytes only: (v << cut) * P is v * (P << cut).
        ulong shortMultiplier = HashPrime << (64 - (8 * _minMatch));
        long windowSize = _windowSize;
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        ZstdDictionaryIndex dictionaryIndex = _dictionaryIndex!;
        ReadOnlySpan<byte> dictionaryContent = _dictionary!.ContentSpan;
        nint limit = end - 8;
        nint ip = Math.Max(blockStart, 1);
        nint anchor = blockStart;
        while (ip <= limit)
        {
            nint matchStart = SearchDoubleWithDictionary(window, ip, anchor, end, windowStartPosition, rep1, longTable, shortTable, longShift, shortShift, shortMultiplier, windowSize, dictionaryIndex, dictionaryContent, out int matchLength, out uint offset);
            if (matchStart < 0)
            {
                break;
            }

            nint source = matchStart - (nint)offset;
            nint back = Math.Min(matchStart - anchor, source);
            if (back > 0)
            {
                int extra = CountBackward(window, (int)matchStart, window, (int)source, (int)back);
                matchStart -= extra;
                matchLength += extra;
            }

            EmitLocal(window, store, (int)anchor, (int)matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            nint a = matchStart + 2;
            if (a <= limit)
            {
                ulong v = Raw.ReadUInt64(window, a);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)a);
                Raw.Set(shortTable, (nuint)((v * shortMultiplier) >> shortShift), (uint)a);
            }

            nint b = ip - 2;
            if (b <= limit && b > 0)
            {
                ulong v = Raw.ReadUInt64(window, b);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)b);
                Raw.Set(shortTable, (nuint)((v * shortMultiplier) >> shortShift), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (nint)second))
                {
                    length = 4 + CountWide(window, ip - (nint)second + 4, ip + 4, end);
                }
                else if (second <= (uint)ip || !DictionaryRepeatMayMatch(window, (int)ip, end, windowStartPosition, second) || (length = DictionaryRepeatLength(window, (int)ip, end, windowStartPosition, second)) == 0)
                {
                    break;
                }

                EmitLocal(window, store, (int)ip, (int)ip, length, second, ref rep1, ref rep2, ref rep3);
                ulong v = Raw.ReadUInt64(window, ip);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)ip);
                Raw.Set(shortTable, (nuint)((v * shortMultiplier) >> shortShift), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[(int)anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
    }

    // The search of FindDouble. From `ip` it looks at one position at a time and writes it into both tables, then tests in this order
    // the first repeat offset at the next position (inside the data, else reaching into the dictionary), the long candidate, the
    // short candidate (for which it also tries the long candidate of the next position, as it is worth more), and the dictionary's
    // long and short candidates. Returns the position of the first match (its length counted forward and its offset in the outs; the
    // caller extends it backward), or -1 when the block has no more positions to try. A miss steps ahead by one plus one for every
    // 256 bytes since the last match.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private nint SearchDoubleWithDictionary(
        ReadOnlySpan<byte> window,
        nint ip,
        nint anchor,
        int end,
        long windowStartPosition,
        uint repeatOffset,
        Span<uint> longTable,
        Span<uint> shortTable,
        int longShift,
        int shortShift,
        ulong shortMultiplier,
        long windowSize,
        ZstdDictionaryIndex dictionaryIndex,
        ReadOnlySpan<byte> dictionaryContent,
        out int matchLength,
        out uint offset)
    {
        // Bounds: as SearchWithDictionary; a long candidate is a position stored earlier, read eight bytes wide, and the next
        // position's eight-byte read is inside the window when it is at most end - 8.
        nuint inWindow = repeatOffset <= windowSize ? repeatOffset : nuint.MaxValue;

        // See SearchWithDictionary: the positions whose repeat offset lands in the dictionary's content, as one range.
        nint repeatLow = -1;
        nuint repeatSpan = 0;
        if (windowStartPosition == 0)
        {
            long low = Math.Max((long)repeatOffset - dictionaryContent.Length, 0);
            long high = Math.Min(Math.Min((long)repeatOffset - 4, windowSize), int.MaxValue);
            if (high >= low)
            {
                repeatLow = (nint)low;
                repeatSpan = (nuint)(high - low);
            }
        }

        uint[] dictionaryLong = dictionaryIndex.Primary;
        uint[] dictionaryShort = dictionaryIndex.Secondary!;
        int dictionaryLongShift = 64 - _dictionaryPrimaryLog;
        int dictionaryShortShift = 64 - _dictionarySecondaryLog;
        uint tagMask = _tagMask, positionMask = _positionMask;
        nint limit = end - 8;
        while (ip <= limit)
        {
            ulong value = Raw.ReadUInt64(window, ip);
            uint low4 = (uint)value;
            ulong longProduct = value * HashPrime;
            ulong shortProduct = value * shortMultiplier;
            nuint hl = (nuint)(longProduct >> longShift);
            nuint hs = (nuint)(shortProduct >> shortShift);
            nint candidateLong = (nint)Raw.Get(longTable, hl);
            nint candidateShort = (nint)Raw.Get(shortTable, hs);
            Raw.Set(longTable, hl, (uint)ip);
            Raw.Set(shortTable, hs, (uint)ip);

            nint ip1 = ip + 1;
            if ((nuint)ip1 >= inWindow && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (nint)inWindow))
            {
                offset = repeatOffset;
                matchLength = 4 + CountWide(window, ip1 - (nint)inWindow + 4, ip1 + 4, end);
                return ip1;
            }

            if ((nuint)(ip1 - repeatLow) <= repeatSpan
                && Raw.ReadUInt32(dictionaryContent, dictionaryContent.Length + ip1 - (nint)repeatOffset) == Raw.ReadUInt32(window, ip1))
            {
                int length = DictionaryRepeatLength(window, (int)ip1, end, windowStartPosition, repeatOffset);
                if (length > 0)
                {
                    offset = repeatOffset;
                    matchLength = length;
                    return ip1;
                }
            }

            if (candidateLong != 0 && ip - candidateLong <= windowSize && Raw.ReadUInt64(window, candidateLong) == value)
            {
                offset = (uint)(ip - candidateLong);
                matchLength = 8 + CountWide(window, candidateLong + 8, ip + 8, end);
                return ip;
            }

            if (candidateShort != 0 && ip - candidateShort <= windowSize && Raw.ReadUInt32(window, candidateShort) == low4)
            {
                // A short match: see whether the next position has a long one, which is worth more.
                nint matchStart = ip;
                offset = (uint)(ip - candidateShort);
                matchLength = 4 + CountWide(window, candidateShort + 4, ip + 4, end);
                if (ip1 <= limit)
                {
                    ulong next = Raw.ReadUInt64(window, ip1);
                    nuint hl1 = (nuint)((next * HashPrime) >> longShift);
                    nint candidateLong1 = (nint)Raw.Get(longTable, hl1);
                    Raw.Set(longTable, hl1, (uint)ip1);
                    if (candidateLong1 != 0 && ip1 - candidateLong1 <= windowSize && Raw.ReadUInt64(window, candidateLong1) == next)
                    {
                        matchStart = ip1;
                        offset = (uint)(ip1 - candidateLong1);
                        matchLength = 8 + CountWide(window, candidateLong1 + 8, ip1 + 8, end);
                    }
                }

                return matchStart;
            }

            // The dictionary's tables: the eight-byte index first, then the one for the shortest match.
            uint longEntry = Raw.Get(dictionaryLong, (nuint)(longProduct >> dictionaryLongShift));
            nint longPosition = (nint)(longEntry & positionMask);
            if (longPosition != 0
                && ((longEntry ^ Tag(low4)) & tagMask) == 0
                && Raw.ReadUInt64(dictionaryContent, longPosition) == value
                && ExtendDictionaryMatch(dictionaryContent, (int)longPosition, window, (int)ip, (int)anchor, end, windowStartPosition, true, out int longStart, out int longLength, out uint longOffset))
            {
                offset = longOffset;
                matchLength = longLength;
                return longStart;
            }

            uint shortEntry = Raw.Get(dictionaryShort, (nuint)(shortProduct >> dictionaryShortShift));
            nint shortPosition = (nint)(shortEntry & positionMask);
            if (shortPosition != 0
                && ((shortEntry ^ Tag(low4)) & tagMask) == 0
                && Raw.ReadUInt32(dictionaryContent, shortPosition) == low4
                && ExtendDictionaryMatch(dictionaryContent, (int)shortPosition, window, (int)ip, (int)anchor, end, windowStartPosition, false, out int shortStart, out int shortLength, out uint shortOffset))
            {
                offset = shortOffset;
                matchLength = shortLength;
                return shortStart;
            }

            ip += 1 + ((ip - anchor) >> 8);
        }

        matchLength = 0;
        offset = 0;
        return -1;
    }

    private void FindDoubleNoDictionary(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        // The search itself is SearchDouble. The repeat offsets live in memory (the emit helper updates them through a reference).
        // Bounds: table indexes below are a 64-bit product shifted down to the table's log, below its length (BeginFrame allocates at
        // least 1 << log entries); positions indexed are at most end - 8, so their eight-byte reads are in the window.
        Span<uint> longTable = _primary;
        Span<uint> shortTable = _secondary;
        int longShift = 64 - _primaryLog;
        int shortShift = 64 - _secondaryLog;
        // The short hash multiplies the low bytes only: (v << cut) * P is v * (P << cut).
        ulong shortMultiplier = HashPrime << (64 - (8 * _minMatch));
        long windowSize = _windowSize;
        nint reach = (nint)Math.Min(windowSize, int.MaxValue);
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        // The first repeat offset as the search tests it: one beyond the window is never used, and uint.MaxValue is never reachable.
        nuint rep = rep1 <= windowSize ? rep1 : uint.MaxValue;
        nint limit = end - 8;
        nint ip = Math.Max(blockStart, 1);
        nint anchor = blockStart;
        while (ip <= limit)
        {
            nint matchStart = SearchDouble(window, ip, anchor, end, rep, longTable, shortTable, longShift, shortShift, shortMultiplier, reach, out int matchLength, out uint offset);
            if (matchStart < 0)
            {
                break;
            }

            nint source = matchStart - (nint)offset;
            nint back = Math.Min(matchStart - anchor, source);
            int extra = CountBackward(window, (int)matchStart, window, (int)source, (int)back);
            matchStart -= extra;
            matchLength += extra;

            EmitLocal(window, store, (int)anchor, (int)matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            rep = rep1 <= windowSize ? rep1 : uint.MaxValue;
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            nint a = matchStart + 2;
            if (a <= limit)
            {
                ulong v = Raw.ReadUInt64(window, a);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)a);
                Raw.Set(shortTable, (nuint)((v * shortMultiplier) >> shortShift), (uint)a);
            }

            nint b = ip - 2;
            if (b <= limit && b > 0)
            {
                // The long table gets the position two before the match's end, the short one the position just before it, as that is the
                // one a later short match is most likely to start from (measured: smaller output than entering the same position in both).
                ulong v = Raw.ReadUInt64(window, b);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)b);
                nint b1 = b + 1;
                ulong v1 = b1 <= limit ? Raw.ReadUInt64(window, b1) : v;
                nint at = b1 <= limit ? b1 : b;
                Raw.Set(shortTable, (nuint)((v1 * shortMultiplier) >> shortShift), (uint)at);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && (nuint)ip >= second && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (nint)second))
                {
                    length = 4 + CountWide(window, ip - (nint)second + 4, ip + 4, end);
                }
                else
                {
                    break;
                }

                EmitLocal(window, store, (int)ip, (int)ip, length, second, ref rep1, ref rep2, ref rep3);
                rep = rep1 <= windowSize ? rep1 : uint.MaxValue;
                ulong v = Raw.ReadUInt64(window, ip);
                Raw.Set(longTable, (nuint)((v * HashPrime) >> longShift), (uint)ip);
                Raw.Set(shortTable, (nuint)((v * shortMultiplier) >> shortShift), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[(int)anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
    }

    // The double-fast strategy's search, apart from the code that emits matches so that its few values stay in registers. From `ip`
    // it hashes one position at a time into both tables and tests in this order the repeat offset at ip + 1, the long candidate of
    // ip and the short candidate of ip (for which it also tries the long candidate of ip + 1, as it is worth more). Returns the
    // position of the first match, with its length counted forward and its offset; the caller extends it backward. Returns -1 when
    // the block has no more positions to try. The step grows by one for every 256 bytes without a match.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint SearchDouble(
        ReadOnlySpan<byte> window,
        nint ip,
        nint anchor,
        int end,
        nuint rep,
        Span<uint> longTable,
        Span<uint> shortTable,
        int longShift,
        int shortShift,
        ulong shortMultiplier,
        nint reach,
        out int matchLength,
        out uint offset)
    {
        // Bounds: see FindDoubleNoDictionary; ip <= end - 8 so the eight-byte read at ip is inside the window, and a candidate is a
        // position stored earlier, at most end - 8.
        nint limit = end - 8;
        while (ip <= limit)
        {
            ulong value = Raw.ReadUInt64(window, ip);
            nuint hl = (nuint)((value * HashPrime) >> longShift);
            nuint hs = (nuint)((value * shortMultiplier) >> shortShift);
            nint candidateLong = (nint)Raw.Get(longTable, hl);
            nint candidateShort = (nint)Raw.Get(shortTable, hs);
            Raw.Set(longTable, hl, (uint)ip);
            Raw.Set(shortTable, hs, (uint)ip);

            nint ip1 = ip + 1;
            if ((nuint)ip1 >= rep && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (nint)rep))
            {
                offset = (uint)rep;
                matchLength = 4 + CountWide(window, ip1 - (nint)rep + 4, ip1 + 4, end);
                return ip1;
            }

            if (Raw.ReadUInt64(window, candidateLong) == value && ip - candidateLong <= reach)
            {
                offset = (uint)(ip - candidateLong);
                matchLength = 8 + CountWide(window, candidateLong + 8, ip + 8, end);
                return ip;
            }

            if (Raw.ReadUInt32(window, candidateShort) == (uint)value && ip - candidateShort <= reach)
            {
                // A short match: see whether the next position has a long one, which is worth more.
                nint matchStart = ip;
                offset = (uint)(ip - candidateShort);
                matchLength = 4 + CountWide(window, candidateShort + 4, ip + 4, end);
                if (ip1 <= limit)
                {
                    ulong ahead = Raw.ReadUInt64(window, ip1);
                    nuint hl1 = (nuint)((ahead * HashPrime) >> longShift);
                    nint candidateLong1 = (nint)Raw.Get(longTable, hl1);
                    Raw.Set(longTable, hl1, (uint)ip1);
                    if (Raw.ReadUInt64(window, candidateLong1) == ahead && ip1 - candidateLong1 <= reach)
                    {
                        matchStart = ip1;
                        offset = (uint)(ip1 - candidateLong1);
                        matchLength = 8 + CountWide(window, candidateLong1 + 8, ip1 + 8, end);
                    }
                }

                return matchStart;
            }

            ip += 1 + ((ip - anchor) >> 8);
        }

        matchLength = 0;
        offset = 0;
        return -1;
    }

    // As EmitLocal, with the repeat offsets in one struct the caller passes by reference.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLocal(
        ReadOnlySpan<byte> window,
        ZstdSequenceStore store,
        int anchor,
        int matchStart,
        int matchLength,
        uint offset,
        ref ZstdRepeatOffsets state)
    {
        uint literalLength = (uint)(matchStart - anchor);
        uint offsetValue = state.Apply(offset, literalLength);
        store.AppendTracked(window, anchor, literalLength, (uint)matchLength, offset, offsetValue);
    }

    // As Emit, with the repeat offsets in the caller's locals so they stay in registers. The block's offsets are handed to the
    // store once, when the search ends (SetTrackedEnd).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitLocal(
        ReadOnlySpan<byte> window,
        ZstdSequenceStore store,
        int anchor,
        int matchStart,
        int matchLength,
        uint offset,
        ref uint rep1,
        ref uint rep2,
        ref uint rep3)
    {
        uint literalLength = (uint)(matchStart - anchor);
        uint offsetValue;
        if (literalLength > 0)
        {
            // RFC 8878 3.1.2.5: with literals, offset codes 1 to 3 mean the three most recent offsets.
            if (offset == rep1)
            {
                offsetValue = 1;
            }
            else if (offset == rep2)
            {
                offsetValue = 2;
                rep2 = rep1;
                rep1 = offset;
            }
            else if (offset == rep3)
            {
                offsetValue = 3;
                rep3 = rep2;
                rep2 = rep1;
                rep1 = offset;
            }
            else
            {
                offsetValue = offset + 3;
                rep3 = rep2;
                rep2 = rep1;
                rep1 = offset;
            }
        }
        else
        {
            var r = new ZstdRepeatOffsets { Offset1 = rep1, Offset2 = rep2, Offset3 = rep3 };
            offsetValue = r.Apply(offset, 0);
            rep1 = r.Offset1;
            rep2 = r.Offset2;
            rep3 = r.Offset3;
        }

        store.AppendTracked(window, anchor, literalLength, (uint)matchLength, offset, offsetValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Emit(
        ReadOnlySpan<byte> window,
        ZstdSequenceStore store,
        int anchor,
        int matchStart,
        int matchLength,
        uint offset,
        ref ZstdRepeatOffsets reps)
    {
        uint literalLength = (uint)(matchStart - anchor);
        uint offsetValue = reps.Apply(offset, literalLength);
        store.AppendTracked(window, anchor, literalLength, (uint)matchLength, offset, offsetValue);
        store.SetTrackedEnd(reps.Offset1, reps.Offset2, reps.Offset3);
    }
}
