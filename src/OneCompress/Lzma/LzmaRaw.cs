using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The only place in the LZMA decoder that skips bounds checks: probability and input reads in the fast loop, and the wide match copy. The window array is allocated with
/// <see cref="LzmaDecoderCore.WindowSlack"/> spare bytes behind the ring, so a copy that rounds its length up to a multiple of
/// eight stays inside the array. Under the <c>Checked</c> build configuration (symbol CHECKED) every access is
/// bounds-checked instead, and the tests and the fuzzer run in that configuration.
/// </summary>
internal static class LzmaRaw
{
    /// <summary>
    /// The probability at <paramref name="index"/>. The caller guarantees <c>0 &lt;= index &lt; p.Length</c>: every index is a fixed
    /// offset of the model layout plus a state, a position state or a tree node, and each of those stays inside its table (the
    /// table sizes are in <see cref="LzmaModel"/>); the array is at least <c>ProbabilityCount(lc, lp)</c> long.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref ushort P(ushort[] p, int index)
    {
#if CHECKED
        return ref p[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(p), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>input[index]</c>. The caller guarantees <c>0 &lt;= index &lt; input.Length</c>: the fast loop starts a symbol only with
    /// at least 32 input bytes left, and one symbol consumes at most 25 (every decision shrinks the range by at most 6.05 bits,
    /// a direct bit by one, and a byte is read for each 8 bits lost).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte In(ReadOnlySpan<byte> input, nint index)
    {
#if CHECKED
        return input[(int)index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(input), index);
#endif
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes from <c>w[src..]</c> to <c>w[dst..]</c> in eight-byte steps, which may write up to
    /// seven bytes past <c>dst + length</c>. The caller guarantees <c>dst - src &gt;= 8</c> or <c>src - dst &gt;= 8 + length</c>
    /// rounded up to eight (so a step never reads what the same step writes), <c>src + length &lt;= ring size</c>,
    /// <c>dst + length &lt;= ring size</c>, and that the bytes past the end of the copy are not window data a later match can
    /// read (the ring is larger than the dictionary by the slack, or they are still unwritten).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyWide(byte[] w, int dst, int src, int length)
    {
#if CHECKED
        for (int i = 0; i < length; i += 8)
        {
            w.AsSpan(src + i, 8).CopyTo(w.AsSpan(dst + i, 8));
        }
#else
        // Bound: src + length and dst + length are at most the ring size, and the array is the ring size plus the slack, so
        // the last step ends at most 7 bytes past the ring.
        ref byte b = ref MemoryMarshal.GetArrayDataReference(w);
        for (int i = 0; i < length; i += 8)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, dst + i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b, src + i)));
        }
#endif
    }

    /// <summary>
    /// As <see cref="CopyWide"/> in sixteen-byte steps, for <c>dst - src &gt;= 16</c>; may write up to fifteen bytes past
    /// <c>dst + length</c> (the slack is <see cref="LzmaDecoderCore.WindowSlack"/>, which exceeds that). <paramref name="length"/> is at least 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CopyWide16(byte[] w, int dst, int src, int length)
    {
#if CHECKED
        for (int i = 0; i < length; i += 16)
        {
            w.AsSpan(src + i, 16).CopyTo(w.AsSpan(dst + i, 16));
        }
#else
        // Bound: src + length and dst + length are at most the ring size, and the array is the ring size plus the slack, so
        // the last step ends at most 15 bytes past the ring.
        ref byte b = ref MemoryMarshal.GetArrayDataReference(w);
        int i = 0;
        do
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, dst + i), Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref b, src + i)));
            i += 16;
        }
        while (i < length);
#endif
    }
}
