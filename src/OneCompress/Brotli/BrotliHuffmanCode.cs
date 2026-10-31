
namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Builds length-limited prefix codes from symbol counts and assigns their canonical codes (RFC 7932 section 3.2). Part of the raw
/// layer: bespoke writers can combine it with <see cref="BrotliPrefixCodeWriter"/>.
/// </summary>
/// <remarks>
/// <para>The construction is plain Huffman: leaves ordered by (count, symbol), two merged queues, and a merge takes the leaf when a leaf
/// and an internal node tie. When a code comes out longer than the limit, every count is raised to a floor that doubles with each
/// attempt (1, 2, 4, ...) and the tree is built again; the floor flattens the rare symbols first, which is where the length comes from.
/// The result depends only on the counts, so the same input always gives the same code.</para>
/// <para>A symbol that is alone in its alphabet gets length 0: a prefix code with one symbol takes no bits (RFC 7932 section 3.4).</para>
/// </remarks>
public static class BrotliHuffmanCode
{
    /// <summary>The longest code length a brotli prefix code may have (RFC 7932 section 3.2).</summary>
    public const int MaxCodeLength = 15;

    /// <summary>Computes code lengths for the given symbol counts.</summary>
    /// <param name="counts">The number of occurrences of each symbol. Symbols with a count of 0 get length 0 and no code.</param>
    /// <param name="maxLength">The longest length allowed, 1 to 15. The number of symbols with a nonzero count must be at most 2 to the power <paramref name="maxLength"/>.</param>
    /// <param name="lengths">Receives one length per symbol; its length must be that of <paramref name="counts"/>.</param>
    /// <exception cref="ArgumentException">The spans differ in length, or too many symbols are used for <paramref name="maxLength"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is outside 1 to 15.</exception>
    public static void BuildLengths(ReadOnlySpan<int> counts, int maxLength, Span<byte> lengths)
    {
        if (counts.Length != lengths.Length)
        {
            throw new ArgumentException("There must be one length per count.", nameof(lengths));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, MaxCodeLength);

        lengths.Clear();
        int used = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                used++;
            }
        }

        if (used > (1 << maxLength))
        {
            throw new ArgumentException("Too many symbols for the length limit.", nameof(counts));
        }

        if (used <= 1)
        {
            return;
        }

        int[] symbols = new int[used];
        int n = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0)
            {
                symbols[n++] = i;
            }
        }

        long[] keys = new long[used];
        long[] weight = new long[(2 * used) - 1];
        int[] link = new int[(2 * used) - 1];
        byte[] depth = new byte[(2 * used) - 1];
        long floor = 1;
        while (true)
        {
            // Leaves are ordered by (weight, symbol); the key packs both so the order is total and independent of the sort's stability.
            for (int i = 0; i < used; i++)
            {
                keys[i] = (Math.Max(counts[symbols[i]], floor) << 32) | (uint)symbols[i];
            }

            Array.Sort(keys);
            for (int i = 0; i < used; i++)
            {
                weight[i] = keys[i] >> 32;
            }

            int leaf = 0;
            int head = used;
            int node = used;
            for (int made = 0; made < used - 1; made++)
            {
                int a = TakeSmallest(weight, ref leaf, used, ref head, node);
                int b = TakeSmallest(weight, ref leaf, used, ref head, node);
                weight[node] = weight[a] + weight[b];
                link[a] = node;
                link[b] = node;
                node++;
            }

            depth[node - 1] = 0;
            int maxDepth = 0;
            for (int i = node - 2; i >= 0; i--)
            {
                depth[i] = (byte)(depth[link[i]] + 1);
                if (i < used && depth[i] > maxDepth)
                {
                    maxDepth = depth[i];
                }
            }

            if (maxDepth <= maxLength)
            {
                for (int i = 0; i < used; i++)
                {
                    lengths[(int)(keys[i] & 0xFFFFFFFF)] = depth[i];
                }

                return;
            }

            floor <<= 1;
        }
    }

    // Takes the lighter of the next leaf and the next internal node. Leaves are 0..leaves-1, internal nodes follow in creation order.
    private static int TakeSmallest(long[] weight, ref int leaf, int leaves, ref int head, int internalEnd)
    {
        if (leaf < leaves && (head >= internalEnd || weight[leaf] <= weight[head]))
        {
            return leaf++;
        }

        return head++;
    }

    /// <summary>Assigns canonical codes to code lengths, in the bit order a brotli stream carries them.</summary>
    /// <param name="lengths">The code length of each symbol; 0 for symbols without a code. The lengths must not oversubscribe the code space.</param>
    /// <param name="codes">Receives, for each symbol, its code with the first bit to be written in the lowest position, so that <c>Write(code, length)</c> emits it. Must be as long as <paramref name="lengths"/>.</param>
    /// <exception cref="ArgumentException">The spans differ in length.</exception>
    public static void AssignCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        if (lengths.Length != codes.Length)
        {
            throw new ArgumentException("There must be one code per length.", nameof(codes));
        }

        // RFC 7932 section 3.2: codes of equal length are consecutive in symbol order, and each length starts after the previous one.
        Span<int> countOfLength = stackalloc int[MaxCodeLength + 2];
        countOfLength.Clear();
        for (int i = 0; i < lengths.Length; i++)
        {
            countOfLength[lengths[i]]++;
        }

        countOfLength[0] = 0;
        Span<int> next = stackalloc int[MaxCodeLength + 2];
        int code = 0;
        for (int bits = 1; bits <= MaxCodeLength; bits++)
        {
            code = (code + countOfLength[bits - 1]) << 1;
            next[bits] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len == 0)
            {
                codes[i] = 0;
                continue;
            }

            int c = next[len]++;
            // Prefix codes are written starting with the code's most significant bit, which a least-significant-first writer needs reversed.
            codes[i] = (ushort)Reverse(c, len);
        }
    }

    private static int Reverse(int code, int length)
    {
        int result = 0;
        for (int i = 0; i < length; i++)
        {
            result = (result << 1) | (code & 1);
            code >>= 1;
        }

        return result;
    }
}
