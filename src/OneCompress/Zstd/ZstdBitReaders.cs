using System.Diagnostics.CodeAnalysis;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Reads a Zstandard backward bitstream (RFC 8878 §4.1 and §4.2.2): bits are written forward, the last
/// byte holds a final <c>1</c> marker bit above the padding, and the decoder starts at that marker and
/// reads toward the beginning of the data. Within each read the bits form a little-endian value, so the
/// first bit read is the most significant bit of the result.
/// </summary>
/// <remarks>
/// Reading past the beginning of the data yields zero bits and sets <see cref="IsOverflowed"/>; it never
/// reads outside the span. A stream that was decoded correctly ends with <see cref="IsFinished"/> true.
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public ref struct ZstdReverseBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private ulong _container; // eight bytes of _data starting at _ptr, little-endian; the next bit is the top bit after _consumed
    private int _consumed;    // bits already taken from the top of _container
    private int _ptr;         // index in _data of the lowest byte of _container
    private readonly int _endBits; // value of _consumed when everything down to the first bit is used and _ptr is 0

    /// <summary>Starts reading a backward bitstream.</summary>
    /// <param name="data">The bytes of the stream, ending with the byte that holds the final marker bit.</param>
    /// <param name="streamOffset">Offset of <paramref name="data"/> in the compressed input, for error messages.</param>
    /// <exception cref="CompressionFormatException">The data is empty or its last byte is zero (no marker bit).</exception>
    public ZstdReverseBitReader(ReadOnlySpan<byte> data, long streamOffset = 0)
    {
        if (data.Length == 0)
        {
            ZstdFormat.ThrowTruncated(streamOffset, "a bitstream of at least one byte");
        }

        byte last = data[^1];
        if (last == 0)
        {
            ZstdFormat.ThrowCorrupt(streamOffset + data.Length - 1, "a final byte with a marker bit set");
        }

        _data = data;
        // The marker bit and the zero padding above it are skipped (RFC 8878 §4.1: "skip 0-7 initial 0-bits and the first 1-bit").
        _consumed = 8 - BitOperations.Log2(last);
        if (data.Length >= 8)
        {
            _ptr = data.Length - 8;
            _container = BinaryPrimitives.ReadUInt64LittleEndian(data[_ptr..]);
            _endBits = 64;
        }
        else
        {
            ulong c = 0;
            for (int i = 0; i < data.Length; i++)
            {
                c |= (ulong)data[i] << (8 * i);
            }

            _ptr = 0;
            _container = c << (8 * (8 - data.Length));
            _endBits = 8 * data.Length;
        }
    }

    /// <summary>Gets a value indicating whether every bit of the stream, down to the first, has been read.</summary>
    public readonly bool IsFinished => _ptr == 0 && _consumed == _endBits;

    /// <summary>Gets a value indicating whether a read went beyond the first bit of the stream.</summary>
    public readonly bool IsOverflowed => _ptr == 0 && _consumed > _endBits;

    /// <summary>Gets the number of bits not yet read.</summary>
    public readonly long BitsRemaining => ((long)_ptr * 8) + _endBits - _consumed;

    /// <summary>Gets the byte index of the register's lowest byte; the fast loops keep it in a local variable.</summary>
    internal readonly int Pointer => _ptr;

    /// <summary>Gets the register, for the fast loops that keep it in a local variable.</summary>
    internal readonly ulong Container => _container;

    /// <summary>Gets the number of bits already taken from the top of the register.</summary>
    internal readonly int Consumed => _consumed;

    /// <summary>Puts back the state a fast loop kept in local variables. The values must come from this reader's own refill rules.</summary>
    internal void Restore(ulong container, int consumed, int pointer)
    {
        _container = container;
        _consumed = consumed;
        _ptr = pointer;
    }

    /// <summary>Refills the internal register so that at least 56 bits can be read without another refill.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Reload()
    {
        if (_ptr == 0)
        {
            return;
        }

        int bytes = _consumed >> 3;
        if (_ptr >= bytes)
        {
            _ptr -= bytes;
            _consumed &= 7;
        }
        else
        {
            _consumed -= _ptr << 3;
            _ptr = 0;
        }

        // _ptr + 8 <= _data.Length: _ptr starts at Length - 8 and only decreases (the stream is at least 8 bytes when _ptr > 0).
        _container = Raw.ReadUInt64(_data, _ptr);
    }

    /// <summary>Reads <paramref name="count"/> bits (0 to 32) and returns them as an integer.</summary>
    /// <param name="count">The number of bits.</param>
    /// <returns>The bits; zeros for any that lie beyond the start of the stream (see <see cref="IsOverflowed"/>).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not in 0 to 32.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadBits(int count)
    {
        if ((uint)count > 32)
        {
            ThrowCount();
        }

        if (_consumed > 32)
        {
            Reload();
        }

        return ReadBitsNoReload(count);
    }

    /// <summary>Reads bits without refilling first. The caller must have called <see cref="Reload"/> since the last 56 bits.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint ReadBitsNoReload(int count)
    {
        // Two shifts so that count == 0 gives 0 (a shift by 64 would be a shift by 0).
        uint value = (uint)(((_container << (_consumed & 63)) >> 1) >> (63 - count));
        _consumed += count;
        return value;
    }

    /// <summary>Returns the next <paramref name="count"/> bits (1 to 32) without consuming them. A refill is done first if needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint PeekBitsNoReload(int count) => (uint)((_container << (_consumed & 63)) >> (64 - count));

    /// <summary>Consumes bits that were looked at with <see cref="PeekBitsNoReload"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Skip(int count) => _consumed += count;

    private static void ThrowCount() => throw new ArgumentOutOfRangeException("count", "The bit count must be between 0 and 32.");
}

/// <summary>
/// Reads a forward little-endian bitstream, as used by FSE table descriptions (RFC 8878 §4.1.1): bits are
/// taken from the least significant end of each byte first.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public ref struct ZstdForwardBitReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPosition;

    /// <summary>Starts reading at <paramref name="bitPosition"/> bits into <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to read.</param>
    /// <param name="bitPosition">The first bit to read, counted from the least significant bit of the first byte.</param>
    public ZstdForwardBitReader(ReadOnlySpan<byte> data, int bitPosition = 0)
    {
        _data = data;
        _bitPosition = bitPosition;
    }

    /// <summary>Gets the position of the next bit.</summary>
    public readonly int BitPosition => _bitPosition;

    /// <summary>Gets the number of bytes touched so far, rounding a partly used byte up.</summary>
    public readonly int BytesConsumed => (_bitPosition + 7) >> 3;

    /// <summary>Gets a value indicating whether more bits were consumed than the data holds.</summary>
    public readonly bool IsOverflowed => _bitPosition > _data.Length * 8;

    /// <summary>Returns the next <paramref name="count"/> bits (0 to 32) without consuming them; bits past the end are zero.</summary>
    /// <param name="count">The number of bits.</param>
    public readonly uint Peek(int count)
    {
        int index = _bitPosition >> 3;
        ulong word = 0;
        if (index < _data.Length)
        {
            ReadOnlySpan<byte> rest = _data[index..];
            if (rest.Length >= 8)
            {
                word = BinaryPrimitives.ReadUInt64LittleEndian(rest);
            }
            else
            {
                for (int i = 0; i < rest.Length; i++)
                {
                    word |= (ulong)rest[i] << (8 * i);
                }
            }
        }

        word >>= _bitPosition & 7;
        return (uint)(word & ((1UL << count) - 1));
    }

    /// <summary>Consumes <paramref name="count"/> bits.</summary>
    /// <param name="count">The number of bits.</param>
    public void Skip(int count) => _bitPosition += count;

    /// <summary>Reads <paramref name="count"/> bits (0 to 32).</summary>
    /// <param name="count">The number of bits.</param>
    /// <returns>The bits as an integer; bits past the end are zero (see <see cref="IsOverflowed"/>).</returns>
    public uint Read(int count)
    {
        uint value = Peek(count);
        _bitPosition += count;
        return value;
    }
}
