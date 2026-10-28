using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// A Zstandard dictionary (RFC 8878 §5): content that acts as history in front of the data, and for
/// dictionaries in the Zstandard format, entropy tables and repeat offsets to start from. A dictionary is
/// immutable and may be shared by any number of decoders on any number of threads. Dictionary IDs from 1 to 32767 and from 2^31 up are
/// reserved by the format (RFC 8878 §5); IDs for your own dictionaries belong in 32768 to 2^31-1, and 0 means no ID.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "This is the Zstandard format's own term; the type is not a collection.")]
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdDictionary
{
    private readonly byte[] _content;

    private ZstdDictionary(
        uint id,
        byte[] content,
        HuffmanDecodingTable? huffman,
        FseDecodingTable? offsets,
        FseDecodingTable? matchLengths,
        FseDecodingTable? literalLengths,
        uint[]? repeatOffsets)
    {
        Id = id;
        _content = content;
        Huffman = huffman;
        Offsets = offsets;
        MatchLengths = matchLengths;
        LiteralLengths = literalLengths;
        RepeatOffsets = repeatOffsets ?? [1, 4, 8];
    }

    /// <summary>Gets the dictionary ID. Raw-content dictionaries have the ID given when they were created, 0 by default.</summary>
    public uint Id { get; }

    /// <summary>Gets the dictionary content: the bytes sequences may copy from.</summary>
    public ReadOnlyMemory<byte> Content => _content;

    /// <summary>Gets a value indicating whether the dictionary carries entropy tables (it is in the Zstandard dictionary format).</summary>
    public bool HasEntropyTables => Huffman is not null;

    internal HuffmanDecodingTable? Huffman { get; }

    internal FseDecodingTable? Offsets { get; }

    internal FseDecodingTable? MatchLengths { get; }

    internal FseDecodingTable? LiteralLengths { get; }

    internal uint[] RepeatOffsets { get; }

    /// <summary>
    /// Creates a dictionary from the bytes of a dictionary file. Data that begins with the Zstandard dictionary
    /// magic number is parsed as the Zstandard dictionary format; anything else is a raw-content dictionary.
    /// </summary>
    /// <param name="data">The dictionary bytes; they are copied.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="CompressionFormatException">The data is in the dictionary format but invalid.</exception>
    /// <exception cref="ArgumentException">The data is raw content shorter than 8 bytes.</exception>
    public static ZstdDictionary Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == ZstdFormat.DictionaryMagic)
        {
            try
            {
                return ParseFormatted(data);
            }
            catch (CompressionFormatException e) when (e.Format == ZstdFormat.Name)
            {
                // The table readers report errors as the "zstd" format; a dictionary's errors say so.
                throw new CompressionFormatException("zstd dictionary", e.Offset, e.Expected, e.IsTruncated);
            }
        }

        return FromContent(data);
    }

    /// <summary>Creates a raw-content dictionary: bytes that act as history, with no entropy tables.</summary>
    /// <param name="content">The content, at least 8 bytes (RFC 8878 §5); it is copied.</param>
    /// <param name="id">The ID that frames use to name this dictionary, or 0 to use it for frames that name none.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="ArgumentException"><paramref name="content"/> is shorter than 8 bytes.</exception>
    public static ZstdDictionary FromContent(ReadOnlySpan<byte> content, uint id = 0)
    {
        if (content.Length < 8)
        {
            throw new ArgumentException("A raw-content dictionary must be at least 8 bytes.", nameof(content));
        }

        return new ZstdDictionary(id, content.ToArray(), null, null, null, null, null);
    }

    /// <summary>
    /// Trains a Zstandard-format dictionary from sample payloads with the COVER family of algorithms. The result depends only on the
    /// samples and the options. See <see cref="ZstdCoverTrainer"/> for the algorithm and its pieces.
    /// </summary>
    /// <param name="samples">The training samples; empty samples are ignored.</param>
    /// <param name="options">The options; null means the defaults (FastCover with parameter search, 110 KiB).</param>
    /// <param name="cancellationToken">Cancels a long search.</param>
    /// <returns>The dictionary file's bytes, ready to save or to pass to <see cref="Parse"/>. To use a fresh dictionary directly for both compressing and decompressing, call <see cref="ZstdCompressionDictionary.Train"/> instead and take <see cref="ZstdCompressionDictionary.ForDecompression"/> from it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes in total.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static byte[] Train(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null, CancellationToken cancellationToken = default) =>
        ZstdCoverTrainer.Train(samples, options, cancellationToken);

    private static ZstdDictionary ParseFormatted(ReadOnlySpan<byte> data)
    {
        const string Format = "zstd dictionary";
        if (data.Length < 8)
        {
            ZstdFormat.ThrowTruncated(data.Length, "a dictionary ID");
        }

        uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int p = 8;

        // RFC 8878 §5: Huffman table for literals, then FSE tables for offsets, match lengths and literals lengths.
        var huffman = new HuffmanDecodingTable();
        p += huffman.ReadDescription(data[p..], p);

        var offsets = new FseDecodingTable();
        p += offsets.ReadDescription(data[p..], ZstdFormat.OffsetMaxSymbol, ZstdFormat.OffsetMaxAccuracyLog, p);

        var matchLengths = new FseDecodingTable();
        p += matchLengths.ReadDescription(data[p..], ZstdFormat.MatchLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog, p);

        var literalLengths = new FseDecodingTable();
        p += literalLengths.ReadDescription(data[p..], ZstdFormat.LiteralLengthMaxSymbol, ZstdFormat.SequenceMaxAccuracyLog, p);

        if (data.Length - p < 12)
        {
            throw new CompressionFormatException(Format, data.Length, "three 4-byte repeat offsets", isTruncated: true);
        }

        uint[] reps = new uint[3];
        int contentLength = data.Length - p - 12;
        for (int i = 0; i < 3; i++)
        {
            reps[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(p + (4 * i))..]);

            // RFC 8878 §5: each repeat offset is at least 1 and at most the content size.
            if (reps[i] == 0 || reps[i] > contentLength)
            {
                throw new CompressionFormatException(Format, p + (4 * i), $"a repeat offset from 1 to the content size ({contentLength}), found {reps[i]}");
            }
        }

        p += 12;
        return new ZstdDictionary(id, data[p..].ToArray(), huffman, offsets, matchLengths, literalLengths, reps);
    }
}
