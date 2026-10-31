
namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// A binary-tree match finder: positions that share a hash form a binary search tree ordered by the bytes that follow, and finding the
/// matches for a position is inserting it into the tree. It finds the longest match among all earlier positions in the dictionary that
/// its depth limit allows, at a cost that grows slowly with repetitive data. The best ratio, used by the higher presets.
/// </summary>
/// <remarks>
/// Each position owns two links (the roots of the subtrees of strings smaller and greater than its own) in a cyclic array of
/// <c>2 * (dictionary size + 1)</c> entries. Inserting walks down from the hash's previous position, comparing, and hangs the visited
/// nodes onto the new node's two sides, which keeps every tree a valid search tree without rotations (the technique of the LZ77
/// literature on tree-based match finders).
/// </remarks>
public sealed class LzmaBinaryTreeMatchFinder : LzmaMatchFinder
{
    private readonly int[] _son;

    /// <summary>Creates a finder.</summary>
    /// <param name="hashBytes">The bytes the main hash covers: 2, 3 or 4.</param>
    /// <param name="dictionarySize">The largest match distance.</param>
    /// <param name="niceLength">The length at which a search stops, 2 to 273.</param>
    /// <param name="depth">The most tree nodes visited per position, at least 1.</param>
    /// <param name="hashDictionarySize">The dictionary size that sizes the hash tables when it differs from <paramref name="dictionarySize"/> (a window cut down for a short input keeps the tables of the full dictionary, so the matches found do not depend on the input length); 0 for <paramref name="dictionarySize"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public LzmaBinaryTreeMatchFinder(int hashBytes, int dictionarySize, int niceLength, int depth, int hashDictionarySize = 0)
        : base(CheckHashBytes(hashBytes), dictionarySize, niceLength, depth, hashDictionarySize)
    {
        _son = new int[2 * _cyclicSize];
    }

    private static int CheckHashBytes(int hashBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hashBytes, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hashBytes, 4);
        return hashBytes;
    }

    /// <inheritdoc />
    public override int FindMatches(byte[] buffer, int position, int available, Span<LzmaMatch> matches)
    {
        int cp = _cyclicPos;
        _cyclicPos = cp + 1 == _cyclicSize ? 0 : cp + 1;
        if (available < _hashBytes)
        {
            _son[2 * cp] = 0;
            _son[(2 * cp) + 1] = 0;
            return 0;
        }

        int lenLimit = Math.Min(available, LzmaModel.MaxMatchLength);
        CheckWindow(buffer, position, lenLimit);
        PrefetchAhead(buffer, position, available, cp, _son, 2);
        int cur = UpdateHashes(buffer, position, out int c2, out int c3);
        int count = 0;
        int maxLen = AddShortMatches(buffer, position, lenLimit, c2, c3, matches, ref count);
        Walk(buffer, position, cp, cur, lenLimit, maxLen, matches, ref count, record: true);
        return count;
    }

    /// <inheritdoc />
    public override void Skip(byte[] buffer, int position, int available)
    {
        int cp = _cyclicPos;
        _cyclicPos = cp + 1 == _cyclicSize ? 0 : cp + 1;
        if (available < _hashBytes)
        {
            _son[2 * cp] = 0;
            _son[(2 * cp) + 1] = 0;
            return;
        }

        int lenLimit = Math.Min(available, LzmaModel.MaxMatchLength);
        CheckWindow(buffer, position, lenLimit);
        PrefetchAhead(buffer, position, available, cp, _son, 2);
        int cur = UpdateHashes(buffer, position, out _, out _);
        int count = 0;
        Walk(buffer, position, cp, cur, lenLimit, int.MaxValue, default, ref count, record: false);
    }

    // A search stops at the nice length and hangs the node it reached below the new one; with fewer bytes than that to compare, the two
    // strings only look equal because the data ends.
    internal override int MinimumLookaheadToRecord => _niceLength;

    private protected override void ResetLinks() => Array.Clear(_son);

    private protected override void RebaseLinks(int delta) => Rebase(_son, delta);

    private void Walk(byte[] buffer, int position, int cp, int cur, int lenLimit, int maxLen, Span<LzmaMatch> matches, ref int count, bool record)
    {
        int[] son = _son;
        int dict = _dictionarySize;
        int cyclic = _cyclicSize;
        int nice = Math.Min(_niceLength, lenLimit);
        int ptr1 = 2 * cp;       // where the next node smaller than the current string attaches
        int ptr0 = (2 * cp) + 1; // where the next node greater than the current string attaches
        int len0 = 0;
        int len1 = 0;
        for (int steps = _depth; ; )
        {
            int delta = position - cur;
            // cur is a stored position: 1 up to position - 1, within the dictionary. (The unsigned test also stops a position that is
            // not behind this one, which only a caller that went backwards could leave in the table.)
            if (cur <= 0 || (uint)(delta - 1) >= (uint)dict || steps-- == 0)
            {
                LzmaMatchRaw.L(son, ptr0) = 0;
                LzmaMatchRaw.L(son, ptr1) = 0;
                return;
            }

            int slot = cp - delta;
            if (slot < 0)
            {
                slot += cyclic;
            }

            int pair = 2 * slot;
            int len = Math.Min(len0, len1);

            // Both children of this node are about to be needed one at a time; their data and their own links are fetched now,
            // so whichever way the comparison goes, the next step does not wait for memory.
            int smallerChild = LzmaMatchRaw.L(son, pair + 1);
            int greaterChild = LzmaMatchRaw.L(son, pair);
            PrefetchNode(buffer, son, position, cp, cyclic, smallerChild, len);
            PrefetchNode(buffer, son, position, cp, cyclic, greaterChild, len);

            if (LzmaMatchRaw.B(buffer, cur + len) == LzmaMatchRaw.B(buffer, position + len))
            {
                len = CommonLength(buffer, cur, position, len + 1, lenLimit);
                if (record && len > maxLen)
                {
                    maxLen = len;
                    matches[count++] = new LzmaMatch { Length = len, Distance = delta };
                }

                if (len >= nice)
                {
                    LzmaMatchRaw.L(son, ptr1) = greaterChild;
                    LzmaMatchRaw.L(son, ptr0) = smallerChild;
                    return;
                }
            }

            if (LzmaMatchRaw.B(buffer, cur + len) < LzmaMatchRaw.B(buffer, position + len))
            {
                LzmaMatchRaw.L(son, ptr1) = cur;
                ptr1 = pair + 1;
                cur = smallerChild;
                len1 = len;
            }
            else
            {
                LzmaMatchRaw.L(son, ptr0) = cur;
                ptr0 = pair;
                cur = greaterChild;
                len0 = len;
            }
        }
    }

    // Prefetch hints for a node the walk may visit next: the byte it will be compared at, and its pair of links.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void PrefetchNode(byte[] buffer, int[] son, int position, int cp, int cyclic, int node, int len)
    {
        if (node > 0)
        {
            int slot = cp - (position - node);
            if (slot < 0)
            {
                slot += cyclic;
            }

            LzmaMatchRaw.PrefetchByte(buffer, node + len);
            LzmaMatchRaw.PrefetchLink(son, 2 * slot);
        }
    }
}
