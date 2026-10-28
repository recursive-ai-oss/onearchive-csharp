using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Finds static dictionary words (RFC 7932 section 8) that continue at a position of the input. Words are indexed by a hash of their first
/// four bytes; a candidate is accepted only after the whole transformed word has been compared with the input, so the index only has to
/// be a good filter.
/// </summary>
internal sealed class BrotliDictionaryIndex
{
    private const int HashBits = 18;

    private static BrotliDictionaryIndex? s_instance;

    private readonly int[] _head = new int[1 << HashBits];
    private readonly int[] _next;
    private readonly int[] _entries; // length << 16 | index

    // Per transformation: the prefix bytes, the kind of elementary transform, how many bytes it omits and the suffix length.
    private readonly byte[][] _prefix = new byte[BrotliTransforms.Count][];
    private readonly BrotliTransformKind[] _kind = new BrotliTransformKind[BrotliTransforms.Count];
    private readonly int[] _omitted = new int[BrotliTransforms.Count];

    // Transformations that the search can find, by the first byte of their prefix (256 for an empty prefix).
    private readonly List<int>[] _byFirstByte = new List<int>[257];

    private BrotliDictionaryIndex()
    {
        int count = 0;
        for (int len = BrotliWords.MinWordLength; len <= BrotliWords.MaxWordLength; len++)
        {
            count += BrotliWords.GetWordCount(len);
        }

        _entries = new int[count];
        _next = new int[count];
        Array.Fill(_head, -1);
        int n = 0;
        // Insert in descending order so that chains list the lowest indexes (cheapest distances) first.
        for (int len = BrotliWords.MaxWordLength; len >= BrotliWords.MinWordLength; len--)
        {
            for (int index = BrotliWords.GetWordCount(len) - 1; index >= 0; index--)
            {
                ReadOnlySpan<byte> word = BrotliWords.GetWord(len, index);
                int h = Hash(word);
                _entries[n] = (len << 16) | index;
                _next[n] = _head[h];
                _head[h] = n;
                n++;
            }
        }

        for (int i = 0; i < _byFirstByte.Length; i++)
        {
            _byFirstByte[i] = [];
        }

        for (int t = 0; t < BrotliTransforms.Count; t++)
        {
            _prefix[t] = BrotliTransforms.GetPrefix(t).ToArray();
            _kind[t] = BrotliTransforms.GetKind(t, out _omitted[t]);
            if (_kind[t] == BrotliTransformKind.OmitFirst)
            {
                continue; // The first bytes of the word are not in the output, so there is nothing to look the word up by.
            }

            _byFirstByte[_prefix[t].Length == 0 ? 256 : _prefix[t][0]].Add(t);
        }
    }

    internal static BrotliDictionaryIndex Instance => LazyInitializer.EnsureInitialized(ref s_instance, static () => new BrotliDictionaryIndex());

    private static int Hash(ReadOnlySpan<byte> four) => (int)((BinaryPrimitives.ReadUInt32LittleEndian(four) * 0x1E35A7BDu) >> (32 - HashBits));

    /// <summary>A dictionary word that matches the input: its length, the word id the distance encodes, and the bytes it covers once transformed.</summary>
    internal readonly record struct Hit(int WordLength, int WordId, int Covered);

    /// <summary>
    /// Finds the dictionary references that match the input at <paramref name="position"/>, where at most <paramref name="maxLength"/> bytes
    /// may be covered.
    /// </summary>
    /// <param name="buffer">The input.</param>
    /// <param name="position">Where the reference would start.</param>
    /// <param name="maxLength">The most bytes the reference may cover.</param>
    /// <param name="allTransforms">True to try every transformation, false for the identity only.</param>
    /// <param name="hits">Receives the matches; the search stops when it is full.</param>
    /// <param name="transformed">Scratch space for a transformed word, at least <see cref="BrotliTransforms.MaxTransformedLength"/> bytes.</param>
    /// <returns>The number of matches.</returns>
    internal int Search(ReadOnlySpan<byte> buffer, int position, int maxLength, bool allTransforms, Span<Hit> hits, Span<byte> transformed)
    {
        int count = 0;
        if (maxLength < 4 || position + 4 > buffer.Length)
        {
            return 0;
        }

        if (!allTransforms)
        {
            return Probe(buffer, position, maxLength, 0, position, transformed, hits, 0);
        }

        // Transformations with an empty prefix start at the position; the others start with their prefix.
        foreach (int t in _byFirstByte[256])
        {
            count = Probe(buffer, position, maxLength, t, position, transformed, hits, count);
        }

        byte first = buffer[position];
        foreach (int t in _byFirstByte[first])
        {
            byte[] prefix = _prefix[t];
            if (position + prefix.Length + 4 <= buffer.Length && buffer.Slice(position, prefix.Length).SequenceEqual(prefix))
            {
                count = Probe(buffer, position, maxLength, t, position + prefix.Length, transformed, hits, count);
            }
        }

        return count;
    }

    private int Probe(ReadOnlySpan<byte> buffer, int position, int maxLength, int transform, int wordStart, Span<byte> transformed, Span<Hit> hits, int count)
    {
        if (wordStart + 4 > buffer.Length)
        {
            return count;
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(wordStart));
        if (_kind[transform] == BrotliTransformKind.Identity || _kind[transform] == BrotliTransformKind.OmitLast)
        {
            if (_head[(int)((raw * 0x1E35A7BDu) >> (32 - HashBits))] < 0)
            {
                return count;
            }
        }

        Span<byte> key = stackalloc byte[4];
        buffer.Slice(wordStart, 4).CopyTo(key);
        switch (_kind[transform])
        {
            case BrotliTransformKind.FermentFirst:
                key[0] = Lower(key[0]);
                break;
            case BrotliTransformKind.FermentAll:
                for (int i = 0; i < 4; i++)
                {
                    key[i] = Lower(key[i]);
                }

                break;
        }

        int omittedLast = _kind[transform] == BrotliTransformKind.OmitLast ? _omitted[transform] : 0;
        for (int e = _head[Hash(key)]; e >= 0 && count < hits.Length; e = _next[e])
        {
            int len = _entries[e] >> 16;
            int index = _entries[e] & 0xFFFF;
            if (len - omittedLast < 4)
            {
                continue;
            }

            ReadOnlySpan<byte> word = BrotliWords.GetWord(len, index);
            if (!word.Slice(0, 4).SequenceEqual(key))
            {
                continue;
            }

            int n = BrotliTransforms.Apply(transform, word, transformed);
            if (n > maxLength || position + n > buffer.Length || !buffer.Slice(position, n).SequenceEqual(transformed.Slice(0, n)))
            {
                continue;
            }

            hits[count++] = new Hit(len, index | (transform << BrotliWords.GetWordBits(len)), n);
        }

        return count;
    }

    private static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
