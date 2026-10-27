using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.Fuzz;

/// <summary>
/// Dictionary training on arbitrary sample sets. The first bytes of the input choose the options (algorithm, search or fixed
/// parameters, d-mer and segment size, score, dictionary size, sample size); the rest is cut into samples. Training must either
/// say the samples are unusable (<see cref="CompressionDictionaryException"/>) or give a dictionary that loads in the decoder and
/// the encoder, is no larger than asked, repeats byte for byte, and round-trips a sample.
/// </summary>
internal static class ZstdTrainingTargets
{
    public static FuzzTarget RoundTrip { get; } = new("zstd-train-roundtrip", static data => Run(data), static e => e is CompressionDictionaryException);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.Length < 7)
        {
            return;
        }

        int flags = input[0];
        int dmer = 2 + (input[1] % 15);
        int segment = dmer + (1 << (input[2] % 11));
        int size = 256 + (((input[3] << 8) | input[4]) % 12000);
        int sampleSize = 1 + (input[5] % 120);
        ReadOnlySpan<byte> data = input[6..];
        if (data.Length > 1 << 16)
        {
            data = data[..(1 << 16)];
        }

        var samples = new List<ReadOnlyMemory<byte>>();
        byte[] copy = data.ToArray();
        for (int p = 0, n = 0; p < copy.Length; p += sampleSize, n++)
        {
            samples.Add(n % 7 == 6 ? ReadOnlyMemory<byte>.Empty : copy.AsMemory(p, Math.Min(sampleSize, copy.Length - p)));
        }

        var options = new ZstdDictionaryTrainingOptions
        {
            Algorithm = (flags & 1) != 0 ? ZstdTrainingAlgorithm.Cover : ZstdTrainingAlgorithm.FastCover,
            Optimize = (flags & 2) != 0,
            Score = (flags & 4) != 0 ? ZstdSegmentScore.Frequency : ZstdSegmentScore.SquareRoot,
            SegmentSize = segment,
            DmerSize = dmer,
            DictionarySize = size,
            Acceleration = 1 + ((flags >> 3) & 3),
            HashBits = 10 + ((flags >> 5) & 3),
            TrainingPercent = (flags & 128) != 0 ? 100 : 75,
            SegmentSizeCandidates = [32, 256],
            DmerSizeCandidates = [dmer < 6 ? 6 : dmer],
            MaxDegreeOfParallelism = 2,
        };

        byte[] dictionary = ZstdDictionary.Train(samples, options);
        if (dictionary.Length > size)
        {
            throw new InvalidOperationException($"dictionary of {dictionary.Length} bytes exceeds the {size} asked for");
        }

        ZstdDictionary decode = ZstdDictionary.Parse(dictionary);
        ZstdCompressionDictionary encode = ZstdCompressionDictionary.Parse(dictionary);
        if ((flags & 16) != 0 && !dictionary.AsSpan().SequenceEqual(ZstdDictionary.Train(samples, options)))
        {
            throw new InvalidOperationException("training is not repeatable");
        }

        foreach (ReadOnlyMemory<byte> sample in samples.Where(static s => s.Length > 0).Take(3))
        {
            byte[] frame = Zstd.Compress(sample.Span, new ZstdCompressionOptions { Level = 3, Dictionary = encode });
            byte[] back = Zstd.Decompress(frame, new ZstdDecompressionOptions { Dictionary = decode });
            if (!back.AsSpan().SequenceEqual(sample.Span))
            {
                throw new InvalidOperationException("round trip mismatch");
            }
        }
    }
}
