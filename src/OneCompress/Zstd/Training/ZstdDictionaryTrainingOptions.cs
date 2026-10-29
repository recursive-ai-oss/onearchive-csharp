using System.Diagnostics.CodeAnalysis;
namespace RecursiveAI.OneCompress.Zstd;

/// <summary>Which segment-selection algorithm picks a trained dictionary's content.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZstdTrainingAlgorithm
{
    /// <summary>
    /// The sampled variant: d-mers are counted in a hash table of 2^<see cref="ZstdDictionaryTrainingOptions.HashBits"/> buckets
    /// (collisions share a count) and only every <see cref="ZstdDictionaryTrainingOptions.Acceleration"/>-th d-mer is looked at.
    /// Much less memory and time than <see cref="Cover"/>, a little less exact.
    /// </summary>
    FastCover = 0,

    /// <summary>The COVER algorithm with exact d-mer counts.</summary>
    Cover = 1,
}

/// <summary>How a segment's d-mer frequencies are combined into its score.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZstdSegmentScore
{
    /// <summary>The sum of the frequencies of the segment's distinct d-mers (the paper's ℓ1 norm).</summary>
    Frequency = 0,

    /// <summary>
    /// The sum of the square roots of those frequencies (the paper's ℓ0.5 norm, up to the monotone final square), which prefers
    /// many moderately common d-mers over a few very common ones. The root is an integer fixed-point root, so scores are exact.
    /// </summary>
    SquareRoot = 1,
}

/// <summary>How <see cref="ZstdDictionaryTrainingOptions.Optimize"/> walks the candidate parameters.</summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum ZstdTrainingSearch
{
    /// <summary>
    /// Tries a handful of the candidates: the segment sizes are searched on a coarse grid, then around the best of it, with the first
    /// d-mer size (the largest up to 8), and the other d-mer sizes and scores are tried at the best segment size. The cost of a
    /// dictionary against segment size is smooth, so this finds a neighbour of the best pair at a fraction of the cost.
    /// </summary>
    Guided = 0,

    /// <summary>Tries every combination of the candidates.</summary>
    Exhaustive = 1,
}

/// <summary>
/// Options for training a Zstandard dictionary (<see cref="ZstdDictionary.Train"/>). The same samples and options always give the same
/// dictionary bytes, on every platform and with any <see cref="MaxDegreeOfParallelism"/>.
/// </summary>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdDictionaryTrainingOptions
{
    /// <summary>Gets the default options: FastCover with parameter search, 110 KiB.</summary>
    public static ZstdDictionaryTrainingOptions Default { get; } = new();

    /// <summary>Gets the size limit of the dictionary in bytes, 256 up to 1 GiB. Default 112640 (110 KiB). The result is smaller when the samples hold less than this worth of useful content.</summary>
    public int DictionarySize { get; init; } = 112640;

    /// <summary>Gets the selection algorithm. Default <see cref="ZstdTrainingAlgorithm.FastCover"/>.</summary>
    public ZstdTrainingAlgorithm Algorithm { get; init; } = ZstdTrainingAlgorithm.FastCover;

    /// <summary>Gets how segment frequencies are scored when <see cref="Optimize"/> is false. Default <see cref="ZstdSegmentScore.SquareRoot"/>.</summary>
    public ZstdSegmentScore Score { get; init; } = ZstdSegmentScore.SquareRoot;

    /// <summary>Gets a value indicating whether to search <see cref="SegmentSizeCandidates"/>, <see cref="DmerSizeCandidates"/> and <see cref="ScoreCandidates"/> for the best combination. Default true. When false, <see cref="SegmentSize"/>, <see cref="DmerSize"/> and <see cref="Score"/> are used as given.</summary>
    public bool Optimize { get; init; } = true;

    /// <summary>Gets the segment size k in bytes (the unit of content copied from the samples), used when <see cref="Optimize"/> is false. Default 256.</summary>
    public int SegmentSize { get; init; } = 256;

    /// <summary>Gets the d-mer size d in bytes (the unit whose frequency is counted), 2 to 16, used when <see cref="Optimize"/> is false. Default 8.</summary>
    public int DmerSize { get; init; } = 8;

    /// <summary>Gets how the search walks the candidates. Default <see cref="ZstdTrainingSearch.Guided"/>.</summary>
    public ZstdTrainingSearch SearchMode { get; init; } = ZstdTrainingSearch.Guided;

    /// <summary>Gets the segment sizes the search tries. Default 32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048.</summary>
    public IReadOnlyList<int> SegmentSizeCandidates { get; init; } = [32, 48, 64, 96, 128, 192, 256, 384, 512, 768, 1024, 1536, 2048];

    /// <summary>Gets the scores the search tries. Default <see cref="ZstdSegmentScore.SquareRoot"/> only: it won more rows than <see cref="ZstdSegmentScore.Frequency"/> on game payloads and Silesia chunks, and halves the search.</summary>
    public IReadOnlyList<ZstdSegmentScore> ScoreCandidates { get; init; } = [ZstdSegmentScore.SquareRoot];

    /// <summary>Gets the d-mer sizes the search tries. Default 6 and 8.</summary>
    public IReadOnlyList<int> DmerSizeCandidates { get; init; } = [6, 8];

    /// <summary>
    /// Gets the share of the samples (in percent, 1 to 100) that candidates are trained on during the search. The rest, spread evenly
    /// through the list (with 75, every fourth sample), is what they are scored on. With 100, candidates are scored on the samples they
    /// were trained on. With a share below 100, sample sets up to 24 MiB are scored on up to four such held-out parts in turn and the
    /// totals added. The winning combination is always trained again on all the samples. Default 75.
    /// </summary>
    public int TrainingPercent { get; init; } = 75;

    /// <summary>Gets the level at which the search compresses the held-out samples to score a candidate, 1 to 22. Default 3.</summary>
    public int ScoringLevel { get; init; } = 3;

    /// <summary>Gets the most held-out samples used to score a candidate; more are thinned evenly. Default 2000.</summary>
    public int MaxScoringSamples { get; init; } = 2000;

    /// <summary>Gets the largest hash table size for <see cref="ZstdTrainingAlgorithm.FastCover"/> as a power of two, 8 to 24. Smaller sample sets use a smaller table (eight to sixteen slots per byte of samples), since a table much larger than the data only costs time. Default 20.</summary>
    public int HashBits { get; init; } = 20;

    /// <summary>Gets the sampling step for <see cref="ZstdTrainingAlgorithm.FastCover"/>, 1 to 10: only every n-th d-mer position counts. Default 1.</summary>
    public int Acceleration { get; init; } = 1;

    /// <summary>Gets the dictionary ID to write, or 0 to derive one from the dictionary bytes (in the range the format leaves free, 32768 to 2^31-1). IDs from 1 to 32767 and from 2^31 up are reserved by the format (RFC 8878 §5) and are rejected. Default 0.</summary>
    public uint DictionaryId { get; init; }

    /// <summary>
    /// Gets how many bytes of samples the trainer looks at. When there are more, samples are thinned evenly (every n-th sample,
    /// n chosen from the sizes alone). Default 64 MiB.
    /// </summary>
    public long MaxTrainingBytes { get; init; } = 64L << 20;

    /// <summary>Gets how many candidates the search scores at once. 0 means the processor count, at most 8. The result does not depend on it. Default 0.</summary>
    public int MaxDegreeOfParallelism { get; init; }
}
