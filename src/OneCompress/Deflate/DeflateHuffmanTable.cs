using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>What a decoded Huffman code stands for in a <see cref="DeflateHuffmanTable"/>.</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateSymbolKind : byte
{
    /// <summary>No code has these bits, or the symbol is one RFC 1951 forbids (literal/length 286 and 287, distance 30 and 31).</summary>
    Invalid = 0,

    /// <summary>A literal byte; <see cref="DeflateHuffmanEntry.Value"/> is the byte.</summary>
    Literal = 1,

    /// <summary>A match length code; <see cref="DeflateHuffmanEntry.Value"/> is the smallest length and <see cref="DeflateHuffmanEntry.ExtraBits"/> bits follow.</summary>
    Length = 2,

    /// <summary>The end-of-block symbol (256).</summary>
    EndOfBlock = 3,

    /// <summary>A match distance code; <see cref="DeflateHuffmanEntry.Value"/> is the smallest distance and <see cref="DeflateHuffmanEntry.ExtraBits"/> bits follow.</summary>
    Distance = 4,

    /// <summary>A link to a second-level table. It never leaves <see cref="DeflateHuffmanTable.Lookup"/>.</summary>
    Link = 5,

    /// <summary>A symbol of the code-length alphabet; <see cref="DeflateHuffmanEntry.Value"/> is the symbol (0 to 18).</summary>
    CodeLengthSymbol = 6,
}

/// <summary>
/// One decoded entry of a <see cref="DeflateHuffmanTable"/>: the code's length, and for the alphabet the table was
/// built for, what the code means, already translated through RFC 1951's base and extra-bit tables.
/// </summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public readonly struct DeflateHuffmanEntry
{
    internal const int KindShift = 12;
    internal const int ExtraShift = 8;
    internal const int ValueShift = 16;

    internal DeflateHuffmanEntry(uint raw) => Raw = raw;

    /// <summary>
    /// Gets the packed form: value in bits 16 to 31, kind in 12 to 15, and below that, for a literal, length, distance or code-length
    /// entry, the code length in bits 8 to 11 and the code length plus the extra bits that follow it in bits 0 to 7, so that a decoder
    /// drops code and extra bits in one shift. For a link to a second-level table, bits 8 to 11 hold that table's index width and
    /// bits 0 to 7 the first-level width.
    /// </summary>
    public uint Raw { get; }

    /// <summary>Gets the length in bits of the code. It is at least 1, also for <see cref="DeflateSymbolKind.Invalid"/>, so that a decoder always consumes input before it fails.</summary>
    public int CodeLength => Kind == DeflateSymbolKind.Link ? (int)(Raw & 0xFF) : (int)((Raw >> ExtraShift) & 15);

    /// <summary>Gets the number of extra bits that follow the code (lengths and distances only).</summary>
    public int ExtraBits => Kind == DeflateSymbolKind.Link ? 0 : (int)(Raw & 0xFF) - (int)((Raw >> ExtraShift) & 15);

    /// <summary>Gets what the code stands for.</summary>
    public DeflateSymbolKind Kind => (DeflateSymbolKind)((Raw >> KindShift) & 15);

    /// <summary>Gets the literal byte, the base length or distance, or the code-length symbol, depending on <see cref="Kind"/>.</summary>
    public int Value => (int)(Raw >> ValueShift);

    internal static uint Pack(DeflateSymbolKind kind, int value, int extraBits, int codeLength) =>
        kind == DeflateSymbolKind.Link
            ? ((uint)value << ValueShift) | ((uint)kind << KindShift) | ((uint)extraBits << ExtraShift) | (uint)codeLength
            : ((uint)value << ValueShift) | ((uint)kind << KindShift) | ((uint)codeLength << ExtraShift) | (uint)(codeLength + extraBits);
}

/// <summary>The alphabet a <see cref="DeflateHuffmanTable"/> decodes (RFC 1951 sections 3.2.5 to 3.2.7).</summary>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum DeflateAlphabet
{
    /// <summary>Literals 0 to 255, end-of-block (256) and length codes 257 to 285; 286 and 287 are accepted in a code but invalid when decoded.</summary>
    LiteralLength,

    /// <summary>Distance codes 0 to 29; 30 and 31 are accepted in a code but invalid when decoded.</summary>
    Distance,

    /// <summary>The code-length alphabet, symbols 0 to 18.</summary>
    CodeLength,
}

/// <summary>
/// A decoding table for a canonical Huffman code as Deflate uses it (RFC 1951 section 3.2.2): bits arrive least
/// significant first, so a code's bits are reversed when they index the table. Short codes resolve in one lookup;
/// longer ones go through a second-level table. Each entry already carries the meaning of its symbol, so the
/// decoder never searches the length and distance tables.
/// </summary>
/// <remarks>
/// <para>A table is built from code lengths with <see cref="TryBuild"/> and can be rebuilt for the next block; it
/// reuses its memory. A built table is read-only and may be shared between threads while nobody rebuilds it.</para>
/// <para>Accepted codes: complete codes; a code with no symbols at all (decoding any bits then yields
/// <see cref="DeflateSymbolKind.Invalid"/>); and a single code of length 1, which RFC 1951 section 3.2.7 allows for the
/// distance alphabet and which zlib also accepts for the literal/length alphabet. Over-subscribed and other incomplete
/// codes are refused.</para>
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class DeflateHuffmanTable
{
    private readonly int _primaryBits;
    private readonly bool _deflate64;
    private uint[] _entries;
    private uint[] _fast = [];
    private readonly int[] _subtableOffset;
    private readonly byte[] _subtableBits;
    private readonly int[] _longPrefixes;

    /// <summary>Creates an empty table for an alphabet. Until <see cref="TryBuild"/> succeeds every lookup is <see cref="DeflateSymbolKind.Invalid"/>.</summary>
    /// <param name="alphabet">The alphabet whose codes the table decodes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="alphabet"/> is not a defined value.</exception>
    public DeflateHuffmanTable(DeflateAlphabet alphabet)
        : this(alphabet, false)
    {
    }

    // With deflate64 the table gives symbol 285 and distance symbols 30 and 31 the meanings of the Deflate64 variant of the format
    // (see Deflate64Tables); every other symbol keeps its RFC 1951 meaning.
    internal DeflateHuffmanTable(DeflateAlphabet alphabet, bool deflate64)
    {
        _deflate64 = deflate64;
        Alphabet = alphabet;
        switch (alphabet)
        {
            case DeflateAlphabet.LiteralLength:
                _primaryBits = 10;
                _entries = new uint[1 << 10 | 1024];
                break;
            case DeflateAlphabet.Distance:
                _primaryBits = 8;
                _entries = new uint[1 << 8 | 512];
                break;
            case DeflateAlphabet.CodeLength:
                _primaryBits = 7; // the longest code-length code is 7 bits, so there are no second-level tables
                _entries = new uint[1 << 7];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(alphabet));
        }

        _subtableOffset = new int[1 << _primaryBits];
        _subtableBits = new byte[1 << _primaryBits];
        _longPrefixes = new int[1 << _primaryBits];
        Array.Fill(_entries, DeflateHuffmanEntry.Pack(DeflateSymbolKind.Invalid, 0, 0, 1));
    }

    /// <summary>Gets the alphabet this table decodes.</summary>
    public DeflateAlphabet Alphabet { get; }

    /// <summary>Gets the number of bits the first-level lookup uses: 10 for literal/length, 8 for distance, 7 for code-length.</summary>
    public int PrimaryBits => _primaryBits;

    internal uint[] Entries => _entries;

    /// <summary>
    /// The literal/length table as the fast loop reads it (empty for other alphabets): every literal entry carries the number of
    /// bytes it stands for in bits 8 to 11 (1 or 2) and its value as one or two bytes (first byte lowest), and bits 0 to 7
    /// hold the bits it consumes. Where two literal codes together fit the first level, the first-level entry stands for both.
    /// Everything else, and the layout of second-level tables, is as in <see cref="Entries"/>.
    /// </summary>
    internal uint[] FastEntries => _fast;

    internal uint PrimaryMask => (1u << _primaryBits) - 1;

    /// <summary>Gets the maximum number of symbols the alphabet's code can have lengths for.</summary>
    public int SymbolCount => Alphabet switch
    {
        DeflateAlphabet.LiteralLength => DeflateTables.LiteralLengthSymbolCount,
        DeflateAlphabet.Distance => DeflateTables.DistanceSymbolCount,
        _ => DeflateTables.CodeLengthSymbolCount,
    };

    /// <summary>
    /// Builds the table from code lengths: <c>codeLengths[s]</c> is the length in bits of symbol <c>s</c>'s code, 0 when the
    /// symbol is unused. Codes are assigned in canonical order (RFC 1951 section 3.2.2).
    /// </summary>
    /// <param name="codeLengths">One length from 0 to 15 per symbol, at most <see cref="SymbolCount"/> of them; missing trailing symbols are unused.</param>
    /// <param name="problem">When the lengths are refused, a phrase completing "expected ..."; otherwise <see langword="null"/>.</param>
    /// <returns>True when the table was built. On false the table is left empty.</returns>
    /// <exception cref="ArgumentException">There are more lengths than <see cref="SymbolCount"/>.</exception>
    public bool TryBuild(ReadOnlySpan<byte> codeLengths, out string? problem)
    {
        if (codeLengths.Length > SymbolCount)
        {
            throw new ArgumentException("Too many code lengths for the alphabet.", nameof(codeLengths));
        }

        Span<int> count = stackalloc int[DeflateTables.MaxCodeLength + 1];
        int total = 0;
        foreach (byte length in codeLengths)
        {
            if (length > DeflateTables.MaxCodeLength)
            {
                problem = "code lengths of at most 15 bits";
                Clear();
                return false;
            }

            count[length]++;
            total++;
        }

        total -= count[0];
        count[0] = 0;

        // Kraft check: "left" is the number of unused codes at the current length.
        int left = 1;
        for (int length = 1; length <= DeflateTables.MaxCodeLength; length++)
        {
            left = (left << 1) - count[length];
            if (left < 0)
            {
                problem = "a Huffman code that is not over-subscribed";
                Clear();
                return false;
            }
        }

        if (left > 0 && total != 0 && !(total == 1 && count[1] == 1))
        {
            problem = "a complete Huffman code";
            Clear();
            return false;
        }

        // Canonical first code of each length (RFC 1951 section 3.2.2, step 1).
        Span<int> next = stackalloc int[DeflateTables.MaxCodeLength + 2];
        int code = 0;
        for (int length = 1; length <= DeflateTables.MaxCodeLength; length++)
        {
            code = (code + count[length - 1]) << 1;
            next[length] = code;
        }

        // Size the table: the first level, plus a second-level table for every first-level prefix that has codes longer than it.
        // Only those prefixes are visited afterwards (a list of them is kept), so a block without long codes, or with few, does not
        // walk all of the first level. _subtableBits is all zero between builds.
        int primary = _primaryBits;
        int primarySize = 1 << primary;
        int longPrefixCount = 0;
        int size = primarySize;
        if (HasLongerThan(count, primary))
        {
            Span<int> nextForSizing = stackalloc int[DeflateTables.MaxCodeLength + 2];
            next.CopyTo(nextForSizing);
            for (int symbol = 0; symbol < codeLengths.Length; symbol++)
            {
                int length = codeLengths[symbol];
                if (length > primary)
                {
                    int reversed = ReverseBits(nextForSizing[length]++, length);
                    int prefix = reversed & (primarySize - 1);
                    int bits = length - primary;
                    if (_subtableBits[prefix] == 0)
                    {
                        _longPrefixes[longPrefixCount++] = prefix;
                    }

                    if (bits > _subtableBits[prefix])
                    {
                        _subtableBits[prefix] = (byte)bits;
                    }
                }
            }

            for (int k = 0; k < longPrefixCount; k++)
            {
                int prefix = _longPrefixes[k];
                _subtableOffset[prefix] = size;
                size += 1 << _subtableBits[prefix];
            }
        }

        if (size > _entries.Length)
        {
            _entries = new uint[size];
        }

        // A complete code gives every first-level index and every second-level slot an entry below; only an incomplete one (a lone
        // one-bit code, or no codes) leaves some to fill.
        if (left > 0)
        {
            Array.Fill(_entries, DeflateHuffmanEntry.Pack(DeflateSymbolKind.Invalid, 0, 0, 1), 0, size);
        }

        for (int k = 0; k < longPrefixCount; k++)
        {
            int prefix = _longPrefixes[k];
            _entries[prefix] = DeflateHuffmanEntry.Pack(DeflateSymbolKind.Link, _subtableOffset[prefix], _subtableBits[prefix], primary);
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            int length = codeLengths[symbol];
            if (length == 0)
            {
                continue;
            }

            uint entry = MakeEntry(symbol, length);
            int reversed = ReverseBits(next[length]++, length);
            if (length <= primary)
            {
                for (int index = reversed; index < primarySize; index += 1 << length)
                {
                    _entries[index] = entry;
                }
            }
            else
            {
                int prefix = reversed & (primarySize - 1);
                int bits = _subtableBits[prefix];
                int offset = _subtableOffset[prefix];
                for (int index = reversed >> primary; index < 1 << bits; index += 1 << (length - primary))
                {
                    _entries[offset + index] = entry;
                }
            }
        }

        for (int k = 0; k < longPrefixCount; k++)
        {
            _subtableBits[_longPrefixes[k]] = 0;
        }

        problem = null;
        BuildFast(size);
        return true;
    }

    // RFC 1951 3.2.2 codes are prefix codes, so when the first literal's code is l1 bits long and the next literal's code is no
    // longer than the (primary - l1) first-level bits that follow it, the first-level index alone decides both symbols.
    private void BuildFast(int size)
    {
        if (Alphabet != DeflateAlphabet.LiteralLength || _deflate64)
        {
            return;
        }

        if (_fast.Length < size)
        {
            _fast = new uint[_entries.Length];
        }

        // The fast table's kind bits are one flag per kind (see DeflateFastLoop): literal 8, length 4, link 2, end of block 1.
        const uint LiteralBits = 8u << DeflateHuffmanEntry.KindShift;
        int primary = _primaryBits;
        for (int i = 0; i < size; i++)
        {
            uint e = _entries[i];
            if (((e >> DeflateHuffmanEntry.KindShift) & 15) == (uint)DeflateSymbolKind.Literal)
            {
                int first = (int)(e & 0xFF);
                e = (e & 0xFFFF0000u) | LiteralBits | (1u << DeflateHuffmanEntry.ExtraShift) | (uint)first;
                if (i < (1 << primary) && first < primary)
                {
                    uint next = _entries[i >> first];
                    int second = (int)(next & 0xFF);
                    if (((next >> DeflateHuffmanEntry.KindShift) & 15) == (uint)DeflateSymbolKind.Literal && second <= primary - first)
                    {
                        e = ((e >> 16) | ((next >> 16) << 8)) << 16 | LiteralBits | (2u << DeflateHuffmanEntry.ExtraShift) | (uint)(first + second);
                    }
                }
            }

            if (((e >> DeflateHuffmanEntry.KindShift) & 15) != 8)
            {
                // Invalid 0, literal 8, length 4, end of block 1, link 2 (one nibble per DeflateSymbolKind value, 0 to 6).
                uint flag = (0x0201480u >> (int)(4 * ((e >> DeflateHuffmanEntry.KindShift) & 15))) & 15;
                e = (e & ~(15u << DeflateHuffmanEntry.KindShift)) | (flag << DeflateHuffmanEntry.KindShift);
            }

            _fast[i] = e;
        }
    }

    private static bool HasLongerThan(ReadOnlySpan<int> count, int length)
    {
        for (int i = length + 1; i < count.Length; i++)
        {
            if (count[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private void Clear() => Array.Fill(_entries, DeflateHuffmanEntry.Pack(DeflateSymbolKind.Invalid, 0, 0, 1), 0, 1 << _primaryBits);

    private uint MakeEntry(int symbol, int length)
    {
        switch (Alphabet)
        {
            case DeflateAlphabet.LiteralLength:
                if (symbol < 256)
                {
                    return DeflateHuffmanEntry.Pack(DeflateSymbolKind.Literal, symbol, 0, length);
                }

                if (symbol == DeflateTables.EndOfBlockSymbol)
                {
                    return DeflateHuffmanEntry.Pack(DeflateSymbolKind.EndOfBlock, 0, 0, length);
                }

                if (_deflate64 && symbol == 285)
                {
                    return DeflateHuffmanEntry.Pack(DeflateSymbolKind.Length, Deflate64.Deflate64Tables.LengthBase[28], Deflate64.Deflate64Tables.LengthExtraBits[28], length);
                }

                if (symbol <= 285)
                {
                    return DeflateHuffmanEntry.Pack(DeflateSymbolKind.Length, DeflateTables.LengthBase[symbol - 257], DeflateTables.LengthExtraBits[symbol - 257], length);
                }

                return DeflateHuffmanEntry.Pack(DeflateSymbolKind.Invalid, 0, 0, length);
            case DeflateAlphabet.Distance:
                if (_deflate64 && symbol < Deflate64.Deflate64Tables.DistanceSymbolCount)
                {
                    return DeflateHuffmanEntry.Pack(DeflateSymbolKind.Distance, Deflate64.Deflate64Tables.DistanceBase[symbol], Deflate64.Deflate64Tables.DistanceExtraBits[symbol], length);
                }

                return symbol < 30
                    ? DeflateHuffmanEntry.Pack(DeflateSymbolKind.Distance, DeflateTables.DistanceBase[symbol], DeflateTables.DistanceExtraBits[symbol], length)
                    : DeflateHuffmanEntry.Pack(DeflateSymbolKind.Invalid, 0, 0, length);
            default:
                return DeflateHuffmanEntry.Pack(DeflateSymbolKind.CodeLengthSymbol, symbol, 0, length);
        }
    }

    /// <summary>
    /// Looks up the code at the start of <paramref name="bits"/> (the next input bits, first bit least significant), resolving
    /// second-level tables. Only the first <see cref="DeflateHuffmanEntry.CodeLength"/> bits of <paramref name="bits"/> matter;
    /// bits past the end of the input must be supplied as zeros, and the caller checks that the code length does not exceed the bits it really has.
    /// </summary>
    /// <param name="bits">At least 15 bits of input, or fewer with the rest zero.</param>
    /// <returns>The entry; never of kind <see cref="DeflateSymbolKind.Link"/>.</returns>
    public DeflateHuffmanEntry Lookup(uint bits)
    {
        uint e = _entries[bits & PrimaryMask];
        if (((e >> DeflateHuffmanEntry.KindShift) & 15) == (uint)DeflateSymbolKind.Link)
        {
            int extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
            e = _entries[(int)(e >> DeflateHuffmanEntry.ValueShift) + (int)((bits >> _primaryBits) & ((1u << extra) - 1))];
        }

        return new DeflateHuffmanEntry(e);
    }

    /// <summary>
    /// Assigns canonical codes (RFC 1951 section 3.2.2) to code lengths, in the form an encoder writes: each code's bits are
    /// reversed, so the code goes out least significant bit first like every other Deflate field.
    /// </summary>
    /// <param name="codeLengths">One length from 0 to 15 per symbol. The lengths are not checked for completeness.</param>
    /// <param name="codes">Receives one code per symbol; unused symbols get 0. At least as long as <paramref name="codeLengths"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="codes"/> is too short, or a length is above 15.</exception>
    public static void GetCanonicalCodes(ReadOnlySpan<byte> codeLengths, Span<ushort> codes)
    {
        if (codes.Length < codeLengths.Length)
        {
            throw new ArgumentException("The codes span is shorter than the lengths.", nameof(codes));
        }

        Span<int> count = stackalloc int[DeflateTables.MaxCodeLength + 1];
        foreach (byte length in codeLengths)
        {
            if (length > DeflateTables.MaxCodeLength)
            {
                throw new ArgumentException("A code length is above 15.", nameof(codeLengths));
            }

            count[length]++;
        }

        count[0] = 0;
        Span<int> next = stackalloc int[DeflateTables.MaxCodeLength + 2];
        int code = 0;
        for (int length = 1; length <= DeflateTables.MaxCodeLength; length++)
        {
            code = (code + count[length - 1]) << 1;
            next[length] = code;
        }

        for (int symbol = 0; symbol < codeLengths.Length; symbol++)
        {
            int length = codeLengths[symbol];
            codes[symbol] = length == 0 ? (ushort)0 : (ushort)ReverseBits(next[length]++, length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReverseBits(int code, int length)
    {
        // Reverse 16 bits with three mask-and-shift steps per half, then drop the unused low end.
        uint v = (uint)code;
        v = ((v >> 1) & 0x5555) | ((v & 0x5555) << 1);
        v = ((v >> 2) & 0x3333) | ((v & 0x3333) << 2);
        v = ((v >> 4) & 0x0F0F) | ((v & 0x0F0F) << 4);
        v = ((v >> 8) & 0x00FF) | ((v & 0x00FF) << 8);
        return (int)(v >> (16 - length));
    }
}
