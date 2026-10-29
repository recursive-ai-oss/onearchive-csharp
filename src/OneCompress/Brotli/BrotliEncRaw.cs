using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
#endif

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The only place in the brotli encoder that skips bounds checks: loads from the input buffer and reads and writes of the match finder
/// tables in the parse loops. Under the <c>Checked</c> build configuration (symbol CHECKED) every access is bounds-checked instead, and the
/// tests and the fuzzer run in that configuration.
/// </summary>
internal static class BrotliEncRaw
{
    /// <summary>
    /// Four bytes of <paramref name="b"/> at <paramref name="index"/>, little endian. The caller guarantees <c>index + 4 &lt;= b.Length</c>:
    /// the parse loops read at positions that have four bytes before the end of the block being parsed, and at candidates that lie
    /// before such a position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Load32(byte[] b, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(index));
#else
        return Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)index));
#endif
    }

    /// <summary>
    /// Eight bytes of <paramref name="b"/> at <paramref name="index"/>, little endian. The caller guarantees <c>index + 8 &lt;= b.Length</c>:
    /// match comparisons load eight bytes only while eight more bytes remain before the end of the block.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Load64(byte[] b, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(index));
#else
        return Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)index));
#endif
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes at <paramref name="index"/> as a little-endian number, for a length of 4 to 8: the other
    /// bytes of the word read are cleared. The caller guarantees <c>index + 8 &lt;= b.Length</c> when the length is above 4, and
    /// <c>index + 4 &lt;= b.Length</c> otherwise.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Load64Or32(byte[] b, int index, int length) =>
        length == 4 ? Load32(b, index) : Load64(b, index) & (ulong.MaxValue >> (8 * (8 - length)));

    /// <summary>
    /// <c>b[index]</c>. The caller guarantees <c>0 &lt;= index &lt; b.Length</c>: it is a position inside the block being parsed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte At(byte[] b, int index)
    {
#if CHECKED
        return b[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of a match finder table. The caller guarantees <c>0 &lt;= index &lt; t.Length</c>: an index is a hash shifted down to
    /// the table's bucket count, optionally combined with a slot number masked to the bucket size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref int Slot(int[] t, int index)
    {
#if CHECKED
        return ref t[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// The number of leading bytes that agree between <c>b[a..]</c> and <c>b[c..]</c>, up to <paramref name="max"/>, starting the comparison
    /// at <paramref name="start"/>. The caller guarantees <c>a &lt; c</c>, <c>start &lt;= max</c> and <c>c + max &lt;= b.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonLength(byte[] b, int a, int c, int start, int max)
    {
        int len = start;
        while (len + 8 <= max)
        {
            ulong x = Load64(b, a + len) ^ Load64(b, c + len);
            if (x != 0)
            {
                return len + (System.Numerics.BitOperations.TrailingZeroCount(x) >> 3);
            }

            len += 8;
        }

        while (len < max && At(b, a + len) == At(b, c + len))
        {
            len++;
        }

        return len;
    }

    /// <summary>
    /// Asks the CPU to load the cache line of <c>t[index]</c> ahead of use. A hint only: it never faults, changes no result, and does
    /// nothing where there is no such instruction. The caller passes an index inside the table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Prefetch(int[] t, int index)
    {
#if !CHECKED
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            unsafe
            {
                System.Runtime.Intrinsics.X86.Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index)));
            }
        }
#endif
    }

    /// <summary>
    /// Stores <paramref name="value"/> little endian at <c>dest[index..index + 8]</c>. The caller guarantees <c>index + 8 &lt;= dest.Length</c>:
    /// the bit writer's span is sized for the stream plus 8 bytes, and <c>index</c> never passes the last byte of the stream.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Store64(Span<byte> dest, int index, ulong value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(dest.Slice(index), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(dest), (nint)(uint)index), value);
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of a small array. The caller guarantees <c>0 &lt;= index &lt; t.Length</c>: a histogram indexed by a byte, or a code
    /// table indexed by a symbol of its alphabet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref uint Entry(uint[] t, int index)
    {
#if CHECKED
        return ref t[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of a histogram of ints. The caller guarantees <c>0 &lt;= index &lt; t.Length</c>: the index is a byte (plus a multiple
    /// of 256 when several histograms share an array of that many) or a symbol of the histogram's alphabet.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref int Count(int[] t, int index)
    {
#if CHECKED
        return ref t[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>a[index]</c> of the fast parse's command array. The caller guarantees <c>0 &lt;= index &lt; a.Length</c>: the index is the command
    /// count, and the array is grown whenever the count reaches its length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref BrotliFastCommand Command(BrotliFastCommand[] a, int index)
    {
#if CHECKED
        return ref a[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), (nint)(uint)index);
#endif
    }
}
