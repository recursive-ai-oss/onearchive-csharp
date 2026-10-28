using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
#endif

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// The only place in the Deflate decoder that skips bounds checks. Each accessor states the bound its caller guarantees by
/// construction. Under the <c>Checked</c> build configuration (symbol CHECKED) every accessor is bounds-checked instead, and
/// the tests and the fuzzer run in that configuration.
/// </summary>
internal static class DeflateRaw
{
    /// <summary>
    /// Reads <c>entries[index]</c>. The caller guarantees <c>index &lt; entries.Length</c>: first-level indices are masked to the
    /// table's first-level size, and second-level indices are an offset and a mask chosen when the table was built, inside its size.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Get(uint[] entries, int index)
    {
#if CHECKED
        return entries[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(entries), (nint)(uint)index);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 8 &lt;= span.Length</c> (the loop runs only while that holds).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Writes one byte at <paramref name="index"/>. The caller guarantees <c>index &lt; span.Length</c> (the loop keeps a margin of the longest match plus 8 bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(Span<byte> span, int index, byte value)
    {
#if CHECKED
        span[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)(uint)index) = value;
#endif
    }

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Get(Span<byte> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// Copies eight bytes from <paramref name="source"/> to <paramref name="destination"/> within one buffer. The caller guarantees both
    /// ranges are inside the buffer (the loop keeps a margin of the longest match plus 8 bytes) and, for overlapping data, that the source
    /// is at least 8 bytes behind the destination so every byte read has already been written.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy8(Span<byte> buffer, int destination, int source)
    {
#if CHECKED
        buffer.Slice(source, 8).CopyTo(buffer.Slice(destination, 8));
#else
        ref byte start = ref MemoryMarshal.GetReference(buffer);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, (nint)(uint)destination), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref start, (nint)(uint)source)));
#endif
    }
}
