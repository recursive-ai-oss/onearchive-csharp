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
    private readonly ulong[] _present7 = new ulong[(1 << HashBits) / 64]; // the same for the first seven bytes of the words that have that many
    private readonly ulong[] _present = new ulong[(1 << HashBits) / 64]; // bit h set when _head[h] names a word: a small filter that stays in cache
    private readonly int[] _next;
    private readonly int[] _entries; // length << 16 | index
    private readonly uint[] _firstFour; // the first four bytes of each entry's word
    private readonly byte[] _fifth;     // its fifth byte, or 0 if it has none

    // Per transformation: the prefix bytes, the kind of elementary transform, how many bytes it omits and the suffix length.
    private readonly byte[][] _prefix = new byte[BrotliTransforms.Count][];
    private readonly BrotliTransformKind[] _kind = new BrotliTransformKind[BrotliTransforms.Count];
    private readonly int[] _omitted = new int[BrotliTransforms.Count];

    // Transformations that share a prefix share the test of whether it matches the input, and those that also share the kind of key
    // share the test of whether the dictionary has any word with that key. _prefixId numbers the distinct prefixes.
    private readonly int[] _prefixId = new int[BrotliTransforms.Count];
    private readonly int _prefixCount;
    private readonly int[] _affix = new int[BrotliTransforms.Count]; // prefix and suffix bytes a transformation adds to the word

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
        _firstFour = new uint[count];
        _fifth = new byte[count];
        Array.Fill(_head, -1);
        int n = 0;
        // Insert in descending order so that chains list the lowest indexes (cheapest distances) first.
        for (int len = BrotliWords.MaxWordLength; len >= BrotliWords.MinWordLength; len--)
        {
            for (int index = BrotliWords.GetWordCount(len) - 1; index >= 0; index--)
            {
                ReadOnlySpan<byte> word = BrotliWords.GetWord(len, index);
                int h = Hash(word);
                if (word.Length >= 7)
                {
                    int h7 = Hash7(BinaryPrimitives.ReadUInt64LittleEndian(word.Slice(0, 7).ToArray().Concat(new byte[1]).ToArray()));
                    _present7[h7 >> 6] |= 1UL << (h7 & 63);
                }

                _entries[n] = (len << 16) | index;
                _firstFour[n] = BinaryPrimitives.ReadUInt32LittleEndian(word);
                _fifth[n] = word.Length > 4 ? word[4] : (byte)0;
                _next[n] = _head[h];
                _head[h] = n;
                n++;
            }
        }

        for (int h = 0; h < _head.Length; h++)
        {
            if (_head[h] >= 0)
            {
                _present[h >> 6] |= 1UL << (h & 63);
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

        var distinct = new List<byte[]>();
        for (int t = 0; t < BrotliTransforms.Count; t++)
        {
            int id = distinct.FindIndex(p => p.AsSpan().SequenceEqual(_prefix[t]));
            if (id < 0)
            {
                id = distinct.Count;
                distinct.Add(_prefix[t]);
            }

            _prefixId[t] = id;
        }

        _prefixCount = distinct.Count;
        for (int t = 0; t < BrotliTransforms.Count; t++)
        {
            _affix[t] = _prefix[t].Length + BrotliTransforms.GetSuffix(t).Length;
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
    /// <param name="minCovered">Matches that would cover fewer bytes than this are not looked for.</param>
    /// <param name="transformed">Scratch space for a transformed word, at least <see cref="BrotliTransforms.MaxTransformedLength"/> bytes.</param>
    /// <returns>The number of matches.</returns>
    internal int Search(ReadOnlySpan<byte> buffer, int position, int maxLength, bool allTransforms, Span<Hit> hits, Span<byte> transformed, int minCovered = 0)
    {
        int count = 0;
        if (maxLength < 4 || position + 4 > buffer.Length)
        {
            return 0;
        }

        if (!allTransforms)
        {
            return Probe(buffer, position, maxLength, 0, position, transformed, hits, 0, minCovered);
        }

        // Transformations with an empty prefix start at the position; the others start with their prefix. Transformations that share a prefix
        // share the test of whether it matches, and those that also share the kind of key (as is, first letter lower-cased, all lower-cased)
        // share the list of words that have the key. Those lists are made on first use, in the order of the hash chain, so the hits come in
        // the same order as if every transformation walked the chain itself. A list that outgrows its room is walked as a chain instead.
        Span<byte> prefixState = stackalloc byte[_prefixCount]; // 0 not yet known, 1 matches, 2 does not
        Span<int> groupStart = stackalloc int[_prefixCount * 3];  // 0 not yet known, -1 no word has the key, -2 walk the chain, else 1 + start in pool
        Span<int> groupCount = stackalloc int[_prefixCount * 3];
        Span<int> pool = stackalloc int[160];
        Span<int> poolMatch = stackalloc int[160]; // for each pooled word, how many of its bytes from the fifth on agree with the input
        int used = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            List<int> transforms = pass == 0 ? _byFirstByte[256] : _byFirstByte[buffer[position]];
            foreach (int t in transforms)
            {
                int id = _prefixId[t];
                byte[] prefix = _prefix[t];
                if (prefixState[id] == 0)
                {
                    prefixState[id] = (byte)(position + prefix.Length + 4 <= buffer.Length && buffer.Slice(position, prefix.Length).SequenceEqual(prefix) ? 1 : 2);
                }

                if (prefixState[id] != 1)
                {
                    continue;
                }

                int kind = _kind[t] switch
                {
                    BrotliTransformKind.FermentFirst => 1,
                    BrotliTransformKind.FermentAll => 2,
                    _ => 0,
                };
                int g = (id * 3) + kind;
                int wordStart = position + prefix.Length;
                if (groupStart[g] == 0)
                {
                    uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(wordStart));
                    uint key = kind == 1 ? LowerFirst(raw) : kind == 2 ? LowerAll(raw) : raw;
                    if (!Present(key))
                    {
                        groupStart[g] = -1;
                    }
                    else
                    {
                        int start = used;
                        for (int e = _head[(int)((key * 0x1E35A7BDu) >> (32 - HashBits))]; e >= 0; e = _next[e])
                        {
                            if (_firstFour[e] != key)
                            {
                                continue;
                            }

                            if (used == pool.Length)
                            {
                                used = start;
                                start = -3; // -(-2) - 1 below; marks the overflow
                                break;
                            }

                            poolMatch[used] = BodyMatch(e, buffer, wordStart);
                            pool[used++] = e;
                        }

                        if (start == -3)
                        {
                            groupStart[g] = -2;
                        }
                        else
                        {
                            groupStart[g] = start + 1;
                            groupCount[g] = used - start;
                        }
                    }
                }

                if (groupStart[g] == -1)
                {
                    continue;
                }

                if (groupStart[g] == -2)
                {
                    count = Probe(buffer, position, maxLength, t, wordStart, transformed, hits, count, minCovered);
                }
                else
                {
                    count = ProbeList(buffer, position, maxLength, t, wordStart, transformed, hits, count, pool.Slice(groupStart[g] - 1, groupCount[g]), poolMatch.Slice(groupStart[g] - 1, groupCount[g]), minCovered);
                }
            }
        }

        return count;
    }

    // The words of a list (entries whose first four bytes are the key) that the transformation turns into the input at the position.
    private int ProbeList(ReadOnlySpan<byte> buffer, int position, int maxLength, int transform, int wordStart, Span<byte> transformed, Span<Hit> hits, int count, ReadOnlySpan<int> entries, ReadOnlySpan<int> bodyMatch, int minCovered)
    {
        BrotliTransformKind kind = _kind[transform];
        int omittedLast = kind == BrotliTransformKind.OmitLast ? _omitted[transform] : 0;
        for (int i = 0; i < entries.Length && count < hits.Length; i++)
        {
            int e = entries[i];
            if (!Accepts(e, kind, omittedLast, bodyMatch[i], _affix[transform], minCovered))
            {
                continue;
            }

            if (TryHit(e, transform, buffer, position, maxLength, transformed, out Hit hit))
            {
                hits[count++] = hit;
            }
        }

        return count;
    }

    // How many bytes of the word, from the fifth on, agree with the input at wordStart (as far as the word and the input go). Past the fourth
    // byte a transformed word shows the word's own bytes (a case change reaches at most the third), so for every transformation but
    // FermentAll a word whose body does not agree up to the part the transformation keeps cannot match. FermentAll changes every letter and
    // is checked in full instead.
    private int BodyMatch(int e, ReadOnlySpan<byte> buffer, int wordStart)
    {
        int len = _entries[e] >> 16;
        if (len <= 4)
        {
            return 0;
        }

        ReadOnlySpan<byte> rest = BrotliWords.GetWord(len, _entries[e] & 0xFFFF).Slice(4);
        int available = Math.Max(0, buffer.Length - (wordStart + 4));
        return rest.Slice(0, Math.Min(rest.Length, available)).CommonPrefixLength(buffer.Slice(Math.Min(buffer.Length, wordStart + 4)));
    }

    // The cheapest of the body tests, made before the word itself is fetched: the fifth byte, when the transformation keeps more than four.
    private bool FifthAgrees(int e, BrotliTransformKind kind, int omittedLast, ReadOnlySpan<byte> buffer, int wordStart) =>
        kind == BrotliTransformKind.FermentAll || (_entries[e] >> 16) - omittedLast <= 4 || (wordStart + 4 < buffer.Length && _fifth[e] == buffer[wordStart + 4]);

    private bool Accepts(int e, BrotliTransformKind kind, int omittedLast, int bodyMatch, int affix, int minCovered)
    {
        int kept = (_entries[e] >> 16) - omittedLast;
        return kept >= 4 && kept + affix >= minCovered && (kind == BrotliTransformKind.FermentAll || bodyMatch >= kept - 4);
    }

    private bool TryHit(int e, int transform, ReadOnlySpan<byte> buffer, int position, int maxLength, Span<byte> transformed, out Hit hit)
    {
        int len = _entries[e] >> 16;
        int index = _entries[e] & 0xFFFF;
        ReadOnlySpan<byte> word = BrotliWords.GetWord(len, index);
        int n = BrotliTransforms.Apply(transform, word, transformed);
        if (n > maxLength || position + n > buffer.Length || !buffer.Slice(position, n).SequenceEqual(transformed.Slice(0, n)))
        {
            hit = default;
            return false;
        }

        hit = new Hit(len, index | (transform << BrotliWords.GetWordBits(len)), n);
        return true;
    }

    private int Probe(ReadOnlySpan<byte> buffer, int position, int maxLength, int transform, int wordStart, Span<byte> transformed, Span<Hit> hits, int count, int minCovered)
    {
        if (wordStart + 4 > buffer.Length)
        {
            return count;
        }

        uint raw = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(wordStart));
        uint keyBytes = _kind[transform] switch
        {
            BrotliTransformKind.FermentFirst => LowerFirst(raw),
            BrotliTransformKind.FermentAll => LowerAll(raw),
            _ => raw,
        };
        if (!Present(keyBytes))
        {
            return count;
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

        uint keyWord = BinaryPrimitives.ReadUInt32LittleEndian(key);
        BrotliTransformKind kind = _kind[transform];
        int omittedLast = kind == BrotliTransformKind.OmitLast ? _omitted[transform] : 0;
        for (int e = _head[Hash(key)]; e >= 0 && count < hits.Length; e = _next[e])
        {
            if (_firstFour[e] == keyWord && FifthAgrees(e, kind, omittedLast, buffer, wordStart) && ((_entries[e] >> 16) - omittedLast + _affix[transform] >= minCovered) && Accepts(e, kind, omittedLast, BodyMatch(e, buffer, wordStart), _affix[transform], minCovered) && TryHit(e, transform, buffer, position, maxLength, transformed, out Hit hit))
            {
                hits[count++] = hit;
            }
        }

        return count;
    }

    private static int Hash7(ulong sevenBytes) => (int)(((sevenBytes & 0x00FFFFFFFFFFFFFFUL) * 0x9E3779B185EBCA87UL) >> (64 - HashBits));

    /// <summary>
    /// False if no dictionary word of at least seven bytes begins with the seven bytes at <paramref name="position"/>, which rules out every
    /// match of the identity transformation that covers seven bytes or more. True when unsure. The caller guarantees <c>position + 8 &lt;= buffer.Length</c>.
    /// </summary>
    internal bool MayHaveLongWord(ReadOnlySpan<byte> buffer, int position)
    {
        int h = Hash7(BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(position)));
        return (_present7[h >> 6] & (1UL << (h & 63))) != 0;
    }

    private bool Present(uint four) => (_present[(int)((four * 0x1E35A7BDu) >> (32 - HashBits)) >> 6] & (1UL << (int)(((four * 0x1E35A7BDu) >> (32 - HashBits)) & 63))) != 0;

    // The four bytes of a lower-cased key, as Probe forms them: the first byte only, or all four.
    private static uint LowerFirst(uint four) => (four & 0xFFFFFF00u) | Lower((byte)four);

    private static uint LowerAll(uint four) => Lower((byte)four) | ((uint)Lower((byte)(four >> 8)) << 8) | ((uint)Lower((byte)(four >> 16)) << 16) | ((uint)Lower((byte)(four >> 24)) << 24);

    private static byte Lower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
