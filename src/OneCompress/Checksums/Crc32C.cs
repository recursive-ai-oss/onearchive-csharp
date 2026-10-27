using System.Runtime.Intrinsics;

namespace RecursiveAI.OneCompress.Checksums;

/// <summary>
/// CRC-32C (Castagnoli, reflected polynomial 0x82F63B78, initial value and final xor 0xFFFFFFFF), as used by iSCSI, ext4 and Btrfs. The check value of "123456789" is 0xE3069283.
/// Use <see cref="Compute"/> for a buffer, or append pieces to a value of this type; any split of the input gives the same value.
/// </summary>
/// <remarks>
/// The value is a mutable struct: copy it only to snapshot the running state. The default value is the checksum of
/// empty input. Output is identical on every platform; vector and instruction paths compute the same function as the table path.
/// </remarks>
public struct Crc32C
{
    private const uint ReflectedPolynomial = 0x82F63B78;

    private static readonly uint[] Tables = Crc32Common.BuildTables(ReflectedPolynomial);
    private static readonly Vector128<ulong> K512 = Crc32Common.FoldConstants(ReflectedPolynomial, 512);
    private static readonly Vector128<ulong> K128 = Crc32Common.FoldConstants(ReflectedPolynomial, 128);

    private uint _value;

    /// <summary>Gets the checksum of <paramref name="data"/>.</summary>
    /// <param name="data">The bytes to checksum; may be empty.</param>
    /// <returns>The 32-bit checksum; the checksum of empty input is 0.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    /// <summary>Continues a checksum: returns the checksum of the earlier input followed by <paramref name="data"/>.</summary>
    /// <param name="checksum">The checksum of the earlier input (0 for none).</param>
    /// <param name="data">The bytes to add; may be empty.</param>
    /// <returns>The checksum of everything so far.</returns>
    public static uint Update(uint checksum, ReadOnlySpan<byte> data) => ~Raw(~checksum, data);

    /// <summary>Gets the checksum of A followed by B from the checksums of each and the length of B.</summary>
    /// <param name="checksumA">The checksum of the first part.</param>
    /// <param name="checksumB">The checksum of the second part.</param>
    /// <param name="lengthB">The length in bytes of the second part.</param>
    /// <returns>The checksum of the concatenation, computed in O(log lengthB) without touching the data.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lengthB"/> is negative or above 2^60.</exception>
    public static uint Combine(uint checksumA, uint checksumB, long lengthB) =>
        Crc32Common.Combine(ReflectedPolynomial, checksumA, checksumB, lengthB);

    /// <summary>Adds <paramref name="data"/> to the running checksum.</summary>
    /// <param name="data">The bytes to add; may be empty.</param>
    public void Append(ReadOnlySpan<byte> data) => _value = Update(_value, data);

    /// <summary>Gets the checksum of everything appended so far. The running state is unchanged.</summary>
    /// <returns>The 32-bit checksum.</returns>
    public readonly uint GetCurrentValue() => _value;

    /// <summary>Returns to the state of a new, empty checksum.</summary>
    public void Reset() => _value = 0;

    // Raw register: the checksum before the final inversion.
    internal static uint Raw(uint s, ReadOnlySpan<byte> d)
    {
        s = Crc32Common.Folded(Tables, K512, K128, s, d, out int used);
        return Crc32Common.Castagnoli(Tables, s, d.Slice(used));
    }

    internal static uint RawScalar(uint s, ReadOnlySpan<byte> d) => Crc32Common.Scalar(Tables, s, d);

    internal static uint RawFolded(uint s, ReadOnlySpan<byte> d)
    {
        s = Crc32Common.Folded(Tables, K512, K128, s, d, out int used);
        return Crc32Common.Scalar(Tables, s, d.Slice(used));
    }

    internal static uint RawInstruction(uint s, ReadOnlySpan<byte> d) => Crc32Common.Castagnoli(Tables, s, d);
}
