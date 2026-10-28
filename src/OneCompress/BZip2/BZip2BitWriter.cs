using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// Writes bits most significant bit first, the order bzip2 uses, into a growing byte buffer. Complete bytes are available through
/// <see cref="Bytes"/>; the bits that do not yet fill a byte wait in the writer.
/// </summary>
[Experimental("ONECOMPRESS004", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
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
