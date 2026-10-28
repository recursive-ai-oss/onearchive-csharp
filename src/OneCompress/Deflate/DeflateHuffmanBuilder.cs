using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// Builds length-limited prefix codes from symbol frequencies, as Deflate needs them (RFC 1951 section 3.2.2).
/// The code is a Huffman code, with lengths above the limit repaired by lengthening the least frequent symbols that have room
/// and then shortening the most frequent ones to use up the freed space. The result depends only on the frequencies.
/// </summary>
/// <remarks>An instance owns scratch arrays and is reused without allocating; it is not thread-safe.</remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateHuffmanBuilder
{
    private const int MaxSymbols = 288;
    private const int SymbolBits = 9;

    private readonly ulong[] _keys = new ulong[MaxSymbols];
    private readonly ulong[] _weights = new ulong[MaxSymbols];
    private readonly int[] _lengths = new int[MaxSymbols];

    /// <summary>
    /// Computes code lengths for <paramref name="frequencies"/>. Symbols with frequency 0 get length 0. When exactly one symbol is
    /// used it gets length 1 (an incomplete code, which RFC 1951 allows only for a distance alphabet).
    /// </summary>
    /// <param name="frequencies">One count per symbol; at most 288 symbols.</param>
    /// <param name="maxBits">The longest code allowed, from 1 to 15 (7 for the code-length alphabet). There must be room: <c>2^maxBits</c> must be at least the number of used symbols.</param>
    /// <param name="lengths">Receives one length per symbol; at least as long as <paramref name="frequencies"/>.</param>
    /// <exception cref="ArgumentException">A span is too short or too long, or the used symbols cannot fit in <paramref name="maxBits"/> bits.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBits"/> is outside 1..15.</exception>
    public void BuildLengths(ReadOnlySpan<uint> frequencies, int maxBits, Span<byte> lengths)
    {
        if (maxBits is < 1 or > DeflateTables.MaxCodeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBits), "The maximum code length must be from 1 to 15.");
        }

        if (frequencies.Length > MaxSymbols)
        {
            throw new ArgumentException("A Deflate alphabet has at most 288 symbols.", nameof(frequencies));
        }

        if (lengths.Length < frequencies.Length)
        {
            throw new ArgumentException("The lengths span is shorter than the frequencies.", nameof(lengths));
        }

        lengths.Slice(0, frequencies.Length).Clear();
        int used = 0;
        for (int symbol = 0; symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] != 0)
            {
                _keys[used++] = ((ulong)frequencies[symbol] << SymbolBits) | (uint)symbol;
            }
        }

        if (used == 0)
        {
            return;
        }

        if (used > (1 << maxBits))
        {
            throw new ArgumentException("More symbols are used than a code of the maximum length can hold.", nameof(frequencies));
        }

        if (used == 1)
        {
            lengths[(int)(_keys[0] & ((1 << SymbolBits) - 1))] = 1;
            return;
        }

        // Unique keys, so any sort order of equal frequencies is the symbol order: the result is fixed by the input.
        _keys.AsSpan(0, used).Sort();
        for (int i = 0; i < used; i++)
        {
            _weights[i] = _keys[i] >> SymbolBits;
        }

        CalculateLengths(_weights, used);
        for (int i = 0; i < used; i++)
        {
            _lengths[i] = Math.Min((int)_weights[i], maxBits);
        }

        LimitLengths(used, maxBits);
        for (int i = 0; i < used; i++)
        {
            lengths[(int)(_keys[i] & ((1 << SymbolBits) - 1))] = (byte)_lengths[i];
        }
    }

    /// <summary>
    /// In-place minimum-redundancy code lengths for weights sorted ascending (Moffat and Katajainen, "In-place calculation of
    /// minimum-redundancy codes", 1995). On return <paramref name="a"/>[i] is the length of the i-th lightest symbol.
    /// </summary>
    private static void CalculateLengths(ulong[] a, int n)
    {
        // Phase 1: build the tree, leaving parent pointers.
        a[0] += a[1];
        int root = 0;
        int leaf = 2;
        for (int next = 1; next < n - 1; next++)
        {
            if (leaf >= n || a[root] < a[leaf])
            {
                a[next] = a[root];
                a[root++] = (ulong)next;
            }
            else
            {
                a[next] = a[leaf++];
            }

            // The second child; the weight lands in a[next] on top of the first child's.
            if (leaf >= n || (root < next && a[root] < a[leaf]))
            {
                a[next] += a[root];
                a[root++] = (ulong)next;
            }
            else
            {
                a[next] += a[leaf++];
            }
        }

        // Phase 2: parent pointers to internal node depths.
        a[n - 2] = 0;
        for (int next = n - 3; next >= 0; next--)
        {
            a[next] = a[(int)a[next]] + 1;
        }

        // Phase 3: internal node depths to leaf depths.
        int available = 1;
        int usedNodes = 0;
        int depth = 0;
        int rootIndex = n - 2;
        int nextLeaf = n - 1;
        while (available > 0)
        {
            while (rootIndex >= 0 && a[rootIndex] == (ulong)depth)
            {
                usedNodes++;
                rootIndex--;
            }

            while (available > usedNodes)
            {
                a[nextLeaf--] = (ulong)depth;
                available--;
            }

            available = 2 * usedNodes;
            depth++;
            usedNodes = 0;
        }
    }

    // Index 0 is the least frequent symbol. After clamping to maxBits the Kraft sum may exceed 1: lengthen the least frequent
    // symbols that still have room until it fits, then give any slack back to the most frequent symbols.
    private void LimitLengths(int n, int maxBits)
    {
        long limit = 1L << maxBits;
        long kraft = 0;
        for (int i = 0; i < n; i++)
        {
            kraft += 1L << (maxBits - _lengths[i]);
        }

        if (kraft == limit)
        {
            return;
        }

        int p = 0;
        while (kraft > limit)
        {
            while (_lengths[p] >= maxBits)
            {
                p++;
            }

            _lengths[p]++;
            kraft -= 1L << (maxBits - _lengths[p]);
        }

        // Give the slack back. Every cost below is a multiple of the cost of shortening a deepest symbol, as is the slack itself,
        // so shortening the most frequent deepest symbol always fits and the code ends up complete.
        while (kraft < limit)
        {
            int deepest = 0;
            int pick = -1;
            for (int i = 0; i < n; i++)
            {
                if (_lengths[i] >= deepest)
                {
                    deepest = _lengths[i];
                    pick = i;
                }
            }

            kraft += 1L << (maxBits - deepest);
            _lengths[pick] = deepest - 1;
        }
    }
}
