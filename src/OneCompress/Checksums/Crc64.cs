using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// CRC-64 as the xz format uses it (ECMA-182 polynomial in reflected form 0xC96C5795D7870F42, initial value and
/// final xor 0xFFFFFFFFFFFFFFFF). The check value of "123456789" is 0x995DC9BBDF1939FA.
/// Use <see cref="Compute"/> for a buffer, or append pieces to a value of this type; any split of the input gives the same value.
/// </summary>
/// <remarks>
/// The value is a mutable struct: copy it only to snapshot the running state. The default value is the checksum of
/// empty input. Output is identical on every platform; the carry-less-multiply path computes the same function as the table path.
/// </remarks>
public struct Crc64
{
    private const ulong ReflectedPolynomial = 0xC96C5795D7870F42;

    private static readonly ulong[] Tables = BuildTables();
    private static readonly Vector128<ulong> K512 = Vector128.Create(XPow(512 + 63), XPow(512 - 1));
    private static readonly Vector128<ulong> K128 = Vector128.Create(XPow(128 + 63), XPow(128 - 1));

    private ulong _value;

    /// <summary>Gets the checksum of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to checksum; may be empty.</param>
    /// <returns>The 64-bit checksum; the checksum of empty input is 0.</returns>
    public static ulong Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a checksum: returns the checksum of the earlier input followed by <paramref name="data"/>.</summary>
    /// <param name="checksum">The checksum of the earlier input (0 for none).</param>
    /// <param name="data">The bytes to add; may be empty.</param>
    /// <returns>The checksum of everything so far.</returns>
    public static ulong Update(ulong checksum, ReadOnlySpan<byte> data) => ~Raw(~checksum, data);

    /// <summary>Gets the checksum of A followed by B from the checksums of each and the length of B.</summary>
    /// <param name="checksumA">The checksum of the first part.</param>
    /// <param name="checksumB">The checksum of the second part.</param>
    /// <param name="lengthB">The length in bytes of the second part.</param>
    /// <returns>The checksum of the concatenation, computed in O(log lengthB) without touching the data.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthB"/> is negative or above 2^60.</exception>
    public static ulong Combine(ulong checksumA, ulong checksumB, long lengthB)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lengthB);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengthB, long.MaxValue / 8);
        return Multiply(checksumA, XPow(lengthB * 8)) ^ checksumB;
    }

    /// <summary>Adds <paramref name="data"/> to the running checksum.</summary>
    /// <param name="data">The bytes to add; may be empty.</param>
    public void Append(ReadOnlySpan<byte> data) => _value = Update(_value, data);

    /// <summary>Gets the checksum of everything appended so far. The running state is unchanged.</summary>
    /// <returns>The 64-bit checksum.</returns>
    public readonly ulong GetCurrentValue() => _value;

    /// <summary>Returns to the state of a new, empty checksum.</summary>
    public void Reset() => _value = 0;

    internal static ulong Raw(ulong s, ReadOnlySpan<byte> d) => RawFolded(s, d);

    internal static ulong RawScalar(ulong s, ReadOnlySpan<byte> d)
    {
        int i = 0;
        int n = d.Length;
        while (n - i >= 8)
        {
            s = Step8(s, Mem.U64(d, i));
            i += 8;
        }

        while (i < n)
        {
            s = Mem.T(Tables, (uint)(s ^ Mem.U8(d, i)) & 0xFF) ^ (s >> 8);
            i++;
        }

        return s;
    }

    internal static ulong RawFolded(ulong s, ReadOnlySpan<byte> d)
    {
        if (d.Length >= CrcFold.MinLength && CrcFold.IsSupported)
        {
            Vector128<ulong> r = CrcFold.Fold(d, s, K512, K128, out int used);
            s = Step8(Step8(0, r.GetElement(0)), r.GetElement(1));
            d = d.Slice(used);
        }

        return RawScalar(s, d);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Step8(ulong s, ulong v)
    {
        v ^= s;
        return Mem.T(Tables, 7 * 256 + (uint)(v & 0xFF)) ^ Mem.T(Tables, 6 * 256 + (uint)((v >> 8) & 0xFF)) ^
               Mem.T(Tables, 5 * 256 + (uint)((v >> 16) & 0xFF)) ^ Mem.T(Tables, 4 * 256 + (uint)((v >> 24) & 0xFF)) ^
               Mem.T(Tables, 3 * 256 + (uint)((v >> 32) & 0xFF)) ^ Mem.T(Tables, 2 * 256 + (uint)((v >> 40) & 0xFF)) ^
               Mem.T(Tables, 1 * 256 + (uint)((v >> 48) & 0xFF)) ^ Mem.T(Tables, (uint)(v >> 56));
    }

    private static ulong[] BuildTables()
    {
        var t = new ulong[8 * 256];
        for (uint n = 0; n < 256; n++)
        {
            ulong c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ ReflectedPolynomial : c >> 1;
            }

            t[n] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int n = 0; n < 256; n++)
            {
                ulong prev = t[((k - 1) * 256) + n];
                t[(k * 256) + n] = (prev >> 8) ^ t[(int)(prev & 0xFF)];
            }
        }

        return t;
    }

    // GF(2)[x] modulo the CRC polynomial, reflected: bit (63 - i) holds the coefficient of x^i.
    private static ulong Multiply(ulong a, ulong b)
    {
        ulong r = 0;
        for (int i = 0; i < 64; i++)
        {
            if ((a & (0x8000000000000000UL >> i)) != 0)
            {
                r ^= b;
            }

            b = (b & 1) != 0 ? (b >> 1) ^ ReflectedPolynomial : b >> 1;
        }

        return r;
    }

    private static ulong XPow(long n)
    {
        ulong r = 0x8000000000000000;
        ulong b = 0x4000000000000000;
        while (n > 0)
        {
            if ((n & 1) != 0)
            {
                r = Multiply(r, b);
            }

            n >>= 1;
            if (n > 0)
            {
                b = Multiply(b, b);
            }
        }

        return r;
    }
}
