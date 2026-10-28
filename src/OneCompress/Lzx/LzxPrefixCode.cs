namespace RecursiveAI.OneCompress.Lzx;

/// <summary>
/// A canonical prefix code ([MS-PATCH] 2.4): codes of equal length are assigned in symbol order, shorter codes first. Decoding uses a
/// 10-bit lookup table for the common short codes and a per-length walk for the rest.
/// </summary>
internal sealed class LzxPrefixCode
{
    private const int FastBits = 10;
    private const int MaxLength = 16;

    private readonly ushort[] _fast = new ushort[1 << FastBits];
    private readonly int[] _first = new int[MaxLength + 2];
    private readonly int[] _count = new int[MaxLength + 2];
    private readonly int[] _offset = new int[MaxLength + 2];
    private readonly ushort[] _sorted;

    public LzxPrefixCode(int maxSymbols)
    {
        _sorted = new ushort[maxSymbols];
    }

    /// <summary>
    /// Builds the code from the symbols' code lengths (0 = unused). A code that has more leaves than a tree can hold is rejected;
    /// an incomplete code is accepted and fails only when a bit pattern that no symbol owns is read.
    /// </summary>
    /// <returns>False if the lengths oversubscribe the code space.</returns>
    public bool Build(ReadOnlySpan<byte> lengths)
    {
        Span<int> count = _count.AsSpan();
        count.Clear();
        foreach (byte l in lengths)
        {
            count[l]++;
        }

        count[0] = 0;
        int code = 0;
        int index = 0;
        for (int len = 1; len <= MaxLength; len++)
        {
            _first[len] = code;
            _offset[len] = index;
            index += count[len];
            code += count[len];
            if (code > (1 << len))
            {
                return false;
            }

            code <<= 1;
        }

        Span<int> next = stackalloc int[MaxLength + 1];
        for (int len = 1; len <= MaxLength; len++)
        {
            next[len] = _offset[len];
        }

        Array.Clear(_fast);
        for (int sym = 0; sym < lengths.Length; sym++)
        {
            int len = lengths[sym];
            if (len == 0)
            {
                continue;
            }

            int slot = next[len]++;
            _sorted[slot] = (ushort)sym;
            if (len <= FastBits)
            {
                int c = _first[len] + (slot - _offset[len]);
                int start = c << (FastBits - len);
                int end = start + (1 << (FastBits - len));
                ushort entry = (ushort)((sym << 5) | len);
                for (int i = start; i < end; i++)
                {
                    _fast[i] = entry;
                }
            }
        }

        return true;
    }

    /// <summary>Decodes one symbol.</summary>
    public int Decode(ref LzxBitReader reader, string what)
    {
        reader.Refill();
        uint bits = reader.Peek(MaxLength);
        ushort entry = _fast[bits >> (MaxLength - FastBits)];
        if (entry != 0)
        {
            reader.Skip(entry & 31);
            return entry >> 5;
        }

        for (int len = FastBits + 1; len <= MaxLength; len++)
        {
            int c = (int)(bits >> (MaxLength - len));
            int rank = c - _first[len];
            if ((uint)rank < (uint)_count[len])
            {
                reader.Skip(len);
                return _sorted[_offset[len] + rank];
            }
        }

        throw reader.Invalid("a code that belongs to a symbol of the " + what);
    }
}
