using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using ArmAes = System.Runtime.Intrinsics.Arm.Aes;
using System.Runtime.Intrinsics.X86;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// Carry-less-multiply folding for reflected CRCs of 32 or 64 bits (Gopal et al., "Fast CRC Computation
/// for Generic Polynomials Using PCLMULQDQ Instruction", Intel, 2009), on x86 PCLMULQDQ and Arm PMULL.
/// </summary>
/// <remarks>
/// A 128-bit register holds 16 message bytes as a polynomial (bit k is the coefficient of x^(127-k)).
/// Folding it forward by d bits multiplies its low half by x^(d+64) and its high half by x^d, both
/// modulo the CRC polynomial, and adds the data d bits further on. A 64x64 carry-less product of
/// reflected operands lands one bit off, so each constant is x^(d+w-1) or x^(d+w-65) mod P
/// for a w-bit CRC rather than the plain power. When the data runs out, the register is fed to the
/// ordinary table-driven update from state zero: that finishes the reduction without Barrett constants.
/// </remarks>
internal static class CrcFold
{
    /// <summary>Gets whether this machine has a carry-less multiply and little-endian lanes.</summary>
    public static bool IsSupported =>
        BitConverter.IsLittleEndian && (Pclmulqdq.IsSupported || (ArmAes.IsSupported && AdvSimd.IsSupported));

    /// <summary>The minimum input length <see cref="Fold"/> accepts.</summary>
    public const int MinLength = 64;

    /// <summary>One fold step: <c>lo(r)*lo(k) ^ hi(r)*hi(k)</c> as carry-less products.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Step(Vector128<ulong> r, Vector128<ulong> k)
    {
        if (Pclmulqdq.IsSupported)
        {
            return Pclmulqdq.CarrylessMultiply(r, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(r, k, 0x11);
        }

        if (ArmAes.IsSupported)
        {
            return ArmAes.PolynomialMultiplyWideningLower(r.GetLower(), k.GetLower()) ^ ArmAes.PolynomialMultiplyWideningUpper(r, k);
        }

        throw new PlatformNotSupportedException();
    }

    /// <summary>
    /// Folds all whole 16-byte blocks of <paramref name="d"/> (at least <see cref="MinLength"/> bytes) into one
    /// register, with <paramref name="state"/> xor-ed into the first block as the CRC register is.
    /// </summary>
    /// <param name="d">The data.</param>
    /// <param name="state">The CRC register (zero-extended when the CRC is 32 bits wide).</param>
    /// <param name="k">The fold constants for the CRC polynomial.</param>
    /// <param name="consumed">The number of bytes folded (a multiple of 16).</param>
    public static Vector128<ulong> Fold(ReadOnlySpan<byte> d, ulong state, in FoldKeys k, out int consumed)
    {
        int n = d.Length;
        Vector128<ulong> x0 = Mem.V128(d, 0).AsUInt64() ^ Vector128.CreateScalar(state);
        int i;
        if (Pclmulqdq.V256.IsSupported && n >= 256)
        {
            // Wide path (VPCLMULQDQ): eight blocks in four 256-bit accumulators, 128 bytes per round. Each
            // 128-bit lane keeps the fold distance of the narrow path, so the result is the same polynomial.
            Vector256<ulong> y0 = Vector256.Create(x0, Mem.V128(d, 16).AsUInt64());
            Vector256<ulong> y1 = Mem.V256(d, 32).AsUInt64();
            Vector256<ulong> y2 = Mem.V256(d, 64).AsUInt64();
            Vector256<ulong> y3 = Mem.V256(d, 96).AsUInt64();
            Vector256<ulong> k1024 = Vector256.Create(k.K1024, k.K1024);
            i = 128;
            while (n - i >= 128)
            {
                y0 = Step(y0, k1024) ^ Mem.V256(d, i).AsUInt64();
                y1 = Step(y1, k1024) ^ Mem.V256(d, i + 32).AsUInt64();
                y2 = Step(y2, k1024) ^ Mem.V256(d, i + 64).AsUInt64();
                y3 = Step(y3, k1024) ^ Mem.V256(d, i + 96).AsUInt64();
                i += 128;
            }

            // Blocks b0..b7 sit in y0..y3 (two per register). Align y0 and y1 four blocks ahead onto y2 and y3,
            // then y0 two blocks ahead onto y1, then the low lane one block onto the high lane.
            Vector256<ulong> k512 = Vector256.Create(k.K512, k.K512);
            Vector256<ulong> k256 = Vector256.Create(k.K256, k.K256);
            y0 = Step(y0, k512) ^ y2;
            y1 = Step(y1, k512) ^ y3;
            y0 = Step(y0, k256) ^ y1;
            x0 = Step(y0.GetLower(), k.K128) ^ y0.GetUpper();
        }
        else if (n >= 256)
        {
            // Eight independent chains hide the multiply latency on cores that can issue more than one per cycle.
            Vector128<ulong> x1 = Mem.V128(d, 16).AsUInt64();
            Vector128<ulong> x2 = Mem.V128(d, 32).AsUInt64();
            Vector128<ulong> x3 = Mem.V128(d, 48).AsUInt64();
            Vector128<ulong> x4 = Mem.V128(d, 64).AsUInt64();
            Vector128<ulong> x5 = Mem.V128(d, 80).AsUInt64();
            Vector128<ulong> x6 = Mem.V128(d, 96).AsUInt64();
            Vector128<ulong> x7 = Mem.V128(d, 112).AsUInt64();
            i = 128;
            while (n - i >= 128)
            {
                x0 = Step(x0, k.K1024) ^ Mem.V128(d, i).AsUInt64();
                x1 = Step(x1, k.K1024) ^ Mem.V128(d, i + 16).AsUInt64();
                x2 = Step(x2, k.K1024) ^ Mem.V128(d, i + 32).AsUInt64();
                x3 = Step(x3, k.K1024) ^ Mem.V128(d, i + 48).AsUInt64();
                x4 = Step(x4, k.K1024) ^ Mem.V128(d, i + 64).AsUInt64();
                x5 = Step(x5, k.K1024) ^ Mem.V128(d, i + 80).AsUInt64();
                x6 = Step(x6, k.K1024) ^ Mem.V128(d, i + 96).AsUInt64();
                x7 = Step(x7, k.K1024) ^ Mem.V128(d, i + 112).AsUInt64();
                i += 128;
            }

            x0 = Step(x0, k.K128) ^ x1;
            x0 = Step(x0, k.K128) ^ x2;
            x0 = Step(x0, k.K128) ^ x3;
            x0 = Step(x0, k.K128) ^ x4;
            x0 = Step(x0, k.K128) ^ x5;
            x0 = Step(x0, k.K128) ^ x6;
            x0 = Step(x0, k.K128) ^ x7;
        }
        else
        {
            Vector128<ulong> x1 = Mem.V128(d, 16).AsUInt64();
            Vector128<ulong> x2 = Mem.V128(d, 32).AsUInt64();
            Vector128<ulong> x3 = Mem.V128(d, 48).AsUInt64();
            i = 64;
            while (n - i >= 64)
            {
                x0 = Step(x0, k.K512) ^ Mem.V128(d, i).AsUInt64();
                x1 = Step(x1, k.K512) ^ Mem.V128(d, i + 16).AsUInt64();
                x2 = Step(x2, k.K512) ^ Mem.V128(d, i + 32).AsUInt64();
                x3 = Step(x3, k.K512) ^ Mem.V128(d, i + 48).AsUInt64();
                i += 64;
            }

            x0 = Step(x0, k.K128) ^ x1;
            x0 = Step(x0, k.K128) ^ x2;
            x0 = Step(x0, k.K128) ^ x3;
        }

        while (n - i >= 16)
        {
            x0 = Step(x0, k.K128) ^ Mem.V128(d, i).AsUInt64();
            i += 16;
        }

        consumed = i;
        return x0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> Step(Vector256<ulong> r, Vector256<ulong> k) =>
        Pclmulqdq.V256.CarrylessMultiply(r, k, 0x00) ^ Pclmulqdq.V256.CarrylessMultiply(r, k, 0x11);
}

/// <summary>Fold constants (low lane for the low half of a register, high lane for the high half) for each fold distance used.</summary>
internal readonly struct FoldKeys
{
    public FoldKeys(Vector128<ulong> k1024, Vector128<ulong> k512, Vector128<ulong> k256, Vector128<ulong> k128)
    {
        K1024 = k1024;
        K512 = k512;
        K256 = k256;
        K128 = k128;
    }

    public Vector128<ulong> K1024 { get; }

    public Vector128<ulong> K512 { get; }

    public Vector128<ulong> K256 { get; }

    public Vector128<ulong> K128 { get; }
}
