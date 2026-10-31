
namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// The constant tables of RFC 1951: how length and distance codes map to value ranges, and the order in which the
/// code-length code's lengths are transmitted. They are shared by the decoder and, later, the encoder.
/// </summary>
public static class DeflateTables
{
    /// <summary>The largest code length a Huffman code may have (RFC 1951 section 3.2.7).</summary>
    public const int MaxCodeLength = 15;

    /// <summary>The size of the literal/length alphabet: 256 literals, the end-of-block symbol and 29 length codes, plus two that are never used.</summary>
    public const int LiteralLengthSymbolCount = 288;

    /// <summary>The size of the distance alphabet: 30 codes, plus two that are never used.</summary>
    public const int DistanceSymbolCount = 32;

    /// <summary>The size of the code-length alphabet (RFC 1951 section 3.2.7).</summary>
    public const int CodeLengthSymbolCount = 19;

    /// <summary>The end-of-block symbol of the literal/length alphabet.</summary>
    public const int EndOfBlockSymbol = 256;

    /// <summary>The size of the sliding window: matches reach back at most this many bytes.</summary>
    public const int WindowSize = 32768;

    /// <summary>The shortest match length.</summary>
    public const int MinMatchLength = 3;

    /// <summary>The longest match length.</summary>
    public const int MaxMatchLength = 258;

    /// <summary>Gets the smallest match length of each length code (symbols 257 to 285, RFC 1951 section 3.2.5).</summary>
    public static ReadOnlySpan<ushort> LengthBase =>
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258,
    ];

    /// <summary>Gets the number of extra bits that follow each length code (symbols 257 to 285).</summary>
    public static ReadOnlySpan<byte> LengthExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0,
    ];

    /// <summary>Gets the smallest distance of each distance code (codes 0 to 29, RFC 1951 section 3.2.5).</summary>
    public static ReadOnlySpan<ushort> DistanceBase =>
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577,
    ];

    /// <summary>Gets the number of extra bits that follow each distance code.</summary>
    public static ReadOnlySpan<byte> DistanceExtraBits =>
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13,
    ];

    /// <summary>Gets the order in which the lengths of the code-length alphabet are transmitted (RFC 1951 section 3.2.7).</summary>
    public static ReadOnlySpan<byte> CodeLengthOrder =>
    [
        16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15,
    ];

    /// <summary>
    /// Fills <paramref name="lengths"/> with the code lengths of the fixed Huffman code (RFC 1951 section 3.2.6)
    /// for the literal/length alphabet.
    /// </summary>
    /// <param name="lengths">Receives 288 lengths.</param>
    /// <exception cref="ArgumentException"><paramref name="lengths"/> is shorter than 288.</exception>
    public static void GetFixedLiteralLengthCodeLengths(Span<byte> lengths)
    {
        if (lengths.Length < LiteralLengthSymbolCount)
        {
            throw new ArgumentException("The span must hold 288 lengths.", nameof(lengths));
        }

        lengths[..144].Fill(8);
        lengths[144..256].Fill(9);
        lengths[256..280].Fill(7);
        lengths[280..288].Fill(8);
    }

    /// <summary>Fills <paramref name="lengths"/> with the 32 code lengths of the fixed distance code: all 5 bits.</summary>
    /// <param name="lengths">Receives 32 lengths.</param>
    /// <exception cref="ArgumentException"><paramref name="lengths"/> is shorter than 32.</exception>
    public static void GetFixedDistanceCodeLengths(Span<byte> lengths)
    {
        if (lengths.Length < DistanceSymbolCount)
        {
            throw new ArgumentException("The span must hold 32 lengths.", nameof(lengths));
        }

        lengths[..DistanceSymbolCount].Fill(5);
    }
}
