using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>Constants and code tables of the Zstandard format (RFC 8878).</summary>
internal static class ZstdFormat
{
    /// <summary>The format name used in exceptions.</summary>
    public const string Name = "zstd";

    /// <summary>RFC 8878 §3.1.1: Magic_Number of a Zstandard frame.</summary>
    public const uint FrameMagic = 0xFD2FB528;

    /// <summary>RFC 8878 §3.1.2: skippable frames use magic numbers 0x184D2A50 to 0x184D2A5F.</summary>
    public const uint SkippableMagicBase = 0x184D2A50;

    /// <summary>RFC 8878 §5: Magic_Number of a dictionary in the Zstandard dictionary format.</summary>
    public const uint DictionaryMagic = 0xEC30A437;

    /// <summary>RFC 8878 §3.1.1.2.2: Block_Maximum_Size is at most 128 KiB.</summary>
    public const int BlockSizeMax = 128 * 1024;

    /// <summary>The longest possible frame header: magic, descriptor, window, dictionary ID and content size.</summary>
    public const int FrameHeaderSizeMax = 4 + 1 + 1 + 4 + 8;

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: literals lengths use codes 0 to 35.</summary>
    public const int LiteralLengthMaxSymbol = 35;

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: match lengths use codes 0 to 52.</summary>
    public const int MatchLengthMaxSymbol = 52;

    /// <summary>Offset codes are limited to 31, so offsets fit in 32 bits (RFC 8878 §3.1.1.3.2.1.1 allows this).</summary>
    public const int OffsetMaxSymbol = 31;

    /// <summary>RFC 8878 §3.1.1.3.2.1: the largest accuracy log of a literals length or match length table.</summary>
    public const int SequenceMaxAccuracyLog = 9;

    /// <summary>RFC 8878 §3.1.1.3.2.1: the largest accuracy log of an offset table.</summary>
    public const int OffsetMaxAccuracyLog = 8;

    /// <summary>RFC 8878 §4.2.1.2: the largest accuracy log of the table that compresses Huffman weights.</summary>
    public const int HuffmanWeightMaxAccuracyLog = 6;

    /// <summary>RFC 8878 §4.2.1: Huffman codes are at most 11 bits long.</summary>
    public const int HuffmanMaxBits = 11;

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: baselines of the literals length codes.</summary>
    public static readonly uint[] LiteralLengthBase =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096,
        8192, 16384, 32768, 65536,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: extra bits of the literals length codes.</summary>
    public static readonly byte[] LiteralLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12,
        13, 14, 15, 16,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: baselines of the match length codes.</summary>
    public static readonly uint[] MatchLengthBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34,
        35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051,
        4099, 8195, 16387, 32771, 65539,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.1.1: extra bits of the match length codes.</summary>
    public static readonly byte[] MatchLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11,
        12, 13, 14, 15, 16,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.2: default distribution of literals lengths (accuracy log 6).</summary>
    public static readonly short[] LiteralLengthDefaultDistribution =
    [
        4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1,
        -1, -1, -1, -1,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.2: default distribution of match lengths (accuracy log 6).</summary>
    public static readonly short[] MatchLengthDefaultDistribution =
    [
        1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1,
        -1, -1, -1, -1, -1,
    ];

    /// <summary>RFC 8878 §3.1.1.3.2.2: default distribution of offset codes (accuracy log 5).</summary>
    public static readonly short[] OffsetDefaultDistribution =
    [
        1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1,
    ];

    private static FseDecodingTable? s_defaultLiteralLengths;
    private static FseDecodingTable? s_defaultMatchLengths;
    private static FseDecodingTable? s_defaultOffsets;

    /// <summary>Gets the shared table for the predefined literals length distribution. It is never modified.</summary>
    public static FseDecodingTable DefaultLiteralLengths => s_defaultLiteralLengths ??= BuildDefault(LiteralLengthDefaultDistribution, 6);

    /// <summary>Gets the shared table for the predefined match length distribution. It is never modified.</summary>
    public static FseDecodingTable DefaultMatchLengths => s_defaultMatchLengths ??= BuildDefault(MatchLengthDefaultDistribution, 6);

    /// <summary>Gets the shared table for the predefined offset distribution. It is never modified.</summary>
    public static FseDecodingTable DefaultOffsets => s_defaultOffsets ??= BuildDefault(OffsetDefaultDistribution, 5);

    private static FseDecodingTable BuildDefault(short[] distribution, int accuracyLog)
    {
        var table = new FseDecodingTable();
        table.Build(distribution, accuracyLog);
        return table;
    }

    /// <summary>Creates the exception for a value the format forbids.</summary>
    [DoesNotReturn]
    public static void ThrowCorrupt(long offset, string expected) =>
        throw new CompressionFormatException(Name, offset, expected);

    /// <summary>Creates the exception for input that ended too early.</summary>
    [DoesNotReturn]
    public static void ThrowTruncated(long offset, string expected) =>
        throw new CompressionFormatException(Name, offset, expected, isTruncated: true);
}
