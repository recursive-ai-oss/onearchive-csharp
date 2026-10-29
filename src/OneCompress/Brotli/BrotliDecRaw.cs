using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
#endif

namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The only place in the brotli decoder that skips bounds checks: the fast command loop's input loads, prefix-code table reads and
/// match copies. Under the <c>Checked</c> build configuration (symbol CHECKED) every access is bounds-checked instead, and the tests and the
/// fuzzer run in that configuration.
/// </summary>
internal static class BrotliDecRaw
{
    /// <summary>
    /// How far past the end of the data a match copy may write: the ring buffer is allocated this much larger than its capacity. The window
    /// of a stream is 16 bytes smaller than the ring (RFC 7932 section 9.1), so the bytes just ahead of the write position are never
    /// referenced again and may hold the surplus of a 16-byte copy.
    /// </summary>
    internal const int RingSlack = 32;

    /// <summary>
    /// Eight bytes of <paramref name="s"/> at <paramref name="index"/>, little endian. The caller guarantees <c>index + 8 &lt;= s.Length</c>:
    /// the fast loop reads only while at least 64 bytes of input remain.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Load64(ReadOnlySpan<byte> s, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(index));
#else
        return Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(s), (nint)(uint)index));
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of a prefix-code table. The caller guarantees <c>0 &lt;= index &lt; t.Length</c>: the index is the offset of a table
    /// built into the array plus eight bits of the stream (root entry) or a second-level offset plus the bits that level covers, and the
    /// table builder has sized the array for both.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint At(uint[] t, int index)
    {
#if CHECKED
        return t[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of the 704-entry insert-and-copy table. The caller guarantees <c>0 &lt;= index &lt; 704</c>: the index is a symbol decoded
    /// from a prefix code over an alphabet of 704 symbols.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong At(ulong[] t, int index)
    {
#if CHECKED
        return t[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>t[index]</c> of the 768-byte context table. The caller guarantees <c>0 &lt;= index &lt; 768</c>: the index is a byte of output plus 0, 256
    /// or 512.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int At768(byte[] t, int index)
    {
#if CHECKED
        return t[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>t[index &amp; 63]</c> of the 64 code offsets of one literal block type; the index is masked, so it is below the span's length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int At64(ReadOnlySpan<int> t, int index)
    {
#if CHECKED
        return t[index & 63];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(t), (nint)(uint)(index & 63));
#endif
    }

    /// <summary>
    /// <c>t[index &amp; 15]</c> of a 16-entry distance symbol table; the index is masked, so it is below the table's length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AtIndex(byte[] t, int index)
    {
#if CHECKED
        return t[index & 15];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)(index & 15));
#endif
    }

    /// <inheritdoc cref="AtIndex"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AtDelta(sbyte[] t, int index)
    {
#if CHECKED
        return t[index & 15];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(t), (nint)(uint)(index & 15));
#endif
    }

    /// <summary>
    /// Copies 16 bytes from <paramref name="distance"/> bytes back to <paramref name="destination"/>. The caller guarantees
    /// <c>16 &lt;= distance &lt;= destination</c> (so the ranges do not overlap) and <c>destination + 16 &lt;= b.Length</c> (the destination is below
    /// the ring's capacity and the array is <see cref="RingSlack"/> larger). The copy is whole even when the match is shorter: the surplus
    /// lands ahead of the write position (see <see cref="RingSlack"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Copy16(byte[] b, int destination, int distance)
    {
#if CHECKED
        if (distance < 16 || distance > destination || destination + 16 > b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        for (int i = 0; i < 16; i++)
        {
            b[destination + i] = b[destination - distance + i];
        }
#else
        ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)destination);
        Unsafe.WriteUnaligned(ref d, Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(ref Unsafe.Subtract(ref d, distance)));
#endif
    }

    /// <summary>
    /// <c>b[index]</c>. The caller guarantees <c>0 &lt;= index &lt; b.Length</c>: a position inside the ring, which is below the ring's capacity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte At(byte[] b, int index)
    {
#if CHECKED
        return b[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)index);
#endif
    }

    /// <summary>
    /// <c>b[index] = value</c>. The caller guarantees <c>0 &lt;= index &lt; b.Length</c>: the fast loop writes only below a limit that is at
    /// most the ring's capacity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Set(byte[] b, int index, byte value)
    {
#if CHECKED
        b[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)index) = value;
#endif
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes inside the ring from <paramref name="distance"/> bytes back to <paramref name="destination"/>,
    /// repeating the output when the ranges overlap (RFC 7932 section 4). The caller guarantees <c>1 &lt;= distance &lt;= destination</c>,
    /// <c>length &gt;= 1</c> and <c>destination + length + 16 &lt;= b.Length</c>: the destination end is at most the ring's capacity and
    /// the array is <see cref="RingSlack"/> larger than that. Up to 15 bytes past the end are overwritten with copies of earlier output
    /// (see <see cref="RingSlack"/>).
    /// </summary>
    internal static void CopyMatch(byte[] b, int destination, int distance, int length)
    {
#if CHECKED
        if (distance < 1 || distance > destination || length < 1 || destination + length + 16 > b.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(distance));
        }

        for (int i = 0; i < length; i++)
        {
            b[destination + i] = b[destination - distance + i];
        }
#else
        ref byte d = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), (nint)(uint)destination);
        ref byte s = ref Unsafe.Subtract(ref d, distance);
        if (distance >= 16)
        {
            int i = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i), Unsafe.ReadUnaligned<System.Runtime.Intrinsics.Vector128<byte>>(ref Unsafe.Add(ref s, i)));
                i += 16;
            }
            while (i < length);
        }
        else if (distance >= 8)
        {
            int i = 0;
            do
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, i)));
                i += 8;
            }
            while (i < length);
        }
        else
        {
            // The output repeats with period distance, so it also repeats with any multiple of it. Write the first multiple that is
            // at least eight bytes bytewise, then go on eight bytes at a time from that distance.
            int period = distance * ((8 + distance - 1) / distance);
            int head = Math.Min(length, period);
            for (int i = 0; i < head; i++)
            {
                Unsafe.Add(ref d, i) = Unsafe.Add(ref s, i);
            }

            if (length > period)
            {
                int i = period;
                do
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, i), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref d, i - period)));
                    i += 8;
                }
                while (i < length);
            }
        }
#endif
    }
}
