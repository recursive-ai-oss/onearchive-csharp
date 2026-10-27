using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>Selects, at compile time, whether the fast compressor also looks matches up in a dictionary.</summary>
internal interface IDictionaryMode
{
    static abstract bool Enabled { get; }
}

internal readonly struct NoDictionary : IDictionaryMode
{
    public static bool Enabled => false;
}

internal readonly struct WithDictionary : IDictionaryMode
{
    public static bool Enabled => true;
}

/// <summary>
/// The fast LZ4 match finder (levels 1 and below): one hash table of recent positions, indexed by a hash of five bytes, and a
/// greedy parse. At each position it looks up the position the same five bytes last occurred at; a candidate whose first four
/// bytes agree and lies within 64 KiB becomes a match, extended backwards over pending literals and forwards as far as it
/// goes. While nothing matches, the step between probes grows by one every 64 failed probes (scaled by the acceleration
/// of levels below 1), so incompressible data is skipped quickly. After a match, the position two bytes before its end is
/// indexed and the position at its end is tried at once.
/// </summary>
/// <remarks>
/// <para>Table entries are positions in a running coordinate: the window's first byte has coordinate <c>origin</c>. Starting
/// a block without history moves the origin past everything seen, so stale entries are further than 64 KiB away and are
/// ignored; no table is cleared per block, which keeps small blocks cheap. A stale entry and an empty one are treated
/// identically, so output never depends on earlier blocks.</para>
/// <para>The parse is the same on every platform: integer arithmetic, a fixed multiplicative hash, no vector decisions.</para>
/// </remarks>
public sealed class Lz4FastBlockCompressor : Lz4BlockCompressor
{
    /// <summary>The default table size, 2^12 entries (16 KiB), small enough to stay in the first-level cache.</summary>
    public const int DefaultHashLog = 12;

    /// <summary>The largest table size, 2^20 entries (4 MiB).</summary>
    public const int MaxHashLog = 20;

    private const int SkipTrigger = 6;
    private const long InitialOrigin = 1 << 16;

    private readonly uint[] _table;
    private readonly int _acceleration;
    private readonly int _hashLog;
    private readonly int _stride;
    private long _origin = InitialOrigin;
    private int _used;
    private Lz4PreparedDictionary? _dictionary;

    /// <summary>Creates a fast compressor.</summary>
    /// <param name="acceleration">1 for the default fast level; larger values skip faster through incompressible data (level <c>1 - acceleration</c>), up to 65537.</param>
    /// <param name="hashLog">The table holds 2^<paramref name="hashLog"/> positions, from 8 to <see cref="MaxHashLog"/>; a larger table finds more of the older matches at the cost of memory and speed (level 2 uses 16).</param>
    /// <param name="interiorStride">0 indexes only the probed positions and the one two bytes before a match's end; a positive value also indexes every <paramref name="interiorStride"/>th position inside each match (level 2 uses 2).</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public Lz4FastBlockCompressor(int acceleration = 1, int hashLog = DefaultHashLog, int interiorStride = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(acceleration, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(acceleration, 65537);
        ArgumentOutOfRangeException.ThrowIfLessThan(hashLog, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashLog, MaxHashLog);
        ArgumentOutOfRangeException.ThrowIfNegative(interiorStride);
        _acceleration = acceleration;
        _hashLog = hashLog;
        _stride = interiorStride;
        _table = new uint[1 << hashLog];
    }

    /// <summary>Gets the acceleration: 1 for level 1, and <c>1 - level</c> for lower levels.</summary>
    public int Acceleration => _acceleration;

    /// <summary>Hashes the low five bytes of eight (a little-endian read) to a table index of <c>64 - shift</c> bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Hash(ulong eightBytes, int shift) => (int)(((eightBytes << 24) * 889523592379UL) >> shift);

    /// <inheritdoc />
    public override void SetDictionary(Lz4Dictionary? dictionary) =>
        _dictionary = dictionary is null || dictionary.Length < 8 ? null : Lz4PreparedDictionary.For(dictionary);

    /// <inheritdoc />
    public override void Reset()
    {
        _origin += _used + Lz4Format.MaxOffset + 1;
        _used = 0;
    }

    /// <inheritdoc />
    public override void Rebase(int removed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(removed);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(removed, _used);
        _origin += removed;
        _used -= removed;

        // Entries that pointed into the dropped bytes now lie before the window; the probes below rely on every entry being
        // either inside the window or further than 64 KiB away, so forget them.
        uint org = (uint)_origin;
        uint[] table = _table;
        for (int i = 0; i < table.Length; i++)
        {
            if ((int)(table[i] - org) < 0)
            {
                table[i] = 0;
            }
        }
    }

    /// <inheritdoc />
    public override int Compress(ReadOnlySpan<byte> window, int historyLength, Span<byte> destination)
    {
        CheckArguments(window, historyLength, destination);
        if (historyLength == 0)
        {
            // A block without history starts afresh, whatever this instance compressed before.
            Reset();
        }

        if (_origin + window.Length + (2 * (Lz4Format.MaxOffset + 1)) >= int.MaxValue)
        {
            // Coordinates would no longer fit in 31 bits: forget the table (this only costs ratio, and only after ~2 GiB).
            Array.Clear(_table);
            _origin = InitialOrigin;
        }

        _used = window.Length;
        Lz4PreparedDictionary? dictionary = historyLength == 0 ? _dictionary : null;
        return dictionary is null
            ? Core<NoDictionary>(window, historyLength, destination, null)
            : Core<WithDictionary>(window, historyLength, destination, dictionary);
    }

    private int Core<TMode>(ReadOnlySpan<byte> src, int start, Span<byte> dst, Lz4PreparedDictionary? prepared)
        where TMode : struct, IDictionaryMode
    {
        int n = src.Length;
        int op = 0;
        int anchor = start;
        int ip = start;
        int mfLimit = n - Lz4Format.MatchFindLimit;
        int matchLimit = n - Lz4Format.LastLiterals;
        uint org = (uint)_origin;
        uint[] table = _table;
        int shift = 64 - _hashLog;
        uint[] dictTable = TMode.Enabled ? prepared!.FastTable(_hashLog) : table;
        ReadOnlySpan<byte> dict = TMode.Enabled ? prepared!.Content : default;
        int dictLen = dict.Length;
        int searchBase = _acceleration << SkipTrigger;

        if (ip <= mfLimit && start == 0)
        {
            // No match can start at the first byte; index it for the bytes that follow.
            Lz4EncRaw.Put(table, Hash(Lz4EncRaw.Read64(src, 0), shift), org);
            ip = 1;
        }

        bool immediate = false;
        int cand = 0;
        while (ip <= mfLimit)
        {
            if (!immediate)
            {
                // Search forward, one probe per step, with a step that grows while nothing is found.
                int forward = ip;
                int step = 1;
                int searchNb = searchBase;
                int h = Hash(Lz4EncRaw.Read64(src, ip), shift);
                bool found = false;
                while (true)
                {
                    ip = forward;
                    forward += step;
                    step = searchNb++ >> SkipTrigger;
                    uint entry = Lz4EncRaw.Get(table, h);
                    Lz4EncRaw.Put(table, h, org + (uint)ip);
                    cand = (int)(entry - org);
                    int hNext = forward <= mfLimit ? Hash(Lz4EncRaw.Read64(src, forward), shift) : 0;
                    if ((uint)(ip - cand - 1) < Lz4Format.MaxOffset && Lz4EncRaw.Read32(src, cand) == Lz4EncRaw.Read32(src, ip))
                    {
                        found = true;
                        break;
                    }

                    if (TMode.Enabled)
                    {
                        uint de = Lz4EncRaw.Get(dictTable, h);
                        if (de != 0)
                        {
                            int dpos = (int)de - 1;
                            if (ip + dictLen - dpos <= Lz4Format.MaxOffset && Lz4EncRaw.Read32(dict, dpos) == Lz4EncRaw.Read32(src, ip))
                            {
                                // A negative position is a dictionary byte: the dictionary sits just before the window.
                                cand = dpos - dictLen;
                                found = true;
                                break;
                            }
                        }
                    }

                    if (forward > mfLimit)
                    {
                        break;
                    }

                    h = hNext;
                }

                if (!found)
                {
                    break;
                }
            }

            immediate = false;

            // Extend backwards over literals that are still pending.
            if (cand >= 0)
            {
                while (ip > anchor && cand > 0 && Lz4EncRaw.Get(src, ip - 1) == Lz4EncRaw.Get(src, cand - 1))
                {
                    ip--;
                    cand--;
                }
            }
            else if (TMode.Enabled)
            {
                while (ip > anchor && cand > -dictLen && Lz4EncRaw.Get(src, ip - 1) == Lz4EncRaw.Get(dict, dictLen + cand - 1))
                {
                    ip--;
                    cand--;
                }
            }

            // Extend forwards; the first four bytes are known equal. The last five bytes of the block stay literals.
            int length;
            if (cand >= 0)
            {
                length = Lz4Format.MinMatch + Lz4EncRaw.CommonLength(src, cand + Lz4Format.MinMatch, ip + Lz4Format.MinMatch, matchLimit);
            }
            else
            {
                length = Lz4Format.MinMatch + DictionaryMatchLength(src, dict, cand, ip, matchLimit);
            }

            op = Lz4BlockWriter.Sequence(dst, op, src, anchor, ip - anchor, ip - cand, length);
            if (_stride > 0)
            {
                // The positions the greedy walk skipped over also become candidates for later matches.
                int stop = Math.Min(ip + length, mfLimit + 1) - 1;
                for (int q = ip + 1; q < stop; q += _stride)
                {
                    Lz4EncRaw.Put(table, Hash(Lz4EncRaw.Read64(src, q), shift), org + (uint)q);
                }
            }

            ip += length;
            anchor = ip;
            if (ip > mfLimit)
            {
                break;
            }

            // Index a position inside the match, then see whether another match starts right here.
            Lz4EncRaw.Put(table, Hash(Lz4EncRaw.Read64(src, ip - 2), shift), org + (uint)(ip - 2));
            int h2 = Hash(Lz4EncRaw.Read64(src, ip), shift);
            uint e2 = Lz4EncRaw.Get(table, h2);
            Lz4EncRaw.Put(table, h2, org + (uint)ip);
            cand = (int)(e2 - org);
            if ((uint)(ip - cand - 1) < Lz4Format.MaxOffset && Lz4EncRaw.Read32(src, cand) == Lz4EncRaw.Read32(src, ip))
            {
                immediate = true;
                continue;
            }

            if (TMode.Enabled)
            {
                uint de = Lz4EncRaw.Get(dictTable, h2);
                if (de != 0)
                {
                    int dpos = (int)de - 1;
                    if (ip + dictLen - dpos <= Lz4Format.MaxOffset && Lz4EncRaw.Read32(dict, dpos) == Lz4EncRaw.Read32(src, ip))
                    {
                        cand = dpos - dictLen;
                        immediate = true;
                        continue;
                    }
                }
            }

            ip++;
        }

        return Lz4BlockWriter.LastLiterals(dst, op, src, anchor, n - anchor);
    }

    // The match starts in the dictionary at cand (negative: -cand bytes before the window) and the first four bytes agree.
    // Returns the number of further equal bytes, comparing past the dictionary's end into the window's own start, as the
    // decoder sees the dictionary and the data as one run of bytes.
    private static int DictionaryMatchLength(ReadOnlySpan<byte> src, ReadOnlySpan<byte> dict, int cand, int ip, int matchLimit)
    {
        int dictRemain = -cand - Lz4Format.MinMatch;
        int dp = dict.Length + cand + Lz4Format.MinMatch;
        int sp = ip + Lz4Format.MinMatch;
        int max = matchLimit - sp;
        int lim = Math.Min(dictRemain, max);
        int k = 0;
        while (k + 8 <= lim)
        {
            ulong diff = Lz4EncRaw.Read64(dict, dp + k) ^ Lz4EncRaw.Read64(src, sp + k);
            if (diff != 0)
            {
                return k + (System.Numerics.BitOperations.TrailingZeroCount(diff) >> 3);
            }

            k += 8;
        }

        while (k < lim && Lz4EncRaw.Get(dict, dp + k) == Lz4EncRaw.Get(src, sp + k))
        {
            k++;
        }

        if (k == dictRemain && k < max)
        {
            k += Lz4EncRaw.CommonLength(src, 0, sp + k, matchLimit);
        }

        return k;
    }
}
