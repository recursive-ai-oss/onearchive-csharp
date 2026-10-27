using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// The only place in the LZ4 decoder that skips bounds checks: wide copies whose callers have already compared the
/// remaining room with the copy width plus the overshoot. Each accessor states the bound its caller guarantees. Under
/// the <c>Checked</c> build configuration (symbol CHECKED) every accessor is bounds-checked instead, and the tests and
/// the fuzzer run in that configuration.
/// </summary>
internal static class Lz4Raw
{
    /// <summary>
    /// Copies 16 bytes. The caller guarantees both 16-byte ranges lie inside their spans and, when both spans are the same
    /// memory, that the ranges do not overlap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(Span<byte> destination, nint destinationIndex, ReadOnlySpan<byte> source, nint sourceIndex)
    {
#if CHECKED
        source.Slice((int)sourceIndex, 16).CopyTo(destination.Slice((int)destinationIndex, 16));
#else
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>As <see cref="Copy16"/> for eight bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy8(Span<byte> destination, nint destinationIndex, ReadOnlySpan<byte> source, nint sourceIndex)
    {
#if CHECKED
        source.Slice((int)sourceIndex, 8).CopyTo(destination.Slice((int)destinationIndex, 8));
#else
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>Reads two bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 2 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadUInt16(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span.Slice((int)index, 2));
#else
        ushort value = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Get(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return span[(int)index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }

    /// <summary>
    /// Writes <paramref name="length"/> bytes at <paramref name="position"/> that repeat the <paramref name="offset"/> bytes before it,
    /// for offsets of 1 to 7 (the source overlaps what is being written). The caller guarantees
    /// <c>1 &lt;= offset &lt;= position</c> and <c>position + length &lt;= destination.Length</c>.
    /// </summary>
    public static void RepeatShort(Span<byte> destination, nint position, nint offset, nint length)
    {
        if (offset == 1)
        {
            destination.Slice((int)position, (int)length).Fill(destination[(int)(position - 1)]);
            return;
        }

        // Once the data holds a whole number of periods of at least 8 bytes, 8-byte steps from that far back are exact.
        nint distance = offset * ((8 + offset - 1) / offset);
        nint head = Math.Min(length, distance);
        for (nint i = 0; i < head; i++)
        {
            destination[(int)(position + i)] = destination[(int)(position + i - offset)];
        }

        nint done = head;
        while (length - done >= 8)
        {
            Copy8(destination, position + done, destination, position + done - distance);
            done += 8;
        }

        for (; done < length; done++)
        {
            destination[(int)(position + done)] = destination[(int)(position + done - offset)];
        }
    }

    /// <summary>Reads four bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Writes eight bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write8(Span<byte> span, nint index, ulong value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span.Slice((int)index, 8), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index), BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));
#endif
    }
}
