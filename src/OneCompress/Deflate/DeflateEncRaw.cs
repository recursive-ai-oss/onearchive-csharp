using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
#endif

namespace RecursiveAI.OneCompress.Deflate;

/// <summary>
/// The only place in the Deflate encoder that skips bounds checks. Each accessor states the bound its caller guarantees by
/// construction. Under the <c>Checked</c> build configuration (symbol CHECKED) every accessor is bounds-checked instead, and
/// the tests and the fuzzer run in that configuration.
/// </summary>
internal static class DeflateEncRaw
{
    /// <summary>Reads <c>table[index]</c>. The caller guarantees <c>index &lt; table.Length</c>: indices are hash values shifted down to the table's bit width, positions masked to the window, or counted loops.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Get(int[] table, int index)
    {
#if CHECKED
        return table[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)(uint)index);
#endif
    }

    /// <summary>Writes <c>table[index]</c>. The same bound as <see cref="Get"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(int[] table, int index, int value)
    {
#if CHECKED
        table[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)(uint)index) = value;
#endif
    }

    /// <summary>Reads <c>data[index]</c>. The caller guarantees <c>index &lt; data.Length</c> (match candidates lie before the current position, and the compared byte lies inside the filled window).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Byte(ReadOnlySpan<byte> data, int index)
    {
#if CHECKED
        return data[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(data), (nint)(uint)index);
#endif
    }

    /// <summary>Reads four bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 4 &lt;= data.Length</c> (positions are hashed only with four bytes of lookahead, and the window has a margin past the filled part).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(ReadOnlySpan<byte> data, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> as a little-endian integer. The caller guarantees <c>index + 8 &lt;= data.Length</c> (comparisons run only while eight bytes remain before the maximum match length).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> data, int index)
    {
#if CHECKED
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), (nint)(uint)index));
        return BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>
    /// The number of equal bytes at the start of <c>data[a..]</c> and <c>data[b..]</c>, up to <paramref name="max"/>.
    /// The caller guarantees <c>a &lt; b</c> and <c>b + max &lt;= data.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MatchLength(ReadOnlySpan<byte> data, int a, int b, int max)
    {
        int length = 0;
        while (length + 8 <= max)
        {
            ulong difference = Read64(data, a + length) ^ Read64(data, b + length);
            if (difference != 0)
            {
                return length + (System.Numerics.BitOperations.TrailingZeroCount(difference) >> 3);
            }

            length += 8;
        }

        while (length < max && Byte(data, a + length) == Byte(data, b + length))
        {
            length++;
        }

        return length;
    }
}
