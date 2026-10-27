using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// The only place in the LZ4 encoder that skips bounds checks: unaligned reads of the input, wide copies, and table
/// accesses whose index was produced by a hash of a fixed width or masked to a power of two. Each accessor states the
/// bound its caller guarantees. Under the <c>Checked</c> build configuration (symbol CHECKED) every accessor is
/// bounds-checked instead, and the tests and the fuzzer run in that configuration.
/// </summary>
internal static class Lz4EncRaw
{
    /// <summary>Reads four bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Get(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index);
#endif
    }

    /// <summary>Writes one byte. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(Span<byte> span, int index, byte value)
    {
#if CHECKED
        span[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index) = value;
#endif
    }

    /// <summary>Writes a little-endian 16-bit value. The caller guarantees <c>0 &lt;= index &amp;&amp; index + 2 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put16(Span<byte> span, int index, ushort value)
    {
#if CHECKED
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(index, 2), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index), BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));
#endif
    }

    /// <summary>
    /// Copies 16 bytes. The caller guarantees both 16-byte ranges lie inside their spans; the ranges may touch the bytes
    /// after the part that matters, which the caller overwrites or ignores.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(Span<byte> destination, int destinationIndex, ReadOnlySpan<byte> source, int sourceIndex)
    {
#if CHECKED
        source.Slice(sourceIndex, 16).CopyTo(destination.Slice(destinationIndex, 16));
#else
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), (nint)sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), (nint)destinationIndex), v);
#endif
    }

    /// <summary>Reads <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c> (a hash shifted down to the table's size).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Get(uint[] table, int index)
    {
#if CHECKED
        return table[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index);
#endif
    }

    /// <summary>Writes <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(uint[] table, int index, uint value)
    {
#if CHECKED
        table[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index) = value;
#endif
    }

    /// <summary>Reads <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c> (an index masked to the table's power-of-two size).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Get(ushort[] table, int index)
    {
#if CHECKED
        return table[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index);
#endif
    }

    /// <summary>Writes <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(ushort[] table, int index, ushort value)
    {
#if CHECKED
        table[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index) = value;
#endif
    }

    /// <summary>
    /// Counts the equal bytes of <c>span[a..]</c> and <c>span[b..]</c>, comparing at most up to <c>span[limit - 1]</c> on the
    /// <paramref name="b"/> side. The caller guarantees <c>0 &lt;= a &lt; b &lt;= limit &lt;= span.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonLength(ReadOnlySpan<byte> span, int a, int b, int limit)
    {
        int start = b;
        // Eight bytes at a time while the b side has eight bytes before the limit (and a < b, so the a side does too).
        while (b + 8 <= limit)
        {
            ulong diff = Read64(span, a) ^ Read64(span, b);
            if (diff != 0)
            {
                return b - start + (BitOperations.TrailingZeroCount(diff) >> 3);
            }

            a += 8;
            b += 8;
        }

        while (b < limit && Get(span, a) == Get(span, b))
        {
            a++;
            b++;
        }

        return b - start;
    }
}
