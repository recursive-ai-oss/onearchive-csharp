namespace RecursiveAI.OneCompress.Brotli;

public sealed partial class BrotliBitWriter
{
    /// <summary>
    /// Hands out room for <paramref name="maxBytes"/> more bytes and the unfinished bits, so that a hot loop can keep the bit accumulator in
    /// locals. The loop ends with <see cref="EndRaw"/>. Bits are appended exactly as <see cref="Write"/> would: the accumulator holds fewer than
    /// 32 bits between writes and a full 32 are stored little endian.
    /// </summary>
    internal Span<byte> BeginRaw(int maxBytes, out ulong accumulator, out int accumulatorBits)
    {
        FlushWholeBytes();
        EnsureRoom(maxBytes + 8);
        accumulator = _accumulator;
        accumulatorBits = _accumulatorBits;
        return _buffer.AsSpan(_length);
    }

    /// <summary>Takes back the state of a loop started with <see cref="BeginRaw"/>.</summary>
    /// <param name="bytesStored">How many bytes the loop stored into the span.</param>
    /// <param name="accumulator">The unfinished bits.</param>
    /// <param name="accumulatorBits">How many bits the accumulator holds.</param>
    /// <param name="bitsAdded">The total number of bits the loop wrote.</param>
    internal void EndRaw(int bytesStored, ulong accumulator, int accumulatorBits, long bitsAdded)
    {
        _length += bytesStored;
        _accumulator = accumulator;
        _accumulatorBits = accumulatorBits;
        BitsWritten += bitsAdded;
    }
}
