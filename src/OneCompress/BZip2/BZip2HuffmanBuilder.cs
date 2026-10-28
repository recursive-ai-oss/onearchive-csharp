using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Builds length-limited Huffman code lengths for a bzip2 table. Every symbol gets a length, used or not (a symbol with
/// frequency 0 counts as 1), because the format codes a length for each symbol of the alphabet (derivation note, fact F5).
/// </summary>
/// <remarks>
/// Huffman's construction with the two-queue method; ties go to the leaf, and leaves are ordered by (frequency, symbol), so the result
/// depends only on the frequencies. When the tree is deeper than the limit the frequencies are halved (rounding up, so none reaches 0) and the tree
/// is rebuilt; this ends because equal frequencies give a balanced tree, at most 9 deep for 258 symbols.
/// </remarks>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BZip2HuffmanBuilder
{
    /// <summary>The longest length the encoder writes by default: the longest the reference compressor was seen to write.</summary>
    public const int DefaultMaxLength = 17;

    private readonly int[] _freq = new int[BZip2HuffmanTable.MaxAlphabetSize];
    private readonly long[] _leaves = new long[BZip2HuffmanTable.MaxAlphabetSize];
    private readonly long[] _weight = new long[2 * BZip2HuffmanTable.MaxAlphabetSize];
    private readonly int[] _parent = new int[2 * BZip2HuffmanTable.MaxAlphabetSize];
    private readonly int[] _depth = new int[2 * BZip2HuffmanTable.MaxAlphabetSize];

    /// <summary>Computes code lengths for the symbols of an alphabet.</summary>
    /// <param name="frequencies">One count per symbol; 3 to 258 of them. A count of 0 is treated as 1.</param>
    /// <param name="lengths">Receives one length per symbol, each 1 to <paramref name="maxLength"/>.</param>
    /// <param name="maxLength">The longest length allowed, 9 to 20.</param>
    /// <exception cref="ArgumentException">The alphabet size is outside 3 to 258 or <paramref name="lengths"/> is shorter than <paramref name="frequencies"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLength"/> is outside 9 to 20.</exception>
    public void Build(ReadOnlySpan<int> frequencies, Span<byte> lengths, int maxLength = DefaultMaxLength)
    {
        int a = frequencies.Length;
        if (a is < BZip2HuffmanTable.MinAlphabetSize or > BZip2HuffmanTable.MaxAlphabetSize)
        {
            throw new ArgumentException("A bzip2 alphabet has 3 to 258 symbols.", nameof(frequencies));
        }

        if (lengths.Length < a)
        {
            throw new ArgumentException("The lengths buffer is shorter than the alphabet.", nameof(lengths));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 9);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxLength, BZip2HuffmanTable.MaxCodeLength);

        int[] f = _freq;
        for (int i = 0; i < a; i++)
        {
            f[i] = Math.Max(frequencies[i], 1);
        }

        while (true)
        {
            for (int i = 0; i < a; i++)
            {
                _leaves[i] = ((long)f[i] << 9) | (uint)i;
            }

            Array.Sort(_leaves, 0, a);
            for (int i = 0; i < a; i++)
            {
                _weight[i] = _leaves[i] >> 9;
            }

            int leaf = 0;
            int head = a;
            int tail = a;
            for (int m = 0; m < a - 1; m++)
            {
                int x = Take(ref leaf, ref head, tail, a);
                int y = Take(ref leaf, ref head, tail, a);
                _weight[tail] = _weight[x] + _weight[y];
                _parent[x] = tail;
                _parent[y] = tail;
                tail++;
            }

            int root = tail - 1;
            _depth[root] = 0;
            int deepest = 0;
            for (int node = root - 1; node >= 0; node--)
            {
                int d = _depth[_parent[node]] + 1;
                _depth[node] = d;
                if (node < a && d > deepest)
                {
                    deepest = d;
                }
            }

            if (deepest <= maxLength)
            {
                for (int i = 0; i < a; i++)
                {
                    lengths[(int)(_leaves[i] & 511)] = (byte)_depth[i];
                }

                return;
            }

            for (int i = 0; i < a; i++)
            {
                f[i] = (f[i] + 1) >> 1;
            }
        }
    }

    private int Take(ref int leaf, ref int head, int tail, int a)
    {
        if (leaf < a && (head >= tail || _weight[leaf] <= _weight[head]))
        {
            return leaf++;
        }

        return head++;
    }
}
