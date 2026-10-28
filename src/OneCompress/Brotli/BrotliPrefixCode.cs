using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// A canonical prefix code over a small alphabet, with the table that decodes it (RFC 7932 section 3). The brotli decoder builds one
/// of these for every literal, insert-and-copy length, distance, block type and block count code of a meta-block; the type is public so
/// that tools can decode brotli's prefix codes on their own.
/// </summary>
/// <remarks>
/// Bits are given in the order the stream carries them: the first bit of a code is the least significant bit of the value passed to
/// <see cref="Decode"/>, which is why the table is indexed by the bit-reversed code (RFC 7932 section 1.5.1).
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BrotliPrefixCode
{
    /// <summary>The longest code a brotli stream may use, in bits (15).</summary>
    public const int MaxCodeLength = BrotliPrefixTables.MaxCodeLength;

    /// <summary>The largest alphabet brotli uses, the 704 insert-and-copy length codes.</summary>
    public const int MaxAlphabetSize = BrotliPrefixTables.MaxAlphabetSize;

    private readonly uint[] _table;

    private BrotliPrefixCode(uint[] table, int alphabetSize)
    {
        _table = table;
        AlphabetSize = alphabetSize;
    }

    /// <summary>The number of symbols in the alphabet.</summary>
    public int AlphabetSize { get; }

    /// <summary>Creates the code with the given code lengths.</summary>
    /// <param name="codeLengths">The code length of each symbol, 0 for a symbol that is not used, at most <see cref="MaxCodeLength"/>.
    /// The lengths must describe a complete code: the sum over the used symbols of 2<sup>-length</sup> is exactly 1.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentException">There are more than <see cref="MaxAlphabetSize"/> symbols, a length is above 15, or the lengths do not form a complete code.</exception>
    public static BrotliPrefixCode FromCodeLengths(ReadOnlySpan<byte> codeLengths)
    {
        if (codeLengths.Length > MaxAlphabetSize)
        {
            throw new ArgumentException("A brotli alphabet has at most 704 symbols.", nameof(codeLengths));
        }

        uint[] table = new uint[1 << BrotliPrefixTables.RootBits];
        int size = BrotliPrefixTables.Build(codeLengths, ref table, 0);
        if (size < 0)
        {
            throw new ArgumentException("The code lengths must be at most 15 and form a complete prefix code.", nameof(codeLengths));
        }

        return new BrotliPrefixCode(table, codeLengths.Length);
    }

    /// <summary>Creates the code of an alphabet in which one symbol takes no bits, as a simple prefix code with one symbol does (RFC 7932 section 3.4).</summary>
    /// <param name="symbol">The symbol.</param>
    /// <param name="alphabetSize">The alphabet size.</param>
    /// <returns>The code.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alphabetSize"/> is not 1 to 704, or <paramref name="symbol"/> is not below it.</exception>
    public static BrotliPrefixCode FromSingleSymbol(int symbol, int alphabetSize)
    {
        if (alphabetSize is < 1 or > MaxAlphabetSize)
        {
            throw new ArgumentOutOfRangeException(nameof(alphabetSize));
        }

        if ((uint)symbol >= (uint)alphabetSize)
        {
            throw new ArgumentOutOfRangeException(nameof(symbol));
        }

        uint[] table = new uint[BrotliPrefixTables.SingleSymbolTableSize];
        BrotliPrefixTables.BuildSingle(symbol, ref table, 0);
        return new BrotliPrefixCode(table, alphabetSize);
    }

    /// <summary>Decodes the next symbol.</summary>
    /// <param name="bits">The next bits of the stream, the first one in the least significant position. At least the code's length in bits must be valid; 15 always suffice.</param>
    /// <param name="bitsUsed">The number of bits the symbol's code takes (0 for a single-symbol code).</param>
    /// <returns>The symbol. A complete code gives every bit pattern a meaning, so there is no failure case.</returns>
    public int Decode(uint bits, out int bitsUsed)
    {
        uint e = _table[bits & BrotliPrefixTables.RootMask];
        if ((e & BrotliPrefixTables.LinkFlag) == 0)
        {
            bitsUsed = (int)(e & 0xFF);
            return (int)(e >> 8);
        }

        int subBits = (int)(e & 0x7F);
        uint sub = _table[(int)(e >> 8) + (int)((bits >> BrotliPrefixTables.RootBits) & ((1u << subBits) - 1))];
        bitsUsed = BrotliPrefixTables.RootBits + (int)(sub & 0xFF);
        return (int)(sub >> 8);
    }
}
