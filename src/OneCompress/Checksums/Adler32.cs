using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// Adler-32 (RFC 1950 section 8.2): two sums modulo 65521, initial value 1. The check value of "123456789" is 0x091E01DE.
/// Use <see cref="Compute"/> for a buffer, or append pieces to a value of this type; any split of the input gives the same value.
/// </summary>
/// <remarks>
/// The value is a mutable struct: copy it only to snapshot the running state. The default value is the checksum of
/// empty input. Output is identical on every platform; the vector paths compute the same function as the scalar path.
/// </remarks>
public struct Adler32
{
    private const uint Base = 65521;

    // The longest run in which the second sum cannot overflow 32 bits before the modulo (RFC 1950 section 8.2
    // leaves the deferral to the implementation; 5552 is the bound for byte values up to 255). A multiple of 16.
    private const int MaxRun = 5552;

    // Stored as value ^ 1 so that default(Adler32) is the empty checksum (value 1).
    private uint _stored;

    /// <summary>Gets the checksum of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to checksum; may be empty.</param>
    /// <returns>The 32-bit checksum (second sum in the high 16 bits); the checksum of empty input is 1.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(1, data);

    /// <summary>Continues a checksum: returns the checksum of the earlier input followed by <paramref name="data"/>.</summary>
    /// <param name="checksum">The checksum of the earlier input (1 for none).</param>
    /// <param name="data">The bytes to add; may be empty.</param>
    /// <returns>The checksum of everything so far.</returns>
    public static uint Update(uint checksum, ReadOnlySpan<byte> data) => Run(checksum, data, Kernel.Best);

    /// <summary>Adds <paramref name="data"/> to the running checksum.</summary>
    /// <param name="data">The bytes to add; may be empty.</param>
    public void Append(ReadOnlySpan<byte> data) => _stored = Update(_stored ^ 1, data) ^ 1;

    /// <summary>Gets the checksum of everything appended so far. The running state is unchanged.</summary>
    /// <returns>The 32-bit checksum.</returns>
    public readonly uint GetCurrentValue() => _stored ^ 1;

    /// <summary>Returns to the state of a new, empty checksum.</summary>
    public void Reset() => _stored = 0;

    internal enum Kernel
    {
        Best,
        Scalar,
        Vector128,
        Avx2,
    }

    internal static bool IsSupported(Kernel k) => k switch
    {
        Kernel.Vector128 => Vector128.IsHardwareAccelerated,
        Kernel.Avx2 => Avx2.IsSupported,
        _ => true,
    };

    internal static uint Run(uint checksum, ReadOnlySpan<byte> d, Kernel kernel)
    {
        if (kernel == Kernel.Best)
        {
            kernel = Avx2.IsSupported ? Kernel.Avx2 : Vector128.IsHardwareAccelerated ? Kernel.Vector128 : Kernel.Scalar;
        }

        uint a = checksum & 0xFFFF;
        uint b = checksum >> 16;
        int i = 0;
        int n = d.Length;
        while (i < n)
        {
            int end = i + Math.Min(n - i, MaxRun);
            if (kernel == Kernel.Avx2 && end - i >= 32)
            {
                i = RunAvx2(ref a, ref b, d, i, end);
            }

            if (kernel != Kernel.Scalar && end - i >= 16)
            {
                i = RunVector128(ref a, ref b, d, i, end);
            }

            for (; i < end; i++)
            {
                a += Mem.U8(d, i);
                b += a;
            }

            a %= Base;
            b %= Base;
        }

        return (b << 16) | a;
    }

    // For a run of N bytes starting with sums (a0, b0): a = a0 + sum(x_p), b = b0 + N*a0 + sum((N - p) * x_p).
    // Per block of B bytes, byte i of block j contributes (n-j-1)*B + (B-i), where n is the block count.
    // The first part is B times the sum over blocks of the byte total of all earlier blocks, kept in `prefix`.

    private static int RunVector128(ref uint a, ref uint b, ReadOnlySpan<byte> d, int i, int end)
    {
        int blocks = (end - i) / 16;
        Vector128<uint> sums = Vector128<uint>.Zero;
        Vector128<uint> prefix = Vector128<uint>.Zero;
        Vector128<uint> weighted = Vector128<uint>.Zero;
        Vector128<ushort> wLo = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
        Vector128<ushort> wHi = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
        for (int j = 0; j < blocks; j++)
        {
            Vector128<byte> v = Mem.V128(d, i);
            Vector128<ushort> lo = Vector128.WidenLower(v);
            Vector128<ushort> hi = Vector128.WidenUpper(v);
            prefix += sums;
            Vector128<ushort> both = lo + hi;
            sums += Vector128.WidenLower(both) + Vector128.WidenUpper(both);
            Vector128<ushort> w = (lo * wLo) + (hi * wHi);
            weighted += Vector128.WidenLower(w) + Vector128.WidenUpper(w);
            i += 16;
        }

        uint total = Vector128.Sum(sums);
        b += ((uint)blocks * 16 * a) + (16 * Vector128.Sum(prefix)) + Vector128.Sum(weighted);
        a += total;
        return i;
    }

    private static int RunAvx2(ref uint a, ref uint b, ReadOnlySpan<byte> d, int i, int end)
    {
        int blocks = (end - i) / 32;
        Vector256<uint> sums = Vector256<uint>.Zero;
        Vector256<uint> prefix = Vector256<uint>.Zero;
        Vector256<uint> weighted = Vector256<uint>.Zero;
        Vector256<byte> zero = Vector256<byte>.Zero;
        Vector256<sbyte> weights = Vector256.Create(
            (sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17,
            16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
        Vector256<short> ones = Vector256.Create((short)1);
        for (int j = 0; j < blocks; j++)
        {
            Vector256<byte> v = Mem.V256(d, i);
            prefix += sums;
            // Eight byte-sums land in the low 16 bits of each 64-bit lane; the other lanes stay zero.
            sums += Avx2.SumAbsoluteDifferences(v, zero).AsUInt32();
            Vector256<short> pairs = Avx2.MultiplyAddAdjacent(v, weights);
            weighted += Avx2.MultiplyAddAdjacent(pairs, ones).AsUInt32();
            i += 32;
        }

        uint total = Vector256.Sum(sums);
        b += ((uint)blocks * 32 * a) + (32 * Vector256.Sum(prefix)) + Vector256.Sum(weighted);
        a += total;
        return i;
    }
}
