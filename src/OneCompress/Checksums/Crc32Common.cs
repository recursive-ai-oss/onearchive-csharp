using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>Table-driven, folded and hardware-instruction kernels shared by the two 32-bit CRCs.</summary>
/// <remarks>All "state" values here are raw CRC registers: not inverted, as the CRC stores them.</remarks>
internal static class Crc32Common
{
    /// <summary>Builds eight 256-entry tables: table k maps a byte to its CRC followed by k zero bytes.</summary>
    public static uint[] BuildTables(uint reflectedPoly)
    {
        var t = new uint[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ reflectedPoly : c >> 1;
            }

            t[n] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int n = 0; n < 256; n++)
            {
                uint prev = t[((k - 1) * 256) + n];
                t[(k * 256) + n] = (prev >> 8) ^ t[prev & 0xFF];
            }
        }

        return t;
    }

    /// <summary>Builds the (low, high) constant pair for a fold of <paramref name="bits"/> bits; see <see cref="CrcFold"/>.</summary>
    public static Vector128<ulong> FoldConstants(uint reflectedPoly, int bits) =>
        Vector128.Create((ulong)XPow(reflectedPoly, bits + 31), XPow(reflectedPoly, bits - 33));

    /// <summary>Advances eight bytes (little-endian in <paramref name="v"/>) through the tables.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Step8(uint[] t, uint s, ulong v)
    {
        uint lo = (uint)v ^ s;
        uint hi = (uint)(v >> 32);
        return Mem.T(t, 7 * 256 + (lo & 0xFF)) ^ Mem.T(t, 6 * 256 + ((lo >> 8) & 0xFF)) ^
               Mem.T(t, 5 * 256 + ((lo >> 16) & 0xFF)) ^ Mem.T(t, 4 * 256 + (lo >> 24)) ^
               Mem.T(t, 3 * 256 + (hi & 0xFF)) ^ Mem.T(t, 2 * 256 + ((hi >> 8) & 0xFF)) ^
               Mem.T(t, 1 * 256 + ((hi >> 16) & 0xFF)) ^ Mem.T(t, hi >> 24);
    }

    /// <summary>Slice-by-8 table update; the portable twin of every faster path.</summary>
    public static uint Scalar(uint[] t, uint s, ReadOnlySpan<byte> d)
    {
        int i = 0;
        int n = d.Length;
        while (n - i >= 8)
        {
            s = Step8(t, s, Mem.U64(d, i));
            i += 8;
        }

        while (i < n)
        {
            s = Mem.T(t, (s ^ Mem.U8(d, i)) & 0xFF) ^ (s >> 8);
            i++;
        }

        return s;
    }

    /// <summary>
    /// Folds the leading whole blocks of <paramref name="d"/> when it holds at least <see cref="CrcFold.MinLength"/> bytes
    /// and the machine can; <paramref name="consumed"/> is 0 otherwise.
    /// </summary>
    public static uint Folded(uint[] t, Vector128<ulong> k512, Vector128<ulong> k128, uint s, ReadOnlySpan<byte> d, out int consumed)
    {
        if (d.Length < CrcFold.MinLength || !CrcFold.IsSupported)
        {
            consumed = 0;
            return s;
        }

        Vector128<ulong> r = CrcFold.Fold(d, s, k512, k128, out consumed);
        return Step8(t, Step8(t, 0, r.GetElement(0)), r.GetElement(1));
    }

    /// <summary>The CRC-32C instruction on x86 or Arm where present, else the tables. Same function either way.</summary>
    public static uint Castagnoli(uint[] t, uint s, ReadOnlySpan<byte> d)
    {
        int i = 0;
        int n = d.Length;
        if (Sse42.X64.IsSupported)
        {
            ulong c = s;
            while (n - i >= 8)
            {
                c = Sse42.X64.Crc32(c, Mem.U64(d, i));
                i += 8;
            }

            s = (uint)c;
            if (n - i >= 4)
            {
                s = Sse42.Crc32(s, Mem.U32(d, i));
                i += 4;
            }

            while (i < n)
            {
                s = Sse42.Crc32(s, Mem.U8(d, i));
                i++;
            }

            return s;
        }

        if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
        {
            while (n - i >= 8)
            {
                s = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32C(s, Mem.U64(d, i));
                i += 8;
            }

            while (i < n)
            {
                s = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(s, Mem.U8(d, i));
                i++;
            }

            return s;
        }

        return Scalar(t, s, d);
    }

    /// <summary>The Arm CRC-32 instruction where present, else the tables.</summary>
    public static uint Ieee(uint[] t, uint s, ReadOnlySpan<byte> d)
    {
        if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
        {
            int i = 0;
            int n = d.Length;
            while (n - i >= 8)
            {
                s = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32(s, Mem.U64(d, i));
                i += 8;
            }

            while (i < n)
            {
                s = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(s, Mem.U8(d, i));
                i++;
            }

            return s;
        }

        return Scalar(t, s, d);
    }

    // ---- GF(2)[x] arithmetic in the reflected representation: bit (31 - i) holds the coefficient of x^i. ----

    /// <summary>Returns a * b modulo the polynomial.</summary>
    public static uint Multiply(uint a, uint b, uint reflectedPoly)
    {
        uint r = 0;
        for (int i = 0; i < 32; i++)
        {
            if ((a & (0x80000000u >> i)) != 0)
            {
                r ^= b;
            }

            b = (b & 1) != 0 ? (b >> 1) ^ reflectedPoly : b >> 1;
        }

        return r;
    }

    /// <summary>Returns x^n modulo the polynomial.</summary>
    public static uint XPow(uint reflectedPoly, long n)
    {
        uint r = 0x80000000;
        uint b = 0x40000000;
        while (n > 0)
        {
            if ((n & 1) != 0)
            {
                r = Multiply(r, b, reflectedPoly);
            }

            n >>= 1;
            if (n > 0)
            {
                b = Multiply(b, b, reflectedPoly);
            }
        }

        return r;
    }

    /// <summary>The CRC of A followed by B from crc(A), crc(B) and the length of B (inversions cancel; see the tests).</summary>
    public static uint Combine(uint reflectedPoly, uint crcA, uint crcB, long lengthB)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lengthB);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthB, long.MaxValue / 8);
        return Multiply(crcA, XPow(reflectedPoly, lengthB * 8), reflectedPoly) ^ crcB;
    }
}
