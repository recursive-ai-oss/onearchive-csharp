namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A hash-chain match finder over a sliding 32 KiB window, the structure the Deflate format was designed around: a table maps a
/// hash of the next few bytes to the latest position with that hash, and a second table links each position to the previous
/// one with the same hash. Searching walks the chain from the newest candidate, so depth trades time for ratio.
/// </summary>
/// <remarks>
/// Positions are indices into a buffer the caller owns and slides (see <see cref="Slide"/>). The hash is a fixed
/// multiplicative hash of 3 or 4 bytes, so the candidates visited depend only on the data. Not thread-safe.
/// </remarks>
public sealed class DeflateHashChainMatchFinder
{
    /// <summary>The position value that means "none"; far enough back that no distance check accepts it.</summary>
    internal const int Null = -(1 << 30);

    private const int WindowMask = DeflateTables.WindowSize - 1;

    private readonly int[] _head;
    private readonly int[] _previous = new int[DeflateTables.WindowSize];
    private readonly int _shift;
    private readonly bool _fourBytes;

    /// <summary>Creates a finder.</summary>
    /// <param name="hashBits">The size of the hash table as a power of two, 8 to 20. The default is 15.</param>
    /// <param name="hashBytes">How many bytes are hashed: 3 finds every match of the format's minimum length, 4 is faster and skips 3-byte matches. The default is 3.</param>
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
        _fourBytes = hashBytes == 4;
        Reset();
    }

    /// <summary>Gets how many bytes at a position must be readable for it to be inserted or searched: 4.</summary>
    public static int RequiredLookahead => 4;

    /// <summary>Forgets every position.</summary>
    public void Reset()
    {
        _head.AsSpan().Fill(Null);
        _previous.AsSpan().Fill(Null);
    }

    /// <summary>Moves every stored position back by <paramref name="delta"/>, after the caller moved its buffer's contents down by that much. Positions that fall below zero are forgotten.</summary>
    /// <param name="delta">The amount the buffer slid; at least 0.</param>
    public void Slide(int delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        SlideTable(_head, delta);
        SlideTable(_previous, delta);
    }

    private static void SlideTable(int[] table, int delta)
    {
        for (int i = 0; i < table.Length; i++)
        {
            int value = table[i] - delta;
            table[i] = value < Null ? Null : value;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private int Hash(ReadOnlySpan<byte> data, int position)
    {
        uint value = DeflateEncRaw.Read32(data, position);
        if (!_fourBytes)
        {
            value &= 0xFFFFFF;
        }

        return (int)((value * 2654435761u) >> _shift);
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
        int hash = Hash(data, position);
        DeflateEncRaw.Set(_previous, position & WindowMask, DeflateEncRaw.Get(_head, hash));
        DeflateEncRaw.Set(_head, hash, position);
    }

    /// <summary>Finds the longest match at a position longer than <paramref name="bestLength"/>, then adds the position to the chains.</summary>
    /// <param name="data">The buffer; bytes before <paramref name="position"/> are the history.</param>
    /// <param name="position">The position to match from; <c>position + 4</c> must not pass the buffer's end.</param>
    /// <param name="maxLength">The longest match to look for (at most 258 and at most the bytes left after <paramref name="position"/>).</param>
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

        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, DeflateTables.MaxMatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, data.Length - position);
        ArgumentOutOfRangeException.ThrowIfNegative(bestLength);
        return FindLongestUnchecked(data, position, maxLength, bestLength, chainDepth, niceLength, oldestPosition, out distance);
    }

    internal int FindLongestUnchecked(ReadOnlySpan<byte> data, int position, int maxLength, int bestLength, int chainDepth, int niceLength, int oldestPosition, out int distance)
    {
        int hash = Hash(data, position);
        int candidate = DeflateEncRaw.Get(_head, hash);
        DeflateEncRaw.Set(_previous, position & WindowMask, candidate);
        DeflateEncRaw.Set(_head, hash, position);
        distance = 0;
        if (bestLength >= maxLength)
        {
            return bestLength;
        }

        int limit = Math.Max(Math.Max(oldestPosition, 0), position - DeflateTables.WindowSize);
        int best = bestLength;
        byte first = DeflateEncRaw.Byte(data, position);
        while (candidate >= limit)
        {
            // A longer match must also agree at the byte just past the best so far, which rejects most candidates cheaply.
            if (DeflateEncRaw.Byte(data, candidate + best) == DeflateEncRaw.Byte(data, position + best) && DeflateEncRaw.Byte(data, candidate) == first)
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

            int next = DeflateEncRaw.Get(_previous, candidate & WindowMask);
            if (next >= candidate)
            {
                // Stale link from a slot a newer position reused; the chain is over.
                break;
            }

            candidate = next;
        }

        return best;
    }
}
