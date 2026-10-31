
namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The parameters of an LZMA stream: the literal context bits (<see cref="Lc"/>), literal position bits (<see cref="Lp"/>), position bits
/// (<see cref="Pb"/>) and the dictionary size. They are stored in the 5-byte header of a <c>.lzma</c> file as one byte
/// <c>(pb * 5 + lp) * 9 + lc</c> and a little-endian 32-bit dictionary size; LZMA2 stores the dictionary size in one byte and the
/// other three in the first chunk that needs them.
/// </summary>
/// <remarks>LZMA specification, "lzma file format" and "The new scheme to encode LZMA properties".</remarks>
public readonly struct LzmaProperties : IEquatable<LzmaProperties>
{
    /// <summary>The smallest dictionary the format uses: a smaller declared size is read as this one.</summary>
    public const uint MinimumDictionarySize = 1 << 12;

    /// <summary>Creates properties.</summary>
    /// <param name="lc">Literal context bits, 0 to 8.</param>
    /// <param name="lp">Literal position bits, 0 to 4.</param>
    /// <param name="pb">Position bits, 0 to 4.</param>
    /// <param name="dictionarySize">The dictionary size in bytes as declared; sizes below <see cref="MinimumDictionarySize"/> are read as that minimum.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bit count is outside its range.</exception>
    public LzmaProperties(int lc, int lp, int pb, uint dictionarySize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lc, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(lp);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lp, 4);
        ArgumentOutOfRangeException.ThrowIfNegative(pb);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pb, 4);
        Lc = lc;
        Lp = lp;
        Pb = pb;
        DictionarySize = Math.Max(dictionarySize, MinimumDictionarySize);
    }

    /// <summary>Gets the number of high bits of the previous byte that select a literal's probability set (0 to 8).</summary>
    public int Lc { get; }

    /// <summary>Gets the number of low bits of the position that select a literal's probability set (0 to 4).</summary>
    public int Lp { get; }

    /// <summary>Gets the number of low bits of the position that select the match and length contexts (0 to 4).</summary>
    public int Pb { get; }

    /// <summary>Gets the dictionary size in bytes, at least <see cref="MinimumDictionarySize"/>.</summary>
    public uint DictionarySize { get; }

    /// <summary>Gets the byte that stores <see cref="Lc"/>, <see cref="Lp"/> and <see cref="Pb"/>.</summary>
    public byte PropertiesByte => (byte)((((Pb * 5) + Lp) * 9) + Lc);

    /// <summary>Splits a properties byte into its three bit counts.</summary>
    /// <param name="value">The byte, <c>(pb * 5 + lp) * 9 + lc</c>.</param>
    /// <param name="lc">Receives the literal context bits.</param>
    /// <param name="lp">Receives the literal position bits.</param>
    /// <param name="pb">Receives the position bits.</param>
    /// <returns>False when <paramref name="value"/> is 225 or more, which no combination encodes.</returns>
    public static bool TryParsePropertiesByte(byte value, out int lc, out int lp, out int pb)
    {
        lc = lp = pb = 0;
        if (value >= 9 * 5 * 5)
        {
            return false;
        }

        lc = value % 9;
        int d = value / 9;
        pb = d / 5;
        lp = d % 5;
        return true;
    }

    /// <summary>Decodes the one-byte LZMA2 dictionary size.</summary>
    /// <param name="value">The byte; 0 to 40 are valid (4 KiB, 6 KiB, 8 KiB, ... 4 GiB - 1).</param>
    /// <param name="size">Receives the size.</param>
    /// <returns>False when <paramref name="value"/> is above 40.</returns>
    public static bool TryGetLzma2DictionarySize(int value, out uint size)
    {
        size = 0;
        if (value is < 0 or > 40)
        {
            return false;
        }

        size = value == 40 ? uint.MaxValue : (2u | (uint)(value & 1)) << ((value / 2) + 11);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(LzmaProperties other) => Lc == other.Lc && Lp == other.Lp && Pb == other.Pb && DictionarySize == other.DictionarySize;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LzmaProperties other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (Lc, Lp, Pb, DictionarySize).GetHashCode();

    /// <summary>Compares two property sets.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>True when all four values are equal.</returns>
    public static bool operator ==(LzmaProperties left, LzmaProperties right) => left.Equals(right);

    /// <summary>Compares two property sets.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns>True when any value differs.</returns>
    public static bool operator !=(LzmaProperties left, LzmaProperties right) => !left.Equals(right);
}
