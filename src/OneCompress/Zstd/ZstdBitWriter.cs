using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Writes a Zstandard bitstream (RFC 8878 §4.1): bits are packed least significant first, and a backward stream ends
/// with a single <c>1</c> marker bit so the decoder can find where the data stops. The destination needs 8 spare bytes
/// beyond what is written because bytes are stored a word at a time.
/// </summary>
internal ref struct ZstdBitWriter
{
    private readonly Span<byte> _destination;
    private ulong _container;
    private int _bitCount;
    private int _position;

    public ZstdBitWriter(Span<byte> destination)
    {
        _destination = destination;
        _container = 0;
        _bitCount = 0;
        _position = 0;
    }

    /// <summary>The number of whole bytes stored by <see cref="Flush"/> so far.</summary>
    public readonly int Position => _position;

    /// <summary>Adds the low <paramref name="count"/> bits of <paramref name="value"/>. The container holds 64 bits, 7 of which may be left over from the last flush.</summary>
    public void Add(uint value, int count)
    {
        _container |= (ulong)(value & (uint)((1UL << count) - 1)) << _bitCount;
        _bitCount += count;
    }

    /// <summary>Adds bits already known to have no higher bits set.</summary>
    public void AddMasked(uint value, int count)
    {
        _container |= (ulong)value << _bitCount;
        _bitCount += count;
    }

    /// <summary>Stores the complete bytes of the container.</summary>
    /// <summary>Hands out the writer's state so a hot loop can keep it in registers; give it back with <see cref="Restore"/>.</summary>
    public readonly void Save(out ulong container, out int bitCount, out int position)
    {
        container = _container;
        bitCount = _bitCount;
        position = _position;
    }

    /// <summary>Takes back the state handed out by <see cref="Save"/>.</summary>
    public void Restore(ulong container, int bitCount, int position)
    {
        _container = container;
        _bitCount = bitCount;
        _position = position;
    }

    /// <summary>Writes whole bytes of the container without checking that the destination has room for eight bytes at the position.</summary>
    public void FlushRaw()
    {
        // The caller keeps Position + 8 inside the destination (the block writer's slack covers the bytes past its size limit).
        Raw.WriteUInt64(_destination, _position, _container);
        int bytes = _bitCount >> 3;
        _position += bytes;
        _container >>= bytes << 3;
        _bitCount &= 7;
    }

    /// <summary>Writes whole bytes of the container to the destination and keeps the remaining bits.</summary>
    public void Flush()
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_destination[_position..], _container);
        int bytes = _bitCount >> 3;
        _position += bytes;
        _container >>= bytes << 3;
        _bitCount &= 7;
    }

    /// <summary>Adds the end marker and the padding, and returns the stream's length in bytes.</summary>
    public int CloseBackward()
    {
        Add(1, 1);
        Flush();
        if (_bitCount > 0)
        {
            _position++;
            _bitCount = 0;
        }

        return _position;
    }

    /// <summary>Pads a forward stream (a table description) to a whole byte and returns its length.</summary>
    public int CloseForward()
    {
        Flush();
        if (_bitCount > 0)
        {
            _position++;
            _bitCount = 0;
        }

        return _position;
    }
}
