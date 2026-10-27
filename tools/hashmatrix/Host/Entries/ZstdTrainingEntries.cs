using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Zstandard dictionary training: the standard inputs, cut into fixed-size samples, train dictionaries with the search and with
/// fixed parameters, with both algorithms, and the entropy-table builder runs over the first bytes of each input. The dictionary bytes
/// must be the same on every platform, vector width and thread count (the search runs on the matrix's thread count, at most 8). An input too
/// small to train on records the error instead.
/// </summary>
internal static class ZstdTrainingEntries
{
    // The search is narrowed so that a matrix column stays quick; the full grid is the default of the options.
    private static readonly int[] s_searchSegments = [64, 256, 1024];
    private static readonly int[] s_searchDmers = [6, 8];

    [ModuleInitializer]
    internal static void Register()
    {
        Matrix.Add("zstd-train", "fastcover-search-4k", (input, ctx) => Train(input, 200, new ZstdDictionaryTrainingOptions
        {
            DictionarySize = 4096,
            SegmentSizeCandidates = s_searchSegments,
            DmerSizeCandidates = s_searchDmers,
            MaxScoringSamples = 200,
            MaxDegreeOfParallelism = Math.Min(ctx.Threads, 8),
        }));
        Matrix.Add("zstd-train", "cover-search-8k", (input, ctx) => Train(input, 500, new ZstdDictionaryTrainingOptions
        {
            Algorithm = ZstdTrainingAlgorithm.Cover,
            DictionarySize = 8192,
            SegmentSizeCandidates = s_searchSegments,
            DmerSizeCandidates = s_searchDmers,
            MaxScoringSamples = 200,
            TrainingPercent = 90,
            MaxDegreeOfParallelism = Math.Min(ctx.Threads, 8),
        }));
        Matrix.Add("zstd-train", "fastcover-k256-d8-16k-accel2", (input, _) => Train(input, 300, new ZstdDictionaryTrainingOptions
        {
            Optimize = false,
            SegmentSize = 256,
            DmerSize = 8,
            DictionarySize = 16384,
            Acceleration = 2,
            HashBits = 18,
        }));
        Matrix.Add("zstd-train", "cover-k128-d6-freq-2k", (input, _) => Train(input, 64, new ZstdDictionaryTrainingOptions
        {
            Optimize = false,
            Algorithm = ZstdTrainingAlgorithm.Cover,
            SegmentSize = 128,
            DmerSize = 6,
            Score = ZstdSegmentScore.Frequency,
            DictionarySize = 2048,
            DictionaryId = 70000,
        }));
        Matrix.Add("zstd-train", "builder-4k", (input, _) =>
        {
            try
            {
                int take = Math.Min(input.Length, 4096);
                return ZstdDictionaryBuilder.Build(input[..Math.Min(take, input.Length)], Samples(input, 150));
            }
            catch (ArgumentException e)
            {
                return Encoding.UTF8.GetBytes(e.GetType().Name);
            }
        });
    }

    private static List<ReadOnlyMemory<byte>> Samples(ReadOnlySpan<byte> input, int size)
    {
        // Only the first 256 KiB: enough to train on and quick to search.
        byte[] data = input[..Math.Min(input.Length, 256 * 1024)].ToArray();
        var list = new List<ReadOnlyMemory<byte>>();
        for (int p = 0; p < data.Length; p += size)
        {
            list.Add(data.AsMemory(p, Math.Min(size, data.Length - p)));
        }

        return list;
    }

    private static byte[] Train(ReadOnlySpan<byte> input, int sampleSize, ZstdDictionaryTrainingOptions options)
    {
        try
        {
            return ZstdDictionary.Train(Samples(input, sampleSize), options);
        }
        catch (CompressionDictionaryException e)
        {
            return Encoding.UTF8.GetBytes(e.GetType().Name);
        }
    }
}
