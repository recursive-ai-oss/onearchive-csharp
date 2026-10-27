using System.Buffers.Binary;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// The fixed inputs, generated from seeded SplitMix64 streams so every column sees the same bytes.
/// Nothing here uses System.Random or anything else whose sequence the runtime could change.
/// </summary>
internal static class Inputs
{
    /// <summary>Names of the standard inputs, in the order they are documented.</summary>
    public static readonly IReadOnlyList<string> Standard =
    [
        "empty", "one-byte", "text-small", "text-64k", "text-3m", "binary-1m", "runs-1m", "zeros-4m", "random-1m",
    ];

    private static readonly Dictionary<string, byte[]> s_cache = [];

    /// <summary>Returns the input of that name, generating it on first use.</summary>
    public static byte[] Get(string name)
    {
        lock (s_cache)
        {
            if (!s_cache.TryGetValue(name, out byte[]? data))
            {
                data = Generate(name);
                s_cache[name] = data;
            }

            return data;
        }
    }

    /// <summary>Drops cached inputs so a long run does not hold all of them at once.</summary>
    public static void Release()
    {
        lock (s_cache)
        {
            s_cache.Clear();
        }
    }

    private static byte[] Generate(string name) => name switch
    {
        "empty" => [],
        "one-byte" => [0x5A],
        "text-small" => Text(0x1001, 300),
        "text-64k" => Text(0x1002, 64 * 1024),
        "text-3m" => Text(0x1003, 3 * 1024 * 1024),
        "binary-1m" => Binary(0x2001, 1024 * 1024),
        "runs-1m" => Runs(0x3001, 1024 * 1024),
        "zeros-4m" => new byte[4 * 1024 * 1024],
        "random-1m" => RandomBytes(0x4001, 1024 * 1024),
        _ => throw new ArgumentException($"unknown matrix input '{name}'", nameof(name)),
    };

    /// <summary>SplitMix64: a fixed, portable generator.</summary>
    private struct Rng(ulong seed)
    {
        private ulong _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>A value in [0, bound), taken from the high bits.</summary>
        public uint Below(uint bound) => (uint)(((Next() >> 32) * bound) >> 32);
    }

    private static byte[] RandomBytes(ulong seed, int size)
    {
        byte[] data = new byte[size];
        Rng rng = new(seed);
        for (int i = 0; i < size; i += 8)
        {
            ulong v = rng.Next();
            for (int j = 0; j < 8 && i + j < size; j++)
            {
                data[i + j] = (byte)(v >> (8 * j));
            }
        }

        return data;
    }

    // Words from a generated vocabulary, picked with a skew toward the early ones so the text
    // has the repetition and word-frequency shape of prose.
    private static byte[] Text(ulong seed, int size)
    {
        Rng rng = new(seed);
        byte[][] vocabulary = new byte[512][];
        for (int i = 0; i < vocabulary.Length; i++)
        {
            byte[] word = new byte[2 + (int)rng.Below(9)];
            for (int j = 0; j < word.Length; j++)
            {
                word[j] = (byte)('a' + rng.Below(26));
            }

            vocabulary[i] = word;
        }

        byte[] data = new byte[size];
        int pos = 0;
        int sentence = 0;
        while (pos < size)
        {
            uint index = rng.Below(512) * rng.Below(512) / 512;
            byte[] word = vocabulary[index];
            for (int j = 0; j < word.Length && pos < size; j++)
            {
                data[pos++] = word[j];
            }

            sentence++;
            byte sep = (byte)' ';
            if (sentence >= 6 + (int)rng.Below(10))
            {
                sentence = 0;
                sep = rng.Below(5) == 0 ? (byte)'\n' : (byte)'.';
            }
            else if (rng.Below(12) == 0)
            {
                sep = (byte)',';
            }

            if (pos < size)
            {
                data[pos++] = sep;
            }

            if (sep == (byte)'.' && pos < size)
            {
                data[pos++] = (byte)' ';
            }
        }

        return data;
    }

    // 32-byte records: counter, a few record types, a stride-addressed pointer, and a payload
    // chosen from a small pool with some random bytes. Resembles tables and executable data.
    private static byte[] Binary(ulong seed, int size)
    {
        Rng rng = new(seed);
        byte[][] pool = new byte[16][];
        for (int i = 0; i < pool.Length; i++)
        {
            pool[i] = new byte[12];
            for (int j = 0; j < 12; j++)
            {
                pool[i][j] = (byte)rng.Next();
            }
        }

        byte[] data = new byte[size];
        byte[] rec = new byte[32];
        for (int offset = 0, n = 0; offset < size; offset += 32, n++)
        {
            Array.Clear(rec);
            BinaryPrimitives.WriteUInt32LittleEndian(rec, (uint)n);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), (ushort)(rng.Below(6) * 0x101));
            BinaryPrimitives.WriteUInt64LittleEndian(rec.AsSpan(8), 0x00007F0000100000UL + ((ulong)n * 48));
            pool[rng.Below(16)].CopyTo(rec, 16);
            rec[28] = (byte)rng.Next();
            rec[29] = (byte)rng.Next();
            rec[30] = (byte)rng.Below(4);
            rec.AsSpan(0, Math.Min(32, size - offset)).CopyTo(data.AsSpan(offset));
        }

        return data;
    }

    // Runs of one byte with lengths from 1 to a few hundred, skewed short.
    private static byte[] Runs(ulong seed, int size)
    {
        Rng rng = new(seed);
        byte[] data = new byte[size];
        int pos = 0;
        while (pos < size)
        {
            byte value = (byte)rng.Next();
            int length = 1 + (int)(rng.Below(300) * rng.Below(300) / 300);
            length = Math.Min(length, size - pos);
            data.AsSpan(pos, length).Fill(value);
            pos += length;
        }

        return data;
    }
}
