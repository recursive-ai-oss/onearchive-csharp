
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Writes bits most significant bit first, the order bzip2 uses, into a growing byte buffer. Complete bytes are available through
/// <see cref="Bytes"/>; the bits that do not yet fill a byte wait in the writer.
/// </summary>
public sealed class BZip2BitWriter
{
    private byte[] _buffer = new byte[4096];
    private int _length;
    private ulong _accumulator;
    private int _bitCount;

    /// <summary>Gets the complete bytes written and not yet discarded.</summary>
    public ReadOnlySpan<byte> Bytes => _buffer.AsSpan(0, _length);

    /// <summary>Gets the number of bits waiting for the rest of their byte (0 to 7).</summary>
    public int PendingBits => _bitCount;

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, most significant of them first.</summary>
    /// <param name="value">The bits; anything above <paramref name="count"/> bits is ignored.</param>
    /// <param name="count">The number of bits, 0 to 32.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 0 to 32.</exception>
    public void Write(uint value, int count)
    {
        if ((uint)count > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (_length + 8 > _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        ulong mask = (1UL << count) - 1;
        _accumulator = (_accumulator << count) | (value & mask);
        _bitCount += count;
        while (_bitCount >= 8)
        {
            _bitCount -= 8;
            _buffer[_length++] = (byte)(_accumulator >> _bitCount);
        }
    }

    /// <summary>
    /// Writes a block's Huffman-coded symbols: group <c>g</c> (every <paramref name="groupSize"/> symbols) uses the entries of table <c>selectors[g]</c>.
    /// An entry is <c>(code &lt;&lt; 5) | length</c>, and the tables lie <paramref name="stride"/> entries apart. The bits are the same as one
    /// <see cref="Write"/> per symbol; they are gathered in a 64-bit register and stored four bytes at a time.
    /// </summary>
    internal void WriteSymbols(ReadOnlySpan<ushort> symbols, ReadOnlySpan<byte> selectors, ReadOnlySpan<uint> entries, int stride, int groupSize)
    {
        // A code is at most 20 bits, so a symbol adds at most three bytes, and a four-byte store may run past the last byte kept.
        int needed = _length + (symbols.Length * 3) + 8;
        if (needed > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));
        }

        Span<byte> buffer = _buffer;
        int length = _length;
        ulong accumulator = _accumulator;
        int bits = _bitCount;
        for (int g = 0; g < selectors.Length; g++)
        {
            int from = g * groupSize;
            ReadOnlySpan<uint> table = entries.Slice(selectors[g] * stride, stride);
            foreach (ushort symbol in symbols.Slice(from, Math.Min(groupSize, symbols.Length - from)))
            {
                uint entry = table[symbol];
                int count = (int)(entry & 31);
                accumulator = (accumulator << count) | (entry >> 5);
                bits += count;
                if (bits >= 32)
                {
                    bits -= 32;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer[length..], (uint)(accumulator >> bits));
                    length += 4;
                }
            }
        }

        while (bits >= 8)
        {
            bits -= 8;
            buffer[length++] = (byte)(accumulator >> bits);
        }

        _length = length;
        _accumulator = accumulator;
        _bitCount = bits;
    }

    /// <summary>Writes one bit.</summary>
    /// <param name="bit">True for 1.</param>
    public void WriteBit(bool bit) => Write(bit ? 1u : 0u, 1);

    /// <summary>Pads with zero bits up to the next byte boundary (nothing when already on one).</summary>
    public void AlignToByte()
    {
        if (_bitCount > 0)
        {
            Write(0, 8 - _bitCount);
        }
    }

    /// <summary>Removes the first <paramref name="count"/> complete bytes, which the caller has taken from <see cref="Bytes"/>.</summary>
    /// <param name="count">The number of bytes, 0 to the length of <see cref="Bytes"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside the available bytes.</exception>
    public void Discard(int count)
    {
        if ((uint)count > (uint)_length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == _length)
        {
            _length = 0;
        }
        else
        {
            _buffer.AsSpan(count, _length - count).CopyTo(_buffer);
            _length -= count;
        }
    }

    /// <summary>Forgets everything, bytes and pending bits.</summary>
    public void Reset()
    {
        _length = 0;
        _accumulator = 0;
        _bitCount = 0;
    }
}
