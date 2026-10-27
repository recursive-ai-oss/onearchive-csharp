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

    internal static int Hash(ReadOnlySpan<byte> data, int index, int minMatch, int log) =>
        HashValue(Raw.ReadUInt64(data, index), 64 - (8 * minMatch), log);

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
    // do not exceed the window size (RFC 8878 §3.1.1.1.2).
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
        int d = (int)indexTable[Hash(window, ip, minMatch, tableLog)];
        if (d == 0)
        {
            return false;
        }

        // Table entries are positions with at least eight bytes after them, so reading eight bytes is in range.
        if (requireEight ? Raw.ReadUInt64(dictionary, d) != Raw.ReadUInt64(window, ip) : Raw.ReadUInt32(dictionary, d) != Raw.ReadUInt32(window, ip))
        {
            return false;
        }

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
        while (start > anchor && d > 0 && Raw.ByteAt(window, start - 1) == Raw.ByteAt(dictionary, d - 1))
        {
            start--;
            d--;
            length++;
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
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        uint[] table = _primary;
        int log = _primaryLog;
        int mls = _minMatch;
        int shift = 64 - (8 * mls);
        long windowSize = _windowSize;
        int baseStep = _stride;
        int limit = end - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        int step = baseStep;
        int nextStep = ip + (1 << SearchStrengthShift);
        while (ip + 1 <= limit)
        {
            // Two neighbouring positions are looked up per round, then the search jumps ahead by the step, which grows the
            // longer the search goes without a match. Bounds: ip + 1 <= end - 8, so both eight-byte reads are in the window;
            // table indexes come from the hash, below 1 << log.
            int ip1 = ip + 1;
            int ip2 = ip + step;
            ulong v0 = Raw.ReadUInt64(window, ip);
            ulong v1 = Raw.ReadUInt64(window, ip1);
            int h0 = HashValue(v0, shift, log);
            int h1 = HashValue(v1, shift, log);
            int candidate0 = (int)Raw.Get(table, h0);
            int candidate1 = (int)Raw.Get(table, h1);
            Raw.Set(table, h0, (uint)ip);
            Raw.Set(table, h1, (uint)ip1);

            int matchStart;
            int matchLength;
            uint offset;
            uint rep = rep1;
            if (ip2 + 4 <= end && rep <= (uint)ip2 && rep <= windowSize && Raw.ReadUInt32(window, ip2) == Raw.ReadUInt32(window, ip2 - (int)rep))
            {
                matchStart = ip2;
                offset = rep;
                matchLength = 4 + Count(window, ip2 - (int)rep + 4, ip2 + 4, end);
            }
            else if (ip - candidate0 <= windowSize && Raw.ReadUInt32(window, candidate0) == (uint)v0)
            {
                matchStart = ip;
                offset = (uint)(ip - candidate0);
                matchLength = 4 + Count(window, candidate0 + 4, ip + 4, end);
            }
            else if (ip1 - candidate1 <= windowSize && Raw.ReadUInt32(window, candidate1) == (uint)v1)
            {
                matchStart = ip1;
                offset = (uint)(ip1 - candidate1);
                matchLength = 4 + Count(window, candidate1 + 4, ip1 + 4, end);
            }
            else
            {
                ip = ip2;
                if (ip >= nextStep)
                {
                    step++;
                    nextStep += 1 << SearchStrengthShift;
                }

                continue;
            }

            // Extend backward over bytes that also match (only for matches inside the window).
            int source = matchStart - (int)offset;
            while (matchStart > anchor && source > 0 && Raw.ByteAt(window, matchStart - 1) == Raw.ByteAt(window, source - 1))
            {
                matchStart--;
                source--;
                matchLength++;
            }

            EmitLocal(window, store, anchor, matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            ip = matchStart + matchLength;
            anchor = ip;
            step = baseStep;
            nextStep = ip + (1 << SearchStrengthShift);
            if (ip > limit)
            {
                break;
            }

            // Index two positions inside the match and the one just before its end, then look for an immediate repeat.
            int a = matchStart + 2;
            if (a <= limit)
            {
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, a), shift, log), (uint)a);
            }

            int b = ip - 2;
            if (b <= limit && b > 0)
            {
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, b), shift, log), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (int)second))
                {
                    length = 4 + Count(window, ip - (int)second + 4, ip + 4, end);
                }
                else
                {
                    break;
                }

                EmitLocal(window, store, ip, ip, length, second, ref rep1, ref rep2, ref rep3);
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, ip), shift, log), (uint)ip);
                ip += length;
                anchor = ip;
                nextStep = ip + (1 << SearchStrengthShift);
            }
        }

        store.AppendLiterals(window[anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
    }

    // With a dictionary attached the search looks at one position at a time: the repeat offset, the table of the data so far,
    // then the dictionary's table. The dictionary's content is the more valuable history, and its matches are worth looking for
    // at every position.
    private void FindSingleWithDictionary(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        uint[] table = _primary;
        int log = _primaryLog;
        int mls = _minMatch;
        int shift = 64 - (8 * mls);
        long windowSize = _windowSize;
        int baseStep = _stride >> 1;
        ZstdDictionaryIndex dictionaryIndex = _dictionaryIndex!;
        int limit = end - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        while (ip <= limit)
        {
            // Bounds: ip <= end - 8 so the eight-byte read at ip is inside the window; table indexes come from the hash.
            int h = HashValue(Raw.ReadUInt64(window, ip), shift, log);
            int candidate = (int)Raw.Get(table, h);
            Raw.Set(table, h, (uint)ip);

            int matchStart;
            int matchLength;
            uint offset;
            int ip1 = ip + 1;
            uint rep = reps.Offset1;
            if (rep <= (uint)ip1 && rep <= windowSize && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (int)rep))
            {
                matchStart = ip1;
                offset = rep;
                matchLength = 4 + Count(window, ip1 - (int)rep + 4, ip1 + 4, end);
            }
            else if ((matchLength = DictionaryRepeatLength(window, ip1, end, windowStartPosition, rep)) > 0)
            {
                matchStart = ip1;
                offset = rep;
            }
            else if (candidate != 0 && ip - candidate <= windowSize && Raw.ReadUInt32(window, candidate) == Raw.ReadUInt32(window, ip))
            {
                matchStart = ip;
                offset = (uint)(ip - candidate);
                matchLength = 4 + Count(window, candidate + 4, ip + 4, end);
            }
            else if (!TryDictionary(window, ip, anchor, end, windowStartPosition, dictionaryIndex.Primary, _dictionaryPrimaryLog, mls, false, out matchStart, out matchLength, out offset))
            {
                ip += baseStep + ((ip - anchor) >> 8);
                continue;
            }

            int source = matchStart - (int)offset;
            while (matchStart > anchor && source > 0 && Raw.ByteAt(window, matchStart - 1) == Raw.ByteAt(window, source - 1))
            {
                matchStart--;
                source--;
                matchLength++;
            }

            Emit(window, store, anchor, matchStart, matchLength, offset, ref reps);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            int a = matchStart + 2;
            if (a <= limit)
            {
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, a), shift, log), (uint)a);
            }

            int b = ip - 2;
            if (b <= limit && b > 0)
            {
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, b), shift, log), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = reps.Offset2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (int)second))
                {
                    length = 4 + Count(window, ip - (int)second + 4, ip + 4, end);
                }
                else if ((length = DictionaryRepeatLength(window, ip, end, windowStartPosition, second)) == 0)
                {
                    break;
                }

                Emit(window, store, ip, ip, length, second, ref reps);
                Raw.Set(table, HashValue(Raw.ReadUInt64(window, ip), shift, log), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[anchor..end]);
    }

    private void FindDouble(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        uint[] longTable = _primary;
        uint[] shortTable = _secondary;
        int longLog = _primaryLog;
        int shortLog = _secondaryLog;
        int mls = _minMatch;
        int shortShift = 64 - (8 * mls);
        long windowSize = _windowSize;
        ZstdDictionaryIndex? dictionaryIndex = _dictionaryIndex;
        int limit = end - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        while (ip <= limit)
        {
            // Bounds: ip <= end - 8 so the eight-byte read at ip is inside the window; table indexes come from the hashes.
            ulong value = Raw.ReadUInt64(window, ip);
            int hl = HashValue(value, 0, longLog);
            int hs = HashValue(value, shortShift, shortLog);
            int candidateLong = (int)Raw.Get(longTable, hl);
            int candidateShort = (int)Raw.Get(shortTable, hs);
            Raw.Set(longTable, hl, (uint)ip);
            Raw.Set(shortTable, hs, (uint)ip);

            int matchStart;
            int matchLength;
            uint offset;
            int ip1 = ip + 1;
            uint rep = reps.Offset1;
            if (rep <= (uint)ip1 && rep <= windowSize && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (int)rep))
            {
                matchStart = ip1;
                offset = rep;
                matchLength = 4 + Count(window, ip1 - (int)rep + 4, ip1 + 4, end);
            }
            else if (dictionaryIndex is not null && (matchLength = DictionaryRepeatLength(window, ip1, end, windowStartPosition, rep)) > 0)
            {
                matchStart = ip1;
                offset = rep;
            }
            else if (candidateLong != 0 && ip - candidateLong <= windowSize && Raw.ReadUInt64(window, candidateLong) == value)
            {
                matchStart = ip;
                offset = (uint)(ip - candidateLong);
                matchLength = 8 + Count(window, candidateLong + 8, ip + 8, end);
            }
            else if (candidateShort != 0 && ip - candidateShort <= windowSize && Raw.ReadUInt32(window, candidateShort) == (uint)value)
            {
                // A short match: see whether the next position has a long one, which is worth more.
                matchStart = ip;
                offset = (uint)(ip - candidateShort);
                matchLength = 4 + Count(window, candidateShort + 4, ip + 4, end);
                if (ip1 <= limit)
                {
                    ulong next = Raw.ReadUInt64(window, ip1);
                    int hl1 = HashValue(next, 0, longLog);
                    int candidateLong1 = (int)Raw.Get(longTable, hl1);
                    Raw.Set(longTable, hl1, (uint)ip1);
                    if (candidateLong1 != 0 && ip1 - candidateLong1 <= windowSize && Raw.ReadUInt64(window, candidateLong1) == next)
                    {
                        matchStart = ip1;
                        offset = (uint)(ip1 - candidateLong1);
                        matchLength = 8 + Count(window, candidateLong1 + 8, ip1 + 8, end);
                    }
                }
            }
            else if (dictionaryIndex is not null
                && (TryDictionary(window, ip, anchor, end, windowStartPosition, dictionaryIndex.Primary, _dictionaryPrimaryLog, 8, true, out matchStart, out matchLength, out offset)
                    || TryDictionary(window, ip, anchor, end, windowStartPosition, dictionaryIndex.Secondary!, _dictionarySecondaryLog, mls, false, out matchStart, out matchLength, out offset)))
            {
                // A match into the dictionary content.
            }
            else
            {
                ip += 1 + ((ip - anchor) >> 8);
                continue;
            }

            int source = matchStart - (int)offset;
            while (matchStart > anchor && source > 0 && Raw.ByteAt(window, matchStart - 1) == Raw.ByteAt(window, source - 1))
            {
                matchStart--;
                source--;
                matchLength++;
            }

            Emit(window, store, anchor, matchStart, matchLength, offset, ref reps);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            int a = matchStart + 2;
            if (a <= limit)
            {
                ulong v = Raw.ReadUInt64(window, a);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)a);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)a);
            }

            int b = ip - 2;
            if (b <= limit && b > 0)
            {
                ulong v = Raw.ReadUInt64(window, b);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)b);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = reps.Offset2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (int)second))
                {
                    length = 4 + Count(window, ip - (int)second + 4, ip + 4, end);
                }
                else if (dictionaryIndex is null || (length = DictionaryRepeatLength(window, ip, end, windowStartPosition, second)) == 0)
                {
                    break;
                }

                Emit(window, store, ip, ip, length, second, ref reps);
                ulong v = Raw.ReadUInt64(window, ip);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)ip);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[anchor..end]);
    }

    private void FindDoubleNoDictionary(
        ReadOnlySpan<byte> window,
        int blockStart,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets reps,
        ZstdSequenceStore store)
    {
        uint[] longTable = _primary;
        uint[] shortTable = _secondary;
        int longLog = _primaryLog;
        int shortLog = _secondaryLog;
        int mls = _minMatch;
        int shortShift = 64 - (8 * mls);
        long windowSize = _windowSize;
        uint rep1 = reps.Offset1, rep2 = reps.Offset2, rep3 = reps.Offset3;
        int limit = end - 8;
        int ip = Math.Max(blockStart, 1);
        int anchor = blockStart;
        while (ip <= limit)
        {
            // Bounds: ip <= end - 8 so the eight-byte read at ip is inside the window; table indexes come from the hashes.
            ulong value = Raw.ReadUInt64(window, ip);
            int hl = HashValue(value, 0, longLog);
            int hs = HashValue(value, shortShift, shortLog);
            int candidateLong = (int)Raw.Get(longTable, hl);
            int candidateShort = (int)Raw.Get(shortTable, hs);
            Raw.Set(longTable, hl, (uint)ip);
            Raw.Set(shortTable, hs, (uint)ip);

            int matchStart;
            int matchLength;
            uint offset;
            int ip1 = ip + 1;
            uint rep = rep1;
            if (rep <= (uint)ip1 && rep <= windowSize && Raw.ReadUInt32(window, ip1) == Raw.ReadUInt32(window, ip1 - (int)rep))
            {
                matchStart = ip1;
                offset = rep;
                matchLength = 4 + Count(window, ip1 - (int)rep + 4, ip1 + 4, end);
            }
            else if (ip - candidateLong <= windowSize && Raw.ReadUInt64(window, candidateLong) == value)
            {
                matchStart = ip;
                offset = (uint)(ip - candidateLong);
                matchLength = 8 + Count(window, candidateLong + 8, ip + 8, end);
            }
            else if (ip - candidateShort <= windowSize && Raw.ReadUInt32(window, candidateShort) == (uint)value)
            {
                // A short match: see whether the next position has a long one, which is worth more.
                matchStart = ip;
                offset = (uint)(ip - candidateShort);
                matchLength = 4 + Count(window, candidateShort + 4, ip + 4, end);
                if (ip1 <= limit)
                {
                    ulong ahead = Raw.ReadUInt64(window, ip1);
                    int hl1 = HashValue(ahead, 0, longLog);
                    int candidateLong1 = (int)Raw.Get(longTable, hl1);
                    Raw.Set(longTable, hl1, (uint)ip1);
                    if (ip1 - candidateLong1 <= windowSize && Raw.ReadUInt64(window, candidateLong1) == ahead)
                    {
                        matchStart = ip1;
                        offset = (uint)(ip1 - candidateLong1);
                        matchLength = 8 + Count(window, candidateLong1 + 8, ip1 + 8, end);
                    }
                }
            }
            else
            {
                ip += 1 + ((ip - anchor) >> 8);
                continue;
            }

            int source = matchStart - (int)offset;
            while (matchStart > anchor && source > 0 && Raw.ByteAt(window, matchStart - 1) == Raw.ByteAt(window, source - 1))
            {
                matchStart--;
                source--;
                matchLength++;
            }

            EmitLocal(window, store, anchor, matchStart, matchLength, offset, ref rep1, ref rep2, ref rep3);
            ip = matchStart + matchLength;
            anchor = ip;
            if (ip > limit)
            {
                break;
            }

            int a = matchStart + 2;
            if (a <= limit)
            {
                ulong v = Raw.ReadUInt64(window, a);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)a);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)a);
            }

            int b = ip - 2;
            if (b <= limit && b > 0)
            {
                ulong v = Raw.ReadUInt64(window, b);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)b);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)b);
            }

            while (ip <= limit)
            {
                uint second = rep2;
                int length;
                if (second != 0 && second <= (uint)ip && second <= windowSize && Raw.ReadUInt32(window, ip) == Raw.ReadUInt32(window, ip - (int)second))
                {
                    length = 4 + Count(window, ip - (int)second + 4, ip + 4, end);
                }
                else
                {
                    break;
                }

                EmitLocal(window, store, ip, ip, length, second, ref rep1, ref rep2, ref rep3);
                ulong v = Raw.ReadUInt64(window, ip);
                Raw.Set(longTable, HashValue(v, 0, longLog), (uint)ip);
                Raw.Set(shortTable, HashValue(v, shortShift, shortLog), (uint)ip);
                ip += length;
                anchor = ip;
            }
        }

        store.AppendLiterals(window[anchor..end]);
        store.SetTrackedEnd(rep1, rep2, rep3);
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
