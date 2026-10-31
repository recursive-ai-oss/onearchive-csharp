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
public sealed class LzmaRangeEncoder
{
    internal RangeState State;

    /// <summary>Creates an encoder.</summary>
    /// <param name="initialCapacity">The initial size of the output buffer, which grows as needed.</param>
    public LzmaRangeEncoder(int initialCapacity = 1 << 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 16);
        State = new RangeState(initialCapacity);
    }

    /// <summary>Gets the bytes that are final so far. Bytes still waiting for a possible carry are not included.</summary>
    public ReadOnlySpan<byte> WrittenSpan => new(State.Buffer, 0, State.Length);

    /// <summary>Gets the number of bytes in <see cref="WrittenSpan"/>.</summary>
    public int WrittenLength => State.Length;

    /// <summary>
    /// Gets an upper bound of the size the stream has now if it were flushed immediately: final bytes, delayed bytes and the five bytes
    /// <see cref="Flush"/> adds. A caller that must fit a stream into a size limit checks this before coding another symbol.
    /// </summary>
    public long PendingLength => State.Length + State.CacheSize + 5;

    /// <summary>Discards the bytes returned by <see cref="WrittenSpan"/>, keeping the coder's state. Use it to hand finished bytes on while a long stream continues.</summary>
    public void ClearWritten() => State.Length = 0;

    /// <summary>Starts a new stream: empty output and the initial range.</summary>
    public void Reset() => State.Reset();

    /// <summary>Codes one bit with an adaptive probability and updates the probability.</summary>
    /// <param name="probability">The probability that the bit is 0, in units of 1/2048.</param>
    /// <param name="bit">0 or 1.</param>
    public void EncodeBit(ref ushort probability, int bit) => State.EncodeBit(ref probability, bit);

    /// <summary>Codes the low <paramref name="count"/> bits of <paramref name="value"/>, most significant first, each with probability one half.</summary>
    /// <param name="value">The bits.</param>
    /// <param name="count">How many bits, 0 to 32.</param>
    public void EncodeDirectBits(uint value, int count) => State.EncodeDirectBits(value, count);

    /// <summary>Codes <paramref name="count"/> bits of <paramref name="symbol"/> through a bit tree, most significant first.</summary>
    /// <param name="probabilities">The probability array.</param>
    /// <param name="offset">The index of the tree's node 0 (the tree uses entries 1 to 2^count - 1 above it).</param>
    /// <param name="count">The number of bits.</param>
    /// <param name="symbol">The value to code.</param>
    public void EncodeTree(Span<ushort> probabilities, int offset, int count, uint symbol) => State.EncodeTree(probabilities, offset, count, symbol);

    /// <summary>Codes <paramref name="count"/> bits of <paramref name="symbol"/> through a bit tree, least significant first.</summary>
    /// <param name="probabilities">The probability array.</param>
    /// <param name="offset">The index of the tree's node 0.</param>
    /// <param name="count">The number of bits.</param>
    /// <param name="symbol">The value to code.</param>
    public void EncodeReverseTree(Span<ushort> probabilities, int offset, int count, uint symbol) => State.EncodeReverseTree(probabilities, offset, count, symbol);

    /// <summary>Ends the stream: writes the bytes that identify the final range. The encoder must be <see cref="Reset"/> before it is used again.</summary>
    public void Flush() => State.Flush();
}

/// <summary>
/// The range coder's registers as a struct, so a symbol writer can copy them into locals for the duration of a symbol (the JIT then keeps
/// them in processor registers instead of reloading them from the heap object after every bit).
/// </summary>
internal struct RangeState
{
    private const int TopValue = 1 << 24;
    private const int ProbabilityBits = 11;
    private const int MoveBits = 5;

    public byte[] Buffer;
    public int Length;
    public ulong Low;
    public uint Range;
    public byte Cache;
    public long CacheSize;

    public RangeState(int capacity)
    {
        Buffer = new byte[capacity];
        Length = 0;
        Low = 0;
        Range = uint.MaxValue;
        Cache = 0;
        CacheSize = 1;
    }

    public void Reset()
    {
        Length = 0;
        Low = 0;
        Range = uint.MaxValue;
        Cache = 0;
        CacheSize = 1;
    }

    // The branch on `bit` is replaced by masks: the bits of a literal are close to random, so a branch would be mispredicted about
    // half the time, and a mispredict costs more than all the arithmetic here. The result is the same as the textbook form
    // (bit 0: range = bound, p += (2048 - p) >> 5; bit 1: low += bound, range -= bound, p -= p >> 5).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EncodeBit(ref ushort probability, int bit)
    {
        uint p = probability;
        uint bound = (Range >> ProbabilityBits) * p;
        uint mask = (uint)-bit;
        Low += bound & mask;
        Range = bound ^ ((bound ^ (Range - bound)) & mask);
        probability = (ushort)(p + (((2048 - p) >> MoveBits) & ~mask) - ((p >> MoveBits) & mask));
        if (Range < TopValue)
        {
            Range <<= 8;
            ShiftLow();
        }
    }

    public void EncodeDirectBits(uint value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            Range >>= 1;
            Low += Range & (uint)-(int)((value >> i) & 1);

            if (Range < TopValue)
            {
                Range <<= 8;
                ShiftLow();
            }
        }
    }

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

    public void Flush()
    {
        for (int i = 0; i < 5; i++)
        {
            ShiftLow();
        }
    }

    // A coded bit leaves the range at 2^17 or more, and a direct bit at 2^23 or more, so one shift of 8 bits always restores the 2^24 floor.
    private void ShiftLow()
    {
        if ((uint)Low < 0xFF000000u || (Low >> 32) != 0)
        {
            byte carry = (byte)(Low >> 32);
            byte temp = Cache;
            do
            {
                if (Length == Buffer.Length)
                {
                    Array.Resize(ref Buffer, Buffer.Length * 2);
                }

                Buffer[Length++] = (byte)(temp + carry);
                temp = 0xFF;
            }
            while (--CacheSize != 0);
            Cache = (byte)(Low >> 24);
        }

        CacheSize++;
        Low = (Low & 0x00FFFFFF) << 8;
    }
}
