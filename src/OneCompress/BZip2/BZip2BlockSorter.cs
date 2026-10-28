using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The forward Burrows–Wheeler transform of a bzip2 block: sorts all cyclic rotations of the block and returns the last column
/// and the row holding the original (derivation note, fact F6).
/// </summary>
/// <remarks>
/// <para>Method: prefix doubling (Manber and Myers, 1990) organised as Larsson and Sadakane describe (1999). Rotations are first
/// bucketed by their first two bytes. A group of rotations that agree on their first <c>h</c> bytes is then split by the group
/// that the rotation <c>h</c> bytes further on belongs to, which orders them on <c>2h</c> bytes; only groups with more than one member are
/// revisited, and <c>h</c> doubles until it passes the block length. Sorting is by (rank, rotation index), so equal rotations
/// (a periodic block) come out in index order whatever the sort routine: the output depends on the block alone.</para>
/// <para>Memory: two 32-bit words and one 64-bit word per block byte, kept between calls.</para>
/// </remarks>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2BlockSorter
{
    private int[] _order = [];
    private int[] _rank = [];
    private long[] _keys = [];
    private readonly int[] _start = new int[65536];
    private readonly int[] _cursor = new int[65536];

    /// <summary>Sorts the rotations of a block.</summary>
    /// <param name="block">The block (the first-stage bytes), 1 to 16 777 215 bytes.</param>
    /// <param name="lastColumn">Receives the last column: byte <c>r</c> is the byte before the start of the rotation in sorted row <c>r</c>. At least <c>block.Length</c> long.</param>
    /// <returns>The row of the rotation that starts at byte 0 (the lowest-numbered one when several are equal).</returns>
    /// <exception cref="ArgumentException">The block is empty, too long, or <paramref name="lastColumn"/> is too short.</exception>
    public int Sort(ReadOnlySpan<byte> block, Span<byte> lastColumn)
    {
        int n = block.Length;
        if (n == 0 || n > 0xFFFFFF)
        {
            throw new ArgumentException("A block has 1 to 16 777 215 bytes.", nameof(block));
        }

        if (lastColumn.Length < n)
        {
            throw new ArgumentException("The last column buffer is shorter than the block.", nameof(lastColumn));
        }

        if (n == 1)
        {
            lastColumn[0] = block[0];
            return 0;
        }

        if (_order.Length < n)
        {
            _order = new int[n];
            _rank = new int[n];
            _keys = new long[n];
        }

        int[] order = _order;
        int[] rank = _rank;
        long[] keys = _keys;

        // Bucket by the first two bytes (wrapping): the rank of a rotation is the row where its group starts.
        Array.Clear(_start);
        for (int i = 0; i < n - 1; i++)
        {
            _start[(block[i] << 8) | block[i + 1]]++;
        }

        _start[(block[n - 1] << 8) | block[0]]++;
        int sum = 0;
        for (int k = 0; k < 65536; k++)
        {
            int c = _start[k];
            _start[k] = sum;
            _cursor[k] = sum;
            sum += c;
        }

        for (int i = 0; i < n; i++)
        {
            int key = (block[i] << 8) | block[i + 1 == n ? 0 : i + 1];
            order[_cursor[key]++] = i;
            rank[i] = _start[key];
        }

        for (int h = 2; h < n; h <<= 1)
        {
            bool unsorted = false;
            int j = 0;
            while (j < n)
            {
                int groupRank = rank[order[j]];
                int e = j + 1;
                while (e < n && rank[order[e]] == groupRank)
                {
                    e++;
                }

                if (e - j > 1)
                {
                    unsorted = true;
                    SortGroup(order, rank, keys, j, e, h, n);
                }

                j = e;
            }

            if (!unsorted)
            {
                break;
            }
        }

        int origin = 0;
        for (int r = 0; r < n; r++)
        {
            int p = order[r];
            if (p == 0)
            {
                origin = r;
                lastColumn[r] = block[n - 1];
            }
            else
            {
                lastColumn[r] = block[p - 1];
            }
        }

        return origin;
    }

    // Orders the rotations order[start..end) by the group of the rotation h bytes on, and gives the new subgroups their ranks.
    private static void SortGroup(int[] order, int[] rank, long[] keys, int start, int end, int h, int n)
    {
        Span<long> k = keys.AsSpan(0, end - start);
        for (int t = 0; t < k.Length; t++)
        {
            int p = order[start + t];
            int q = p + h;
            if (q >= n)
            {
                q -= n;
            }

            k[t] = ((long)rank[q] << 32) | (uint)p;
        }

        k.Sort();
        long previous = -1;
        int subStart = start;
        for (int t = 0; t < k.Length; t++)
        {
            int p = (int)(k[t] & 0xFFFFFFFF);
            long key = k[t] >> 32;
            if (key != previous)
            {
                previous = key;
                subStart = start + t;
            }

            order[start + t] = p;
            rank[p] = subStart;
        }
    }
}
