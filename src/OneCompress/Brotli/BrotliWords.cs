using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The brotli static dictionary (RFC 7932 section 8 and Appendix A): 122,784 bytes of words for the lengths 4 to 24, which a
/// compressed stream refers to by a distance that reaches past everything it could copy from.
/// </summary>
/// <remarks>
/// The data lives in the assembly as read-only static data and is only reachable from here, so an application that never uses
/// brotli does not carry it. A reference to word <c>index</c> of length <c>length</c> with transform <c>t</c> has the distance
/// <c>maxDistance + 1 + index + (t &lt;&lt; <see cref="GetWordBits"/>(length))</c>, where <c>maxDistance</c> is the smaller of the window
/// size and the number of bytes produced so far.
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class BrotliWords
{
    /// <summary>The shortest word length the dictionary has (4).</summary>
    public const int MinWordLength = 4;

    /// <summary>The longest word length the dictionary has (24).</summary>
    public const int MaxWordLength = 24;

    /// <summary>The size of the dictionary in bytes (122,784).</summary>
    public const int Size = 122_784;

    // RFC 7932 section 8: DOFFSET[length + 1] = DOFFSET[length] + length * NWORDS[length].
    private static readonly int[] s_offsets = CreateOffsets();

    private static int[] CreateOffsets()
    {
        var offsets = new int[MaxWordLength + 2];
        ReadOnlySpan<byte> bits = BrotliDictionaryData.NDBits;
        for (int length = 0; length <= MaxWordLength; length++)
        {
            offsets[length + 1] = offsets[length] + (length < MinWordLength ? 0 : length * (1 << bits[length]));
        }

        return offsets;
    }

    /// <summary>The whole dictionary: the words of length 4, then those of length 5, and so on.</summary>
    public static ReadOnlySpan<byte> Data => BrotliDictionaryData.Words;

    /// <summary>The base-2 logarithm of the number of words of the given length (the NDBITS of the RFC); zero for lengths without words.</summary>
    /// <param name="length">The word length.</param>
    /// <returns>The number of address bits, 5 to 11 for lengths 4 to 24, or 0 if <paramref name="length"/> is outside that range.</returns>
    public static int GetWordBits(int length) => (uint)(length - MinWordLength) <= MaxWordLength - MinWordLength ? BrotliDictionaryData.NDBits[length] : 0;

    /// <summary>The number of words of the given length.</summary>
    /// <param name="length">The word length.</param>
    /// <returns>The word count, or 0 if <paramref name="length"/> is outside 4 to 24.</returns>
    public static int GetWordCount(int length) => (uint)(length - MinWordLength) <= MaxWordLength - MinWordLength ? 1 << BrotliDictionaryData.NDBits[length] : 0;

    /// <summary>Returns a word of the dictionary.</summary>
    /// <param name="length">The word length, 4 to 24.</param>
    /// <param name="index">The index among the words of that length, 0 up to <see cref="GetWordCount"/> minus one.</param>
    /// <returns>The <paramref name="length"/> bytes of the word.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> or <paramref name="index"/> is out of range.</exception>
    public static ReadOnlySpan<byte> GetWord(int length, int index)
    {
        if ((uint)(length - MinWordLength) > MaxWordLength - MinWordLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The dictionary has words of length 4 to 24.");
        }

        if ((uint)index >= (uint)(1 << BrotliDictionaryData.NDBits[length]))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "The index is not below the number of words of this length.");
        }

        return BrotliDictionaryData.Words.Slice(s_offsets[length] + (index * length), length);
    }

    // Used by the decoder after it has checked both arguments.
    internal static ReadOnlySpan<byte> GetWordUnchecked(int length, int index) =>
        BrotliDictionaryData.Words.Slice(s_offsets[length] + (index * length), length);
}
