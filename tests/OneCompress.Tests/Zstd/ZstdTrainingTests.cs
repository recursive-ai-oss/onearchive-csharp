using System.Diagnostics;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>Dictionary training: the dictionaries it writes load and help, and the odd inputs end well.</summary>
public class ZstdTrainingTests
{
    /// <summary>Small structured records, like the payloads of a game or a log: shared keys and vocabulary, varying numbers.</summary>
    internal static List<ReadOnlyMemory<byte>> Samples(int count, uint seed = 1)
    {
        string[] names = ["lantern", "brick", "player", "door", "chest", "torch", "wall", "spawn", "enemy", "pickup"];
        uint x = seed * 2654435761u + 12345;
        uint Next()
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            return x;
        }

        var list = new List<ReadOnlyMemory<byte>>(count);
        for (int i = 0; i < count; i++)
        {
            string name = names[Next() % (uint)names.Length];
            string s = $"{{\"id\":{Next() % 5000},\"kind\":\"{name}\",\"pos\":[{Next() % 256},{Next() % 256},{Next() % 64}],\"state\":\"{(Next() % 3 == 0 ? "active" : "idle")}\",\"tick\":{1000 + (i * 3)}}}";
            list.Add(System.Text.Encoding.ASCII.GetBytes(s));
        }

        return list;
    }

    private static byte[] Train(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null) =>
        ZstdDictionary.Train(samples, options, TestContext.Current.CancellationToken);

    private static long CompressedTotal(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdCompressionDictionary? dictionary)
    {
        long total = 0;
        foreach (ReadOnlyMemory<byte> s in samples)
        {
            byte[] frame = Zstd.Zstd.Compress(s.Span, new ZstdCompressionOptions { Level = 3, Dictionary = dictionary });
            total += frame.Length;
        }

        return total;
    }

    [Theory]
    [InlineData(ZstdTrainingAlgorithm.FastCover)]
    [InlineData(ZstdTrainingAlgorithm.Cover)]
    public void TrainedDictionaryLoadsAndHelps(ZstdTrainingAlgorithm algorithm)
    {
        List<ReadOnlyMemory<byte>> train = Samples(2000, 1);
        List<ReadOnlyMemory<byte>> test = Samples(300, 2);
        byte[] bytes = Train(train, new ZstdDictionaryTrainingOptions { Algorithm = algorithm, DictionarySize = 4096 });

        Assert.True(bytes.Length <= 4096);
        Assert.True(bytes.Length > 1000);
        ZstdDictionary decode = ZstdDictionary.Parse(bytes);
        ZstdCompressionDictionary encode = ZstdCompressionDictionary.Parse(bytes);
        Assert.True(decode.HasEntropyTables);
        Assert.InRange(decode.Id, 32768u, 0x7FFFFFFFu);
        Assert.Equal(decode.Id, encode.Id);

        long without = CompressedTotal(test, null);
        long with = CompressedTotal(test, encode);
        Assert.True(with < without * 0.6, $"dictionary should help a lot: {with} vs {without}");

        foreach (ReadOnlyMemory<byte> s in test.Take(50))
        {
            byte[] frame = Zstd.Zstd.Compress(s.Span, new ZstdCompressionOptions { Level = 3, Dictionary = encode });
            byte[] back = Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { Dictionary = decode });
            Assert.True(back.AsSpan().SequenceEqual(s.Span));
        }
    }

    [Fact]
    public void ResultIsIndependentOfThreadCountAndRepeatable()
    {
        List<ReadOnlyMemory<byte>> train = Samples(800, 7);
        var one = new ZstdDictionaryTrainingOptions { DictionarySize = 8192, MaxDegreeOfParallelism = 1 };
        var many = new ZstdDictionaryTrainingOptions { DictionarySize = 8192, MaxDegreeOfParallelism = 8 };
        byte[] a = Train(train, one);
        byte[] b = Train(train, many);
        byte[] c = Train(train, one);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void FixedParametersAreHonouredAndNeedNoSearch()
    {
        List<ReadOnlyMemory<byte>> train = Samples(500, 3);
        var options = new ZstdDictionaryTrainingOptions { Optimize = false, SegmentSize = 64, DmerSize = 6, DictionarySize = 2048, DictionaryId = 40000 };
        byte[] bytes = Train(train, options);
        Assert.Equal(40000u, ZstdDictionary.Parse(bytes).Id);
        Assert.True(bytes.Length <= 2048);
        byte[] content = ZstdCoverTrainer.SelectContent(train, options, TestContext.Current.CancellationToken);
        Assert.InRange(content.Length, 8, 2048);
    }

    [Fact]
    public void BuilderRebuildsTablesOfAnotherToolsDictionary()
    {
        List<ReadOnlyMemory<byte>> train = Samples(500, 5);
        byte[] made = Train(train, new ZstdDictionaryTrainingOptions { DictionarySize = 4096 });
        ZstdDictionary parsed = ZstdDictionary.Parse(made);
        byte[] rebuilt = ZstdDictionaryBuilder.Build(made, Samples(300, 6));
        ZstdDictionary again = ZstdDictionary.Parse(rebuilt);
        Assert.Equal(parsed.Id, again.Id);
        Assert.True(parsed.Content.Span.SequenceEqual(again.Content.Span));

        byte[] fromRaw = ZstdDictionaryBuilder.Build(parsed.Content.Span, train, 50000);
        Assert.Equal(50000u, ZstdDictionary.Parse(fromRaw).Id);
        Assert.Throws<ArgumentException>(() => ZstdDictionaryBuilder.Build(parsed.Content.Span, train, 100));
        Assert.Throws<ArgumentException>(() => ZstdDictionaryBuilder.Build(new byte[7], train));
    }

    [Fact]
    public void NoSamplesOrTooLittleDataIsATypedError()
    {
        Assert.Throws<CompressionDictionaryException>(() => Train([]));
        Assert.Throws<CompressionDictionaryException>(() => Train([ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty]));
        Assert.Throws<CompressionDictionaryException>(() => Train([new byte[7]]));
        Assert.Throws<ArgumentNullException>(() => Train(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => Train(Samples(10), new ZstdDictionaryTrainingOptions { DictionarySize = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Train(Samples(10), new ZstdDictionaryTrainingOptions { Optimize = false, DmerSize = 1 }));
        Assert.Throws<ArgumentException>(() => Train(Samples(10), new ZstdDictionaryTrainingOptions { DictionaryId = 5 }));
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(1, 100)]
    [InlineData(2, 5)]
    [InlineData(3, 40)]
    [InlineData(50, 7)]
    public void TinyAndSingleSampleSetsGiveValidDictionaries(int count, int length)
    {
        var samples = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < count; i++)
        {
            byte[] s = new byte[length];
            for (int j = 0; j < length; j++)
            {
                s[j] = (byte)((i * 31) + (j * 7));
            }

            samples.Add(s);
        }

        // Smaller than d, smaller than the dictionary size, and with a single symbol repeated: all must load.
        byte[] bytes = Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 1024 });
        ZstdDictionary.Parse(bytes);
        ZstdCompressionDictionary dictionary = ZstdCompressionDictionary.Parse(bytes);
        byte[] frame = Zstd.Zstd.Compress(samples[0].Span, new ZstdCompressionOptions { Dictionary = dictionary });
        Assert.True(Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(bytes) }).AsSpan().SequenceEqual(samples[0].Span));
    }

    [Fact]
    public void ConstantAndEmptyMixedSamplesWork()
    {
        var samples = new List<ReadOnlyMemory<byte>> { new byte[300], ReadOnlyMemory<byte>.Empty, new byte[50], new byte[1] };
        byte[] bytes = Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 512 });
        ZstdCompressionDictionary.Parse(bytes);
    }

    [Fact]
    public void ManySamplesAndByteLimitAreThinnedDeterministically()
    {
        var many = new List<ReadOnlyMemory<byte>>();
        List<ReadOnlyMemory<byte>> base1 = Samples(2000, 9);
        for (int i = 0; i < 100_000; i++)
        {
            many.Add(base1[i % base1.Count]);
        }

        var options = new ZstdDictionaryTrainingOptions { DictionarySize = 4096, MaxTrainingBytes = 200_000, Optimize = false, SegmentSize = 128, DmerSize = 8 };
        byte[] a = Train(many, options);
        byte[] b = Train(many, options);
        Assert.Equal(a, b);
        ZstdDictionary.Parse(a);

        // One sample longer than the limit is cut at the limit.
        byte[] huge = new byte[1_000_000];
        new Random(4).NextBytes(huge);
        byte[] c = Train([huge], new ZstdDictionaryTrainingOptions { DictionarySize = 2048, MaxTrainingBytes = 100_000, Optimize = false });
        ZstdDictionary.Parse(c);
    }

    [Fact]
    public void HashBitsBelowTheUsualMinimumAreAccepted()
    {
        // A hash table smaller than 2^12 slots is a valid choice for the sampled variant.
        foreach (int bits in new[] { 8, 10, 11 })
        {
            byte[] bytes = Train(Samples(300), new ZstdDictionaryTrainingOptions { DictionarySize = 2048, HashBits = bits, Optimize = true, DmerSizeCandidates = [8], SegmentSizeCandidates = [64, 256] });
            ZstdDictionary.Parse(bytes);
        }
    }

    [Fact]
    public void CancellationIsHonoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ZstdDictionary.Train(Samples(500), null, cts.Token));
    }

    [Fact]
    public void LargerDictionariesDoNotCompressWorse()
    {
        List<ReadOnlyMemory<byte>> train = Samples(3000, 11);
        List<ReadOnlyMemory<byte>> test = Samples(300, 12);
        long previous = long.MaxValue;
        foreach (int size in new[] { 1024, 4096 })
        {
            byte[] bytes = Train(train, new ZstdDictionaryTrainingOptions { DictionarySize = size, Optimize = false, SegmentSize = 128 });
            long total = CompressedTotal(test, ZstdCompressionDictionary.Parse(bytes));
            Assert.True(total <= previous * 1.03, $"{size}: {total} vs {previous}");
            previous = total;
        }
    }

    [Fact]
    [Trait("Category", "Interop")]
    public void ReferenceToolLoadsOurDictionaryBothWays()
    {
        if (Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") != "1")
        {
            return;
        }

        string tool = Environment.GetEnvironmentVariable("ONEARCHIVE_ZSTD") ?? "zstd";
        string dir = Path.Combine(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath(), "zstd-training-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        try
        {
            List<ReadOnlyMemory<byte>> train = Samples(2000, 1);
            List<ReadOnlyMemory<byte>> test = Samples(100, 2);
            byte[] bytes = Train(train, new ZstdDictionaryTrainingOptions { DictionarySize = 4096 });
            string dictPath = Path.Combine(dir, "d.zdict");
            File.WriteAllBytes(dictPath, bytes);
            ZstdCompressionDictionary ours = ZstdCompressionDictionary.Parse(bytes);
            ZstdDictionary decode = ZstdDictionary.Parse(bytes);
            for (int i = 0; i < test.Count; i++)
            {
                string raw = Path.Combine(dir, "s.bin");
                File.WriteAllBytes(raw, test[i].ToArray());

                // Ours -> the tool.
                byte[] frame = Zstd.Zstd.Compress(test[i].Span, new ZstdCompressionOptions { Level = 3, Dictionary = ours });
                File.WriteAllBytes(Path.Combine(dir, "ours.zst"), frame);
                Run(tool, $"-q -f -D \"{dictPath}\" -d \"{Path.Combine(dir, "ours.zst")}\" -o \"{Path.Combine(dir, "ours.out")}\"");
                Assert.Equal(test[i].ToArray(), File.ReadAllBytes(Path.Combine(dir, "ours.out")));

                // The tool -> ours.
                Run(tool, $"-q -f -3 -D \"{dictPath}\" \"{raw}\" -o \"{Path.Combine(dir, "ref.zst")}\"");
                byte[] back = Zstd.Zstd.Decompress(File.ReadAllBytes(Path.Combine(dir, "ref.zst")), new ZstdDecompressionOptions { Dictionary = decode });
                Assert.Equal(test[i].ToArray(), back);
            }

            // A dictionary trained by the tool: its entropy tables can be rebuilt by ours and still load in the tool.
            string trainDir = Path.Combine(dir, "train");
            Directory.CreateDirectory(trainDir);
            for (int i = 0; i < train.Count; i++)
            {
                File.WriteAllBytes(Path.Combine(trainDir, $"{i:D5}.bin"), train[i].ToArray());
            }

            string refDict = Path.Combine(dir, "ref.zdict");
            Run(tool, $"-q --train \"{trainDir}\"/* --maxdict=4096 -o \"{refDict}\"");
            byte[] refBytes = File.ReadAllBytes(refDict);
            byte[] rebuilt = ZstdDictionaryBuilder.Build(refBytes, train);
            string rebuiltPath = Path.Combine(dir, "rebuilt.zdict");
            File.WriteAllBytes(rebuiltPath, rebuilt);
            string sample = Path.Combine(dir, "s2.bin");
            File.WriteAllBytes(sample, test[0].ToArray());
            Run(tool, $"-q -f -3 -D \"{rebuiltPath}\" \"{sample}\" -o \"{Path.Combine(dir, "rb.zst")}\"");
            byte[] rb = Zstd.Zstd.Decompress(File.ReadAllBytes(Path.Combine(dir, "rb.zst")), new ZstdDecompressionOptions { Dictionary = ZstdDictionary.Parse(rebuilt) });
            Assert.Equal(test[0].ToArray(), rb);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void Run(string tool, string args)
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", $"{tool} {args}"]) { RedirectStandardError = true, RedirectStandardOutput = true };
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} {args}: {err}");
    }
}
