using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// Reads the bit-packed fields of a Deflate stream (RFC 1951 section 3.1.1): bytes are consumed in order and bits are
/// taken from the least significant end of each byte first. Multi-bit values are read least significant bit first;
/// Huffman codes are matched through <see cref="DeflateHuffmanTable"/>, which expects exactly this order.
/// </summary>
/// <remarks>
/// <para>The reader never reads past the end of its span. Methods that may run out of input return false and leave
/// every bit they could load in the reader, so the caller can continue with more input: the reader's state is
/// <see cref="Position"/>, <see cref="BitBuffer"/> and <see cref="BitCount"/>, and a new reader created from them over the next
/// bytes carries on exactly. The reader loads a byte only when it needs more bits than it holds (the fast
/// <see cref="Refill"/> excepted), so between fields it holds fewer than 8 bits and <see cref="AlignToByte"/> never
/// discards a whole byte.</para>
/// </remarks>
[Experimental("ONECOMPRESS003", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public ref struct DeflateBitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _position;
    private ulong _bits;
    private int _count;

    /// <summary>Starts reading <paramref name="source"/>.</summary>
    /// <param name="source">The bytes to read.</param>
    /// <param name="bitBuffer">Bits carried over from an earlier reader: the next bit is the least significant one.</param>
    /// <param name="bitCount">The number of valid bits in <paramref name="bitBuffer"/>, 0 to 63; bits above must be zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitCount"/> is outside 0 to 63.</exception>
    public DeflateBitReader(ReadOnlySpan<byte> source, ulong bitBuffer = 0, int bitCount = 0)
    {
        if ((uint)bitCount > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitCount));
        }

        _source = source;
        _position = 0;
        _bits = bitBuffer;
        _count = bitCount;
    }

    /// <summary>Gets the number of bytes of the source taken into the bit buffer so far.</summary>
    public readonly int Position => _position;

    /// <summary>Gets the number of source bytes not yet taken into the bit buffer.</summary>
    public readonly int Remaining => _source.Length - _position;

    /// <summary>Gets the bits loaded and not yet consumed; the next bit is the least significant. Bits above <see cref="BitCount"/> are zero after any method except <see cref="Refill"/>.</summary>
    public readonly ulong BitBuffer => _bits;

    /// <summary>Gets the number of valid bits in <see cref="BitBuffer"/>.</summary>
    public readonly int BitCount => _count;

    internal ReadOnlySpan<byte> Source => _source;

    internal void Set(int position, ulong bits, int count)
    {
        _position = position;
        _bits = bits;
        _count = count;
    }

    /// <summary>
    /// Tops the buffer up to between 56 and 63 bits with one 8-byte load, when at least 8 source bytes remain; otherwise loads the remaining
    /// bytes one at a time (so possibly fewer bits). After this call bits above <see cref="BitCount"/> may hold the following bits
    /// of the input instead of zeros; <see cref="TrimBuffer"/> clears them.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Refill()
    {
        if (_source.Length - _position >= 8)
        {
            _bits |= BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(_position, 8)) << _count;
            _position += (63 - _count) >> 3;
            _count |= 56;
        }
        else
        {
            while (_count <= 56 && _position < _source.Length)
            {
                _bits |= (ulong)_source[_position++] << _count;
                _count += 8;
            }
        }
    }

    /// <summary>Clears the bits above <see cref="BitCount"/>.</summary>
    public void TrimBuffer() => _bits &= (1UL << _count) - 1;

    /// <summary>Gives back the whole bytes held in the buffer, as if they had not been loaded, and keeps the partial byte's bits.</summary>
    /// <returns>The number of bytes given back.</returns>
    public int ReturnWholeBytes()
    {
        int bytes = _count >> 3;
        _position -= bytes;
        _count &= 7;
        _bits &= (1UL << _count) - 1;
        return bytes;
    }

    /// <summary>Loads source bytes, one at a time, until the buffer holds at least <paramref name="bitCount"/> bits.</summary>
    /// <param name="bitCount">0 to 56.</param>
    /// <returns>True when the buffer holds that many bits; false when the source ran out first (everything loaded stays in the buffer).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnsure(int bitCount)
    {
        while (_count < bitCount)
        {
            if (_position >= _source.Length)
            {
                return false;
            }

            _bits |= (ulong)_source[_position++] << _count;
            _count += 8;
        }

        return true;
    }

    /// <summary>Reads a value of <paramref name="bitCount"/> bits, least significant bit first (RFC 1951 section 3.1.1).</summary>
    /// <param name="bitCount">0 to 32.</param>
    /// <param name="value">The value, when enough input is left.</param>
    /// <returns>False, consuming nothing, when fewer than <paramref name="bitCount"/> bits remain.</returns>
    public bool TryReadBits(int bitCount, out uint value)
    {
        if (!TryEnsure(bitCount))
        {
            value = 0;
            return false;
        }

        value = (uint)(_bits & ((1UL << bitCount) - 1));
        _bits >>= bitCount;
        _count -= bitCount;
        return true;
    }

    /// <summary>Returns the next <paramref name="bitCount"/> buffered bits without consuming them. The caller makes sure the buffer holds them.</summary>
    /// <param name="bitCount">0 to 32.</param>
    /// <returns>The bits, first bit least significant.</returns>
    public readonly uint PeekBits(int bitCount) => (uint)(_bits & ((1UL << bitCount) - 1));

    /// <summary>Consumes <paramref name="bitCount"/> buffered bits. The caller makes sure the buffer holds them.</summary>
    /// <param name="bitCount">0 to <see cref="BitCount"/>.</param>
    public void DropBits(int bitCount)
    {
        _bits >>= bitCount;
        _count -= bitCount;
    }

    /// <summary>Discards the bits up to the next byte boundary (RFC 1951 section 3.2.4: before a stored block's length).</summary>
    public void AlignToByte() => DropBits(_count & 7);

    /// <summary>
    /// Decodes the next Huffman code without consuming it. Loads source bytes only as far as needed to know the code, so a code
    /// shorter than the buffered bits resolves without touching the source.
    /// </summary>
    /// <param name="table">A built table.</param>
    /// <param name="entry">The entry; its <see cref="DeflateHuffmanEntry.CodeLength"/> bits are what <see cref="DropBits"/> consumes. Never of kind <see cref="DeflateSymbolKind.Link"/>.</param>
    /// <returns>False when the source ran out before the code could be resolved.</returns>
    public bool TryPeekSymbol(DeflateHuffmanTable table, out DeflateHuffmanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(table);
        uint[] entries = table.Entries;
        while (true)
        {
            uint e = entries[(int)(_bits & table.PrimaryMask)];
            uint kind = (e >> DeflateHuffmanEntry.KindShift) & 15;
            if (kind == (uint)DeflateSymbolKind.Link)
            {
                int primary = table.PrimaryBits;
                if (_count >= primary)
                {
                    int extra = (int)((e >> DeflateHuffmanEntry.ExtraShift) & 15);
                    e = entries[(int)(e >> DeflateHuffmanEntry.ValueShift) + (int)((_bits >> primary) & ((1u << extra) - 1))];
                    if ((int)((e >> DeflateHuffmanEntry.ExtraShift) & 15) <= _count)
                    {
                        entry = new DeflateHuffmanEntry(e);
                        return true;
                    }
                }
            }
            else if ((int)((e >> DeflateHuffmanEntry.ExtraShift) & 15) <= _count)
            {
                entry = new DeflateHuffmanEntry(e);
                return true;
            }

            if (_position >= _source.Length)
            {
                entry = default;
                return false;
            }

            _bits |= (ulong)_source[_position++] << _count;
            _count += 8;
        }
    }
}
