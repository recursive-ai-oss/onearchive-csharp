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
