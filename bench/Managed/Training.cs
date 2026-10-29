using System.Diagnostics;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.ManagedBench;

/// <summary>Dictionary training, one implementation per process so peak memory (VmHWM) is its own.</summary>
internal static class Training
{
    private const int SampleSize = 8192;
    private const int SampleCount = 1024;

    public static int Run(string impl, string corpora)
    {
        // 1024 samples of 8 KiB spread evenly over the file (8 MiB of training data).
        using var f = File.OpenRead(Path.Combine(corpora, "silesia.tar"));
        var samples = new List<byte[]>();
        for (int i = 0; i < SampleCount; i++)
        {
            var b = new byte[SampleSize];
            f.Position = (f.Length - SampleSize) * i / (SampleCount - 1);
            f.ReadExactly(b);
            samples.Add(b);
        }

        long before = Hwm();
        var sw = Stopwatch.StartNew();
        byte[] dict = impl switch
        {
            "ours" => ZstdDictionary.Train(samples.Select(s => (ReadOnlyMemory<byte>)s).ToList(), new ZstdDictionaryTrainingOptions { DictionarySize = 112 << 10 }),
            "zstdsharp" => ZstdSharp.DictBuilder.TrainFromBuffer(samples, 112 << 10),
            _ => throw new ArgumentException(impl),
        };
        sw.Stop();

        // Quality: total size of the samples compressed at level 3 with the dictionary (ours encoder for both).
        long total = 0;
        var cd = ZstdCompressionDictionary.Parse(dict);
        foreach (var s in samples.Where((_, i) => i % 4 == 0))
        {
            total += Zstd.Compress(s, new ZstdCompressionOptions { Level = 3, Dictionary = cd }).Length;
        }

        Console.WriteLine($"train\t{impl}\tseconds={sw.Elapsed.TotalSeconds:F2}\tpeak_rss_mib={Hwm() >> 20}\trss_before_mib={before >> 20}\tdict_bytes={dict.Length}\tsamples_total_bytes_with_dict={total}\tload={File.ReadAllText("/proc/loadavg").Split(' ')[0]}");
        return 0;
    }

    private static long Hwm()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("VmHWM:", StringComparison.Ordinal))
            {
                return long.Parse(line.Split(" ", StringSplitOptions.RemoveEmptyEntries)[1], System.Globalization.CultureInfo.InvariantCulture) * 1024;
            }
        }

        return -1;
    }
}
