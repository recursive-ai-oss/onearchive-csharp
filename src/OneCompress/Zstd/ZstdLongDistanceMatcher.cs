using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>A long match found by <see cref="ZstdLongDistanceMatcher"/>: <see cref="Length"/> bytes at <see cref="Start"/> equal to the bytes <see cref="Offset"/> positions earlier.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct ZstdLongMatch
{
    /// <summary>Creates a match.</summary>
    /// <param name="start">Window index of the first matched byte.</param>
    /// <param name="length">The number of matched bytes.</param>
    /// <param name="offset">The distance back to the source of the match, at least 1.</param>
    public ZstdLongMatch(int start, int length, uint offset)
    {
        Start = start;
        Length = length;
        Offset = offset;
    }

    /// <summary>Gets the window index of the first matched byte.</summary>
    public int Start { get; }

    /// <summary>Gets the number of matched bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the distance back to the source of the match.</summary>
    public uint Offset { get; }
}

/// <summary>
/// The knobs of the long-distance matcher. <see cref="Resolve"/> fills in the defaults from the window.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct ZstdLongDistanceParameters
{
    /// <summary>The window log used when long-distance matching is on and the caller named none: 27, which a default <c>zstd -d</c> decodes.</summary>
    public const int DefaultWindowLog = 27;

    /// <summary>The match length that is searched for unless the caller says otherwise.</summary>
    public const int DefaultMinMatch = 64;

    /// <summary>The smallest hash table log (entries, as a power of two).</summary>
    public const int MinHashLog = 6;

    /// <summary>The largest hash table log.</summary>
    public const int MaxHashLog = 26;

    /// <summary>The smallest accepted minimum match length.</summary>
    public const int MinMinMatch = 16;

    /// <summary>The largest accepted minimum match length.</summary>
    public const int MaxMinMatch = 4096;

    /// <summary>Gets the log of the farthest distance a match may reach.</summary>
    public int WindowLog { get; init; }

    /// <summary>Gets the log of the number of table entries (each is eight bytes).</summary>
    public int HashLog { get; init; }

    /// <summary>Gets the log of the number of entries in a bucket: how many positions with the same hash are remembered.</summary>
    public int BucketSizeLog { get; init; }

    /// <summary>Gets the log of the sampling rate: about one position in <c>2^HashRateLog</c> is remembered and searched from.</summary>
    public int HashRateLog { get; init; }

    /// <summary>Gets the length a match must have to be reported.</summary>
    public int MinMatch { get; init; }

    /// <summary>
    /// Fills in defaults. The table gets about one entry per sampled position of a full window: the hash log is the window log
    /// minus 7 (6 to 24), and the sampling rate is what makes the window fill the table.
    /// </summary>
    /// <param name="windowLog">The log of the farthest distance a match may reach, 10 to 31.</param>
    /// <param name="sizeLog">The log of the amount of data the matcher will see, when known, so the table need not be larger; otherwise <paramref name="windowLog"/>.</param>
    /// <param name="hashLog">The table log, or 0 for the default.</param>
    /// <param name="minMatch">The minimum match length, or 0 for <see cref="DefaultMinMatch"/>.</param>
    /// <param name="bucketSizeLog">The bucket size log (1 to 4), or 0 for the default of 2 (four entries).</param>
    /// <param name="hashRateLog">The sampling rate log (1 to 24), or 0 for the default.</param>
    /// <returns>The parameters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range.</exception>
    public static ZstdLongDistanceParameters Resolve(int windowLog, int sizeLog, int hashLog = 0, int minMatch = 0, int bucketSizeLog = 0, int hashRateLog = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowLog, ZstdCompressionParameters.MinWindowLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowLog, 31);
        if (hashLog != 0 && (hashLog < MinHashLog || hashLog > MaxHashLog))
        {
            throw new ArgumentOutOfRangeException(nameof(hashLog), hashLog, $"The hash log must be 0 or from {MinHashLog} to {MaxHashLog}.");
        }

        if (minMatch != 0 && (minMatch < MinMinMatch || minMatch > MaxMinMatch))
        {
            throw new ArgumentOutOfRangeException(nameof(minMatch), minMatch, $"The minimum match must be 0 or from {MinMinMatch} to {MaxMinMatch}.");
        }

        if (bucketSizeLog < 0 || bucketSizeLog > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketSizeLog), bucketSizeLog, "The bucket size log must be 0 or from 1 to 4.");
        }

        if (hashRateLog < 0 || hashRateLog > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(hashRateLog), hashRateLog, "The hash rate log must be 0 or from 1 to 24.");
        }

        int tableLog = hashLog != 0 ? hashLog : Math.Clamp(Math.Min(windowLog, sizeLog) - 7, MinHashLog, 24);
        int bucket = Math.Min(bucketSizeLog == 0 ? 2 : bucketSizeLog, tableLog - 1);
        int rate = hashRateLog != 0 ? hashRateLog : Math.Clamp(Math.Min(windowLog, sizeLog) - tableLog, 1, 24);
        return new ZstdLongDistanceParameters
        {
            WindowLog = windowLog,
            HashLog = tableLog,
            BucketSizeLog = bucket,
            HashRateLog = rate,
            MinMatch = minMatch != 0 ? minMatch : DefaultMinMatch,
        };
    }
}

/// <summary>
/// Finds long repeats far back in the data, much farther than a level's own match finder reaches, and hands them to the block
/// writer ahead of the ordinary search (see <see cref="ZstdLongSequencer"/>).
/// </summary>
/// <remarks>
/// <para>Anchoring is content-defined, as in rsync-style and gear-hash chunkers: a gear hash (a shift-and-add over a table of fixed
/// 64-bit constants, so it covers the last 64 bytes) runs over every byte, and a position is a checkpoint when the top
/// <see cref="ZstdLongDistanceParameters.HashRateLog"/> bits of the hash are zero. The same 64 bytes anywhere in the data give the same
/// checkpoint, so a repeat is anchored at the same place in both copies however far apart they are, and only about one position in
/// <c>2^HashRateLog</c> costs a table access. A checkpoint is looked up in a bucket of recent checkpoints with the same hash, the
/// candidates are verified against the bytes, extended forward and backward, and the longest one at least
/// <see cref="ZstdLongDistanceParameters.MinMatch"/> long is reported; then the checkpoint is remembered.</para>
/// <para>Everything is integer arithmetic on the data and the parameters, so the matches are the same on every platform. The matcher
/// reads the data once, in order, block after block: call <see cref="FindMatches(ReadOnlySpan{byte}, int, int, List{ZstdLongMatch})"/> for consecutive blocks and
/// <see cref="Slide"/> when the window buffer moves. Finding the checkpoints (<see cref="Scan"/>) only reads the data, so it can run for
/// several ranges on several threads while one thread at a time does the matching. Dictionary content is not indexed.</para>
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdLongDistanceMatcher
{
    // The gear hash covers this many bytes: each step shifts the hash left one bit, so a byte's constant leaves after 64 steps.
    private const int HashSpan = 64;

    // How many checkpoints ahead a table bucket is fetched.
    private const int PrefetchDistance = 8;

    private static readonly ulong[] s_gear = BuildGear();

    private ulong[] _table = [];
    private int _tableSize;
    private int _bucketLog;
    private int _bucketShift;
    private int _rateShift;
    private int _minMatch;
    private long _maxDistance;

    private ZstdLongCheckpoints? _scratch;

    // The constants come from the SplitMix64 sequence from a fixed seed, so they are the same everywhere.
    private static ulong[] BuildGear()
    {
        var gear = new ulong[256];
        ulong state = 0x2545F4914F6CDD1DUL;
        for (int i = 0; i < gear.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            gear[i] = z ^ (z >> 31);
        }

        return gear;
    }

    /// <summary>Prepares for a new stream of data: empties the table.</summary>
    /// <param name="parameters">The parameters, from <see cref="ZstdLongDistanceParameters.Resolve"/>.</param>
    public void BeginFrame(ZstdLongDistanceParameters parameters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.HashLog, ZstdLongDistanceParameters.MinHashLog);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.HashLog, ZstdLongDistanceParameters.MaxHashLog);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.HashRateLog, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.HashRateLog, 63);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.BucketSizeLog, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(parameters.BucketSizeLog, parameters.HashLog);
        ArgumentOutOfRangeException.ThrowIfLessThan(parameters.MinMatch, ZstdLongDistanceParameters.MinMinMatch);
        _tableSize = 1 << parameters.HashLog;
        if (_table.Length < _tableSize)
        {
            _table = new ulong[_tableSize];
        }
        else
        {
            Array.Clear(_table, 0, _tableSize);
        }

        _bucketLog = parameters.BucketSizeLog;
        _bucketShift = 64 - (parameters.HashLog - parameters.BucketSizeLog);
        _rateShift = 64 - parameters.HashRateLog;
        _minMatch = parameters.MinMatch;
        _maxDistance = 1L << Math.Min(parameters.WindowLog, 31);
    }

    /// <summary>Tells the matcher that the window was moved down by <paramref name="delta"/> bytes: remembered positions below it are gone.</summary>
    /// <param name="delta">The number of bytes removed from the front of the window.</param>
    public void Slide(int delta)
    {
        // An entry holds position + 1 in its low half, so zero means empty. Dropped entries are always older than the window,
        // so a stream that slides and a one-shot call that never does choose the same matches.
        ulong[] table = _table;
        ulong d = (uint)delta;
        for (int i = 0; i < _tableSize; i++)
        {
            ulong e = table[i];
            ulong p = e & 0xFFFF_FFFFUL;
            table[i] = p > d ? e - d : 0;
        }
    }

    /// <summary>
    /// Finds the checkpoints of <c>window[start..end]</c> (the first step of <see cref="FindMatches(ReadOnlySpan{byte}, int, int, ZstdLongCheckpoints, ref int, List{ZstdLongMatch})"/>).
    /// It reads the data and the parameters only, never the table, so scans of different ranges can run on different threads while
    /// another thread is matching.
    /// </summary>
    /// <param name="window">The data; the <see cref="HashSpan"/> bytes before <paramref name="start"/> (as many as there are) are read too.</param>
    /// <param name="start">Index of the first byte to scan.</param>
    /// <param name="end">Index just past the last byte to scan.</param>
    /// <param name="checkpoints">Receives the checkpoints, replacing what it held.</param>
    public void Scan(ReadOnlySpan<byte> window, int start, int end, ZstdLongCheckpoints checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if ((uint)end > (uint)window.Length || start < 0 || start > end)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        if (_tableSize == 0)
        {
            throw new InvalidOperationException("BeginFrame has not been called.");
        }

        checkpoints.Clear();
        ulong mask = ~0UL << _rateShift;
        ulong hash = HashBefore(window, start);
        int j = start;
        while (true)
        {
            j = ScanToCheckpoint(window, j, end, ref hash, mask);
            if (j >= end)
            {
                break;
            }

            // A checkpoint before the 64th byte of the data has no full window of bytes under it and is not a position at all.
            if (j >= HashSpan - 1)
            {
                ulong mixed = (hash ^ (hash >> 29)) * 0xBF58476D1CE4E5B9UL;
                checkpoints.Add(j, (int)(mixed >> _bucketShift) << _bucketLog, (uint)mixed);
            }

            j++;
        }
    }

    /// <summary>
    /// Scans one block and appends the long matches that start in it, remembering its checkpoints. Blocks must come in order with
    /// no gap: each call's <paramref name="blockStart"/> is the previous call's <paramref name="blockEnd"/> (or the start of the data),
    /// and the bytes before a block are still in the window.
    /// </summary>
    /// <param name="window">The history followed by the block. Bytes up to <paramref name="blockEnd"/> are valid.</param>
    /// <param name="blockStart">Index of the block's first byte; no match starts before it.</param>
    /// <param name="blockEnd">Index just past the block's last byte; no match reaches past it.</param>
    /// <param name="matches">Receives the matches, in order of position, not overlapping, each inside the block; it is not cleared.</param>
    public void FindMatches(ReadOnlySpan<byte> window, int blockStart, int blockEnd, List<ZstdLongMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if ((uint)blockEnd > (uint)window.Length || blockStart < 0 || blockStart > blockEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(blockEnd));
        }

        _scratch ??= new ZstdLongCheckpoints();
        Scan(window, blockStart, blockEnd, _scratch);
        int next = 0;
        FindMatches(window, blockStart, blockEnd, _scratch, ref next, matches);
    }

    /// <summary>
    /// Appends the long matches that start in a block, given the checkpoints of a larger range found by <see cref="Scan"/>, and
    /// remembers the block's checkpoints. Blocks must come in order with no gap, as for
    /// <see cref="FindMatches(ReadOnlySpan{byte}, int, int, List{ZstdLongMatch})"/>; this is the step that must not run on two threads at once.
    /// </summary>
    /// <param name="window">The history followed by the block.</param>
    /// <param name="blockStart">Index of the block's first byte; no match starts before it.</param>
    /// <param name="blockEnd">Index just past the block's last byte; no match reaches past it.</param>
    /// <param name="checkpoints">The checkpoints of a range that includes the block.</param>
    /// <param name="next">The index of the first checkpoint not yet used; start at 0 for the first block of the range. Updated.</param>
    /// <param name="matches">Receives the matches; it is not cleared.</param>
    public void FindMatches(
        ReadOnlySpan<byte> window,
        int blockStart,
        int blockEnd,
        ZstdLongCheckpoints checkpoints,
        ref int next,
        List<ZstdLongMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(matches);
        if ((uint)blockEnd > (uint)window.Length || blockStart < 0 || blockStart > blockEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(blockEnd));
        }

        int searchFrom = blockStart;
        int anchor = blockStart;
        int count = checkpoints.Count;
        int n = next;
        while (n < count && checkpoints.PositionAt(n) < blockEnd)
        {
            // The bucket of a checkpoint a few steps ahead is fetched while this one is searched: the table is far larger than the cache.
            if (n + PrefetchDistance < count)
            {
                Prefetch(checkpoints.BucketAt(n + PrefetchDistance));
            }

            Checkpoint(window, checkpoints.PositionAt(n) - (HashSpan - 1), checkpoints.BucketAt(n), checkpoints.CheckAt(n), blockEnd, ref searchFrom, ref anchor, matches);
            n++;
        }

        next = n;
    }

    // A hint to the processor only: it changes no result, so it needs no scalar twin.
    private unsafe void Prefetch(int bucket)
    {
        if (Sse.IsSupported)
        {
            fixed (ulong* entry = &_table[bucket])
            {
                Sse.Prefetch0(entry);
            }
        }
    }

    // Gear hash state before byte j: the 63 bytes before it (fewer at the start of the data, which only matters for positions
    // that are not checkpoints anyway).
    private static ulong HashBefore(ReadOnlySpan<byte> window, int j)
    {
        ulong[] gear = s_gear;
        ulong h = 0;
        for (int q = Math.Max(0, j - (HashSpan - 1)); q < j; q++)
        {
            h = (h << 1) + Raw.Get(gear, Raw.ByteAt(window, q));
        }

        return h;
    }

    // Runs the gear hash over window[j..end] and returns the index of the first byte after which the hash is a checkpoint (its top
    // bits, those of mask, are zero), with the hash after that byte in hash; or end when there is none, with the hash after the last byte.
    // A method of its own with no calls in it, so that the loop keeps everything in registers.
    private static int ScanToCheckpoint(ReadOnlySpan<byte> window, int j, int end, ref ulong hash, ulong mask)
    {
        ulong[] gear = s_gear;
        ulong h = hash;

        // Bounds: j + 8 <= end <= window.Length for the eight-byte read; gear indexes are single bytes into a table of 256.
        while (j + 8 <= end)
        {
            ulong v = Raw.ReadUInt64(window, j);
            ulong h0 = (h << 1) + Raw.Get(gear, (int)(v & 0xFF));
            if ((h0 & mask) == 0)
            {
                hash = h0;
                return j;
            }

            ulong h1 = (h0 << 1) + Raw.Get(gear, (int)((v >> 8) & 0xFF));
            if ((h1 & mask) == 0)
            {
                hash = h1;
                return j + 1;
            }

            ulong h2 = (h1 << 1) + Raw.Get(gear, (int)((v >> 16) & 0xFF));
            if ((h2 & mask) == 0)
            {
                hash = h2;
                return j + 2;
            }

            ulong h3 = (h2 << 1) + Raw.Get(gear, (int)((v >> 24) & 0xFF));
            if ((h3 & mask) == 0)
            {
                hash = h3;
                return j + 3;
            }

            ulong h4 = (h3 << 1) + Raw.Get(gear, (int)((v >> 32) & 0xFF));
            if ((h4 & mask) == 0)
            {
                hash = h4;
                return j + 4;
            }

            ulong h5 = (h4 << 1) + Raw.Get(gear, (int)((v >> 40) & 0xFF));
            if ((h5 & mask) == 0)
            {
                hash = h5;
                return j + 5;
            }

            ulong h6 = (h5 << 1) + Raw.Get(gear, (int)((v >> 48) & 0xFF));
            if ((h6 & mask) == 0)
            {
                hash = h6;
                return j + 6;
            }

            h = (h6 << 1) + Raw.Get(gear, (int)(v >> 56));
            if ((h & mask) == 0)
            {
                hash = h;
                return j + 7;
            }

            j += 8;
        }

        while (j < end)
        {
            h = (h << 1) + Raw.Get(gear, Raw.ByteAt(window, j));
            if ((h & mask) == 0)
            {
                hash = h;
                return j;
            }

            j++;
        }

        hash = h;
        return end;
    }

    // Looks for a match at the checkpoint of position i (the byte after which the hash was taken, minus the window), then remembers it.
    private void Checkpoint(
        ReadOnlySpan<byte> window,
        int i,
        int bucket,
        uint check,
        int blockEnd,
        ref int searchFrom,
        ref int anchor,
        List<ZstdLongMatch> matches)
    {
        if (i >= searchFrom)
        {
            int bestStart = 0;
            int bestLength = 0;
            uint bestOffset = 0;
            int size = 1 << _bucketLog;
            for (int e = 0; e < size; e++)
            {
                ulong entry = _table[bucket + e];
                if ((uint)(entry >> 32) != check || (uint)entry == 0)
                {
                    continue;
                }

                int source = (int)(uint)entry - 1;
                if (source >= i || i - source > _maxDistance)
                {
                    continue;
                }

                int forward = CommonLength(window, source, i, blockEnd);
                int back = 0;
                while (i - back > anchor && source - back > 0 && Raw.ByteAt(window, i - back - 1) == Raw.ByteAt(window, source - back - 1))
                {
                    back++;
                }

                if (forward + back > bestLength)
                {
                    bestLength = forward + back;
                    bestStart = i - back;
                    bestOffset = (uint)(i - source);
                }
            }

            if (bestLength >= _minMatch)
            {
                matches.Add(new ZstdLongMatch(bestStart, bestLength, bestOffset));
                searchFrom = bestStart + bestLength;
                anchor = searchFrom;
            }
        }

        // Remember the checkpoint: newest first, the oldest of the bucket falls out.
        int count = 1 << _bucketLog;
        for (int e = count - 1; e > 0; e--)
        {
            _table[bucket + e] = _table[bucket + e - 1];
        }

        _table[bucket] = ((ulong)check << 32) | (uint)(i + 1);
    }

    // The number of equal bytes at window[a..] and window[b..], where a < b, up to window[limit].
    private static int CommonLength(ReadOnlySpan<byte> window, int a, int b, int limit)
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
}

/// <summary>
/// The checkpoints of a range of data, found by <see cref="ZstdLongDistanceMatcher.Scan"/> and used by the matcher's matching step. A
/// buffer that is reused from range to range.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdLongCheckpoints
{
    private int[] _position = new int[256];
    private int[] _bucket = new int[256];
    private uint[] _check = new uint[256];

    /// <summary>Gets the number of checkpoints.</summary>
    public int Count { get; private set; }

    internal int PositionAt(int index) => _position[index];

    internal int BucketAt(int index) => _bucket[index];

    internal uint CheckAt(int index) => _check[index];

    internal void Clear() => Count = 0;

    internal void Add(int position, int bucket, uint check)
    {
        if (Count == _position.Length)
        {
            Array.Resize(ref _position, Count * 2);
            Array.Resize(ref _bucket, Count * 2);
            Array.Resize(ref _check, Count * 2);
        }

        _position[Count] = position;
        _bucket[Count] = bucket;
        _check[Count] = check;
        Count++;
    }
}

/// <summary>
/// Builds a block's sequences from long matches and an ordinary match finder: the long matches go in first, and the finder
/// parses the stretches between them.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class ZstdLongSequencer
{
    // How much of the end of a long match the finder is told about, so the data just after the match can refer back into it.
    private const int PrimeTail = 256;

    /// <summary>
    /// Fills <paramref name="store"/> with the sequences of <c>window[start..end]</c>: each long match becomes a sequence, and
    /// <paramref name="finder"/> parses the gaps (and so works with any strategy). The finder is told the repeat offsets in
    /// force at the start of each gap, which follow from the sequences before it.
    /// </summary>
    /// <param name="finder">The ordinary match finder, already begun for the frame.</param>
    /// <param name="window">The history followed by the block.</param>
    /// <param name="start">Index of the block's first byte.</param>
    /// <param name="end">Index just past the block's last byte.</param>
    /// <param name="windowStartPosition">How many bytes of the frame precede <c>window[0]</c>.</param>
    /// <param name="repeatOffsets">The repeat offsets in force at the start of the block.</param>
    /// <param name="matches">The long matches of the block, in order, not overlapping, each inside <c>[start, end)</c>; possibly empty.</param>
    /// <param name="store">The empty store that receives the sequences and literals.</param>
    public static void Fill(
        ZstdMatchFinder finder,
        ReadOnlySpan<byte> window,
        int start,
        int end,
        long windowStartPosition,
        ZstdRepeatOffsets repeatOffsets,
        ReadOnlySpan<ZstdLongMatch> matches,
        ZstdSequenceStore store)
    {
        ArgumentNullException.ThrowIfNull(finder);
        ArgumentNullException.ThrowIfNull(store);
        if (matches.IsEmpty)
        {
            finder.FindSequences(window, start, end, windowStartPosition, repeatOffsets, store);
            return;
        }

        ZstdRepeatOffsets reps = repeatOffsets;
        int position = start;
        int literalsInSequences = 0;
        int sequencesSeen = 0;
        foreach (ZstdLongMatch match in matches)
        {
            if (match.Start > position)
            {
                finder.FindSequences(window, position, match.Start, windowStartPosition, reps, store);
            }

            // Follow the repeat offsets through what the finder just added; its trailing literals lead into the long match.
            ReadOnlySpan<ZstdSequence> sequences = store.Sequences;
            for (; sequencesSeen < sequences.Length; sequencesSeen++)
            {
                _ = reps.Apply(sequences[sequencesSeen].Offset, sequences[sequencesSeen].LiteralLength);
                literalsInSequences += (int)sequences[sequencesSeen].LiteralLength;
            }

            uint literalLength = (uint)(store.LiteralCount - literalsInSequences);
            store.AddSequence(literalLength, (uint)match.Length, match.Offset);
            _ = reps.Apply(match.Offset, literalLength);
            literalsInSequences += (int)literalLength;
            sequencesSeen++;
            position = match.Start + match.Length;
            finder.Prime(window[..end], Math.Max(match.Start, position - PrimeTail), position);
        }

        if (position < end)
        {
            finder.FindSequences(window, position, end, windowStartPosition, reps, store);
        }
    }
}
