using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Content selection against a plain restatement of the COVER algorithm (Liao, Petri, Moffat and Wirth 2016, section 3): every
/// window of every epoch is scored from scratch with a set of its distinct d-mers. The trainer's sliding window, count cells and ring
/// buffer must pick exactly what this picks.
/// </summary>
public class ZstdTrainingSelectionTests
{
    private static List<ReadOnlyMemory<byte>> RandomSamples(int seed, int count, int maxLength, int alphabet)
    {
        var random = new Random(seed);
        var list = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < count; i++)
        {
            byte[] s = new byte[1 + random.Next(maxLength)];
            for (int j = 0; j < s.Length; j++)
            {
                // Runs and a small alphabet make d-mers repeat inside samples and across them.
                s[j] = j > 3 && random.Next(4) == 0 ? s[j - 1 - random.Next(3)] : (byte)random.Next(alphabet);
            }

            list.Add(s);
        }

        return list;
    }

    private static uint ISqrt(ulong x)
    {
        uint r = (uint)Math.Sqrt(x);
        while ((ulong)r * r > x)
        {
            r--;
        }

        while ((ulong)(r + 1) * (r + 1) <= x)
        {
            r++;
        }

        return r;
    }

    private static byte[] Reference(List<ReadOnlyMemory<byte>> samples, int k, int d, int dictionarySize, ZstdSegmentScore score)
    {
        var data = new List<byte>();
        var starts = new List<int>();
        foreach (ReadOnlyMemory<byte> s in samples.Where(static s => s.Length > 0))
        {
            starts.Add(data.Count);
            data.AddRange(s.ToArray());
        }

        starts.Add(data.Count);
        int n = data.Count;
        byte[] bytes = [.. data];
        int[] sampleEnd = new int[n];
        for (int s = 0; s + 1 < starts.Count; s++)
        {
            for (int p = starts[s]; p < starts[s + 1]; p++)
            {
                sampleEnd[p] = starts[s + 1];
            }
        }

        string Key(int p) => Convert.ToHexString(bytes, p, d);
        bool Valid(int p) => p + d <= sampleEnd[p];

        var frequency = new Dictionary<string, uint>();
        for (int s = 0; s + 1 < starts.Count; s++)
        {
            var seen = new HashSet<string>();
            for (int p = starts[s]; p < starts[s + 1]; p++)
            {
                if (Valid(p))
                {
                    seen.Add(Key(p));
                }
            }

            foreach (string key in seen)
            {
                frequency[key] = frequency.GetValueOrDefault(key) + 1;
            }
        }

        var weight = new Dictionary<string, uint>();
        foreach ((string key, uint f) in frequency)
        {
            weight[key] = f < 2 ? 0 : score == ZstdSegmentScore.SquareRoot ? ISqrt((ulong)f << 16) : f;
        }

        int epochs = Math.Clamp((dictionarySize + k - 1) / k, 1, Math.Max(1, n / k));
        int epochLength = n / epochs;
        int remaining = dictionarySize;
        var chosen = new List<(int Start, int Length, long Score, int Epoch)>();
        for (int e = 0; e < epochs && remaining >= d; e++)
        {
            int e0 = e * epochLength;
            int e1 = e == epochs - 1 ? n : e0 + epochLength;
            int window = Math.Min(Math.Min(k, remaining), e1 - e0);
            if (window < d)
            {
                continue;
            }

            long best = 0;
            int bestStart = -1;
            for (int start = e0; start + window <= e1; start++)
            {
                var distinct = new HashSet<string>();
                for (int p = start; p + d <= start + window; p++)
                {
                    if (Valid(p))
                    {
                        distinct.Add(Key(p));
                    }
                }

                long total = 0;
                foreach (string key in distinct)
                {
                    total += weight[key];
                }

                if (total > best)
                {
                    best = total;
                    bestStart = start;
                }
            }

            if (bestStart < 0)
            {
                continue;
            }

            for (int p = bestStart; p + d <= bestStart + window; p++)
            {
                if (Valid(p))
                {
                    weight[Key(p)] = 0;
                }
            }

            chosen.Add((bestStart, window, best, e));
            remaining -= window;
        }

        if (chosen.Count == 0)
        {
            return bytes[..Math.Min(dictionarySize, n)];
        }

        chosen.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Epoch.CompareTo(b.Epoch));
        var content = new List<byte>();
        foreach ((int start, int length, _, _) in chosen)
        {
            content.AddRange(bytes[start..(start + length)]);
        }

        return content.Count >= 8 ? [.. content] : bytes[..Math.Min(Math.Max(dictionarySize, 8), n)];
    }

    [Theory]
    [InlineData(1, 40, 60, 6, 8, 5, 300, ZstdSegmentScore.SquareRoot)]
    [InlineData(2, 25, 200, 4, 16, 3, 700, ZstdSegmentScore.Frequency)]
    [InlineData(3, 60, 30, 12, 6, 4, 400, ZstdSegmentScore.SquareRoot)]
    [InlineData(4, 8, 400, 40, 9, 6, 500, ZstdSegmentScore.SquareRoot)]
    [InlineData(5, 30, 80, 3, 4, 2, 256, ZstdSegmentScore.Frequency)]
    public void SelectionMatchesThePlainAlgorithm(int seed, int count, int maxLength, int alphabet, int k, int d, int size, ZstdSegmentScore score)
    {
        List<ReadOnlyMemory<byte>> samples = RandomSamples(seed, count, maxLength, alphabet);
        byte[] expected = Reference(samples, k, d, size, score);
        foreach (ZstdTrainingAlgorithm algorithm in new[] { ZstdTrainingAlgorithm.Cover, ZstdTrainingAlgorithm.FastCover })
        {
            byte[] actual = ZstdCoverTrainer.SelectContent(samples, new ZstdDictionaryTrainingOptions
            {
                Algorithm = algorithm,
                Optimize = false,
                SegmentSize = k,
                DmerSize = d,
                Score = score,
                DictionarySize = size,
                HashBits = 24,
            }, TestContext.Current.CancellationToken);

            // FastCover hashes d-mers into 2^HashBits slots, so on this much data nothing collides at 24 bits and it agrees with the
            // exact algorithm (the hashed table is smaller for small sets: eight slots per byte at least, which can collide).
            if (algorithm == ZstdTrainingAlgorithm.Cover)
            {
                Assert.Equal(expected, actual);
            }
            else
            {
                Assert.True(actual.Length >= 8 && actual.Length <= size);
            }
        }
    }

    [Fact]
    public void WindowsLongerThan64KiBUseTheWidePathAndAgreeWithThePlainAlgorithm()
    {
        // Eleven samples of 6010 bytes, one epoch, a window of 66000 bytes: a hundred placements to score from scratch.
        var random = new Random(9);
        var samples = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < 11; i++)
        {
            byte[] s = new byte[6010];
            for (int j = 0; j < s.Length; j++)
            {
                s[j] = j > 3 && random.Next(4) == 0 ? s[j - 1 - random.Next(3)] : (byte)random.Next(5);
            }

            samples.Add(s);
        }

        byte[] expected = Reference(samples, 66000, 6, 66000, ZstdSegmentScore.SquareRoot);
        byte[] actual = ZstdCoverTrainer.SelectContent(samples, new ZstdDictionaryTrainingOptions
        {
            Algorithm = ZstdTrainingAlgorithm.Cover,
            Optimize = false,
            SegmentSize = 66000,
            DmerSize = 6,
            DictionarySize = 66000,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SelectionDoesNotDependOnWhatTheBuffersHeldBefore()
    {
        // The work arrays come from a shared pool and are not cleared: a training that runs after another must give the same bytes.
        List<ReadOnlyMemory<byte>> a = RandomSamples(21, 200, 500, 7);
        List<ReadOnlyMemory<byte>> b = RandomSamples(22, 90, 900, 3);
        var options = new ZstdDictionaryTrainingOptions { DictionarySize = 3000, MaxDegreeOfParallelism = 2 };
        byte[] first = ZstdDictionary.Train(a, options, TestContext.Current.CancellationToken);
        _ = ZstdDictionary.Train(b, options, TestContext.Current.CancellationToken);
        _ = ZstdCoverTrainer.SelectContent(b, new ZstdDictionaryTrainingOptions { Optimize = false, DictionarySize = 9000 }, TestContext.Current.CancellationToken);
        Assert.Equal(first, ZstdDictionary.Train(a, options, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void FewSamplesEndInValidDictionaries(int count)
    {
        // With only a handful of samples some folds would hold out nothing; the trainer falls back to one part.
        List<ReadOnlyMemory<byte>> samples = RandomSamples(30 + count, count, 300, 9);
        foreach (int percent in new[] { 50, 75, 100 })
        {
            byte[] dictionary = ZstdDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 1500, TrainingPercent = percent, MaxDegreeOfParallelism = 2 }, TestContext.Current.CancellationToken);
            Assert.True(dictionary.Length <= 1500);
            _ = ZstdCompressionDictionary.Parse(dictionary);
        }
    }

    [Fact]
    public void GuidedAndExhaustiveSearchesBothWorkAndTheGuidedOneIsClose()
    {
        List<ReadOnlyMemory<byte>> samples = ZstdTrainingTests.Samples(600, 3);
        List<ReadOnlyMemory<byte>> test = ZstdTrainingTests.Samples(200, 4);
        long Total(ZstdTrainingSearch search)
        {
            byte[] bytes = ZstdDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 4096, SearchMode = search }, TestContext.Current.CancellationToken);
            ZstdCompressionDictionary dictionary = ZstdCompressionDictionary.Parse(bytes);
            return test.Sum(s => Zstd.Zstd.Compress(s.Span, new ZstdCompressionOptions { Level = 3, Dictionary = dictionary }).Length);
        }

        long guided = Total(ZstdTrainingSearch.Guided);
        long exhaustive = Total(ZstdTrainingSearch.Exhaustive);
        Assert.True(guided <= exhaustive * 1.03, $"guided {guided} vs exhaustive {exhaustive}");
    }
}
