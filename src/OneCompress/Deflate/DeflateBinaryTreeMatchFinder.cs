namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// A binary-tree match finder over a sliding 32 KiB window. Each position is inserted into a tree keyed by the bytes that follow it,
/// so one walk from the root reports every match length the window offers, each with its nearest distance, in increasing length.
/// That is what a parser needs to choose between a longer match and a nearer one.
/// </summary>
/// <remarks>
/// Positions are indices into a buffer the caller owns and slides (see <see cref="Slide"/>). The trees are keyed by a fixed
/// multiplicative hash of 4 bytes, and a second table keeps the latest position for each 3-byte hash, which is where 3-byte matches
/// come from. The walk depends only on the data and the arguments. Not thread-safe.
/// </remarks>
public sealed class DeflateBinaryTreeMatchFinder
{
    /// <summary>The number of matches a single search can report: one per length from 3 to 258.</summary>
    public const int MaxMatches = DeflateTables.MaxMatchLength - DeflateTables.MinMatchLength + 1;

    private const int Null = DeflateHashChainMatchFinder.Null;
    private const int WindowMask = DeflateTables.WindowSize - 1;
    private const int ShortHashBits = 16;

    private readonly int[] _head;
    private readonly int[] _shortHead = new int[1 << ShortHashBits];
    private readonly int[] _sons = new int[2 * DeflateTables.WindowSize];
    private readonly int _shift;

    /// <summary>Creates a finder.</summary>
    /// <param name="hashBits">The size of the table of tree roots as a power of two, 12 to 20. The default is 16.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hashBits"/> is out of range.</exception>
    public DeflateBinaryTreeMatchFinder(int hashBits = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hashBits, 12);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashBits, 20);
        _head = new int[1 << hashBits];
        _shift = 32 - hashBits;
        Reset();
    }

    /// <summary>Gets how many bytes at a position must be readable for it to be searched or inserted: 4.</summary>
    public static int RequiredLookahead => 4;

    /// <summary>Forgets every position.</summary>
    public void Reset()
    {
        _head.AsSpan().Fill(Null);
        _shortHead.AsSpan().Fill(Null);
        _sons.AsSpan().Fill(Null);
    }

    /// <summary>Moves every stored position back by <paramref name="delta"/>, after the caller moved its buffer's contents down by that much. Positions that fall below zero are forgotten.</summary>
    /// <param name="delta">The amount the buffer slid; at least 0.</param>
    public void Slide(int delta)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        SlideTable(_head, delta);
        SlideTable(_shortHead, delta);
        SlideTable(_sons, delta);
    }

    private static void SlideTable(int[] table, int delta)
    {
        for (int i = 0; i < table.Length; i++)
        {
            int value = table[i] - delta;
            table[i] = value < Null ? Null : value;
        }
    }

    /// <summary>Adds a position without reporting matches.</summary>
    /// <param name="data">The buffer.</param>
    /// <param name="position">The position; <c>position + 4</c> must not pass the buffer's end.</param>
    /// <param name="depth">The most tree nodes to visit.</param>
    /// <param name="niceLength">Stop descending at a node that matches this many bytes.</param>
    /// <param name="oldestPosition">Positions before this are ignored.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> is out of range for the buffer.</exception>
    public void Insert(ReadOnlySpan<byte> data, int position, int depth, int niceLength, int oldestPosition)
    {
        if ((uint)position > (uint)(data.Length - 4))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        Search(data, position, Math.Min(DeflateTables.MaxMatchLength, data.Length - position), depth, niceLength, oldestPosition, null, null);
    }

    /// <summary>Reports the matches at a position, then adds the position.</summary>
    /// <param name="data">The buffer; bytes before <paramref name="position"/> are the history.</param>
    /// <param name="position">The position; <c>position + 4</c> must not pass the buffer's end.</param>
    /// <param name="maxLength">The longest match to look for (at most 258 and at most the bytes left after <paramref name="position"/>), at least 4.</param>
    /// <param name="depth">The most tree nodes to visit.</param>
    /// <param name="niceLength">Stop descending at a node that matches this many bytes.</param>
    /// <param name="oldestPosition">Candidates before this position are ignored.</param>
    /// <param name="lengths">Receives the match lengths, strictly increasing; at least <see cref="MaxMatches"/> long.</param>
    /// <param name="distances">Receives the nearest distance for each length; at least <see cref="MaxMatches"/> long.</param>
    /// <returns>The number of matches found, 0 if none.</returns>
    /// <exception cref="ArgumentException">An output array is too short.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range for the buffer.</exception>
    public int FindMatches(ReadOnlySpan<byte> data, int position, int maxLength, int depth, int niceLength, int oldestPosition, int[] lengths, int[] distances)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        ArgumentNullException.ThrowIfNull(distances);
        if (lengths.Length < MaxMatches || distances.Length < MaxMatches)
        {
            throw new ArgumentException("The output arrays must hold 256 matches.");
        }

        if ((uint)position > (uint)(data.Length - 4))
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, DeflateTables.MaxMatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, data.Length - position);
        return Search(data, position, maxLength, depth, niceLength, oldestPosition, lengths, distances);
    }

    internal int Search(ReadOnlySpan<byte> data, int position, int maxLength, int depth, int niceLength, int oldestPosition, int[]? lengths, int[]? distances)
    {
        uint value = DeflateEncRaw.Read32(data, position);
        int hash = (int)((value * 2654435761u) >> _shift);
        int shortHash = (int)(((value & 0xFFFFFF) * 2654435761u) >> (32 - ShortHashBits));
        int current = DeflateEncRaw.Get(_head, hash);
        DeflateEncRaw.Set(_head, hash, position);
        int shortCandidate = DeflateEncRaw.Get(_shortHead, shortHash);
        DeflateEncRaw.Set(_shortHead, shortHash, position);
        int limit = Math.Max(Math.Max(oldestPosition, 0), position - DeflateTables.WindowSize);
        int count = 0;
        int best = 2;
        if (lengths is not null && shortCandidate >= limit)
        {
            int length = DeflateEncRaw.MatchLength(data, shortCandidate, position, maxLength);
            if (length >= DeflateTables.MinMatchLength)
            {
                best = length;
                lengths[0] = length;
                distances![0] = position - shortCandidate;
                count = 1;
            }
        }

        int leftSlot = 2 * (position & WindowMask);
        int rightSlot = leftSlot + 1;
        int leftLength = 0;
        int rightLength = 0;
        while (true)
        {
            if (current < limit || depth-- <= 0)
            {
                DeflateEncRaw.Set(_sons, leftSlot, Null);
                DeflateEncRaw.Set(_sons, rightSlot, Null);
                break;
            }

            int length = Math.Min(leftLength, rightLength);
            length += DeflateEncRaw.MatchLength(data, current + length, position + length, maxLength - length);
            int node = 2 * (current & WindowMask);
            if (length > best && lengths is not null)
            {
                best = length;
                lengths[count] = length;
                distances![count++] = position - current;
            }

            if (length >= maxLength || length >= niceLength)
            {
                // Same bytes as the node for as far as anyone will look: take over its place in the tree.
                DeflateEncRaw.Set(_sons, leftSlot, DeflateEncRaw.Get(_sons, node));
                DeflateEncRaw.Set(_sons, rightSlot, DeflateEncRaw.Get(_sons, node + 1));
                break;
            }

            if (DeflateEncRaw.Byte(data, current + length) < DeflateEncRaw.Byte(data, position + length))
            {
                DeflateEncRaw.Set(_sons, leftSlot, current);
                leftSlot = node + 1;
                leftLength = length;
                current = DeflateEncRaw.Get(_sons, leftSlot);
            }
            else
            {
                DeflateEncRaw.Set(_sons, rightSlot, current);
                rightSlot = node;
                rightLength = length;
                current = DeflateEncRaw.Get(_sons, rightSlot);
            }
        }

        return count;
    }
}
