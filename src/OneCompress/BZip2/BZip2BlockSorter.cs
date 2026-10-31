
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The forward Burrows–Wheeler transform of a bzip2 block: sorts all cyclic rotations of the block and returns the last column
/// and the row holding the original (derivation note, fact F6).
/// </summary>
/// <remarks>
/// <para>Method, in two phases. First the rotations are bucketed by their first two bytes (a counting pass), and each bucket is sorted by
/// the next five bytes: a rotation is packed with those five bytes into one 64-bit word (key in the upper 40 bits, rotation index in the
/// lower 24), the words are sorted as plain integers (a most-significant-byte radix split for large buckets, then a quicksort whose
/// partition has no data-dependent branch), and runs of equal keys are left to the second phase. After this the rotations are ordered on
/// their first seven bytes.
/// The second phase is prefix doubling (Manber and Myers, 1990) organised as Larsson and Sadakane describe (1999). A group of rotations that
/// agree on their first <c>h</c> bytes is split by the group that the rotation <c>h</c> bytes further on belongs to, which orders them on
/// <c>2h</c> bytes, and <c>h</c> doubles until it passes the block length. Only the groups that are still tied are revisited. Both
/// phases break ties by rotation index, so equal rotations (a periodic block) come out in index order whatever the sort routine: the output
/// depends on the block alone.</para>
/// <para>Memory: two 32-bit words and two 64-bit words per block byte (the sort keys and the radix scratch), kept between calls.</para>
/// </remarks>
public sealed class BZip2BlockSorter
{
    private const int KeyBytes = 5;
    private const int FirstDepth = 2;
    private const int SortedDepth = FirstDepth + KeyBytes; // 7 bytes
    private const int Tail = SortedDepth + 8;
    private const ulong IndexMask = 0xFFFFFF;
    private const int RadixThreshold = 1024;

    private int[] _order = [];
    private int[] _rank = [];
    private ulong[] _keys = [];
    private ulong[] _scratch = [];
    private byte[] _ext = [];
    private int[] _groups = [];
    private int[] _nextGroups = [];
    private int _groupCount;
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
            _keys = new ulong[n];
            _scratch = new ulong[n];
        }

        if (_ext.Length < n + Tail)
        {
            _ext = new byte[n + Tail];
        }

        // The block followed by its own beginning, so that a read of eight bytes at any rotation start never needs a wrap check.
        byte[] ext = _ext;
        block.CopyTo(ext);
        for (int i = 0; i < Tail; i++)
        {
            ext[n + i] = block[i % n];
        }

        int[] order = _order;
        int[] rank = _rank;
        ulong[] keys = _keys;

        // Bucket by the first two bytes (wrapping).
        Array.Clear(_start);
        for (int i = 0; i < n; i++)
        {
            _start[(ext[i] << 8) | ext[i + 1]]++;
        }

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
            int key = (ext[i] << 8) | ext[i + 1];
            keys[_cursor[key]++] = KeyAt(ext, i, FirstDepth);
        }

        _groupCount = 0;
        for (int k = 0; k < 65536; k++)
        {
            int lo = _start[k];
            int hi = _cursor[k];
            if (hi - lo == 1)
            {
                int p = (int)(keys[lo] & IndexMask);
                order[lo] = p;
                rank[p] = lo;
            }
            else if (hi - lo > 1)
            {
                SortRange(keys, order, rank, lo, hi);
            }
        }

        if (_groupCount > 0)
        {
            Double(order, rank, keys, n);
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

    // The five bytes at position + depth, big-endian in the upper 40 bits, with the rotation index below them.
    private static ulong KeyAt(byte[] ext, int position, int depth) =>
        (System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(ext.AsSpan(position + depth)) & 0xFFFFFFFFFF000000UL) | (uint)position;

    // keys[lo..hi) hold rotations (index in the low 24 bits) with their key bytes filled in. Sorts them, then deals with each run of equal
    // keys: a single rotation is final; a longer run is tied on SortedDepth bytes, gets the rank of its first row and is left to the
    // doubling phase.
    private void SortRange(ulong[] keys, int[] order, int[] rank, int lo, int hi)
    {
        RadixSort(keys, lo, hi, 0);
        int a = lo;
        while (a < hi)
        {
            ulong key = keys[a] >> 24;
            int b = a + 1;
            while (b < hi && (keys[b] >> 24) == key)
            {
                b++;
            }

            for (int i = a; i < b; i++)
            {
                int p = (int)(keys[i] & IndexMask);
                order[i] = p;
                rank[p] = a;
            }

            if (b - a > 1)
            {
                _groupCount = Append(ref _groups, _groupCount, a, b);
            }

            a = b;
        }
    }

    // Sorts keys[lo..hi) by their five key bytes. Ranges of RadixThreshold or more are split on one key byte at a time (byte `level`, most
    // significant first) with a counting pass; smaller ranges, and ranges where the bytes left are few, go to the library sort. Either way the
    // words end up in increasing order, so the result does not depend on the route taken.
    private void RadixSort(ulong[] keys, int lo, int hi, int level)
    {
        int size = hi - lo;
        if (size < RadixThreshold || level >= KeyBytes)
        {
            if (size > 1)
            {
                SortWords(keys.AsSpan(lo, size));
            }

            return;
        }

        int shift = 56 - (8 * level);
        Span<int> count = stackalloc int[257];
        count.Clear();
        Span<ulong> range = keys.AsSpan(lo, size);
        foreach (ulong v in range)
        {
            count[(int)((v >> shift) & 0xFF) + 1]++;
        }

        int largest = 0;
        for (int b = 1; b <= 256; b++)
        {
            largest = Math.Max(largest, count[b]);
            count[b] += count[b - 1];
        }

        if (largest != size)
        {
            Span<ulong> scratch = _scratch.AsSpan(lo, size);
            Span<int> cursor = stackalloc int[256];
            count[..256].CopyTo(cursor);
            foreach (ulong v in range)
            {
                scratch[cursor[(int)((v >> shift) & 0xFF)]++] = v;
            }

            scratch.CopyTo(range);
        }

        for (int b = 0; b < 256; b++)
        {
            int from = count[b];
            int to = count[b + 1];
            if (to - from > 1)
            {
                RadixSort(keys, lo + from, lo + to, level + 1);
            }
        }
    }

    // Sorts 64-bit words in increasing order. Every word in a call is distinct (the low bits hold the rotation index), which lets the
    // partition below run without a branch per element: a quicksort whose comparisons are branch mispredictions in the library sort costs
    // more than the sort's other work here. The result is the sorted sequence of the input, so it cannot depend on how it is reached.
    private static void SortWords(Span<ulong> a)
    {
        int budget = 2 * (System.Numerics.BitOperations.Log2((uint)a.Length) + 1);
        while (a.Length > 16)
        {
            if (budget-- == 0)
            {
                a.Sort();
                return;
            }

            int last = a.Length - 1;
            int mid = last >> 1;
            // Median of three goes to the end as the pivot.
            if (a[mid] < a[0])
            {
                (a[mid], a[0]) = (a[0], a[mid]);
            }

            if (a[last] < a[0])
            {
                (a[last], a[0]) = (a[0], a[last]);
            }

            if (a[last] < a[mid])
            {
                (a[last], a[mid]) = (a[mid], a[last]);
            }

            // Now a[0] <= a[mid] <= a[last]; the median a[mid] is the pivot, moved next to the end.
            ulong pivot = a[mid];
            a[mid] = a[last - 1];
            a[last - 1] = pivot;

            int i = 1;
            for (int j = 1; j < last - 1; j++)
            {
                ulong v = a[j];
                ulong w = a[i];
                a[j] = w;
                a[i] = v;
                i += v < pivot ? 1 : 0;
            }

            a[last - 1] = a[i];
            a[i] = pivot;

            // Recurse into the smaller part, loop on the larger.
            Span<ulong> left = a[..i];
            Span<ulong> right = a[(i + 1)..];
            if (left.Length < right.Length)
            {
                SortWords(left);
                a = right;
            }
            else
            {
                SortWords(right);
                a = left;
            }
        }

        for (int x = 1; x < a.Length; x++)
        {
            ulong v = a[x];
            int y = x - 1;
            while (y >= 0 && a[y] > v)
            {
                a[y + 1] = a[y];
                y--;
            }

            a[y + 1] = v;
        }
    }

    // Prefix doubling over the groups that are still tied after SortedDepth bytes.
    private void Double(int[] order, int[] rank, ulong[] keys, int n)
    {
        for (int h = SortedDepth; h < n && _groupCount > 0; h <<= 1)
        {
            int count = _groupCount;
            int[] groups = _groups;
            int[] next = _nextGroups;
            int nextCount = 0;
            for (int g = 0; g < count; g++)
            {
                nextCount = SortGroup(order, rank, keys, groups[g * 2], groups[(g * 2) + 1], h, n, ref next, nextCount);
            }

            _nextGroups = groups;
            _groups = next;
            _groupCount = nextCount;
        }
    }

    // Orders the rotations order[start..end) by the group of the rotation h bytes on, and gives the new subgroups their ranks.
    // Subgroups with more than one member are appended to `next`.
    private static int SortGroup(int[] order, int[] rank, ulong[] keys, int start, int end, int h, int n, ref int[] next, int nextCount)
    {
        Span<ulong> k = keys.AsSpan(0, end - start);
        for (int t = 0; t < k.Length; t++)
        {
            int p = order[start + t];
            int q = p + h;
            if (q >= n)
            {
                q -= n;
            }

            k[t] = ((ulong)(uint)rank[q] << 32) | (uint)p;
        }

        if (k.Length <= 12)
        {
            // Most tied groups are a handful of rotations; an insertion sort beats the library call there.
            for (int a = 1; a < k.Length; a++)
            {
                ulong v = k[a];
                int b = a - 1;
                while (b >= 0 && k[b] > v)
                {
                    k[b + 1] = k[b];
                    b--;
                }

                k[b + 1] = v;
            }
        }
        else
        {
            SortWords(k);
        }

        ulong previous = ulong.MaxValue;
        int subStart = start;
        for (int t = 0; t < k.Length; t++)
        {
            int p = (int)(k[t] & 0xFFFFFFFF);
            ulong key = k[t] >> 32;
            if (key != previous)
            {
                if (previous != ulong.MaxValue && start + t - subStart > 1)
                {
                    nextCount = Append(ref next, nextCount, subStart, start + t);
                }

                previous = key;
                subStart = start + t;
            }

            order[start + t] = p;
            rank[p] = subStart;
        }

        if (end - subStart > 1)
        {
            nextCount = Append(ref next, nextCount, subStart, end);
        }

        return nextCount;
    }

    private static int Append(ref int[] list, int count, int start, int end)
    {
        if ((count + 1) * 2 > list.Length)
        {
            Array.Resize(ref list, Math.Max(1024, list.Length * 2));
        }

        list[count * 2] = start;
        list[(count * 2) + 1] = end;
        return count + 1;
    }
}
