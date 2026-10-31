using System.Buffers.Binary;
using System.Numerics;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Turns dictionary content and the samples it is meant for into a complete Zstandard-format dictionary (RFC 8878 §5): the magic
/// number, the dictionary ID, a Huffman table for literals, FSE tables for offsets, match lengths and literal lengths, the three
/// repeat offsets and the content. The tables come from compressing every sample with the content as history (greedy parse, repeat
/// offsets tried first) and counting what the parse produced, so frames that look like the samples can start from good tables.
/// </summary>
/// <remarks>
/// The result depends only on the arguments. Every byte gets a Huffman code and every literal length, match length and offset code up
/// to a bound a probability, the smallest for those the samples never produced, so that frames that differ a little from the
/// samples can still start from the dictionary's tables.
/// </remarks>
public static class ZstdDictionaryBuilder
{
    // The level whose parse stands in for the frames the dictionary will serve.
    private const int StatisticsLevel = 3;

    // Offset codes the table always covers: matches reach back at least this far in the data that follows (128 KiB blocks).
    private const int MinOffsetSymbols = 18;

    /// <summary>
    /// Builds a Zstandard-format dictionary around <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The dictionary content, at least 8 bytes. If it begins with the dictionary magic number it is taken to be a
    /// whole dictionary (for example one made by another tool): its content is kept and its tables are replaced by fresh ones.</param>
    /// <param name="samples">The samples the tables are measured on; may be empty, in which case the tables are the format's defaults for sequences and a flat Huffman code over all bytes.</param>
    /// <param name="dictionaryId">The ID to write, or 0 to derive one from the dictionary bytes (in the range from 32768 to 2^31-1 that the format leaves free).</param>
    /// <returns>The dictionary file's bytes; <see cref="ZstdDictionary.Parse"/> and <see cref="ZstdCompressionDictionary.Parse"/> read them.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> is shorter than 8 bytes, or an ID in the reserved ranges was given.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <exception cref="CompressionFormatException"><paramref name="content"/> begins with the dictionary magic number but is not a valid dictionary.</exception>
    public static byte[] Build(ReadOnlySpan<byte> content, IReadOnlyList<ReadOnlyMemory<byte>> samples, uint dictionaryId = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (content.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(content) == ZstdFormat.DictionaryMagic)
        {
            ZstdDictionary parsed = ZstdDictionary.Parse(content);
            if (dictionaryId == 0)
            {
                dictionaryId = parsed.Id;
            }

            content = parsed.Content.Span;
        }

        if (content.Length < 8)
        {
            throw new ArgumentException("Dictionary content must be at least 8 bytes.", nameof(content));
        }

        if (dictionaryId != 0 && (dictionaryId < 32768 || dictionaryId >= 0x80000000u))
        {
            // RFC 8878 §5: IDs up to 32767 and from 2^31 up are reserved.
            throw new ArgumentException("Dictionary IDs from 1 to 32767 and from 2^31 up are reserved by the format.", nameof(dictionaryId));
        }

        var stats = new Statistics();
        CollectStatistics(content, samples, stats);
        return Assemble(content, stats, dictionaryId);
    }

    /// <summary>The histograms of what the samples' parse produced.</summary>
    internal sealed class Statistics
    {
        public readonly uint[] Literals = new uint[256];
        public readonly uint[] LiteralLengths = new uint[ZstdFormat.LiteralLengthMaxSymbol + 1];
        public readonly uint[] MatchLengths = new uint[ZstdFormat.MatchLengthMaxSymbol + 1];
        public readonly uint[] Offsets = new uint[ZstdFormat.OffsetMaxSymbol + 1];
        public long Sequences;

        public void Add(Statistics other)
        {
            for (int i = 0; i < Literals.Length; i++)
            {
                Literals[i] += other.Literals[i];
            }

            for (int i = 0; i < LiteralLengths.Length; i++)
            {
                LiteralLengths[i] += other.LiteralLengths[i];
            }

            for (int i = 0; i < MatchLengths.Length; i++)
            {
                MatchLengths[i] += other.MatchLengths[i];
            }

            for (int i = 0; i < Offsets.Length; i++)
            {
                Offsets[i] += other.Offsets[i];
            }

            Sequences += other.Sequences;
        }
    }

    internal static void CollectStatistics(ReadOnlySpan<byte> content, IReadOnlyList<ReadOnlyMemory<byte>> samples, Statistics stats)
    {
        // Each sample is parsed by the fast match finder at level 3 with the content as its history, the way a frame compressed with
        // this dictionary would be: the repeat offsets and match lengths that come out are the ones the tables will have to code.
        var dictionary = ZstdCompressionDictionary.FromContent(content);
        var finder = new ZstdFastMatchFinder();
        var store = new ZstdSequenceStore();
        for (int s = 0; s < samples.Count; s++)
        {
            ReadOnlySpan<byte> sample = samples[s].Span;
            if (sample.Length == 0)
            {
                continue;
            }

            ZstdCompressionParameters parameters = ZstdCompressionParameters.Resolve(StatisticsLevel, sample.Length, content.Length);
            finder.BeginFrame(parameters, dictionary);
            ZstdRepeatOffsets reps = ZstdRepeatOffsets.Initial;
            for (int start = 0; start < sample.Length; start += store.BlockSize)
            {
                int end = Math.Min(start + store.BlockSize, sample.Length);
                store.Clear();
                finder.FindSequences(sample, start, end, 0, reps, store);
                foreach (ZstdSequence q in store.Sequences)
                {
                    // Apply rotates the offsets as a decoder will and returns the offset value the sequence carries (RFC 8878 §3.1.1.5).
                    uint value = reps.Apply(q.Offset, q.LiteralLength);
                    stats.LiteralLengths[ZstdBlockWriter.LiteralLengthCode(q.LiteralLength)]++;
                    stats.MatchLengths[ZstdBlockWriter.MatchLengthCode(q.MatchLength)]++;
                    stats.Offsets[Math.Min(BitOperations.Log2(value), ZstdFormat.OffsetMaxSymbol)]++;
                    stats.Sequences++;
                }

                foreach (byte b in store.Literals)
                {
                    stats.Literals[b]++;
                }
            }
        }
    }

    internal static byte[] Assemble(ReadOnlySpan<byte> content, Statistics stats, uint dictionaryId)
    {
        byte[] header = new byte[8 + 130 + (3 * 400) + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, ZstdFormat.DictionaryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dictionaryId);
        int p = 8;
        p += WriteHuffman(header.AsSpan(p), stats.Literals);
        p += WriteSequenceTable(header.AsSpan(p), stats.Offsets, ZstdFormat.OffsetMaxAccuracyLog, Math.Clamp(BitOperations.Log2((uint)content.Length + 3) + 1, MinOffsetSymbols, ZstdFormat.OffsetMaxSymbol + 1), ZstdFormat.OffsetDefaultDistribution, 5);
        p += WriteSequenceTable(header.AsSpan(p), stats.MatchLengths, ZstdFormat.SequenceMaxAccuracyLog, ZstdFormat.MatchLengthMaxSymbol + 1, ZstdFormat.MatchLengthDefaultDistribution, 6);
        p += WriteSequenceTable(header.AsSpan(p), stats.LiteralLengths, ZstdFormat.SequenceMaxAccuracyLog, ZstdFormat.LiteralLengthMaxSymbol + 1, ZstdFormat.LiteralLengthDefaultDistribution, 6);

        // RFC 8878 §5: repeat offsets start as 1, 4, 8, the same as a frame without a dictionary; each must fit in the content.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(p + 8), 8);
        p += 12;

        byte[] result = new byte[p + content.Length];
        header.AsSpan(0, p).CopyTo(result);
        content.CopyTo(result.AsSpan(p));
        if (dictionaryId == 0)
        {
            // Derived ID: the xxHash64 of the dictionary with a zero ID, folded into the range the format leaves free.
            ulong h = XxHash64.Compute(result);
            dictionaryId = 32768 + (uint)(h % (0x80000000UL - 32768));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), dictionaryId);
        }

        return result;
    }

    private static int WriteHuffman(Span<byte> destination, uint[] literals)
    {
        // Every byte gets a code, the rare and unseen ones the longest: a frame with a byte the samples never had can still use the
        // dictionary's table instead of paying for one of its own.
        Span<uint> histogram = stackalloc uint[256];
        for (int s = 0; s < 256; s++)
        {
            histogram[s] = Math.Max(literals[s], 1);
        }

        var table = new HuffmanEncodingTable();
        int written = Describe(table, histogram, 255, destination);
        if (written < 0)
        {
            // A perfectly flat code cannot be described (no samples, or a uniform spread): use a text-like prior instead.
            FillAllBytes(histogram);
            written = Describe(table, histogram, 255, destination);
        }

        if (written < 0)
        {
            throw new InvalidOperationException("The Huffman table could not be described.");
        }

        return written;
    }

    private static int Describe(HuffmanEncodingTable table, ReadOnlySpan<uint> histogram, int maxSymbol, Span<byte> destination) =>
        table.Build(histogram, maxSymbol) ? table.WriteDescription(destination) : -1;

    // A prior for text-like data: lower-case letters and the space most likely, then capitals and digits, other printable bytes, controls.
    private static void FillAllBytes(Span<uint> histogram)
    {
        for (int s = 0; s < 256; s++)
        {
            histogram[s] = s == ' ' ? 64u
                : s is >= 'a' and <= 'z' ? 32u
                : s is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? 16u
                : s is >= 32 and < 127 ? 8u
                : s < 32 ? 2u
                : 1u;
        }
    }

    // Every code up to symbolCount - 1 gets at least the smallest probability, as the offset, match length and literal length codes
    // of a frame that differs a little from the samples would otherwise force the encoder to write tables of its own.
    private static int WriteSequenceTable(Span<byte> destination, uint[] counts, int maxAccuracyLog, int symbolCount, short[] defaults, int defaultLog)
    {
        uint seen = 0;
        for (int s = 0; s < counts.Length; s++)
        {
            seen += counts[s];
        }

        if (seen == 0)
        {
            return FseEncodingTable.WriteDescription(destination, defaults, defaultLog);
        }

        Span<uint> histogram = stackalloc uint[counts.Length];
        uint total = 0;
        for (int s = 0; s < symbolCount; s++)
        {
            histogram[s] = Math.Max(counts[s], 1);
            total += histogram[s];
        }

        int maxSymbol = symbolCount - 1;
        int accuracyLog = FseEncodingTable.OptimalAccuracyLog(maxAccuracyLog, (int)Math.Min(total, int.MaxValue), maxSymbol);
        Span<short> normalized = stackalloc short[counts.Length];
        int count = FseEncodingTable.Normalize(normalized, accuracyLog, histogram, total, maxSymbol);
        return FseEncodingTable.WriteDescription(destination, normalized[..count], accuracyLog);
    }
}
