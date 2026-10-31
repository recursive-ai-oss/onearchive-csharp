using System.Buffers.Binary;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The CRC-32 bzip2 uses for blocks and streams: polynomial 0x04C11DB7 processed most significant bit first (the "non-reflected"
/// form, not the one gzip and zip use), initial value 0xFFFFFFFF and a final complement. It covers a block's bytes as the user
/// sees them, before the first run-length stage. A stream's combined CRC folds the block CRCs together with
/// <see cref="Combine"/>.
/// </summary>
public static class BZip2Crc
{
    /// <summary>The value a running CRC register starts from.</summary>
    public const uint Initial = 0xFFFFFFFF;

    private const uint Polynomial = 0x04C11DB7;

    // Slicing-by-8: Tables[k][b] is the register after byte b followed by k zero bytes.
    private static readonly uint[][] Tables = BuildTables();

    private static uint[][] BuildTables()
    {
        uint[][] t = new uint[8][];
        t[0] = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ Polynomial : c << 1;
            }

            t[0][i] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            t[k] = new uint[256];
            for (int i = 0; i < 256; i++)
            {
                uint prev = t[k - 1][i];
                t[k][i] = (prev << 8) ^ t[0][prev >> 24];
            }
        }

        return t;
    }

    /// <summary>Feeds bytes into a running CRC register.</summary>
    /// <param name="state">The register: <see cref="Initial"/> at the start, or the result of an earlier call.</param>
    /// <param name="data">The bytes.</param>
    /// <returns>The new register. Complement it (<c>~</c>) to get the CRC.</returns>
    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        uint[] t0 = Tables[0];
        while (data.Length >= 8)
        {
            uint a = state ^ BinaryPrimitives.ReadUInt32BigEndian(data);
            uint b = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
            state = Tables[7][a >> 24] ^ Tables[6][(a >> 16) & 0xFF] ^ Tables[5][(a >> 8) & 0xFF] ^ Tables[4][a & 0xFF]
                  ^ Tables[3][b >> 24] ^ Tables[2][(b >> 16) & 0xFF] ^ Tables[1][(b >> 8) & 0xFF] ^ t0[b & 0xFF];
            data = data[8..];
        }

        foreach (byte x in data)
        {
            state = (state << 8) ^ t0[(state >> 24) ^ x];
        }

        return state;
    }

    /// <summary>Computes the CRC of a buffer.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The CRC as bzip2 stores it.</returns>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(Initial, data);

    /// <summary>Folds a block's CRC into a stream's combined CRC: rotate the combined value left by one bit, then XOR the block CRC.</summary>
    /// <param name="combined">The combined CRC so far (0 for a new stream).</param>
    /// <param name="blockCrc">The block's CRC.</param>
    /// <returns>The new combined CRC.</returns>
    public static uint Combine(uint combined, uint blockCrc) => ((combined << 1) | (combined >> 31)) ^ blockCrc;
}
