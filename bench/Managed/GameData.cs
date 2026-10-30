using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.ManagedBench;

/// <summary>
/// Small-payload data from the generated game corpus: the bench set (seed 2) is what gets compressed, and the
/// dictionaries are trained on the other set (seed 1), so no payload is in its own dictionary.
/// </summary>
internal static class GameData
{
    private const int MaxFrames = 20000;
    private const int MaxSnapshots = 3000;
    private const long MaxBytes = 48L << 20;

    private static readonly Dictionary<string, byte[]> Dictionaries = [];

    /// <summary>Evenly spaced payloads of one kind (every game), concatenated, with each payload's length.</summary>
    public static (byte[] Data, int[] Parts) Load(string corpora, string kind)
    {
        var files = Manifest(corpora, "seed2", kind);
        int max = kind == "frames" ? MaxFrames : MaxSnapshots;
        var picked = Spread(files, max);
        var parts = new List<int>();
        using var ms = new MemoryStream();
        foreach (var path in picked)
        {
            var b = File.ReadAllBytes(path);
            if (b.Length == 0 || ms.Length + b.Length > MaxBytes) continue;
            ms.Write(b);
            parts.Add(b.Length);
        }

        return (ms.ToArray(), parts.ToArray());
    }

    /// <summary>A trained zstd dictionary (standard format) for one kind, made once per process by our trainer from the other seed.</summary>
    public static byte[] Dictionary(string corpora, string kind)
    {
        lock (Dictionaries)
        {
            if (Dictionaries.TryGetValue(kind, out var d)) return d;
            var samples = Spread(Manifest(corpora, "seed1", kind), 4000).Select(p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p)).Where(m => m.Length > 0).ToList();
            int size = kind == "frames" ? 16 << 10 : 64 << 10;
            d = ZstdDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = size });
            Dictionaries[kind] = d;
            return d;
        }
    }

    private static List<string> Manifest(string corpora, string seed, string kind)
    {
        string root = Path.Combine(corpora, "game", seed);
        var list = new List<string>();
        foreach (var line in File.ReadLines(Path.Combine(root, "manifest.tsv")).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length > 3 && f[1] == kind && long.Parse(f[3], CultureInfo.InvariantCulture) > 0) list.Add(Path.Combine(root, f[2]));
        }

        return list;
    }

    private static List<string> Spread(List<string> all, int max)
    {
        if (all.Count <= max) return all;
        var r = new List<string>(max);
        for (int i = 0; i < max; i++) r.Add(all[(int)((long)i * all.Count / max)]);
        return r;
    }
}
