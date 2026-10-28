using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Lzx;

/// <summary>
/// Reads the LZX bitstream ([MS-PATCH] 2.1.1): little-endian 16-bit words whose bits are consumed from the most significant end.
/// Past the end of the input the reader supplies zero bits, so a code can be peeked at the very end of a frame, but it raises
/// <see cref="CompressionFormatException"/> as soon as a bit that was never in the input is consumed.
/// </summary>
internal ref struct LzxBitReader
{
    private readonly ReadOnlySpan<byte> _source;
    private readonly long _baseOffset;
    private int _position;
    private ulong _buffer;
    private int _count;
    private int _phantom;

    public LzxBitReader(ReadOnlySpan<byte> source, long baseOffset)
    {
        _source = source;
        _baseOffset = baseOffset;
        _position = 0;
        _buffer = 0;
        _count = 0;
        _phantom = 0;
    }

    /// <summary>Gets the number of buffered bits (real ones; zero if the reader is past the end).</summary>
    public readonly int RealBitCount => Math.Max(0, _count - _phantom);

    /// <summary>Gets the stream offset of the next unread byte of the source, counting buffered bits as unread.</summary>
    public readonly long StreamOffset => _baseOffset + Math.Min(_source.Length, _position - (RealBitCount >> 3));

    public readonly long BaseOffset => _baseOffset;

    public readonly ReadOnlySpan<byte> Source => _source;

    /// <summary>Tops the buffer up to at least 49 bits (zero bits stand in beyond the end of the input).</summary>
    public void Refill()
    {
        while (_count <= 48)
        {
            ulong word;
            if (_position + 1 < _source.Length)
            {
                word = BinaryPrimitives.ReadUInt16LittleEndian(_source.Slice(_position, 2));
                _position += 2;
            }
            else
            {
                word = 0;
                _phantom += 16;
            }

            _buffer |= word << (48 - _count);
            _count += 16;
        }

        if (_phantom > _count)
        {
            throw Truncated();
        }
    }

    /// <summary>Returns the next <paramref name="n"/> bits (1 to 32) without consuming them.</summary>
    public readonly uint Peek(int n) => (uint)(_buffer >> (64 - n));

    /// <summary>Consumes <paramref name="n"/> bits that were peeked.</summary>
    public void Skip(int n)
    {
        _buffer <<= n;
        _count -= n;
    }

    /// <summary>Reads <paramref name="n"/> bits (0 to 32).</summary>
    public uint ReadBits(int n)
    {
        if (n == 0)
        {
            return 0;
        }

        if (_count < n + 16)
        {
            Refill();
        }

        uint v = Peek(n);
        Skip(n);
        return v;
    }

    /// <summary>
    /// Discards bits up to the next 16-bit boundary of the input. When the reader already sits on a boundary and
    /// <paramref name="fullWordIfAligned"/> is set, a whole word is discarded.
    /// </summary>
    public void AlignToWord(bool fullWordIfAligned)
    {
        Refill();
        int real = _count - _phantom;
        int drop = real & 15;
        if (drop == 0 && fullWordIfAligned)
        {
            drop = 16;
        }

        if (drop > real)
        {
            throw Truncated();
        }

        Skip(drop);
    }

    /// <summary>Gives back the buffered whole words and returns the byte offset in the source of the first unread byte. The reader must be word-aligned.</summary>
    public int TakeBytePosition()
    {
        int real = RealBitCount;
        int pos = _position - (real >> 3);
        _position = pos;
        _buffer = 0;
        _count = 0;
        _phantom = 0;
        return pos;
    }

    /// <summary>Moves the read position to <paramref name="position"/> after bytes were taken directly from the source.</summary>
    public void SetBytePosition(int position)
    {
        _position = position;
        _buffer = 0;
        _count = 0;
        _phantom = 0;
    }

    /// <summary>Checks that no bit beyond the input was consumed and returns the byte offset of the first unread word.</summary>
    public int FinishFrame()
    {
        if (_phantom > _count)
        {
            throw Truncated();
        }

        return TakeBytePosition();
    }

    public readonly CompressionFormatException Truncated() => new("lzx", _baseOffset + _source.Length, "more compressed data for this frame", isTruncated: true);

    public readonly CompressionFormatException Invalid(string expected) => new("lzx", StreamOffset, expected);
}
