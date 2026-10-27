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

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Get(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }
}
