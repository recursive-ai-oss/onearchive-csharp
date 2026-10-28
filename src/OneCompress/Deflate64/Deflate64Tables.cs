using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Deflate64;

/// <summary>
/// The constants of Deflate64 (zip method 9) where they differ from Deflate (RFC 1951). Everything not listed here is as in
/// <see cref="Deflate.DeflateTables"/>: block types, the code-length alphabet, Huffman code construction and the length symbols
/// 257 to 284. The values come from the experiments in docs/deflate64-derivation.md (facts F1 to F4).
/// </summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class Deflate64Tables
{
    /// <summary>The history a match may reach into: 65536 bytes (the distance of the farthest match).</summary>
    public const int WindowSize = 65536;

    /// <summary>The shortest match: 3 bytes.</summary>
    public const int MinMatchLength = 3;

    /// <summary>The longest match: 65538 bytes, length symbol 285 with all 16 extra bits set.</summary>
    public const int MaxMatchLength = 65538;

    /// <summary>The number of literal/length symbols a code may describe (286 and 287 stay invalid when decoded).</summary>
    public const int LiteralLengthSymbolCount = 288;

    /// <summary>The number of distance symbols: 32. Symbols 30 and 31 are valid here.</summary>
    public const int DistanceSymbolCount = 32;

    /// <summary>
    /// The smallest match length of length symbols 257 to 285 (index 0 to 28). Symbol 285 stands for 3 to 65538 and takes 16
    /// extra bits; in Deflate it is the single length 258.
    /// </summary>
    public static ReadOnlySpan<ushort> LengthBase =>
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
        35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 3,
    ];

    /// <summary>The extra bits that follow length symbols 257 to 285 (index 0 to 28): 16 for symbol 285.</summary>
    public static ReadOnlySpan<byte> LengthExtraBits =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
        3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 16,
    ];

    /// <summary>The smallest distance of distance symbols 0 to 31. Symbols 30 and 31 start at 32769 and 49153.</summary>
    public static ReadOnlySpan<ushort> DistanceBase =>
    [
        1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
        257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577, 32769, 49153,
    ];

    /// <summary>The extra bits that follow distance symbols 0 to 31: 14 for symbols 30 and 31.</summary>
    public static ReadOnlySpan<byte> DistanceExtraBits =>
    [
        0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
        7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14,
    ];

    /// <summary>The length symbol (257 to 285) an encoder writes for a match of <paramref name="length"/> bytes: 257 to 284 for 3 to 257, 285 from 258 up.</summary>
    /// <param name="length">3 to 65538.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is out of range.</exception>
    public static int LengthSymbol(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, MinMatchLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, MaxMatchLength);
        if (length >= 258)
        {
            return 285;
        }

        ReadOnlySpan<ushort> lengthBase = LengthBase;
        int slot = 27;
        while (lengthBase[slot] > length)
        {
            slot--;
        }

        return 257 + slot;
    }

    /// <summary>The distance symbol (0 to 31) an encoder writes for a distance of <paramref name="distance"/> bytes.</summary>
    /// <param name="distance">1 to 65536.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is out of range.</exception>
    public static int DistanceSymbol(int distance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(distance, WindowSize);
        ReadOnlySpan<ushort> distanceBase = DistanceBase;
        int slot = 31;
        while (distanceBase[slot] > distance)
        {
            slot--;
        }

        return slot;
    }
}
