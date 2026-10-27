using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A dictionary prepared for compression (RFC 8878 §5): its content acts as history in front of every frame, and a
/// dictionary in the Zstandard format also brings the Huffman tree and sequence tables to start from and the initial repeat
/// offsets. All of that is digested once here, so compressing with it costs nothing extra per frame.
/// </summary>
/// <remarks>
/// The search tables that match finders build over the content are made on first use for each parameter set and kept, so the
/// first frame at a new level pays for them and later frames do not. A dictionary is immutable from the caller's view and may
/// be shared by any number of encoders on any number of threads.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is the Zstandard format's own term; the type is not a collection.")]
public sealed class ZstdCompressionDictionary
{
    private readonly byte[] _content;
    private readonly object _indexLock = new();
    private ZstdDictionaryIndex[] _indexes = [];
    private ZstdChainDictionaryIndex[] _chainIndexes = [];

    private ZstdCompressionDictionary(
        uint id,
        byte[] content,
        ZstdRepeatOffsets repeatOffsets,
        HuffmanEncodingTable? huffman,
        FseEncodingTable? literalLengths,
        FseEncodingTable? offsets,
        FseEncodingTable? matchLengths)
    {
        Id = id;
        _content = content;
        RepeatOffsets = repeatOffsets;
        HuffmanTable = huffman;
        LiteralLengthTable = literalLengths;
        OffsetTable = offsets;
        MatchLengthTable = matchLengths;
    }

    /// <summary>Gets the dictionary ID written into frames (0 for none).</summary>
    public uint Id { get; }

    /// <summary>Gets the dictionary content: the bytes matches may copy from.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Gets a value indicating whether the dictionary carries entropy tables (it is in the Zstandard dictionary format).</summary>
    public bool HasEntropyTables => HuffmanTable is not null;

    internal ZstdRepeatOffsets RepeatOffsets { get; }

    internal HuffmanEncodingTable? HuffmanTable { get; }

    internal FseEncodingTable? LiteralLengthTable { get; }

    internal FseEncodingTable? OffsetTable { get; }

    internal FseEncodingTable? MatchLengthTable { get; }

    internal ReadOnlySpan<byte> ContentSpan => _content;

    /// <summary>
    /// Creates a dictionary from the bytes of a dictionary file. Data that begins with the Zstandard dictionary magic number is
    /// parsed as the Zstandard dictionary format; anything else is a raw-content dictionary with ID 0.
    /// </summary>
    /// <param name="data">The dictionary bytes; they are copied.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="CompressionFormatException">The data is in the dictionary format but invalid.</exception>
    /// <exception cref="ArgumentException">The data is raw content shorter than 8 bytes.</exception>
    public static ZstdCompressionDictionary Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(data) != ZstdFormat.DictionaryMagic)
        {
            return FromContent(data);
        }

        // The decoder's parser validates every field; this pass reads the same fields again for the encoder's tables.
        _ = ZstdDictionary.Parse(data);
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int p = 8;
        var huffmanDecoder = new HuffmanDecodingTable();
        p += huffmanDecoder.ReadDescription(data[p..], p);
        var huffman = new HuffmanEncodingTable();
        huffman.BuildFromWeights(huffmanDecoder.Weights);

        FseEncodingTable offsets = ReadTable(data, ref p, ZstdFormat.OffsetMaxSymbol, ZstdFormat.OffsetMaxAccuracyLog);
        FseEncodingTable matchLengths = ReadTable(data, ref p, ZstdFormat.MatchLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog);
        FseEncodingTable literalLengths = ReadTable(data, ref p, ZstdFormat.LiteralLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog);
        var reps = new ZstdRepeatOffsets
        {
            Offset1 = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]),
            Offset2 = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 4)..]),
            Offset3 = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + 8)..]),
        };
        p += 12;
        return new ZstdCompressionDictionary(id, data[p..].ToArray(), reps, huffman, literalLengths, offsets, matchLengths);
    }

    /// <summary>Creates a raw-content dictionary: bytes that act as history, with no entropy tables.</summary>
    /// <param name="content">The content, at least 8 bytes; it is copied.</param>
    /// <param name="id">The ID written into frames, or 0 to write none.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> is shorter than 8 bytes.</exception>
    public static ZstdCompressionDictionary FromContent(ReadOnlySpan<byte> content, uint id = 0)
    {
        if (content.Length < 8)
        {
            throw new ArgumentException("A raw-content dictionary must be at least 8 bytes.", nameof(content));
        }

        return new ZstdCompressionDictionary(id, content.ToArray(), ZstdRepeatOffsets.Initial, null, null, null, null);
    }

    private static FseEncodingTable ReadTable(ReadOnlySpan<byte> data, ref int p, int maxSymbol, int maxAccuracyLog)
    {
        Span<short> counts = stackalloc short[256];
        p += FseDecodingTable.ReadCounts(data[p..], maxSymbol, maxAccuracyLog, counts, out int accuracyLog, out int symbolCount, p);
        var table = new FseEncodingTable();
        table.Build(counts[..symbolCount], accuracyLog);
        return table;
    }

    /// <summary>Gets (building it on first use) the search tables over the content for the fast strategies.</summary>
    internal ZstdDictionaryIndex GetIndex(int minMatch, int hashLog, int chainLog, bool doubleHash)
    {
        foreach (ZstdDictionaryIndex index in Volatile.Read(ref _indexes))
        {
            if (index.Matches(minMatch, hashLog, chainLog, doubleHash))
            {
                return index;
            }
        }

        lock (_indexLock)
        {
            foreach (ZstdDictionaryIndex index in _indexes)
            {
                if (index.Matches(minMatch, hashLog, chainLog, doubleHash))
                {
                    return index;
                }
            }

            var built = ZstdDictionaryIndex.Build(_content, minMatch, hashLog, chainLog, doubleHash);
            var list = new ZstdDictionaryIndex[_indexes.Length + 1];
            _indexes.CopyTo(list, 0);
            list[^1] = built;
            Volatile.Write(ref _indexes, list);
            return built;
        }
    }

    /// <summary>Gets (building it on first use) the hash-chain search tables over the content for the chain and tree strategies.</summary>
    internal ZstdChainDictionaryIndex GetChainIndex(int minMatch, int hashLog, int chainLog)
    {
        foreach (ZstdChainDictionaryIndex index in Volatile.Read(ref _chainIndexes))
        {
            if (index.Matches(minMatch, hashLog, chainLog))
            {
                return index;
            }
        }

        lock (_indexLock)
        {
            foreach (ZstdChainDictionaryIndex index in _chainIndexes)
            {
                if (index.Matches(minMatch, hashLog, chainLog))
                {
                    return index;
                }
            }

            var built = ZstdChainDictionaryIndex.Build(_content, minMatch, hashLog, chainLog);
            var list = new ZstdChainDictionaryIndex[_chainIndexes.Length + 1];
            _chainIndexes.CopyTo(list, 0);
            list[^1] = built;
            Volatile.Write(ref _chainIndexes, list);
            return built;
        }
    }
}

/// <summary>Hash chains over a dictionary's content, made for one parameter set and then only read.</summary>
internal sealed class ZstdChainDictionaryIndex
{
    private readonly int _minMatch;
    private readonly int _hashLog;
    private readonly int _chainLog;

    private ZstdChainDictionaryIndex(int minMatch, int hashLog, int chainLog, uint[] head, uint[] chain)
    {
        _minMatch = minMatch;
        _hashLog = hashLog;
        _chainLog = chainLog;
        Head = head;
        Chain = chain;
    }

    /// <summary>Gets the most recent content position (0 for none) of each hash value.</summary>
    public uint[] Head { get; }

    /// <summary>Gets, for the position <c>p</c>, at index <c>p &amp; mask</c>, the previous position with the same hash.</summary>
    public uint[] Chain { get; }

    public bool Matches(int minMatch, int hashLog, int chainLog) => _minMatch == minMatch && _hashLog == hashLog && _chainLog == chainLog;

    public static ZstdChainDictionaryIndex Build(byte[] content, int minMatch, int hashLog, int chainLog)
    {
        // Every position with eight bytes after it is indexed once, so a frame only reads the chains.
        ReadOnlySpan<byte> data = content;
        var head = new uint[1 << hashLog];
        var chain = new uint[1 << chainLog];
        int mask = (1 << chainLog) - 1;
        for (int i = 1; i + 8 <= data.Length; i++)
        {
            int h = ZstdFastMatchFinder.Hash(data, i, minMatch, hashLog);
            chain[i & mask] = head[h];
            head[h] = (uint)i;
        }

        return new ZstdChainDictionaryIndex(minMatch, hashLog, chainLog, head, chain);
    }
}

/// <summary>Hash tables over a dictionary's content, made for one parameter set and then only read.</summary>
internal sealed class ZstdDictionaryIndex
{
    private readonly int _minMatch;
    private readonly int _hashLog;
    private readonly int _chainLog;
    private readonly bool _doubleHash;

    private ZstdDictionaryIndex(int minMatch, int hashLog, int chainLog, bool doubleHash, uint[] primary, uint[]? secondary)
    {
        _minMatch = minMatch;
        _hashLog = hashLog;
        _chainLog = chainLog;
        _doubleHash = doubleHash;
        Primary = primary;
        Secondary = secondary;
    }

    /// <summary>The table of the fast strategy, or the long-hash table of the double-hash strategy; entries are content positions, 0 for empty.</summary>
    public uint[] Primary { get; }

    /// <summary>The short-hash table of the double-hash strategy.</summary>
    public uint[]? Secondary { get; }

    public bool Matches(int minMatch, int hashLog, int chainLog, bool doubleHash) =>
        _minMatch == minMatch && _hashLog == hashLog && _doubleHash == doubleHash && (!doubleHash || _chainLog == chainLog);

    public static ZstdDictionaryIndex Build(byte[] content, int minMatch, int hashLog, int chainLog, bool doubleHash)
    {
        // Every position gets an entry (later positions replace earlier ones that hash alike): the dictionary is digested once,
        // so the cost is paid once, and the more positions known the more of the dictionary a frame can use.
        ReadOnlySpan<byte> data = content;
        if (!doubleHash)
        {
            var table = new uint[1 << hashLog];
            for (int i = 1; i + 8 <= data.Length; i++)
            {
                table[ZstdFastMatchFinder.Hash(data, i, minMatch, hashLog)] = (uint)i;
            }

            return new ZstdDictionaryIndex(minMatch, hashLog, chainLog, false, table, null);
        }

        var longTable = new uint[1 << hashLog];
        var shortTable = new uint[1 << chainLog];
        for (int i = 1; i + 8 <= data.Length; i++)
        {
            longTable[ZstdFastMatchFinder.Hash(data, i, 8, hashLog)] = (uint)i;
            shortTable[ZstdFastMatchFinder.Hash(data, i, minMatch, chainLog)] = (uint)i;
        }

        return new ZstdDictionaryIndex(minMatch, hashLog, chainLog, true, longTable, shortTable);
    }
}
