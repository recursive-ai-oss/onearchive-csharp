using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.Smoke;

// The high-compression LZ4 levels must write the same bytes on a 32-bit target as on a 64-bit one. The search packs a match
// length and an offset into one 64-bit value, and a native-width shift once lost the length here. The input is generated, so
// the expected digest was taken from the 64-bit runtime and holds for every platform.
internal static class Lz4HighCheck
{
    private const string Expected = "6C6F4D5D8AB2427BBA216661180F4C7B43EFE2E181B4DE0DF176EB7F09D8C30D";

    public static bool Run()
    {
        byte[] input = Make(150_000, 5);
        var dictionary = new Lz4Dictionary(Make(8_000, 9));
        var all = new StringBuilder();
        for (int level = 2; level <= 12; level++)
        {
            foreach (Lz4Dictionary? dict in new[] { null, dictionary })
            {
                byte[] block = Lz4Block.Compress(input, new Lz4CompressionOptions { Level = level, Dictionary = dict });
                all.Append(level).Append(dict is null ? " block " : " block-dict ").Append(Convert.ToHexString(SHA256.HashData(block))).Append('\n');
            }

            if (level is 3 or 9 or 12)
            {
                byte[] frame = Lz4.Compress(input, new Lz4CompressionOptions { Level = level, BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB });
                all.Append(level).Append(" frame-linked ").Append(Convert.ToHexString(SHA256.HashData(frame))).Append('\n');
            }
        }

        string digest = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(all.ToString())));
        if (digest == Expected)
        {
            return true;
        }

        Console.Error.WriteLine("lz4 high-compression bytes differ from the 64-bit output: " + digest);
        Console.Error.Write(all.ToString());
        return false;
    }

    // Words from a small vocabulary in a skewed order, so chains are long and matches of every length occur.
    private static byte[] Make(int length, ulong seed)
    {
        string[] words = ["alpha", "beta", "gamma", "delta", "position", "velocity", "health", "tick", "owner", "state", "0000", "ffff"];
        var output = new byte[length];
        ulong state = seed;
        int at = 0;
        while (at < length)
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            if ((z & 7) == 0)
            {
                output[at++] = (byte)(z >> 8);
                continue;
            }

            string w = words[(int)((z >> 16) % (ulong)words.Length)];
            for (int i = 0; i < w.Length && at < length; i++)
            {
                output[at++] = (byte)w[i];
            }

            if (at < length)
            {
                output[at++] = (byte)' ';
            }
        }

        return output;
    }
}
