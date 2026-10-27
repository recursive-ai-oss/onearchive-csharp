using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// The only place in the checksum code that skips bounds checks. Every caller's index is bounded by its
/// loop condition (data reads) or by construction (table lookups); each accessor states the bound it
/// relies on. In the <c>Checked</c> build configuration every accessor verifies its bound instead.
/// </summary>
internal static class Mem
{
    /// <summary>Reads 4 bytes at <paramref name="i"/>. Caller guarantees <c>0 &lt;= i &amp;&amp; i + 4 &lt;= s.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint U32(ReadOnlySpan<byte> s, int i)
    {
#if CHECKED
        CheckRange(s.Length, i, 4);
#endif
        uint v = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref Unsafe.AsRef(in MemoryMarshal.GetReference(s)), i));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    /// <summary>Reads 8 bytes at <paramref name="i"/>. Caller guarantees <c>0 &lt;= i &amp;&amp; i + 8 &lt;= s.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong U64(ReadOnlySpan<byte> s, int i)
    {
#if CHECKED
        CheckRange(s.Length, i, 8);
#endif
        ulong v = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref Unsafe.AsRef(in MemoryMarshal.GetReference(s)), i));
        return BitConverter.IsLittleEndian ? v : BinaryPrimitives.ReverseEndianness(v);
    }

    /// <summary>Loads 16 bytes at <paramref name="i"/>. Caller guarantees <c>0 &lt;= i &amp;&amp; i + 16 &lt;= s.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<byte> V128(ReadOnlySpan<byte> s, int i)
    {
#if CHECKED
        CheckRange(s.Length, i, 16);
#endif
        return Vector128.LoadUnsafe(ref Unsafe.AsRef(in MemoryMarshal.GetReference(s)), (nuint)i);
    }

    /// <summary>Loads 32 bytes at <paramref name="i"/>. Caller guarantees <c>0 &lt;= i &amp;&amp; i + 32 &lt;= s.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<byte> V256(ReadOnlySpan<byte> s, int i)
    {
#if CHECKED
        CheckRange(s.Length, i, 32);
#endif
        return Vector256.LoadUnsafe(ref Unsafe.AsRef(in MemoryMarshal.GetReference(s)), (nuint)i);
    }

    /// <summary>Reads one byte. Caller guarantees <c>0 &lt;= i &amp;&amp; i &lt; s.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8(ReadOnlySpan<byte> s, int i)
    {
#if CHECKED
        CheckRange(s.Length, i, 1);
#endif
        return Unsafe.Add(ref Unsafe.AsRef(in MemoryMarshal.GetReference(s)), i);
    }

    /// <summary>Table lookup. Caller guarantees <c>0 &lt;= i &lt; t.Length</c> (indices are table number * 256 + a byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint T(uint[] t, uint i)
    {
#if CHECKED
        return t[i];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)i);
#endif
    }

    /// <summary>Table lookup. Caller guarantees <c>0 &lt;= i &lt; t.Length</c> (indices are table number * 256 + a byte).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong T(ulong[] t, uint i)
    {
#if CHECKED
        return t[i];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)i);
#endif
    }

#if CHECKED
    private static void CheckRange(int length, int index, int size)
    {
        if (index < 0 || (long)index + size > length)
        {
            throw new InvalidOperationException($"checksum read of {size} bytes at {index} outside a span of {length}");
        }
    }
#endif
}
