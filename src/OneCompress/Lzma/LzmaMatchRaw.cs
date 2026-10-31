using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
#endif

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The only place in the LZMA match finders that skips bounds checks: the tree walk's reads of the data and of the link array.
/// Under the <c>Checked</c> build configuration (symbol CHECKED) every access is bounds-checked instead, and the tests and the
/// fuzzer run in that configuration. The prefetch hints change no result.
/// </summary>
internal static class LzmaMatchRaw
{
    /// <summary>
    /// <c>buffer[index]</c>. The caller guarantees <c>0 &lt;= index &lt; buffer.Length</c>: a tree walk reads <c>cur + len</c> and
    /// <c>position + len</c> with <c>cur &lt; position</c> and <c>len</c> at most the length limit, and the entry points have checked
    /// that <c>position + limit</c> lies inside the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte B(byte[] buffer, int index)
    {
#if CHECKED
        return buffer[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// A reference to <c>links[index]</c>. The caller guarantees <c>0 &lt;= index &lt; links.Length</c>: indexes are twice a slot in
    /// the cyclic array (plus one), and the slot is reduced into <c>[0, cyclicSize)</c> while the array holds twice that.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref int L(int[] links, int index)
    {
#if CHECKED
        return ref links[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(links), (nint)(uint)index);
#endif
    }

    /// <summary>Asks the CPU to load the cache line of <c>buffer[index]</c>; a hint only, never faults, and a no-op where there is no such instruction.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrefetchByte(byte[] buffer, int index)
    {
#if !CHECKED
        if (Sse.IsSupported)
        {
            unsafe
            {
                Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)(uint)index)));
            }
        }
#endif
    }

    /// <summary>As <see cref="PrefetchByte"/> for <c>links[index]</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrefetchLink(int[] links, int index)
    {
#if !CHECKED
        if (Sse.IsSupported)
        {
            unsafe
            {
                Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(links), (nint)(uint)index)));
            }
        }
#endif
    }

    /// <summary>
    /// Four bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 4 &lt;= buffer.Length</c>
    /// (the entry points have checked the window).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Bound: callers read 8 bytes at an index of at most (the checked window end) - 8, which CheckWindow has validated.
    public static ulong Read64(byte[] buffer, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(index));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(byte[] buffer, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(index));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(buffer), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }
}
