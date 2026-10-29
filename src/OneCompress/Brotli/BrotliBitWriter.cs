using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// Writes bit fields in the order brotli streams use (RFC 7932 section 1.5.1): fields are packed starting at the least significant
/// bit of each byte, and a multi-bit value goes out with its least significant bit first.
/// </summary>
/// <remarks>
/// The writer collects bytes in an internal buffer that grows as needed. Complete bytes can be taken out at any time with
/// <see cref="Drain"/> while the bits of an unfinished byte stay inside. Prefix codes are written with
/// <see cref="BrotliHuffmanCode"/>, which hands this writer the code bits already in stream order.
/// </remarks>
[Experimental("ONECOMPRESS008", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed partial class BrotliBitWriter
{
    private byte[] _buffer = new byte[4096];
    private int _read;
    private int _length;
    private ulong _accumulator;
    private int _accumulatorBits;

    /// <summary>Gets the number of complete bytes waiting to be taken out.</summary>
    public int PendingBytes => _length - _read;

    /// <summary>Gets the total number of bits written since the last <see cref="Reset"/>, including those already drained.</summary>
    public long BitsWritten { get; private set; }

    /// <summary>Gets the number of bits in the unfinished byte, 0 to 7, when the writer is not byte aligned.</summary>
    public int BitsInPartialByte => _accumulatorBits & 7;

    /// <summary>Forgets everything written so far.</summary>
    public void Reset()
    {
        _read = 0;
        _length = 0;
        _accumulator = 0;
        _accumulatorBits = 0;
        BitsWritten = 0;
    }

    /// <summary>Remembers the current position so that <see cref="Rewind"/> can undo what is written after it.</summary>
    /// <returns>A mark for <see cref="Rewind"/>. It is valid only until bytes are drained or the writer is reset.</returns>
    public Mark GetMark() => new(_length, _accumulator, _accumulatorBits, BitsWritten);

    /// <summary>Undoes everything written since <paramref name="mark"/> was taken.</summary>
    /// <param name="mark">A mark from <see cref="GetMark"/>; no bytes may have been drained since.</param>
    public void Rewind(Mark mark)
    {
        _length = mark.Length;
        _accumulator = mark.Accumulator;
        _accumulatorBits = mark.AccumulatorBits;
        BitsWritten = mark.Bits;
    }

    /// <summary>A position in the writer.</summary>
    /// <param name="Length">Complete bytes held.</param>
    /// <param name="Accumulator">The unfinished bits.</param>
    /// <param name="AccumulatorBits">How many bits the accumulator holds.</param>
    /// <param name="Bits">Total bits written.</param>
    public readonly record struct Mark(int Length, ulong Accumulator, int AccumulatorBits, long Bits);

    /// <summary>Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>, least significant bit first.</summary>
    /// <param name="value">The value; bits above <paramref name="bitCount"/> must be zero.</param>
    /// <param name="bitCount">The number of bits, 0 to 32.</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Write(uint value, int bitCount)
    {
        _accumulator |= (ulong)value << _accumulatorBits;
        _accumulatorBits += bitCount;
        BitsWritten += bitCount;
        if (_accumulatorBits >= 32)
        {
            EnsureRoom(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), (uint)_accumulator);
            _length += 4;
            _accumulator >>= 32;
            _accumulatorBits -= 32;
        }
    }

    /// <summary>Writes the prefix code of each of <paramref name="symbols"/>; every code must be at most 15 bits long.</summary>
    internal void WriteSymbols(ReadOnlySpan<byte> symbols, ushort[] codes, byte[] lengths)
    {
        // Every code is at most 15 bits, so two bytes per symbol plus the accumulator's spill are enough room.
        EnsureRoom((2 * symbols.Length) + 8);
        ulong accumulator = _accumulator;
        int bits = _accumulatorBits;
        int length = _length;
        long total = 0;
        byte[] buffer = _buffer;
        foreach (byte symbol in symbols)
        {
            int n = lengths[symbol];
            accumulator |= (ulong)codes[symbol] << bits;
            bits += n;
            total += n;
            if (bits >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(length), (uint)accumulator);
                length += 4;
                accumulator >>= 32;
                bits -= 32;
            }
        }

        _accumulator = accumulator;
        _accumulatorBits = bits;
        _length = length;
        BitsWritten += total;
    }

    /// <summary>Pads with zero bits up to the next byte boundary.</summary>
    public void AlignToByte()
    {
        int pad = (-_accumulatorBits) & 7;
        BitsWritten += pad;
        _accumulatorBits += pad;
        FlushWholeBytes();
    }

    /// <summary>Writes bytes. The writer must be byte aligned.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <exception cref="InvalidOperationException">The writer is in the middle of a byte.</exception>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        if ((_accumulatorBits & 7) != 0)
        {
            throw new InvalidOperationException("Bytes can only be written at a byte boundary.");
        }

        FlushWholeBytes();
        EnsureRoom(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
        BitsWritten += 8L * bytes.Length;
    }

    /// <summary>Moves complete bytes into <paramref name="destination"/>.</summary>
    /// <param name="destination">Receives the bytes.</param>
    /// <returns>The number of bytes moved, at most <see cref="PendingBytes"/>.</returns>
    public int Drain(Span<byte> destination)
    {
        FlushWholeBytes();
        int n = Math.Min(destination.Length, _length - _read);
        _buffer.AsSpan(_read, n).CopyTo(destination);
        _read += n;
        if (_read == _length)
        {
            _read = 0;
            _length = 0;
        }

        return n;
    }

    /// <summary>Returns the complete bytes written so far and clears them.</summary>
    /// <returns>A new array holding <see cref="PendingBytes"/> bytes.</returns>
    public byte[] TakeBytes()
    {
        FlushWholeBytes();
        byte[] result = _buffer.AsSpan(_read, _length - _read).ToArray();
        _read = 0;
        _length = 0;
        return result;
    }

    private void FlushWholeBytes()
    {
        while (_accumulatorBits >= 8)
        {
            EnsureRoom(1);
            _buffer[_length++] = (byte)_accumulator;
            _accumulator >>= 8;
            _accumulatorBits -= 8;
        }
    }

    private void EnsureRoom(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        if (_read > 0)
        {
            Buffer.BlockCopy(_buffer, _read, _buffer, 0, _length - _read);
            _length -= _read;
            _read = 0;
            if (_length + extra <= _buffer.Length)
            {
                return;
            }
        }

        Array.Resize(ref _buffer, (int)Math.Min(Array.MaxLength, Math.Max((long)_buffer.Length * 2, (long)_length + extra)));
    }
}
