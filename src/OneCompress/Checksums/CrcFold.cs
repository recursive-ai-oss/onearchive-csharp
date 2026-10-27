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
    /// <param name="k512">Constants for a 512-bit fold: low lane multiplies the low half, high lane the high half.</param>
    /// <param name="k128">Constants for a 128-bit fold.</param>
    /// <param name="consumed">The number of bytes folded (a multiple of 16).</param>
    public static Vector128<ulong> Fold(ReadOnlySpan<byte> d, ulong state, Vector128<ulong> k512, Vector128<ulong> k128, out int consumed)
    {
        int n = d.Length;
        Vector128<ulong> x0 = Mem.V128(d, 0).AsUInt64() ^ Vector128.CreateScalar(state);
        Vector128<ulong> x1 = Mem.V128(d, 16).AsUInt64();
        Vector128<ulong> x2 = Mem.V128(d, 32).AsUInt64();
        Vector128<ulong> x3 = Mem.V128(d, 48).AsUInt64();
        int i = 64;
        while (n - i >= 64)
        {
            x0 = Step(x0, k512) ^ Mem.V128(d, i).AsUInt64();
            x1 = Step(x1, k512) ^ Mem.V128(d, i + 16).AsUInt64();
            x2 = Step(x2, k512) ^ Mem.V128(d, i + 32).AsUInt64();
            x3 = Step(x3, k512) ^ Mem.V128(d, i + 48).AsUInt64();
            i += 64;
        }

        x0 = Step(x0, k128) ^ x1;
        x0 = Step(x0, k128) ^ x2;
        x0 = Step(x0, k128) ^ x3;
        while (n - i >= 16)
        {
            x0 = Step(x0, k128) ^ Mem.V128(d, i).AsUInt64();
            i += 16;
        }

        consumed = i;
        return x0;
    }
}
