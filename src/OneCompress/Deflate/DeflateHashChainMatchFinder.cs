using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A hash-chain match finder over a sliding 32 KiB window, the structure the Deflate format was designed around: a table maps a
/// hash of the next four bytes to the latest position with that hash, and a second table links each position to the previous
/// one with the same hash (as a distance, so it never needs adjusting when the buffer slides). Searching walks the chain from the
/// newest candidate, so depth trades time for ratio. Matches of 3 bytes come from a separate table that keeps the latest position
/// for each 3-byte hash.
/// </summary>
/// <remarks>
/// Positions are indices into a buffer the caller owns and slides (see <see cref="Slide"/>). The hashes are fixed
/// multiplicative hashes, so the candidates visited depend only on the data. Not thread-safe.
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateHashChainMatchFinder
{
    /// <summary>The position value that means "none"; far enough back that no distance check accepts it.</summary>
    internal const int Null = -(1 << 30);

    private const int WindowMask = DeflateTables.WindowSize - 1;

    private const int ShortHashBits = 15;

    private readonly int[] _head;
    private readonly int[]? _shortHead;
    private readonly ushort[] _previous = new ushort[DeflateTables.WindowSize];
    private readonly int _shift;

    /// <summary>Creates a finder.</summary>
    /// <param name="hashBits">The size of the hash table as a power of two, 8 to 20. The default is 15.</param>
    /// <param name="hashBytes">The shortest match reported: 3 also keeps a table for 3-byte matches (the nearest only), 4 is faster and skips them. The default is 3.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public DeflateHashChainMatchFinder(int hashBits = 15, int hashBytes = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hashBits, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashBits, 20);
        if (hashBytes is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(hashBytes), "Hash 3 or 4 bytes.");
        }

        _head = new int[1 << hashBits];
        _shift = 32 - hashBits;
        _shortHead = hashBytes == 3 ? new int[1 << ShortHashBits] : null;
        Reset();
    }

    /// <summary>Gets how many bytes at a position must be readable for it to be inserted or searched: 4.</summary>
    public static int RequiredLookahead => 4;

    /// <summary>Forgets every position.</summary>
    public void Reset()
    {
        _head.AsSpan().Fill(Null);
        _shortHead?.AsSpan().Fill(Null);
        _previous.AsSpan().Clear();
    }

    /// <summary>Moves every stored position back by <paramref name="delta"/>, after the caller moved its buffer's contents down by that much. Positions that fall below zero are forgotten.</summary>
    /// <param name="delta">The amount the buffer slid; at least 0.</param>
    public void Slide(int delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        SlideTable(_head, delta);
        if (_shortHead is not null)
        {
            SlideTable(_shortHead, delta);
        }
    }

    // Same result with or without vector hardware: table[i] = max(table[i] - delta, Null) (the subtraction cannot overflow, since
    // every entry is at least Null and delta is below 2^30).
    internal static void SlideTable(int[] table, int delta)
    {
        int i = 0;
        if (Vector256.IsHardwareAccelerated && table.Length >= Vector256<int>.Count)
        {
            Vector256<int> d = Vector256.Create(delta);
            Vector256<int> floor = Vector256.Create(Null);
            for (; i + Vector256<int>.Count <= table.Length; i += Vector256<int>.Count)
            {
                Vector256<int> v = Vector256.Create(table.AsSpan(i, Vector256<int>.Count));
                Vector256.Max(v - d, floor).CopyTo(table.AsSpan(i, Vector256<int>.Count));
            }
        }

        for (; i < table.Length; i++)
        {
            int value = table[i] - delta;
            table[i] = value < Null ? Null : value;
        }
    }

    /// <summary>Adds a position to the chains without searching.</summary>
    /// <param name="data">The buffer.</param>
    /// <param name="position">The position; <c>position + 4</c> must not pass the buffer's end.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is negative or has fewer than 4 bytes after it.</exception>
    public void Insert(ReadOnlySpan<byte> data, int position)
    {
        if ((uint)position > (uint)(data.Length - 4))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        InsertUnchecked(data, position);
    }

    internal void InsertUnchecked(ReadOnlySpan<byte> data, int position)
    {
        uint value = DeflateEncRaw.Read32(data, position);
        int hash = (int)((value * 2654435761u) >> _shift);
        int candidate = DeflateEncRaw.Get(_head, hash);
        DeflateEncRaw.Set(_head, hash, position);
        DeflateEncRaw.SetLink(_previous, position & WindowMask, (uint)(position - candidate) <= WindowMask + 1 ? (ushort)(position - candidate) : (ushort)0);
        if (_shortHead is not null)
        {
            DeflateEncRaw.Set(_shortHead, (int)(((value & 0xFFFFFF) * 2654435761u) >> (32 - ShortHashBits)), position);
        }
    }

    // The parsers bind the finder to the encoder's window once per parse, so a search takes few arguments and keeps its state in registers.
    private byte[] _data = [];
    private int _end;
    private int _oldest;
    private int _nice;

    internal void Bind(byte[] data, int end, int oldest, int nice)
    {
        _data = data;
        _end = end;
        _oldest = oldest;
        _nice = nice;
    }

    // As FindLongestUnchecked, over the bound window: the longest match at position beyond best, or best. The tables are copied to
    // locals first: the stores below go through references, which the compiler must assume could change any field, so a field would
    // be loaded again (with its null check) after every store and on every turn of the candidate loop.
    internal int Find(int position, int best, int depth, out int distance)
    {
        byte[] data = _data;
        int[] head = _head;
        ushort[] previous = _previous;
        int maxLength = Math.Min(DeflateTables.MaxMatchLength, _end - position);
        uint value = DeflateEncRaw.Read32(data, position);
        int hash = (int)((value * 2654435761u) >> _shift);
        int candidate = DeflateEncRaw.Get(head, hash);
        DeflateEncRaw.Set(head, hash, position);
        DeflateEncRaw.SetLink(previous, position & WindowMask, (uint)(position - candidate) <= WindowMask + 1 ? (ushort)(position - candidate) : (ushort)0);
        distance = 0;
        int limit = Math.Max(_oldest, position - DeflateTables.WindowSize);
        int nice = _nice;
        int[]? shortHead = _shortHead;
        if (shortHead is not null)
        {
            int shortHash = (int)(((value & 0xFFFFFF) * 2654435761u) >> (32 - ShortHashBits));
            int shortCandidate = DeflateEncRaw.Get(shortHead, shortHash);
            DeflateEncRaw.Set(shortHead, shortHash, position);
            if (best < DeflateTables.MinMatchLength && shortCandidate >= limit)
            {
                int length = DeflateEncRaw.MatchLength(data, shortCandidate, position, maxLength);
                if (length >= DeflateTables.MinMatchLength)
                {
                    best = length;
                    distance = position - shortCandidate;
                }
            }
        }

        if (best >= maxLength)
        {
            return best;
        }

        while (candidate >= limit)
        {
            int offset = best > 3 ? best - 3 : 0;
            if (DeflateEncRaw.Read32(data, candidate + offset) == DeflateEncRaw.Read32(data, position + offset))
            {
                int length = DeflateEncRaw.MatchLength(data, candidate, position, maxLength);
                if (length > best)
                {
                    best = length;
                    distance = position - candidate;
                    if (length >= nice || length >= maxLength)
                    {
                        break;
                    }
                }
            }

            if (--depth <= 0)
            {
                break;
            }

            int step = DeflateEncRaw.GetLink(previous, candidate & WindowMask);
            if (step == 0)
            {
                break;
            }

            candidate -= step;
        }

        return best;
    }

    internal void InsertBound(int position) => InsertUnchecked(_data, position);

    // Inserts positions first..last-1 of the bound window, exactly as InsertUnchecked would one at a time; the tables stay in
    // locals for the whole run, which is most of the work in text where matches cover most positions.
    internal void InsertRange(int first, int last)
    {
        byte[] data = _data;
        int[] head = _head;
        ushort[] previous = _previous;
        int[]? shortHead = _shortHead;
        int shift = _shift;
        for (int position = first; position < last; position++)
        {
            uint value = DeflateEncRaw.Read32(data, position);
            int hash = (int)((value * 2654435761u) >> shift);
            int candidate = DeflateEncRaw.Get(head, hash);
            DeflateEncRaw.Set(head, hash, position);
            DeflateEncRaw.SetLink(previous, position & WindowMask, (uint)(position - candidate) <= WindowMask + 1 ? (ushort)(position - candidate) : (ushort)0);
            if (shortHead is not null)
            {
                DeflateEncRaw.Set(shortHead, (int)(((value & 0xFFFFFF) * 2654435761u) >> (32 - ShortHashBits)), position);
            }
        }
    }

    // The two-probe search for the fastest level: each 4-byte hash owns a bucket of the latest two positions, both are tried, and
    // no links are stored. Returns 0 when neither matches for at least 4 bytes.
    internal int FindFirstUnchecked(ReadOnlySpan<byte> data, int position, int maxLength, int oldestPosition, out int distance)
    {
        int[] table = _head;
        uint value = DeflateEncRaw.Read32(data, position);
        int bucket = (int)((value * 2654435761u) >> _shift) & ~1;
        int first = DeflateEncRaw.Get(table, bucket);
        int second = DeflateEncRaw.Get(table, bucket + 1);
        DeflateEncRaw.Set(table, bucket + 1, first);
        DeflateEncRaw.Set(table, bucket, position);
        distance = 0;
        int limit = Math.Max(oldestPosition, position - DeflateTables.WindowSize);
        int best = 0;
        if (first >= limit && DeflateEncRaw.Read32(data, first) == value)
        {
            distance = position - first;
            best = 4 + DeflateEncRaw.MatchLength(data, first + 4, position + 4, maxLength - 4);
            if (best >= maxLength)
            {
                return best;
            }
        }

        if (second >= limit && DeflateEncRaw.Read32(data, second) == value)
        {
            int length = 4 + DeflateEncRaw.MatchLength(data, second + 4, position + 4, maxLength - 4);
            if (length > best)
            {
                best = length;
                distance = position - second;
            }
        }

        return best;
    }

    // Positions first..last-1 of the data, entered as InsertFirstUnchecked would one at a time, with the table in a local.
    internal void InsertFirstRange(byte[] data, int first, int last)
    {
        int[] table = _head;
        int shift = _shift;
        for (int position = first; position < last; position++)
        {
            int bucket = (int)((DeflateEncRaw.Read32(data, position) * 2654435761u) >> shift) & ~1;
            DeflateEncRaw.Set(table, bucket + 1, DeflateEncRaw.Get(table, bucket));
            DeflateEncRaw.Set(table, bucket, position);
        }
    }

    internal void InsertFirstUnchecked(ReadOnlySpan<byte> data, int position)
    {
        int bucket = (int)((DeflateEncRaw.Read32(data, position) * 2654435761u) >> _shift) & ~1;
        DeflateEncRaw.Set(_head, bucket + 1, DeflateEncRaw.Get(_head, bucket));
        DeflateEncRaw.Set(_head, bucket, position);
    }

    /// <summary>Finds the longest match at a position longer than <paramref name="bestLength"/>, then adds the position to the chains.</summary>
    /// <param name="data">The buffer; bytes before <paramref name="position"/> are the history.</param>
    /// <param name="position">The position to match from; <c>position + 4</c> must not pass the buffer's end.</param>
    /// <param name="maxLength">The longest match to look for (at most 258 and at most the bytes left after <paramref name="position"/>), at least 4.</param>
    /// <param name="bestLength">A length to beat; only a longer match is reported. Use 2 to find any match of 3 or more.</param>
    /// <param name="chainDepth">The most candidates to compare.</param>
    /// <param name="niceLength">Stop at the first match this long.</param>
    /// <param name="oldestPosition">Candidates before this position are ignored (a full flush forgets the history this way).</param>
    /// <param name="distance">Receives the distance of the match found.</param>
    /// <returns>The length found, or <paramref name="bestLength"/> when nothing longer exists (then <paramref name="distance"/> is 0).</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range for the buffer.</exception>
    public int FindLongest(ReadOnlySpan<byte> data, int position, int maxLength, int bestLength, int chainDepth, int niceLength, int oldestPosition, out int distance)
    {
        if ((uint)position > (uint)(data.Length - 4))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, DeflateTables.MaxMatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, data.Length - position);
        ArgumentOutOfRangeException.ThrowIfNegative(bestLength);
        return FindLongestUnchecked(data, position, maxLength, bestLength, chainDepth, niceLength, oldestPosition, out distance);
    }

    internal int FindLongestUnchecked(ReadOnlySpan<byte> data, int position, int maxLength, int bestLength, int chainDepth, int niceLength, int oldestPosition, out int distance)
    {
        uint value = DeflateEncRaw.Read32(data, position);
        int hash = (int)((value * 2654435761u) >> _shift);
        int candidate = DeflateEncRaw.Get(_head, hash);
        DeflateEncRaw.Set(_head, hash, position);
        DeflateEncRaw.SetLink(_previous, position & WindowMask, (uint)(position - candidate) <= WindowMask + 1 ? (ushort)(position - candidate) : (ushort)0);
        distance = 0;
        int limit = Math.Max(Math.Max(oldestPosition, 0), position - DeflateTables.WindowSize);
        int best = bestLength;
        if (_shortHead is not null)
        {
            int shortHash = (int)(((value & 0xFFFFFF) * 2654435761u) >> (32 - ShortHashBits));
            int shortCandidate = DeflateEncRaw.Get(_shortHead, shortHash);
            DeflateEncRaw.Set(_shortHead, shortHash, position);
            if (best < DeflateTables.MinMatchLength && shortCandidate >= limit)
            {
                int length = DeflateEncRaw.MatchLength(data, shortCandidate, position, maxLength);
                if (length >= DeflateTables.MinMatchLength)
                {
                    best = length;
                    distance = position - shortCandidate;
                }
            }
        }

        if (best >= maxLength)
        {
            return best;
        }

        while (candidate >= limit)
        {
            // A longer match must agree on the four bytes that end just past the best length so far (for a first match, the first
            // four bytes, which the hash only suggests); that rejects most candidates with one compare.
            int offset = best > 3 ? best - 3 : 0;
            if (DeflateEncRaw.Read32(data, candidate + offset) == DeflateEncRaw.Read32(data, position + offset))
            {
                int length = DeflateEncRaw.MatchLength(data, candidate, position, maxLength);
                if (length > best)
                {
                    best = length;
                    distance = position - candidate;
                    if (length >= niceLength || length >= maxLength)
                    {
                        break;
                    }
                }
            }

            if (--chainDepth <= 0)
            {
                break;
            }

            int step = DeflateEncRaw.GetLink(_previous, candidate & WindowMask);
            if (step == 0)
            {
                break;
            }

            candidate -= step;
        }

        return best;
    }
}
