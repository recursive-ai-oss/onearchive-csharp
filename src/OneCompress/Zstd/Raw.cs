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

    /// <summary>As <see cref="Copy16"/> for eight bytes.</summary>
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
}
