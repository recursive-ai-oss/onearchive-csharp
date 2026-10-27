using System.Numerics;
using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// xxHash64 (xxHash specification, XXH64): a 64-bit non-cryptographic hash with a 64-bit seed. Zstandard frames store the
/// low 32 bits of the seed-0 value as their content checksum (RFC 8878 section 3.1.1). For input "" and seed 0 the value is 0xEF46DB3751D8E999.
/// Use <see cref="Compute(ReadOnlySpan{byte})"/> for a buffer, or append pieces to a value of this type; any split of the input gives the same value.
/// </summary>
/// <remarks>
/// The value is a mutable struct holding a 32-byte buffer: copy it only to snapshot the running state. The default value is
/// a hash of empty input with seed 0; use <see cref="XxHash64(ulong)"/> for another seed. Output is identical on every platform.
/// The four 64-bit multiplies per round have no SIMD form on the baseline instruction sets, so there is no vector path;
/// the four lanes already run in parallel in scalar registers.
/// </remarks>
public struct XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    private readonly ulong _seed;
    private ulong _v1, _v2, _v3, _v4;
    private ulong _total;
    private int _buffered;
    private Buffer32 _buffer;

    /// <summary>Creates a hash that starts from <paramref name="seed"/>.</summary>
    /// <param name="seed">The seed; <see cref="Reset"/> keeps it.</param>
    public XxHash64(ulong seed) : this()
    {
        _seed = seed;
    }

    /// <summary>Gets the hash of <paramref name="data"/> with seed 0.</summary>
    /// <param name="data">The bytes to hash; may be empty.</param>
    /// <returns>The 64-bit hash.</returns>
    public static ulong Compute(ReadOnlySpan<byte> data) => Compute(data, 0);

    /// <summary>Gets the hash of <paramref name="data"/> with the given seed.</summary>
    /// <param name="data">The bytes to hash; may be empty.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The 64-bit hash.</returns>
    public static ulong Compute(ReadOnlySpan<byte> data, ulong seed)
    {
        ulong v1 = seed + P1 + P2;
        ulong v2 = seed + P2;
        ulong v3 = seed;
        ulong v4 = seed - P1;
        int whole = data.Length & ~31;
        if (whole > 0)
        {
            Stripes(ref v1, ref v2, ref v3, ref v4, data, whole);
        }

        return Finish(v1, v2, v3, v4, (ulong)data.Length, seed, data.Slice(whole));
    }

    /// <summary>Adds <paramref name="data"/> to the running hash.</summary>
    /// <param name="data">The bytes to add; may be empty.</param>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (_total == 0)
        {
            _v1 = _seed + P1 + P2;
            _v2 = _seed + P2;
            _v3 = _seed;
            _v4 = _seed - P1;
        }

        _total += (ulong)data.Length;
        Span<byte> buf = _buffer;
        if (_buffered > 0)
        {
            int take = Math.Min(32 - _buffered, data.Length);
            data.Slice(0, take).CopyTo(buf.Slice(_buffered));
            _buffered += take;
            data = data.Slice(take);
            if (_buffered < 32)
            {
                return;
            }

            Stripes(ref _v1, ref _v2, ref _v3, ref _v4, buf, 32);
            _buffered = 0;
        }

        int whole = data.Length & ~31;
        if (whole > 0)
        {
            Stripes(ref _v1, ref _v2, ref _v3, ref _v4, data, whole);
        }

        data.Slice(whole).CopyTo(buf);
        _buffered = data.Length - whole;
    }

    /// <summary>Gets the hash of everything appended so far. The running state is unchanged.</summary>
    /// <returns>The 64-bit hash.</returns>
    public readonly ulong GetCurrentValue()
    {
        ReadOnlySpan<byte> buf = _buffer;
        return Finish(_v1, _v2, _v3, _v4, _total, _seed, buf.Slice(0, _buffered));
    }

    /// <summary>Returns to the state of a new, empty hash with the same seed.</summary>
    public void Reset()
    {
        _total = 0;
        _buffered = 0;
    }

    // XXH64 steps 1 and 2: four independent lanes, 32 bytes per round.
    private static void Stripes(ref ulong v1, ref ulong v2, ref ulong v3, ref ulong v4, ReadOnlySpan<byte> d, int end)
    {
        ulong a = v1, b = v2, c = v3, e = v4;
        for (int i = 0; i < end; i += 32)
        {
            a = Round(a, Mem.U64(d, i));
            b = Round(b, Mem.U64(d, i + 8));
            c = Round(c, Mem.U64(d, i + 16));
            e = Round(e, Mem.U64(d, i + 24));
        }

        v1 = a;
        v2 = b;
        v3 = c;
        v4 = e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + (input * P2), 31) * P1;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Merge(ulong h, ulong v) => ((h ^ Round(0, v)) * P1) + P4;

    // XXH64 steps 3 to 5: merge the lanes, add the length, consume the last (up to 31) bytes, avalanche.
    private static ulong Finish(ulong v1, ulong v2, ulong v3, ulong v4, ulong total, ulong seed, ReadOnlySpan<byte> tail)
    {
        ulong h;
        if (total >= 32)
        {
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
        {
            h = seed + P5;
        }

        h += total;
        int i = 0;
        for (; tail.Length - i >= 8; i += 8)
        {
            h ^= Round(0, Mem.U64(tail, i));
            h = (BitOperations.RotateLeft(h, 27) * P1) + P4;
        }

        if (tail.Length - i >= 4)
        {
            h ^= Mem.U32(tail, i) * P1;
            h = (BitOperations.RotateLeft(h, 23) * P2) + P3;
            i += 4;
        }

        for (; i < tail.Length; i++)
        {
            h ^= Mem.U8(tail, i) * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    [InlineArray(32)]
    private struct Buffer32
    {
        private byte _element;
    }
}
