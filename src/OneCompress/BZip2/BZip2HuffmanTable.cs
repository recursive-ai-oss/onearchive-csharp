
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// One of the (two to six) canonical prefix codes of a bzip2 block. Codes are read most significant bit first. Lengths run from
/// 1 to 20 bits. Codes are assigned in order of length, and within a length in order of symbol, each length starting at twice the
/// first code after the previous length's last code. A table must not be oversubscribed (Kraft sum above 1); a table with a Kraft
/// sum below 1 is accepted, and the unassigned codes are invalid when they are met.
/// </summary>
public sealed class BZip2HuffmanTable
{
    /// <summary>The longest code length (20 bits).</summary>
    public const int MaxCodeLength = 20;

    /// <summary>The largest alphabet: RUNA, RUNB, 255 move-to-front positions and the end-of-block symbol.</summary>
    public const int MaxAlphabetSize = 258;

    /// <summary>The smallest alphabet: RUNA, RUNB and the end-of-block symbol, for a block with one distinct byte.</summary>
    public const int MinAlphabetSize = 3;

    internal const int FastBits = 10;

    private readonly byte[] _lengths = new byte[MaxAlphabetSize];
    private readonly uint[] _codes = new uint[MaxAlphabetSize];
    private readonly ushort[] _symbolsByCode = new ushort[MaxAlphabetSize];
    private readonly ushort[] _fast = new ushort[1 << FastBits];
    private readonly uint[] _limit = new uint[MaxCodeLength + 2];     // one past the last code of each length
    private readonly uint[] _first = new uint[MaxCodeLength + 2];     // first code of each length
    private readonly int[] _offset = new int[MaxCodeLength + 2];      // index in _symbolsByCode of each length's first symbol
    private int _alphabetSize;
    private int _maxLength;

    /// <summary>Gets the number of symbols in the table.</summary>
    public int AlphabetSize => _alphabetSize;

    /// <summary>Gets the length of the longest code.</summary>
    public int MaxLength => _maxLength;

    /// <summary>Builds the table from code lengths, one per symbol.</summary>
    /// <param name="lengths">Between <see cref="MinAlphabetSize"/> and <see cref="MaxAlphabetSize"/> lengths, each 1 to 20.</param>
    /// <returns>True when the table was built; false when a length is out of range or the lengths oversubscribe the code space
    /// (the table is then unusable until built again).</returns>
    /// <exception cref="ArgumentException">There are too few or too many lengths.</exception>
    public bool TryBuild(ReadOnlySpan<byte> lengths)
    {
        if (lengths.Length is < MinAlphabetSize or > MaxAlphabetSize)
        {
            throw new ArgumentException($"A bzip2 alphabet has {MinAlphabetSize} to {MaxAlphabetSize} symbols.", nameof(lengths));
        }

        _alphabetSize = 0;
        Span<int> count = stackalloc int[MaxCodeLength + 2];
        int max = 0;
        foreach (byte l in lengths)
        {
            if (l is < 1 or > MaxCodeLength)
            {
                return false;
            }

            count[l]++;
            max = Math.Max(max, l);
        }

        long space = 1L << MaxCodeLength;
        for (int l = 1; l <= MaxCodeLength; l++)
        {
            space -= (long)count[l] << (MaxCodeLength - l);
        }

        if (space < 0)
        {
            return false;
        }

        lengths.CopyTo(_lengths);
        _alphabetSize = lengths.Length;
        _maxLength = max;

        // Symbols by (length, symbol), the first code and the end of each length's codes.
        Span<int> next = stackalloc int[MaxCodeLength + 2];
        int index = 0;
        uint code = 0;
        for (int l = 1; l <= MaxCodeLength; l++)
        {
            _first[l] = code;
            _offset[l] = index;
            next[l] = index;
            index += count[l];
            code += (uint)count[l];
            _limit[l] = code;
            code <<= 1;
        }

        for (int s = 0; s < lengths.Length; s++)
        {
            int l = lengths[s];
            int at = next[l]++;
            _symbolsByCode[at] = (ushort)s;
            _codes[s] = _first[l] + (uint)(at - _offset[l]);
        }

        Array.Clear(_fast);
        for (int s = 0; s < lengths.Length; s++)
        {
            int l = lengths[s];
            if (l <= FastBits)
            {
                int start = (int)(_codes[s] << (FastBits - l));
                int span = 1 << (FastBits - l);
                ushort entry = (ushort)((s << 5) | l);
                _fast.AsSpan(start, span).Fill(entry);
            }
        }

        return true;
    }

    /// <summary>Builds the table from code lengths, one per symbol.</summary>
    /// <param name="lengths">Between <see cref="MinAlphabetSize"/> and <see cref="MaxAlphabetSize"/> lengths, each 1 to 20.</param>
    /// <exception cref="ArgumentException">A length is out of range, the lengths oversubscribe the code space, or there are too few or too many.</exception>
    public void Build(ReadOnlySpan<byte> lengths)
    {
        if (!TryBuild(lengths))
        {
            throw new ArgumentException("The code lengths are outside 1 to 20 or oversubscribe the code space.", nameof(lengths));
        }
    }

    /// <summary>Gets the code length of a symbol.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The length in bits.</returns>
    public int GetLength(int symbol) => _lengths[CheckSymbol(symbol)];

    /// <summary>Gets the code of a symbol, to be written most significant bit first in <see cref="GetLength"/> bits.</summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns>The code.</returns>
    public uint GetCode(int symbol) => _codes[CheckSymbol(symbol)];

    private int CheckSymbol(int symbol)
    {
        if ((uint)symbol >= (uint)_alphabetSize)
        {
            throw new ArgumentOutOfRangeException(nameof(symbol));
        }

        return symbol;
    }

    /// <summary>Decodes one symbol from the next bits of the input.</summary>
    /// <param name="bits">The next 20 bits of the input, first bit in bit 19; bits beyond the end of the data may be anything.</param>
    /// <param name="length">The number of bits the symbol's code used.</param>
    /// <returns>The symbol, or -1 when the bits start with an unassigned code.</returns>
    public int Decode(uint bits, out int length)
    {
        ushort entry = _fast[(bits >> (MaxCodeLength - FastBits)) & ((1 << FastBits) - 1)];
        if (entry != 0)
        {
            length = entry & 31;
            return entry >> 5;
        }

        return DecodeLong(bits, out length);
    }

    // Codes longer than FastBits bits, and invalid prefixes.
    internal int DecodeLong(uint bits, out int length)
    {
        for (int l = FastBits + 1; l <= _maxLength; l++)
        {
            uint c = (bits & ((1u << MaxCodeLength) - 1)) >> (MaxCodeLength - l);
            if (c < _limit[l])
            {
                length = l;
                return _symbolsByCode[_offset[l] + (int)(c - _first[l])];
            }
        }

        length = 0;
        return -1;
    }

    internal ushort[] FastTable => _fast;
}
