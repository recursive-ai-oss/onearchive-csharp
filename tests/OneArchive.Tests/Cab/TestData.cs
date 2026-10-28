namespace RecursiveAI.OneArchive.Tests.Cab;

internal static class TestData
{
    /// <summary>Deterministic text-like data with runs, noise, long repeats and (optionally) x86 call opcodes.</summary>
    public static byte[] Mixed(int length, int seed, bool calls = false)
    {
        var rng = new Random(seed);
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "lorem ", "ipsum ", "dolor ", "sit ", "amet\n", "consectetur "];
        var data = new List<byte>(length + 4096);
        while (data.Count < length)
        {
            int kind = rng.Next(20);
            if (kind < 12)
            {
                data.AddRange(System.Text.Encoding.ASCII.GetBytes(words[rng.Next(words.Length)]));
            }
            else if (kind < 15)
            {
                int n = rng.Next(1, 40);
                byte b = (byte)rng.Next(256);
                for (int i = 0; i < n; i++)
                {
                    data.Add(b);
                }
            }
            else if (kind < 16)
            {
                int n = rng.Next(300, 3000);
                byte b = (byte)rng.Next(256);
                for (int i = 0; i < n; i++)
                {
                    data.Add(b);
                }
            }
            else if (kind < 18 && calls)
            {
                data.Add(0xE8);
                data.AddRange(BitConverter.GetBytes(rng.Next(-5000, 60000)));
            }
            else
            {
                int n = rng.Next(1, 30);
                for (int i = 0; i < n; i++)
                {
                    data.Add((byte)rng.Next(256));
                }
            }
        }

        return [.. data.Take(length)];
    }
}
