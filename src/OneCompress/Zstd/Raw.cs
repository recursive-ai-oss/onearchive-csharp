using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The only place in the decoder that skips bounds checks. Each accessor states the bound its caller guarantees by
/// construction (a table index masked or sized to the table, a copy guarded by the buffer's length). Under the
/// <c>Checked</c> build configuration (symbol CHECKED) every accessor is bounds-checked instead, and the tests and
/// the fuzzer run in that configuration.
/// </summary>
internal static class Raw
{
    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(ReadOnlySpan<T> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }

    /// <summary>Reads sequence table row <c>rows[index]</c> as its packed 64-bit value. The caller guarantees <c>index &lt; rows.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Row(SequenceEntry[] rows, nuint index)
    {
#if CHECKED
        return Unsafe.As<SequenceEntry, ulong>(ref rows[checked((int)index)]);
#else
        return Unsafe.As<SequenceEntry, ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(rows), index));
#endif
    }

    /// <summary>As <see cref="ReadUInt64(ReadOnlySpan{byte}, int)"/> with a native-width index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(checked((int)index), 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>As <see cref="Copy16(Span{byte}, int, ReadOnlySpan{byte}, int)"/> with native-width indices.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(Span<byte> destination, nint destinationIndex, ReadOnlySpan<byte> source, nint sourceIndex)
    {
#if CHECKED
        source.Slice(checked((int)sourceIndex), 16).CopyTo(destination.Slice(checked((int)destinationIndex), 16));
#else
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>As <see cref="Copy8(Span{byte}, int, ReadOnlySpan{byte}, int)"/> with native-width indices.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy8(Span<byte> destination, nint destinationIndex, ReadOnlySpan<byte> source, nint sourceIndex)
    {
#if CHECKED
        source.Slice(checked((int)sourceIndex), 8).CopyTo(destination.Slice(checked((int)destinationIndex), 8));
#else
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>Writes two bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 2 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt16(Span<byte> span, nint index, ushort value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(checked((int)index), 2), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index), BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));
#endif
    }

    /// <summary>As <see cref="Get{T}(T[], int)"/> with a native-width index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(T[] array, nuint index)
    {
#if CHECKED
        return array[checked((int)index)];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);
#endif
    }

    /// <summary>As <see cref="Set(Span{byte}, int, byte)"/> with a native-width index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(Span<byte> span, nint index, byte value)
    {
#if CHECKED
        span[checked((int)index)] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), index) = value;
#endif
    }

    /// <summary>Reads <c>array[index]</c>. The caller guarantees <c>0 &lt;= index &lt; array.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(T[] array, int index)
    {
#if CHECKED
        return array[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);
#endif
    }

    /// <summary>References <c>array[index]</c> without a bounds check. The caller guarantees <c>0 &lt;= index &lt; array.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref readonly T Ref<T>(T[] array, int index)
    {
#if CHECKED
        return ref array[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>
    /// Copies 16 bytes from <paramref name="source"/> at <paramref name="sourceIndex"/> to <paramref name="destination"/> at
    /// <paramref name="destinationIndex"/>. The caller guarantees both 16-byte ranges lie inside their spans and, when both
    /// spans are the same memory, that the ranges do not overlap.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(Span<byte> destination, int destinationIndex, ReadOnlySpan<byte> source, int sourceIndex)
    {
#if CHECKED
        source.Slice(sourceIndex, 16).CopyTo(destination.Slice(destinationIndex, 16));
#else
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>
    /// Compares 16 bytes of <paramref name="array"/> from <paramref name="index"/> with <paramref name="value"/> and returns a mask
    /// with bit <c>i</c> set when byte <c>i</c> is equal. The vector and scalar paths give the same mask. The caller guarantees
    /// <c>index + 16 &lt;= array.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint EqualMask16(byte[] array, int index, byte value)
    {
#if !CHECKED
        if (Vector128.IsHardwareAccelerated)
        {
            Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index));
            return Vector128.Equals(v, Vector128.Create(value)).ExtractMostSignificantBits();
        }
#endif
        uint mask = 0;
        for (int i = 0; i < 16; i++)
        {
            if (array[index + i] == value)
            {
                mask |= 1u << i;
            }
        }

        return mask;
    }

    /// <summary>As <see cref="Copy16(Span{byte}, int, ReadOnlySpan{byte}, int)"/> for eight bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy8(Span<byte> destination, int destinationIndex, ReadOnlySpan<byte> source, int sourceIndex)
    {
#if CHECKED
        source.Slice(sourceIndex, 8).CopyTo(destination.Slice(destinationIndex, 8));
#else
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), destinationIndex), v);
#endif
    }

    /// <summary>Writes <c>span[index] = value</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(Span<byte> span, int index, byte value)
    {
#if CHECKED
        span[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), index) = value;
#endif
    }

    /// <summary>Reads four bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Writes <c>array[index] = value</c>. The caller guarantees <c>0 &lt;= index &lt; array.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(T[] array, int index, T value)
    {
#if CHECKED
        array[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index) = value;
#endif
    }

    /// <summary>Reads <c>span[index]</c> of a byte span. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ByteAt(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }

    /// <summary>References <c>array[index]</c> for reading and writing without a bounds check. The caller guarantees <c>0 &lt;= index &lt; array.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref T RefRw<T>(T[] array, int index)
    {
#if CHECKED
        return ref array[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), index);
#endif
    }

    /// <summary>Writes eight bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt64(Span<byte> span, int index, ulong value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(index, 8), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index), BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));
#endif
    }

    /// <summary>Reads four bytes at <paramref name="index"/> as a little-endian integer, with a native-width index. The caller guarantees <c>index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ReadUInt32(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(checked((int)index), 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads <c>span[index]</c> of a table, with a native-width index. The caller guarantees <c>index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Get<T>(Span<T> span, nuint index)
    {
#if CHECKED
        return span[checked((int)index)];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }

    /// <summary>Writes <c>span[index] = value</c> of a table, with a native-width index. The caller guarantees <c>index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set<T>(Span<T> span, nuint index, T value)
    {
#if CHECKED
        span[checked((int)index)] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), index) = value;
#endif
    }
}
