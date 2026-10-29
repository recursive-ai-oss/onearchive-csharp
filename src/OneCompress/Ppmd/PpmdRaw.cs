using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
#endif

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>
/// The only place in the PPMd models that skips bounds checks. The model memory is one byte array; every offset the models
/// pass here comes from the model's own sub-allocator (a text pointer below the units area, or a unit or state the allocator
/// handed out inside the array), never directly from the compressed input: any bit stream selects a path through the model,
/// and the model's links stay inside its array whatever the stream says. The accessors read and write whole fields at
/// offsets with at least 4 (or 2) bytes behind them inside the array. Under the <c>Checked</c> build configuration
/// (symbol CHECKED) every accessor is bounds-checked instead, and the tests and the fuzzer run in that configuration.
/// </summary>
internal static class PpmdRaw
{
    /// <summary>A reference to <c>m[index]</c>. The caller guarantees <c>0 &lt;= index &lt; m.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref byte At(Span<byte> m, int index)
    {
#if CHECKED
        return ref m[index];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index);
#endif
    }

    /// <summary>A reference to <c>table[symbol]</c> for a 256-entry table: a byte cannot index past it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ref byte Slot(byte[] table, byte symbol)
    {
#if CHECKED
        return ref table[symbol];
#else
        return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)symbol);
#endif
    }

    /// <summary>Reads the little-endian 16-bit field at <c>index</c>. The caller guarantees <c>0 &lt;= index &lt;= m.Length - 2</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int U16(Span<byte> m, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(m.Slice(index, 2));
#else
        ushort v = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
#endif
    }

    /// <summary>Reads the little-endian 32-bit field at <c>index</c>. The caller guarantees <c>0 &lt;= index &lt;= m.Length - 4</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int U32(Span<byte> m, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(m.Slice(index, 4));
#else
        int v = Unsafe.ReadUnaligned<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v);
#endif
    }

    /// <summary>Writes the low 16 bits of <paramref name="value"/> little-endian at <c>index</c>. The caller guarantees <c>0 &lt;= index &lt;= m.Length - 2</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetU16(Span<byte> m, int index, int value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(m.Slice(index, 2), (ushort)value);
#else
        ushort v = (ushort)value;
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index), BitConverter.IsLittleEndian ? v : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(v));
#endif
    }

    /// <summary>Writes <paramref name="value"/> little-endian at <c>index</c>. The caller guarantees <c>0 &lt;= index &lt;= m.Length - 4</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetU32(Span<byte> m, int index, int value)
    {
#if CHECKED
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(m.Slice(index, 4), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index), BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));
#endif
    }

    /// <summary>
    /// Adds up the frequencies of the symbols in <paramref name="count"/> consecutive 6-byte states starting at <paramref name="first"/> that are
    /// not masked (<c>mask[symbol]</c> is 0xFF for a symbol still possible, 0 for one already ruled out). The states are one symbol list the
    /// model's allocator handed out, so the whole range lies inside the array. The loop keeps everything in registers: it is the hottest
    /// loop after an escape from a context with many symbols.
    /// </summary>
    public static uint SumUnmasked(Span<byte> m, byte[] mask, int first, int count)
    {
#if CHECKED
        uint total = 0;
        for (int i = 0; i < count; i++, first += 6)
        {
            total += (uint)(m[first + 1] & mask[m[first]]);
        }

        return total;
#else
        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)first);
        ref byte mk = ref MemoryMarshal.GetArrayDataReference(mask);
        uint a = 0;
        uint b = 0;
        for (; count >= 4; count -= 4, p = ref Unsafe.Add(ref p, 24))
        {
            a += (uint)(Unsafe.Add(ref p, 1) & Unsafe.Add(ref mk, (nint)p));
            b += (uint)(Unsafe.Add(ref p, 7) & Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 6)));
            a += (uint)(Unsafe.Add(ref p, 13) & Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 12)));
            b += (uint)(Unsafe.Add(ref p, 19) & Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 18)));
        }

        for (; count > 0; count--, p = ref Unsafe.Add(ref p, 6))
        {
            a += (uint)(Unsafe.Add(ref p, 1) & Unsafe.Add(ref mk, (nint)p));
        }

        return a + b;
#endif
    }

    /// <summary>
    /// Walks the 6-byte states from <paramref name="first"/> subtracting each unmasked frequency from <paramref name="count"/> until it goes
    /// negative (as a signed value) and returns the index of the state that did it. The caller has checked that <paramref name="count"/> is
    /// below the sum <see cref="SumUnmasked"/> gives for the list, so the walk ends inside the list.
    /// </summary>
    public static int SeekUnmasked(Span<byte> m, byte[] mask, int first, ref uint count)
    {
        uint c = count;
#if CHECKED
        int s = first;
        for (;;)
        {
            c -= (uint)(m[s + 1] & mask[m[s]]);
            if ((int)c < 0)
            {
                break;
            }

            s += 6;
        }

        count = c;
        return s;
#else
        ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)first);
        ref byte p = ref start;
        ref byte mk = ref MemoryMarshal.GetArrayDataReference(mask);
        for (;;)
        {
            c -= (uint)(Unsafe.Add(ref p, 1) & Unsafe.Add(ref mk, (nint)p));
            if ((int)c < 0)
            {
                break;
            }

            p = ref Unsafe.Add(ref p, 6);
        }

        count = c;
        return first + (int)Unsafe.ByteOffset(ref start, ref p);
#endif
    }

    /// <summary>
    /// Looks for <paramref name="symbol"/> among <paramref name="count"/> consecutive 6-byte states from <paramref name="first"/>, adding the
    /// unmasked frequencies of the states before it to <paramref name="sum"/>. Returns the number of states passed (the match's position), or
    /// <paramref name="count"/> when the symbol is not there. Same bounds as <see cref="SumUnmasked"/>.
    /// </summary>
    public static int FindSymbolUnmasked(Span<byte> m, byte[] mask, int first, int count, int symbol, out uint sum)
    {
        uint total = 0;
        int i = 0;
#if CHECKED
        for (; i < count && m[first] != symbol; i++, first += 6)
        {
            total += (uint)(m[first + 1] & mask[m[first]]);
        }
#else
        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)first);
        ref byte mk = ref MemoryMarshal.GetArrayDataReference(mask);
        for (; i < count && p != symbol; i++, p = ref Unsafe.Add(ref p, 6))
        {
            total += (uint)(Unsafe.Add(ref p, 1) & Unsafe.Add(ref mk, (nint)p));
        }
#endif
        sum = total;
        return i;
    }

    /// <summary>Clears <c>mask[symbol]</c> for the symbol of each of <paramref name="count"/> consecutive 6-byte states starting at <paramref name="first"/> (same bounds as <see cref="SumUnmasked"/>).</summary>
    public static void MaskStates(Span<byte> m, byte[] mask, int first, int count)
    {
#if CHECKED
        for (int i = 0; i < count; i++, first += 6)
        {
            mask[m[first]] = 0;
        }
#else
        ref byte p = ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)first);
        ref byte mk = ref MemoryMarshal.GetArrayDataReference(mask);
        for (; count >= 4; count -= 4, p = ref Unsafe.Add(ref p, 24))
        {
            Unsafe.Add(ref mk, (nint)p) = 0;
            Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 6)) = 0;
            Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 12)) = 0;
            Unsafe.Add(ref mk, (nint)Unsafe.Add(ref p, 18)) = 0;
        }

        for (; count > 0; count--, p = ref Unsafe.Add(ref p, 6))
        {
            Unsafe.Add(ref mk, (nint)p) = 0;
        }
#endif
    }

    /// <summary>Swaps the 6-byte states at <paramref name="a"/> and <paramref name="b"/>; both are states of one symbol list, so both lie inside the array.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap6(Span<byte> m, int a, int b)
    {
        int aLo = U32(m, a);
        int aHi = U16(m, a + 4);
        int bLo = U32(m, b);
        int bHi = U16(m, b + 4);
        SetU32(m, a, bLo);
        SetU16(m, a + 4, bHi);
        SetU32(m, b, aLo);
        SetU16(m, b + 4, aHi);
    }

    /// <summary>
    /// Hints that the model memory at <paramref name="index"/> (the context a likely symbol leads to) is about to be read: the memory is far
    /// larger than the caches and the next symbol starts with a read of that context. It changes no result; it does nothing under
    /// <c>Checked</c> or on CPUs without a prefetch instruction, and an index outside the array is ignored (a hint never faults).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Prefetch(Span<byte> m, int index)
    {
#if !CHECKED
        if (Sse.IsSupported && (uint)index < (uint)m.Length)
        {
            unsafe
            {
                Sse.Prefetch0(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(m), (nint)(uint)index)));
            }
        }
#endif
    }

    /// <summary>Hints that the symbol list of context <paramref name="context"/> is about to be read (see <see cref="Prefetch"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PrefetchStats(Span<byte> m, int context) => Prefetch(m, U32(m, context + 4));
}
