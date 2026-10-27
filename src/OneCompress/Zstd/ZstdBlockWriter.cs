using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Turns a block's literals and sequences into a Zstandard block (RFC 8878 §3.1.1.2): it picks raw, RLE or compressed by size,
/// and inside a compressed block picks each entropy mode (predefined, RLE, FSE-compressed, or repeat of the previous table) by
/// integer cost. It carries what later blocks of the frame may reuse: the repeat offsets, the Huffman tree and the three
/// sequence tables.
/// </summary>
/// <remarks>
/// Call <see cref="BeginFrame"/> before the first block of every frame. A block that is stored raw or as RLE leaves the carried
/// state untouched, exactly as the decoder sees it. The writer is not thread-safe; it reuses its buffers, so a steady
/// state allocates nothing.
/// </remarks>
public sealed class ZstdBlockWriter
{
    // Symbol-code tables for literal lengths below 64 and match lengths (minus 3) below 128.
    private static readonly byte[] s_literalLengthCodes = BuildCodeTable(ZstdFormat.LiteralLengthBase, 64, 0);
    private static readonly byte[] s_matchLengthCodes = BuildCodeTable(ZstdFormat.MatchLengthBase, 128, 3);

    // Per table kind (literal lengths, offsets, match lengths): the format's largest accuracy log, and the largest symbol the predefined table has.
    private static readonly int[] s_maxAccuracyLogs = [ZstdFormat.SequenceMaxAccuracyLog, ZstdFormat.OffsetMaxAccuracyLog, ZstdFormat.SequenceMaxAccuracyLog];
    private static readonly int[] s_defaultMaxSymbols = [ZstdFormat.LiteralLengthMaxSymbol, 28, ZstdFormat.MatchLengthMaxSymbol];

    private static FseEncodingTable? s_defaultLiteralLengths;
    private static FseEncodingTable? s_defaultMatchLengths;
    private static FseEncodingTable? s_defaultOffsets;

    /// <summary>
    /// The spare bytes <see cref="WriteBlock"/> needs in the destination beyond the block and its header: bits are stored a
    /// word at a time and table descriptions are written before the size of the result is known.
    /// </summary>
    public const int DestinationSlack = 320;

    private const int MinimumSequencesForNewTable = 64;
    private const int MinimumLiteralsForNewTree = 64;

    // Blocks with at most this many sequences price each table choice by running the encoder's state machine; larger blocks
    // use the histogram estimate, which is within a fraction of a percent there and costs less.
    private const int ExactCostLimit = 1500;

    private readonly FseEncodingTable[,] _own = new FseEncodingTable[3, 2];
    private readonly FseEncodingTable?[] _previous = new FseEncodingTable?[3];
    private readonly FseEncodingTable?[] _chosen = new FseEncodingTable?[3];
    private readonly HuffmanEncodingTable[] _ownHuffman = [new HuffmanEncodingTable(), new HuffmanEncodingTable()];
    private HuffmanEncodingTable? _previousHuffman;

    private byte[] _literalScratch = [];
    private byte[] _llCodes = new byte[256];
    private byte[] _mlCodes = new byte[256];
    private byte[] _ofCodes = new byte[256];
    private SequenceCode[] _seqCodes = new SequenceCode[256];
    private uint[] _resolvedOffsets = new uint[256];

    // Everything the bitstream loop needs of one sequence, in one record: its three codes and its extra bits (literal-length and
    // match-length extras packed side by side, then the offset's), so the loop reads one array.
    private struct SequenceCode
    {
        public byte Ll;
        public byte Ml;
        public byte Of;
        public byte LlMlBits;
        public uint LlMlExtra;
        public uint OfExtra;
    }
    private readonly uint[] _histogram4 = new uint[4 * 256];
    private readonly uint[] _literalHistogram = new uint[256];
    private readonly uint[][] _codeHistogram = [new uint[64], new uint[64], new uint[64]];
    private readonly short[] _normalized = new short[64];
    private readonly byte[] _description = new byte[160];
    private ZstdRepeatOffsets _repeatOffsets = ZstdRepeatOffsets.Initial;
    private bool _compressLiterals = true;

    /// <summary>Creates a block writer.</summary>
    public ZstdBlockWriter()
    {
        for (int k = 0; k < 3; k++)
        {
            _own[k, 0] = new FseEncodingTable();
            _own[k, 1] = new FseEncodingTable();
        }
    }

    /// <summary>Gets the repeat offsets as the decoder will have them after the blocks written so far.</summary>
    public ZstdRepeatOffsets RepeatOffsets => _repeatOffsets;

    /// <summary>
    /// Starts a frame: the repeat offsets and the reusable tables become those of the dictionary, or the format's initial
    /// values when there is none.
    /// </summary>
    /// <param name="dictionary">The dictionary the frame is compressed with, or <see langword="null"/>.</param>
    /// <param name="compressLiterals">False to store literals raw, which the fastest levels do.</param>
    public void BeginFrame(ZstdCompressionDictionary? dictionary, bool compressLiterals = true)
    {
        _compressLiterals = compressLiterals;
        _repeatOffsets = dictionary?.RepeatOffsets ?? ZstdRepeatOffsets.Initial;
        _previous[0] = dictionary?.LiteralLengthTable;
        _previous[1] = dictionary?.OffsetTable;
        _previous[2] = dictionary?.MatchLengthTable;
        _previousHuffman = dictionary?.HuffmanTable;
    }

    /// <summary>
    /// Declares the decoder's repeat offsets unknown, as they are at the start of a parallel job that follows another job in the
    /// same frame: until the job has coded three explicit offsets of its own, no repeat code is used, because the decoder's values
    /// come from the end of the previous job. The three stand-ins are above any offset a frame can use, so they match nothing.
    /// </summary>
    internal void ForgetRepeatOffsets() =>
        _repeatOffsets = new ZstdRepeatOffsets { Offset1 = 0xFFFF_FFF0, Offset2 = 0xFFFF_FFF1, Offset3 = 0xFFFF_FFF2 };

    /// <summary>
    /// Writes one block, header included.
    /// </summary>
    /// <param name="blockData">The block's original bytes (used for raw and RLE blocks); 0 to 128 KiB.</param>
    /// <param name="store">The literals and sequences that reproduce <paramref name="blockData"/> given the history.</param>
    /// <param name="isLastBlock">Whether this is the last block of the frame.</param>
    /// <param name="destination">Receives the block; it needs <c>blockData.Length + 3 + <see cref="DestinationSlack"/></c> bytes.</param>
    /// <returns>The number of bytes written.</returns>
    public int WriteBlock(ReadOnlySpan<byte> blockData, ZstdSequenceStore store, bool isLastBlock, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(store);
        int n = blockData.Length;
        if (n > ZstdFormat.BlockSizeMax)
        {
            throw new ArgumentException("A block holds at most 128 KiB.", nameof(blockData));
        }

        if (destination.Length < n + 3 + DestinationSlack)
        {
            throw new ArgumentException("The destination must hold the block, its header and the slack.", nameof(destination));
        }

        uint last = isLastBlock ? 1u : 0u;
        if (n > 1 && store.SequenceCount <= 1 && blockData.IndexOfAnyExcept(blockData[0]) < 0)
        {
            WriteHeader(destination, last, 1, n);
            destination[3] = blockData[0];
            return 4;
        }

        int size = n >= 8 ? TryCompress(blockData, store, destination[3..]) : -1;
        if (size > 0 && size < n)
        {
            WriteHeader(destination, last, 2, size);
            return 3 + size;
        }

        // Compressing did not pay: store the bytes. Nothing the failed attempt computed is kept.
        WriteHeader(destination, last, 0, n);
        blockData.CopyTo(destination[3..]);
        return 3 + n;
    }

    /// <summary>Writes a raw block header and the bytes of a block that is stored uncompressed.</summary>
    /// <returns>The number of bytes written.</returns>
    public static int WriteRawBlock(ReadOnlySpan<byte> blockData, bool isLastBlock, Span<byte> destination)
    {
        WriteHeader(destination, isLastBlock ? 1u : 0u, 0, blockData.Length);
        blockData.CopyTo(destination[3..]);
        return 3 + blockData.Length;
    }

    /// <summary>Writes an RLE block: the header and the one byte that repeats <paramref name="length"/> times.</summary>
    /// <returns>4.</returns>
    public static int WriteRleBlock(byte value, int length, bool isLastBlock, Span<byte> destination)
    {
        WriteHeader(destination, isLastBlock ? 1u : 0u, 1, length);
        destination[3] = value;
        return 4;
    }

    private static void WriteHeader(Span<byte> destination, uint last, uint type, int size)
    {
        uint header = last | (type << 1) | ((uint)size << 3);
        destination[0] = (byte)header;
        destination[1] = (byte)(header >> 8);
        destination[2] = (byte)(header >> 16);
    }

    private static byte[] BuildCodeTable(uint[] bases, int length, int minimum)
    {
        var table = new byte[length];
        for (int v = 0; v < length; v++)
        {
            int code = 0;
            while (code + 1 < bases.Length && bases[code + 1] <= v + minimum)
            {
                code++;
            }

            table[v] = (byte)code;
        }

        return table;
    }

    private static FseEncodingTable Default(int kind)
    {
        switch (kind)
        {
            case 0:
                return s_defaultLiteralLengths ??= BuildDefault(ZstdFormat.LiteralLengthDefaultDistribution, 6);
            case 1:
                return s_defaultOffsets ??= BuildDefault(ZstdFormat.OffsetDefaultDistribution, 5);
            default:
                return s_defaultMatchLengths ??= BuildDefault(ZstdFormat.MatchLengthDefaultDistribution, 6);
        }
    }

    private static FseEncodingTable BuildDefault(short[] distribution, int accuracyLog)
    {
        var table = new FseEncodingTable();
        table.Build(distribution, accuracyLog);
        return table;
    }

    /// <summary>The code of a literal length (RFC 8878 §3.1.1.3.2.1.1).</summary>
    internal static int LiteralLengthCode(uint literalLength) =>
        literalLength < 64 ? s_literalLengthCodes[literalLength] : BitOperations.Log2(literalLength) + 19;

    /// <summary>The code of a match length (RFC 8878 §3.1.1.3.2.1.1).</summary>
    internal static int MatchLengthCode(uint matchLength)
    {
        uint m = matchLength - 3;
        return m < 128 ? s_matchLengthCodes[m] : BitOperations.Log2(m) + 36;
    }

    // Compresses the block body (literals section and sequences section); returns its size, or -1 if it does not fit in the
    // block's size (the caller then stores the block raw). Carried state changes only on success.
    private int TryCompress(ReadOnlySpan<byte> blockData, ZstdSequenceStore store, Span<byte> destination)
    {
        int limit = blockData.Length;
        ReadOnlySpan<byte> literals = store.Literals;
        int p = WriteLiterals(literals, destination, limit, out HuffmanEncodingTable? newHuffman);
        if (p < 0 || p >= limit - 8)
        {
            return -1;
        }

        int nseq = store.SequenceCount;
        if (nseq == 0)
        {
            destination[p++] = 0;
            if (p >= limit)
            {
                return -1;
            }

            if (newHuffman is not null)
            {
                _previousHuffman = newHuffman;
            }

            return p;
        }

        int q = WriteSequences(store, destination, p, limit, out ZstdRepeatOffsets reps);
        if (q < 0)
        {
            return -1;
        }

        if (newHuffman is not null)
        {
            _previousHuffman = newHuffman;
        }

        _repeatOffsets = reps;
        return q;
    }

    // ---- Literals section (RFC 8878 §3.1.1.3.1) ----

    private int WriteLiterals(ReadOnlySpan<byte> literals, Span<byte> destination, int limit, out HuffmanEncodingTable? adopted)
    {
        adopted = null;
        int n = literals.Length;
        if (n == 0)
        {
            destination[0] = 0;
            return 1;
        }

        int rawHeader = n < 32 ? 1 : n < 4096 ? 2 : 3;
        if (n < 8 || !_compressLiterals)
        {
            return WriteRawLiterals(literals, destination, rawHeader);
        }

        uint[] histogram = _literalHistogram;
        int maxSymbol = Histogram(literals, histogram);
        int distinct = 0;
        for (int s = 0; s <= maxSymbol; s++)
        {
            if (histogram[s] != 0)
            {
                distinct++;
            }
        }

        if (distinct == 1)
        {
            // RLE literals: the header, then the one byte.
            int h = WriteSizeHeader(destination, 1, n);
            destination[h] = literals[0];
            return h + 1;
        }

        int rawCost = rawHeader + n;
        int bestCost = rawCost;
        int choice = 0; // 0 raw, 1 new tree, 2 previous tree
        HuffmanEncodingTable candidate = _previousHuffman == _ownHuffman[0] ? _ownHuffman[1] : _ownHuffman[0];
        int descriptionSize = 0;
        bool singleStream = n < 256;

        // The previous tree's cost is cheap to know. A new tree costs about the entropy of the literals plus its description
        // (roughly 8 bytes and 3 bits per distinct symbol); when the best alternative at hand (the previous tree, else raw) is no
        // dearer than that, building a tree cannot pay. Small blocks with a dictionary, which is most of what the fast levels
        // see, skip the tree build this way.
        int previousCost = int.MaxValue;
        if (_previousHuffman is not null && _previousHuffman.TryEncodedBits(histogram, maxSymbol, out long previousBits))
        {
            int streamBytes = StreamBytes(previousBits, singleStream, n);
            previousCost = CompressedHeaderSize(n, streamBytes, singleStream) + streamBytes;
        }

        bool tryNew = n >= MinimumLiteralsForNewTree;
        if (tryNew)
        {
            int estimate = CompressedHeaderSize(n, 0, singleStream) + 8 + ((distinct * 3) / 8)
                + StreamBytes(ZstdCost.EntropyLowerBound(histogram, maxSymbol, n) / ZstdCost.Bit, singleStream, n);
            tryNew = estimate < Math.Min(rawCost, previousCost);
        }

        bool built = tryNew && candidate.Build(histogram, maxSymbol);
        if (built)
        {
            descriptionSize = candidate.WriteDescription(_description);
            if (descriptionSize > 0)
            {
                long bits = candidate.EncodedBits(histogram, maxSymbol);
                int streamBytes = StreamBytes(bits, singleStream, n);
                int cost = CompressedHeaderSize(n, streamBytes + descriptionSize, singleStream) + descriptionSize + streamBytes;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    choice = 1;
                }
            }
        }

        if (previousCost < bestCost)
        {
            bestCost = previousCost;
            choice = 2;
        }

        if (choice == 0)
        {
            return WriteRawLiterals(literals, destination, rawHeader);
        }

        HuffmanEncodingTable table = choice == 1 ? candidate : _previousHuffman!;
        if (_literalScratch.Length < n + 64)
        {
            _literalScratch = new byte[ZstdFormat.BlockSizeMax + 64];
        }

        int streamSize = singleStream ? table.Compress1(literals, _literalScratch) : table.Compress4(literals, _literalScratch);
        int treeSize = choice == 1 ? descriptionSize : 0;
        if (streamSize < 0 || treeSize + streamSize >= n)
        {
            return WriteRawLiterals(literals, destination, rawHeader);
        }

        int header = WriteCompressedHeader(destination, choice == 1 ? 2u : 3u, n, treeSize + streamSize, singleStream);
        if (choice == 1)
        {
            _description.AsSpan(0, treeSize).CopyTo(destination[header..]);
            adopted = candidate;
        }

        _literalScratch.AsSpan(0, streamSize).CopyTo(destination[(header + treeSize)..]);
        return header + treeSize + streamSize;
    }

    private static int StreamBytes(long bits, bool singleStream, int n)
    {
        // One stream ends with a marker bit; four streams add the 6-byte size table and up to a byte of padding each.
        if (singleStream)
        {
            return (int)((bits + 8) >> 3);
        }

        return 6 + (int)((bits + 7) >> 3) + 4;
    }

    private static int CompressedHeaderSize(int regenerated, int compressed, bool singleStream)
    {
        if (singleStream || (regenerated < 1024 && compressed < 1024))
        {
            return 3;
        }

        return regenerated < 16384 && compressed < 16384 ? 4 : 5;
    }

    private static int WriteRawLiterals(ReadOnlySpan<byte> literals, Span<byte> destination, int header)
    {
        int h = WriteSizeHeader(destination, 0, literals.Length);
        literals.CopyTo(destination[h..]);
        return h + literals.Length;
    }

    // RFC 8878 §3.1.1.3.1.1: raw and RLE literals sections use 1, 2 or 3 header bytes depending on the size.
    private static int WriteSizeHeader(Span<byte> destination, uint type, int size)
    {
        if (size < 32)
        {
            destination[0] = (byte)(type | ((uint)size << 3));
            return 1;
        }

        if (size < 4096)
        {
            uint v = type | (1u << 2) | ((uint)size << 4);
            destination[0] = (byte)v;
            destination[1] = (byte)(v >> 8);
            return 2;
        }

        uint w = type | (3u << 2) | ((uint)size << 4);
        destination[0] = (byte)w;
        destination[1] = (byte)(w >> 8);
        destination[2] = (byte)(w >> 16);
        return 3;
    }

    // RFC 8878 §3.1.1.3.1.1: compressed literals headers carry both sizes; the size format also says one or four streams.
    private static int WriteCompressedHeader(Span<byte> destination, uint type, int regenerated, int compressed, bool singleStream)
    {
        ulong v;
        int bytes;
        if (singleStream)
        {
            v = type | (0u << 2) | ((ulong)regenerated << 4) | ((ulong)compressed << 14);
            bytes = 3;
        }
        else if (regenerated < 1024 && compressed < 1024)
        {
            v = type | (1u << 2) | ((ulong)regenerated << 4) | ((ulong)compressed << 14);
            bytes = 3;
        }
        else if (regenerated < 16384 && compressed < 16384)
        {
            v = type | (2u << 2) | ((ulong)regenerated << 4) | ((ulong)compressed << 18);
            bytes = 4;
        }
        else
        {
            v = type | (3u << 2) | ((ulong)regenerated << 4) | ((ulong)compressed << 22);
            bytes = 5;
        }

        for (int i = 0; i < bytes; i++)
        {
            destination[i] = (byte)(v >> (8 * i));
        }

        return bytes;
    }

    private int Histogram(ReadOnlySpan<byte> data, uint[] histogram)
    {
        if (data.Length < 1024)
        {
            // Small blocks: one table, no merge.
            Array.Clear(histogram);
            foreach (byte value in data)
            {
                histogram[value]++;
            }

            int top = 0;
            for (int s = 255; s > 0; s--)
            {
                if (histogram[s] != 0)
                {
                    top = s;
                    break;
                }
            }

            return top;
        }

        uint[] h4 = _histogram4;
        Array.Clear(h4);
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            h4[data[i]]++;
            h4[256 + data[i + 1]]++;
            h4[512 + data[i + 2]]++;
            h4[768 + data[i + 3]]++;
        }

        for (; i < data.Length; i++)
        {
            h4[data[i]]++;
        }

        int max = 0;
        for (int s = 0; s < 256; s++)
        {
            uint c = h4[s] + h4[256 + s] + h4[512 + s] + h4[768 + s];
            histogram[s] = c;
            if (c != 0)
            {
                max = s;
            }
        }

        return max;
    }

    // ---- Sequences section (RFC 8878 §3.1.1.3.2) ----

    private enum Mode
    {
        Predefined = 0,
        Rle = 1,
        Compressed = 2,
        Repeat = 3,
    }

    // Turns the sequences into codes, extra bits and code histograms, resolving repeat offsets as it goes. A method of its own so
    // that its few live values stay in registers.
    private static void ComputeCodes(
        ReadOnlySpan<ZstdSequence> sequences,
        SequenceCode[] seqCodes,
        uint[] llHistogram,
        uint[] ofHistogram,
        uint[] mlHistogram,
        ReadOnlySpan<uint> offsetValues,
        out int maxLl,
        out int maxOf,
        out int maxMl)
    {
        int nseq = sequences.Length;
        maxLl = 0;
        maxOf = 0;
        maxMl = 0;
        ReadOnlySpan<byte> llLookup = s_literalLengthCodes;
        ReadOnlySpan<byte> mlLookup = s_matchLengthCodes;
        for (int i = 0; i < nseq; i++)
        {
            // i < nseq <= every code array's length (EnsureCodeCapacity); codes are at most 52, inside the histograms' 53+ entries.
            ZstdSequence s = Raw.Get(sequences, i);
            uint ofValue = Raw.Get(offsetValues, i);
            uint lls = s.LiteralLength;
            int ll = lls < 64 ? Raw.Get(llLookup, (int)lls) : BitOperations.Log2(lls) + 19;
            uint m = s.MatchLength - 3;
            int ml = m < 128 ? Raw.Get(mlLookup, (int)m) : BitOperations.Log2(m) + 36;
            int of = BitOperations.Log2(ofValue);
            int llBitCount = Raw.Get(ZstdFormat.LiteralLengthBits, ll);
            int mlBitCount = Raw.Get(ZstdFormat.MatchLengthBits, ml);
            ref SequenceCode record = ref Raw.RefRw(seqCodes, i);
            record.Ll = (byte)ll;
            record.Ml = (byte)ml;
            record.Of = (byte)of;
            record.LlMlBits = (byte)(llBitCount + mlBitCount);
            record.LlMlExtra = (lls - Raw.Get(ZstdFormat.LiteralLengthBase, ll)) | ((s.MatchLength - Raw.Get(ZstdFormat.MatchLengthBase, ml)) << llBitCount);
            record.OfExtra = ofValue - (1u << of);
            Raw.RefRw(llHistogram, ll)++;
            Raw.RefRw(mlHistogram, ml)++;
            Raw.RefRw(ofHistogram, of)++;
            maxLl = Math.Max(maxLl, ll);
            maxMl = Math.Max(maxMl, ml);
            maxOf = Math.Max(maxOf, of);
        }

    }

    // Sequences with no literals are rare; they use the shifted repeat-offset meaning (RFC 8878 §3.1.2.5), kept in one place.
    private static uint ApplyZeroLiteralRepeat(uint offset, ref uint rep1, ref uint rep2, ref uint rep3)
    {
        var r = new ZstdRepeatOffsets { Offset1 = rep1, Offset2 = rep2, Offset3 = rep3 };
        uint value = r.Apply(offset, 0);
        rep1 = r.Offset1;
        rep2 = r.Offset2;
        rep3 = r.Offset3;
        return value;
    }

    private int WriteSequences(ZstdSequenceStore store, Span<byte> destination, int p, int limit, out ZstdRepeatOffsets reps)
    {
        ReadOnlySpan<ZstdSequence> sequences = store.Sequences;
        int nseq = sequences.Length;
        EnsureCodeCapacity(nseq);
        byte[] llCodes = _llCodes;
        byte[] mlCodes = _mlCodes;
        byte[] ofCodes = _ofCodes;
        SequenceCode[] seqCodes = _seqCodes;
        uint[] llHistogram = _codeHistogram[0];
        uint[] ofHistogram = _codeHistogram[1];
        uint[] mlHistogram = _codeHistogram[2];
        Array.Clear(llHistogram);
        Array.Clear(ofHistogram);
        Array.Clear(mlHistogram);
        ReadOnlySpan<uint> offsetValues;
        if (store.TryGetTracked(_repeatOffsets, out offsetValues, out ZstdRepeatOffsets trackedEnd))
        {
            // The match finder resolved the repeat offsets as it went.
            reps = trackedEnd;
        }
        else
        {
            reps = _repeatOffsets;
            uint[] resolved = _resolvedOffsets.Length >= nseq ? _resolvedOffsets : (_resolvedOffsets = new uint[Math.Max(nseq, _resolvedOffsets.Length * 2)]);
            for (int i = 0; i < nseq; i++)
            {
                resolved[i] = reps.Apply(sequences[i].Offset, sequences[i].LiteralLength);
            }

            offsetValues = resolved;
        }

        ComputeCodes(sequences, seqCodes, llHistogram, ofHistogram, mlHistogram, offsetValues, out int maxLl, out int maxOf, out int maxMl);
        if (nseq <= ExactCostLimit)
        {
            // Small blocks price their table choices exactly, which walks the code sequences.
            for (int i = 0; i < nseq; i++)
            {
                llCodes[i] = seqCodes[i].Ll;
                mlCodes[i] = seqCodes[i].Ml;
                ofCodes[i] = seqCodes[i].Of;
            }
        }

        // The number of sequences (RFC 8878 §3.1.1.3.2.1).
        if (nseq < 128)
        {
            destination[p++] = (byte)nseq;
        }
        else if (nseq < 0x7F00)
        {
            destination[p++] = (byte)((nseq >> 8) + 128);
            destination[p++] = (byte)nseq;
        }
        else
        {
            destination[p++] = 255;
            BinaryPrimitives.WriteUInt16LittleEndian(destination[p..], (ushort)(nseq - 0x7F00));
            p += 2;
        }

        int modePosition = p++;
        FseEncodingTable?[] chosen = _chosen;
        Span<Mode> modes = stackalloc Mode[3];
        Span<int> rleSymbols = stackalloc int[3];
        Span<int> maxSymbols = stackalloc int[3] { maxLl, maxOf, maxMl };
        int[] maxLogs = s_maxAccuracyLogs;
        int[] defaultMaxSymbol = s_defaultMaxSymbols;

        // Tables are described in the order literal lengths, offsets, match lengths.
        for (int k = 0; k < 3; k++)
        {
            uint[] histogram = _codeHistogram[k];
            int maxSymbol = maxSymbols[k];
            int distinct = 0;
            for (int s = 0; s <= maxSymbol; s++)
            {
                if (histogram[s] != 0)
                {
                    distinct++;
                }
            }

            if (distinct == 1)
            {
                // An RLE table costs one byte and nothing per symbol.
                modes[k] = Mode.Rle;
                rleSymbols[k] = maxSymbol;
                continue;
            }

            int bestCost = int.MaxValue;
            Mode bestMode = Mode.Compressed;
            byte[] codes = k == 0 ? llCodes : k == 1 ? ofCodes : mlCodes;
            bool exact = nseq <= ExactCostLimit;
            ReadOnlySpan<byte> codeSpan = codes.AsSpan(0, nseq);
            FseEncodingTable? previous = _previous[k];
            if (previous is not null && previous.Contains(maxSymbol) && previous.Cost(histogram, maxSymbol) != int.MaxValue)
            {
                int cost = exact ? previous.ExactBits(codeSpan) * ZstdCost.Bit : previous.Cost(histogram, maxSymbol);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = Mode.Repeat;
                }
            }

            if (maxSymbol <= defaultMaxSymbol[k])
            {
                FseEncodingTable predefined = Default(k);
                int cost = exact ? predefined.ExactBits(codeSpan) * ZstdCost.Bit : predefined.Cost(histogram, maxSymbol);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestMode = Mode.Predefined;
                }
            }

            int accuracyLog = FseEncodingTable.OptimalAccuracyLog(maxLogs[k], nseq, maxSymbol);

            // A new table costs at least the entropy of the codes plus a two-byte description. When repeating the previous table or
            // the predefined one is already cheaper than that, normalizing and building a table cannot pay; and a block of fewer
            // than 64 sequences hardly ever repays a table of its own, so it chooses between the two without trying one.
            bool tryNew = bestCost == int.MaxValue
                || (nseq >= MinimumSequencesForNewTable
                    && ZstdCost.EntropyLowerBound(histogram, maxSymbol, nseq) + (16 * ZstdCost.Bit) < bestCost);
            if (tryNew && distinct <= (1 << accuracyLog))
            {
                FseEncodingTable.Normalize(_normalized, accuracyLog, histogram, (uint)nseq, maxSymbol);
                ReadOnlySpan<short> counts = _normalized.AsSpan(0, maxSymbol + 1);
                int descriptionBytes = FseEncodingTable.WriteDescription(_description, counts, accuracyLog);
                int cost;
                if (exact)
                {
                    FseEncodingTable scratch = _previous[k] == _own[k, 0] ? _own[k, 1] : _own[k, 0];
                    scratch.Build(counts, accuracyLog);
                    cost = scratch.ExactBits(codeSpan) * ZstdCost.Bit;
                }
                else
                {
                    cost = FseEncodingTable.Cost(counts, accuracyLog, histogram, maxSymbol);
                }

                if (cost != int.MaxValue)
                {
                    cost += descriptionBytes * 8 * ZstdCost.Bit;
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestMode = Mode.Compressed;
                    }
                }
            }

            modes[k] = bestMode;
        }

        destination[modePosition] = (byte)(((int)modes[0] << 6) | ((int)modes[1] << 4) | ((int)modes[2] << 2));
        for (int k = 0; k < 3; k++)
        {
            switch (modes[k])
            {
                case Mode.Rle:
                    destination[p++] = (byte)rleSymbols[k];
                    chosen[k] = null;
                    break;
                case Mode.Predefined:
                    chosen[k] = Default(k);
                    break;
                case Mode.Repeat:
                    chosen[k] = _previous[k];
                    break;
                default:
                {
                    uint[] histogram = _codeHistogram[k];
                    int maxSymbol = maxSymbols[k];
                    int accuracyLog = FseEncodingTable.OptimalAccuracyLog(maxLogs[k], nseq, maxSymbol);
                    FseEncodingTable.Normalize(_normalized, accuracyLog, histogram, (uint)nseq, maxSymbol);
                    ReadOnlySpan<short> counts = _normalized.AsSpan(0, maxSymbol + 1);
                    p += FseEncodingTable.WriteDescription(destination[p..], counts, accuracyLog);
                    if (p >= limit)
                    {
                        return -1;
                    }

                    FseEncodingTable target = _previous[k] == _own[k, 0] ? _own[k, 1] : _own[k, 0];
                    target.Build(counts, accuracyLog);
                    chosen[k] = target;
                    break;
                }
            }
        }

        // The bitstream, written backward (RFC 8878 §3.1.1.3.2.1 and §4.1.2): the last sequence's states first.
        FseEncodingTable? llTable = chosen[0];
        FseEncodingTable? ofTable = chosen[1];
        FseEncodingTable? mlTable = chosen[2];
        int total = EncodeBitstream(seqCodes, nseq, llTable, ofTable, mlTable, destination[p..], p, limit);
        if (total < 0)
        {
            return -1;
        }

        total += p;
        for (int k = 0; k < 3; k++)
        {
            if (modes[k] == Mode.Compressed)
            {
                _previous[k] = chosen[k];
            }
            else if (modes[k] != Mode.Repeat)
            {
                _previous[k] = null;
            }
        }

        return total;
    }

    // The bitstream of the sequences section, written backward (RFC 8878 §4.1 and §3.1.1.3.2.1): the last sequence's states come
    // first, then each earlier sequence as its three state transitions followed by its extra bits in the reverse of the order the
    // decoder reads them (literal length, match length, offset). Returns the byte count, or -1 if it passes the limit.
    // `start` is the size of the section so far and only serves the limit check. Each Write64 below lands inside `output`: the
    // size check at the end of every sequence keeps the position at most `limit`, and the destination has the writer's slack beyond.
    private static int EncodeBitstream(
        SequenceCode[] codes,
        int nseq,
        FseEncodingTable? llTable,
        FseEncodingTable? ofTable,
        FseEncodingTable? mlTable,
        Span<byte> output,
        int start,
        int limit)
    {
        int last = nseq - 1;
        SequenceCode first = codes[last];
        int llState = llTable?.InitialState(first.Ll) ?? 0;
        int ofState = ofTable?.InitialState(first.Of) ?? 0;
        int mlState = mlTable?.InitialState(first.Ml) ?? 0;
        ulong container = 0;
        int bitCount = 0;
        int position = 0;

        // The last sequence's extras (up to 32 + 31 bits, flushed in between).
        container = first.LlMlExtra;
        bitCount = first.LlMlBits;
        Raw.WriteUInt64(output, position, container);
        position += bitCount >> 3;
        container >>= bitCount & ~7;
        bitCount &= 7;
        container |= (ulong)first.OfExtra << bitCount;
        bitCount += first.Of;
        Raw.WriteUInt64(output, position, container);
        position += bitCount >> 3;
        container >>= bitCount & ~7;
        bitCount &= 7;

        if (llTable is not null && ofTable is not null && mlTable is not null)
        {
            // The common case: all three tables in use. Per-table arrays are read through locals.
            ushort[] llNext = llTable.StateTable;
            ushort[] ofNext = ofTable.StateTable;
            ushort[] mlNext = mlTable.StateTable;
            FseEncodingTable.SymbolTransform[] llT = llTable.Transforms;
            FseEncodingTable.SymbolTransform[] ofT = ofTable.Transforms;
            FseEncodingTable.SymbolTransform[] mlT = mlTable.Transforms;
            for (int i = last - 1; i >= 0; i--)
            {
                // Bounds: i < nseq <= codes.Length; the codes are at most 52 and the transform arrays have 256 entries; a valid
                // state shifted down by the bits it emits indexes inside the table (see FseEncodingTable.Encode).
                ref SequenceCode sc = ref Raw.RefRw(codes, i);
                FseEncodingTable.SymbolTransform t = Raw.Get(ofT, sc.Of);
                uint nb = ((uint)ofState + t.NbBits) >> 16;
                container |= (ulong)((uint)ofState & ((1u << (int)nb) - 1)) << bitCount;
                bitCount += (int)nb;
                ofState = Raw.Get(ofNext, (ofState >> (int)nb) + t.FindState);

                t = Raw.Get(mlT, sc.Ml);
                nb = ((uint)mlState + t.NbBits) >> 16;
                container |= (ulong)((uint)mlState & ((1u << (int)nb) - 1)) << bitCount;
                bitCount += (int)nb;
                mlState = Raw.Get(mlNext, (mlState >> (int)nb) + t.FindState);

                t = Raw.Get(llT, sc.Ll);
                nb = ((uint)llState + t.NbBits) >> 16;
                container |= (ulong)((uint)llState & ((1u << (int)nb) - 1)) << bitCount;
                bitCount += (int)nb;
                llState = Raw.Get(llNext, (llState >> (int)nb) + t.FindState);

                // At most 27 bits of states and 7 left over make 34; literal-length and match-length extras of up to 28 bits
                // fit beside them. Larger ones (rare) go in two halves with a flush between.
                uint extra = sc.LlMlExtra;
                int extraBits = sc.LlMlBits;
                if (extraBits > 28)
                {
                    container |= (ulong)(extra & 0xFFFF) << bitCount;
                    bitCount += 16;
                    Raw.WriteUInt64(output, position, container);
                    position += bitCount >> 3;
                    container >>= bitCount & ~7;
                    bitCount &= 7;
                    extra >>= 16;
                    extraBits -= 16;
                }

                container |= (ulong)extra << bitCount;
                bitCount += extraBits;
                Raw.WriteUInt64(output, position, container);
                position += bitCount >> 3;
                container >>= bitCount & ~7;
                bitCount &= 7;
                container |= (ulong)sc.OfExtra << bitCount;
                bitCount += sc.Of;
                Raw.WriteUInt64(output, position, container);
                position += bitCount >> 3;
                container >>= bitCount & ~7;
                bitCount &= 7;
                if (start + position > limit)
                {
                    return -1;
                }
            }
        }
        else
        {
            // Some table is a run-length table: it emits nothing.
            for (int i = last - 1; i >= 0; i--)
            {
                SequenceCode sc = codes[i];
                if (ofTable is not null)
                {
                    ofState = ofTable.Encode(ofState, sc.Of, out uint b, out int c);
                    container |= (ulong)b << bitCount;
                    bitCount += c;
                }

                if (mlTable is not null)
                {
                    mlState = mlTable.Encode(mlState, sc.Ml, out uint b, out int c);
                    container |= (ulong)b << bitCount;
                    bitCount += c;
                }

                if (llTable is not null)
                {
                    llState = llTable.Encode(llState, sc.Ll, out uint b, out int c);
                    container |= (ulong)b << bitCount;
                    bitCount += c;
                }

                // Extras as above.
                uint extra = sc.LlMlExtra;
                int extraBits = sc.LlMlBits;
                if (extraBits > 28)
                {
                    container |= (ulong)(extra & 0xFFFF) << bitCount;
                    bitCount += 16;
                    Raw.WriteUInt64(output, position, container);
                    position += bitCount >> 3;
                    container >>= bitCount & ~7;
                    bitCount &= 7;
                    extra >>= 16;
                    extraBits -= 16;
                }

                container |= (ulong)extra << bitCount;
                bitCount += extraBits;
                Raw.WriteUInt64(output, position, container);
                position += bitCount >> 3;
                container >>= bitCount & ~7;
                bitCount &= 7;
                container |= (ulong)sc.OfExtra << bitCount;
                bitCount += sc.Of;
                Raw.WriteUInt64(output, position, container);
                position += bitCount >> 3;
                container >>= bitCount & ~7;
                bitCount &= 7;
                if (start + position > limit)
                {
                    return -1;
                }
            }
        }

        var writer = new ZstdBitWriter(output);
        writer.Restore(container, bitCount, position);
        if (mlTable is not null)
        {
            writer.Add((uint)(mlState - (1 << mlTable.AccuracyLog)), mlTable.AccuracyLog);
        }

        if (ofTable is not null)
        {
            writer.Add((uint)(ofState - (1 << ofTable.AccuracyLog)), ofTable.AccuracyLog);
        }

        if (llTable is not null)
        {
            writer.Add((uint)(llState - (1 << llTable.AccuracyLog)), llTable.AccuracyLog);
        }

        writer.Flush();
        int size = writer.CloseBackward();
        return start + size >= limit ? -1 : size;
    }

    private void EnsureCodeCapacity(int count)
    {
        if (_llCodes.Length >= count)
        {
            return;
        }

        int size = Math.Max(count, _llCodes.Length * 2);
        _llCodes = new byte[size];
        _mlCodes = new byte[size];
        _ofCodes = new byte[size];
        _seqCodes = new SequenceCode[size];
    }
}
