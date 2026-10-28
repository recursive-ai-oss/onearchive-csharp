using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The LZMA range encoder: the inverse of <see cref="LzmaRangeDecoder"/>. Binary decisions are coded either with an adaptive 11-bit
/// probability or with a fixed probability of one half, and the finished bytes collect in an internal buffer that the caller reads
/// with <see cref="WrittenSpan"/> and clears with <see cref="ClearWritten"/>.
/// </summary>
/// <remarks>
/// LZMA specification, "Range Decoder" and "The Bit Decoding with Probability Model"; the encoder keeps a 33-bit <c>low</c> and delays
/// bytes whose value a later carry could still change (the usual cache-and-count scheme). The first byte written is always zero, which
/// the decoder reads as part of its five initial bytes. A stream ends with <see cref="Flush"/>.
/// </remarks>
[Experimental("ONECOMPRESS005", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class LzmaRangeEncoder
{
    private const int TopValue = 1 << 24;
    private const int ProbabilityBits = 11;
    private const int MoveBits = 5;

    private byte[] _buffer;
    private int _length;
    private ulong _low;
    private uint _range = uint.MaxValue;
    private byte _cache;
    private long _cacheSize = 1;

    /// <summary>Creates an encoder.</summary>
    /// <param name="initialCapacity">The initial size of the output buffer, which grows as needed.</param>
    public LzmaRangeEncoder(int initialCapacity = 1 << 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 16);
        _buffer = new byte[initialCapacity];
    }

    /// <summary>Gets the bytes that are final so far. Bytes still waiting for a possible carry are not included.</summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, _length);

    /// <summary>Gets the number of bytes in <see cref="WrittenSpan"/>.</summary>
    public int WrittenLength => _length;

    /// <summary>
    /// Gets an upper bound of the size the stream has now if it were flushed immediately: final bytes, delayed bytes and the five bytes
    /// <see cref="Flush"/> adds. A caller that must fit a stream into a size limit checks this before coding another symbol.
    /// </summary>
    public long PendingLength => _length + _cacheSize + 5;

    /// <summary>Discards the bytes returned by <see cref="WrittenSpan"/>, keeping the coder's state. Use it to hand finished bytes on while a long stream continues.</summary>
    public void ClearWritten() => _length = 0;

    /// <summary>Starts a new stream: empty output and the initial range.</summary>
    public void Reset()
    {
        _length = 0;
        _low = 0;
        _range = uint.MaxValue;
        _cache = 0;
        _cacheSize = 1;
    }

    /// <summary>Codes one bit with an adaptive probability and updates the probability.</summary>
    /// <param name="probability">The probability that the bit is 0, in units of 1/2048.</param>
    /// <param name="bit">0 or 1.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBit(ref ushort probability, int bit)
    {
        uint p = probability;
        uint bound = (_range >> ProbabilityBits) * p;
        if (bit == 0)
        {
            _range = bound;
            probability = (ushort)(p + ((2048 - p) >> MoveBits));
        }
        else
        {
            _low += bound;
            _range -= bound;
            probability = (ushort)(p - (p >> MoveBits));
        }

        while (_range < TopValue)
        {
            _range <<= 8;
            ShiftLow();
        }
    }

    /// <summary>Codes the low <paramref name="count"/> bits of <paramref name="value"/>, most significant first, each with probability one half.</summary>
    /// <param name="value">The bits.</param>
    /// <param name="count">How many bits, 0 to 32.</param>
    public void EncodeDirectBits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            _range >>= 1;
            if (((value >> i) & 1) != 0)
            {
                _low += _range;
            }

            while (_range < TopValue)
            {
                _range <<= 8;
                ShiftLow();
            }
        }
    }

    /// <summary>Codes <paramref name="count"/> bits of <paramref name="symbol"/> through a bit tree, most significant first.</summary>
    /// <param name="probabilities">The probability array.</param>
    /// <param name="offset">The index of the tree's node 0 (the tree uses entries 1 to 2^count - 1 above it).</param>
    /// <param name="count">The number of bits.</param>
    /// <param name="symbol">The value to code.</param>
    public void EncodeTree(Span<ushort> probabilities, int offset, int count, uint symbol)
    {
        uint m = 1;
        for (int i = count - 1; i >= 0; i--)
        {
            int bit = (int)((symbol >> i) & 1);
            EncodeBit(ref probabilities[offset + (int)m], bit);
            m = (m << 1) | (uint)bit;
        }
    }

    /// <summary>Codes <paramref name="count"/> bits of <paramref name="symbol"/> through a bit tree, least significant first.</summary>
    /// <param name="probabilities">The probability array.</param>
    /// <param name="offset">The index of the tree's node 0.</param>
    /// <param name="count">The number of bits.</param>
    /// <param name="symbol">The value to code.</param>
    public void EncodeReverseTree(Span<ushort> probabilities, int offset, int count, uint symbol)
    {
        uint m = 1;
        for (int i = 0; i < count; i++)
        {
            int bit = (int)(symbol & 1);
            symbol >>= 1;
            EncodeBit(ref probabilities[offset + (int)m], bit);
            m = (m << 1) | (uint)bit;
        }
    }

    /// <summary>Ends the stream: writes the bytes that identify the final range. The encoder must be <see cref="Reset"/> before it is used again.</summary>
    public void Flush()
    {
        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    private void ShiftLow()
    {
        if ((uint)_low < 0xFF000000u || (_low >> 32) != 0)
        {
            byte carry = (byte)(_low >> 32);
            byte temp = _cache;
            do
            {
                if (_length == _buffer.Length)
                {
                    Array.Resize(ref _buffer, _buffer.Length * 2);
                }

                _buffer[_length++] = (byte)(temp + carry);
                temp = 0xFF;
            }
            while (--_cacheSize != 0);
            _cache = (byte)(_low >> 24);
        }

        _cacheSize++;
        _low = (_low & 0x00FFFFFF) << 8;
    }
}
