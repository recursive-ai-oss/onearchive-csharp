using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
#if !CHECKED
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// The only place in the LZ4 encoder that skips bounds checks: unaligned reads of the input, wide copies, and table
/// accesses whose index was produced by a hash of a fixed width or masked to a power of two. Each accessor states the
/// bound its caller guarantees. Under the <c>Checked</c> build configuration (symbol CHECKED) every accessor is
/// bounds-checked instead, and the tests and the fuzzer run in that configuration.
/// </summary>
internal static class Lz4EncRaw
{
    /// <summary>Reads four bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads four bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 4 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Read32(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt32LittleEndian(span.Slice((int)index, 4));
#else
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads eight bytes at <paramref name="index"/> (little-endian). The caller guarantees <c>0 &lt;= index &amp;&amp; index + 8 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read64(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return BinaryPrimitives.ReadUInt64LittleEndian(span.Slice((int)index, 8));
#else
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
#endif
    }

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Get(ReadOnlySpan<byte> span, int index)
    {
#if CHECKED
        return span[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index);
#endif
    }

    /// <summary>Reads <c>span[index]</c>. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Get(ReadOnlySpan<byte> span, nint index)
    {
#if CHECKED
        return span[(int)index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetReference(span), index);
#endif
    }

    /// <summary>Writes one byte. The caller guarantees <c>0 &lt;= index &lt; span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(Span<byte> span, int index, byte value)
    {
#if CHECKED
        span[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index) = value;
#endif
    }

    /// <summary>Writes a little-endian 16-bit value. The caller guarantees <c>0 &lt;= index &amp;&amp; index + 2 &lt;= span.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put16(Span<byte> span, int index, ushort value)
    {
#if CHECKED
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(index, 2), value);
#else
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (nint)index), BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));
#endif
    }

    /// <summary>
    /// Copies 16 bytes. The caller guarantees both 16-byte ranges lie inside their spans; the ranges may touch the bytes
    /// after the part that matters, which the caller overwrites or ignores.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Copy16(Span<byte> destination, int destinationIndex, ReadOnlySpan<byte> source, int sourceIndex)
    {
#if CHECKED
        source.Slice(sourceIndex, 16).CopyTo(destination.Slice(destinationIndex, 16));
#else
        Vector128<byte> v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref MemoryMarshal.GetReference(source), (nint)sourceIndex));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(destination), (nint)destinationIndex), v);
#endif
    }

    /// <summary>Reads <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c> (a hash shifted down to the table's size).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Get(uint[] table, int index)
    {
#if CHECKED
        return table[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index);
#endif
    }

    /// <summary>Writes <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(uint[] table, int index, uint value)
    {
#if CHECKED
        table[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index) = value;
#endif
    }

    /// <summary>Reads <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c> (an index masked to the table's power-of-two size).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Get(ushort[] table, int index)
    {
#if CHECKED
        return table[index];
#else
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index);
#endif
    }

    /// <summary>Writes <c>table[index]</c>. The caller guarantees <c>0 &lt;= index &lt; table.Length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Put(ushort[] table, int index, ushort value)
    {
#if CHECKED
        table[index] = value;
#else
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(table), (nint)index) = value;
#endif
    }

    /// <summary>
    /// Counts the equal bytes of <c>span[a..]</c> and <c>span[b..]</c>, comparing at most up to <c>span[limit - 1]</c> on the
    /// <paramref name="b"/> side. The caller guarantees <c>0 &lt;= a &lt; b &lt;= limit &lt;= span.Length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonLength(ReadOnlySpan<byte> span, int a, int b, int limit)
    {
        int start = b;
        // Eight bytes at a time while the b side has eight bytes before the limit (and a < b, so the a side does too).
        while (b + 8 <= limit)
        {
            ulong diff = Read64(span, a) ^ Read64(span, b);
            if (diff != 0)
            {
                return b - start + (BitOperations.TrailingZeroCount(diff) >> 3);
            }

            a += 8;
            b += 8;
        }

        while (b < limit && Get(span, a) == Get(span, b))
        {
            a++;
            b++;
        }

        return b - start;
    }
}

/// <summary>
/// A hash table of 32-bit positions held by reference, so a hot loop does not re-check the array on every access. Indices are
/// produced by a hash shifted down to the table's size; each accessor states that bound. Under the <c>Checked</c> build
/// configuration it holds the array and bounds-checks.
/// </summary>
internal readonly ref struct Lz4Table
{
#if CHECKED
    private readonly uint[] _array;

    public Lz4Table(uint[] array) => _array = array;

    /// <summary>Reads entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get(nint index) => _array[index];

    /// <summary>Writes entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(nint index, uint value) => _array[index] = value;
#else
    private readonly ref uint _first;

    public Lz4Table(uint[] array) => _first = ref MemoryMarshal.GetArrayDataReference(array);

    /// <summary>Reads entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Get(nint index) => Unsafe.Add(ref _first, index);

    /// <summary>Writes entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(nint index, uint value) => Unsafe.Add(ref _first, index) = value;
#endif
}

/// <summary>The 16-bit counterpart of <see cref="Lz4Table"/>, for hash-chain links indexed by a position masked to the table's size.</summary>
internal readonly ref struct Lz4Table16
{
#if CHECKED
    private readonly ushort[] _array;

    public Lz4Table16(ushort[] array) => _array = array;

    /// <summary>Reads entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Get(nint index) => _array[index];
#else
    private readonly ref ushort _first;

    public Lz4Table16(ushort[] array) => _first = ref MemoryMarshal.GetArrayDataReference(array);

    /// <summary>Reads entry <paramref name="index"/>; the caller guarantees <c>0 &lt;= index &lt; table length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort Get(nint index) => Unsafe.Add(ref _first, index);
#endif
}

/// <summary>
/// The input of a block compressor held by reference, so a hot loop carries one register for it instead of a pointer and a
/// length. Indices are native-width and every accessor states the bound its caller guarantees; under the <c>Checked</c> build
/// configuration it holds a span and bounds-checks.
/// </summary>
internal readonly ref struct Lz4In
{
#if CHECKED
    private readonly ReadOnlySpan<byte> _span;

    public Lz4In(ReadOnlySpan<byte> span) => _span = span;

    /// <summary>Reads four bytes (little-endian); the caller guarantees <c>0 &lt;= index &amp;&amp; index + 4 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(nint index) => BinaryPrimitives.ReadUInt32LittleEndian(_span.Slice((int)index, 4));

    /// <summary>Reads eight bytes (little-endian); the caller guarantees <c>0 &lt;= index &amp;&amp; index + 8 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Read64(nint index) => BinaryPrimitives.ReadUInt64LittleEndian(_span.Slice((int)index, 8));

    /// <summary>Reads one byte; the caller guarantees <c>0 &lt;= index &lt; length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Get(nint index) => _span[(int)index];

    /// <summary>Copies 16 bytes to <paramref name="destination"/>; both ranges must lie inside their buffers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Copy16To(Lz4Out destination, nint destinationIndex, nint index) => destination.Write16(destinationIndex, _span.Slice((int)index, 16));
#else
    private readonly ref byte _first;

    public Lz4In(ReadOnlySpan<byte> span) => _first = ref MemoryMarshal.GetReference(span);

    /// <summary>Reads four bytes (little-endian); the caller guarantees <c>0 &lt;= index &amp;&amp; index + 4 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Read32(nint index)
    {
        uint value = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref _first, index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    /// <summary>Reads eight bytes (little-endian); the caller guarantees <c>0 &lt;= index &amp;&amp; index + 8 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong Read64(nint index)
    {
        ulong value = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref _first, index));
        return BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
    }

    /// <summary>Reads one byte; the caller guarantees <c>0 &lt;= index &lt; length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Get(nint index) => Unsafe.Add(ref _first, index);

    /// <summary>Copies 16 bytes to <paramref name="destination"/>; both ranges must lie inside their buffers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Copy16To(Lz4Out destination, nint destinationIndex, nint index) =>
        destination.Write16(destinationIndex, Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref _first, index)));
#endif

    /// <summary>
    /// Counts the equal bytes of the data at <paramref name="a"/> and <paramref name="b"/>, comparing at most up to
    /// <paramref name="limit"/> on the <paramref name="b"/> side. The caller guarantees <c>0 &lt;= a &lt; b &lt;= limit &lt;= length</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nint CommonLength(nint a, nint b, nint limit)
    {
        nint start = b;
        while (b + 8 <= limit)
        {
            ulong diff = Read64(a) ^ Read64(b);
            if (diff != 0)
            {
                return b - start + (BitOperations.TrailingZeroCount(diff) >> 3);
            }

            a += 8;
            b += 8;
        }

        while (b < limit && Get(a) == Get(b))
        {
            a++;
            b++;
        }

        return b - start;
    }
}

/// <summary>The output of a block compressor held by reference; see <see cref="Lz4In"/>.</summary>
internal readonly ref struct Lz4Out
{
#if CHECKED
    private readonly Span<byte> _span;

    public Lz4Out(Span<byte> span) => _span = span;

    /// <summary>Writes one byte; the caller guarantees <c>0 &lt;= index &lt; length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(nint index, byte value) => _span[(int)index] = value;

    /// <summary>Writes a little-endian 16-bit value; the caller guarantees <c>0 &lt;= index &amp;&amp; index + 2 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put16(nint index, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_span.Slice((int)index, 2), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write16(nint index, ReadOnlySpan<byte> sixteen) => sixteen.CopyTo(_span.Slice((int)index, 16));
#else
    private readonly ref byte _first;

    public Lz4Out(Span<byte> span) => _first = ref MemoryMarshal.GetReference(span);

    /// <summary>Writes one byte; the caller guarantees <c>0 &lt;= index &lt; length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put(nint index, byte value) => Unsafe.Add(ref _first, index) = value;

    /// <summary>Writes a little-endian 16-bit value; the caller guarantees <c>0 &lt;= index &amp;&amp; index + 2 &lt;= length</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Put16(nint index, ushort value) =>
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref _first, index), BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write16(nint index, Vector128<byte> sixteen) => Unsafe.WriteUnaligned(ref Unsafe.Add(ref _first, index), sixteen);
#endif
}
