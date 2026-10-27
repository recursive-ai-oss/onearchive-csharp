using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// xxHash32 (xxHash specification, XXH32): a 32-bit non-cryptographic hash with a 32-bit seed, used by the LZ4
/// frame format for header and content checksums. For input "" and seed 0 the value is 0x02CC5D05.
/// Use <see cref="Compute(ReadOnlySpan{byte})"/> for a buffer, or append pieces to a value of this type; any split of the input gives the same value.
/// </summary>
/// <remarks>
/// The value is a mutable struct holding a 16-byte buffer: copy it only to snapshot the running state. The default value is
/// a hash of empty input with seed 0; use <see cref="XxHash32(uint)"/> for another seed. Output is identical on every platform;
/// the vector path computes the same function as the scalar path.
/// </remarks>
public struct XxHash32
{
    private const uint P1 = 2654435761U;
    private const uint P2 = 2246822519U;
    private const uint P3 = 3266489917U;
    private const uint P4 = 668265263U;
    private const uint P5 = 374761393U;

    private readonly uint _seed;
    private uint _v1, _v2, _v3, _v4;
    private ulong _total;
    private int _buffered;
    private Buffer16 _buffer;

    /// <summary>Creates a hash that starts from <paramref name="seed"/>.</summary>
    /// <param name="seed">The seed; <see cref="Reset"/> keeps it.</param>
    public XxHash32(uint seed) : this()
    {
        _seed = seed;
    }

    /// <summary>Gets the hash of <paramref name="data"/> with seed 0.</summary>
    /// <param name="data">The bytes to hash; may be empty.</param>
    /// <returns>The 32-bit hash.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Compute(data, 0, vector: true);

    /// <summary>Gets the hash of <paramref name="data"/> with the given seed.</summary>
    /// <param name="data">The bytes to hash; may be empty.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The 32-bit hash.</returns>
    public static uint Compute(ReadOnlySpan<byte> data, uint seed) => Compute(data, seed, vector: true);

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
            int take = Math.Min(16 - _buffered, data.Length);
            data.Slice(0, take).CopyTo(buf.Slice(_buffered));
            _buffered += take;
            data = data.Slice(take);
            if (_buffered < 16)
            {
                return;
            }

            Stripes(ref _v1, ref _v2, ref _v3, ref _v4, buf, 16, vector: true);
            _buffered = 0;
        }

        int whole = data.Length & ~15;
        if (whole > 0)
        {
            Stripes(ref _v1, ref _v2, ref _v3, ref _v4, data, whole, vector: true);
        }

        data.Slice(whole).CopyTo(buf);
        _buffered = data.Length - whole;
    }

    /// <summary>Gets the hash of everything appended so far. The running state is unchanged.</summary>
    /// <returns>The 32-bit hash.</returns>
    public readonly uint GetCurrentValue()
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

    internal static uint Compute(ReadOnlySpan<byte> d, uint seed, bool vector)
    {
        uint v1 = seed + P1 + P2;
        uint v2 = seed + P2;
        uint v3 = seed;
        uint v4 = seed - P1;
        int whole = d.Length & ~15;
        if (whole > 0)
        {
            Stripes(ref v1, ref v2, ref v3, ref v4, d, whole, vector);
        }

        return Finish(v1, v2, v3, v4, (ulong)d.Length, seed, d.Slice(whole));
    }

    // XXH32 steps 1 and 2: four independent lanes, 16 bytes per round. The vector form holds the lanes in one register.
    private static void Stripes(ref uint v1, ref uint v2, ref uint v3, ref uint v4, ReadOnlySpan<byte> d, int end, bool vector)
    {
        int i = 0;
        if (vector && Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            Vector128<uint> acc = Vector128.Create(v1, v2, v3, v4);
            for (; i < end; i += 16)
            {
                Vector128<uint> input = Mem.V128(d, i).AsUInt32();
                acc += input * P2;
                acc = Vector128.ShiftLeft(acc, 13) | Vector128.ShiftRightLogical(acc, 19);
                acc *= P1;
            }

            v1 = acc.GetElement(0);
            v2 = acc.GetElement(1);
            v3 = acc.GetElement(2);
            v4 = acc.GetElement(3);
            return;
        }

        uint a = v1, b = v2, c = v3, e = v4;
        for (; i < end; i += 16)
        {
            a = Round(a, Mem.U32(d, i));
            b = Round(b, Mem.U32(d, i + 4));
            c = Round(c, Mem.U32(d, i + 8));
            e = Round(e, Mem.U32(d, i + 12));
        }

        v1 = a;
        v2 = b;
        v3 = c;
        v4 = e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Round(uint acc, uint input) => BitOperations.RotateLeft(acc + (input * P2), 13) * P1;

    // XXH32 steps 3 to 5: merge the lanes, add the length, consume the last (up to 15) bytes, avalanche.
    private static uint Finish(uint v1, uint v2, uint v3, uint v4, ulong total, uint seed, ReadOnlySpan<byte> tail)
    {
        uint h = total >= 16
            ? BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18)
            : seed + P5;
        h += (uint)total;
        int i = 0;
        for (; tail.Length - i >= 4; i += 4)
        {
            h = BitOperations.RotateLeft(h + (Mem.U32(tail, i) * P3), 17) * P4;
        }

        for (; i < tail.Length; i++)
        {
            h = BitOperations.RotateLeft(h + (Mem.U8(tail, i) * P5), 11) * P1;
        }

        h ^= h >> 15;
        h *= P2;
        h ^= h >> 13;
        h *= P3;
        h ^= h >> 16;
        return h;
    }

    [InlineArray(16)]
    private struct Buffer16
    {
        private byte _element;
    }
}
