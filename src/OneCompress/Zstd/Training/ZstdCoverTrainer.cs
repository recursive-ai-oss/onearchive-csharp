namespace RecursiveAI.OneCompress.Zstd;

/// <summary>A segment size and d-mer size for COVER-style training.</summary>
/// <param name="SegmentSize">The segment size k in bytes.</param>
/// <param name="DmerSize">The d-mer size d in bytes.</param>
/// <param name="Score">How d-mer frequencies are combined into a segment's score.</param>
public readonly record struct ZstdCoverParameters(int SegmentSize, int DmerSize, ZstdSegmentScore Score = ZstdSegmentScore.SquareRoot);

/// <summary>
/// Trains Zstandard dictionaries with the COVER family of algorithms (Liao, Petri, Moffat and Wirth, "Effective Construction of
/// Relative Lempel-Ziv Dictionaries", WWW 2016, section 3). The pieces are public so that they can be composed: choose the content
/// (<see cref="SelectContent"/>), search the parameters (<see cref="Optimize"/>), and turn content into a dictionary with
/// <see cref="ZstdDictionaryBuilder"/>; <see cref="Train"/> does all three.
/// </summary>
/// <remarks>
/// <para>The samples are laid end to end and cut into as many equal epochs as the dictionary has room for segments of k bytes. In each
/// epoch the segment whose distinct d-mers have the highest total weight is chosen (a d-mer's weight is the number of samples that
/// contain it, or its square root), and the weights of the d-mers it contains are set to zero so that later epochs look for
/// something new. Segments are written in order of increasing score, so the most useful content is nearest the data and has the
/// smallest offsets.</para>
/// <para>Every comparison is on integers and ties go to the earliest position, the smaller score order, the smaller d and then the
/// smaller k, so the result depends only on the samples and the options.</para>
/// </remarks>
public static class ZstdCoverTrainer
{
    // A dictionary file's header (magic, ID, tables, repeat offsets) is usually 150 to 350 bytes; this much is set aside for it
    // when the content is first chosen, and the real size is used when the content is chosen again.
    private const int HeaderEstimate = 350;

    // The tables of a candidate are measured on at most this many training samples, so that a search costs a few parses, not one per
    // candidate over all the data.
    private const int CandidateStatSamples = 2000;
    private const long CandidateStatBytes = 4L << 20;

    // Sample sets up to this size are scored on four folds.
    private const long CrossValidateBytes = 24L << 20;

    /// <summary>
    /// Trains a complete dictionary (content chosen by COVER or FastCover, tables measured on the samples) no larger than
    /// <see cref="ZstdDictionaryTrainingOptions.DictionarySize"/>.
    /// </summary>
    /// <param name="samples">The training samples; empty samples are ignored.</param>
    /// <param name="options">The options; null means the defaults.</param>
    /// <param name="cancellationToken">Cancels a long search.</param>
    /// <returns>The dictionary file's bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null or holds a null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes, or the dictionary size is too small to hold any content.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static byte[] Train(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= ZstdDictionaryTrainingOptions.Default;
        ZstdTrainingSet set = Prepare(samples, options);
        ZstdCoverParameters parameters = options.Optimize
            ? Search(set, options, cancellationToken)
            : Validate(new ZstdCoverParameters(options.SegmentSize, options.DmerSize, options.Score));

        ZstdDmerIndex index = ZstdDmerIndex.Build(set, parameters.DmerSize, options, cancellationToken);
        var stats = new ZstdDictionaryBuilder.Statistics();

        // First choice of content with room reserved for the header, to measure the header; then the content that fills the rest.
        int size = options.DictionarySize;
        byte[] content = SelectFrom(set, index, parameters, Math.Max(size - HeaderEstimate, 16), cancellationToken);
        ZstdDictionaryBuilder.CollectStatistics(content, set.Slice(0, set.Count, CandidateStatSamples, CandidateStatBytes), stats);
        int header = ZstdDictionaryBuilder.Assemble(content, stats, 0).Length - content.Length;
        int budget = Math.Max(size - header, 16);
        if (budget != content.Length)
        {
            content = SelectFrom(set, index, parameters, budget, cancellationToken);
        }

        stats = new ZstdDictionaryBuilder.Statistics();
        CollectTableStatistics(set, parameters, content, stats, options, cancellationToken);
        byte[] dictionary = ZstdDictionaryBuilder.Assemble(content, stats, options.DictionaryId);
        if (dictionary.Length > size)
        {
            // The final tables were bigger than the estimate: drop the least valuable content, which is at the front.
            int excess = dictionary.Length - size;
            if (content.Length - excess < 8)
            {
                throw new CompressionDictionaryException("zstd", options.DictionaryId, $"The dictionary size {size} is too small to hold the tables and any content.");
            }

            content = content[excess..];
            dictionary = ZstdDictionaryBuilder.Assemble(content, stats, options.DictionaryId);
        }

        return dictionary;
    }

    /// <summary>
    /// Chooses dictionary content with the fixed <see cref="ZstdDictionaryTrainingOptions.SegmentSize"/> and
    /// <see cref="ZstdDictionaryTrainingOptions.DmerSize"/> (no parameter search).
    /// </summary>
    /// <param name="samples">The training samples.</param>
    /// <param name="options">The options; null means the defaults. <see cref="ZstdDictionaryTrainingOptions.DictionarySize"/> is the size of the content returned, at most.</param>
    /// <param name="cancellationToken">Cancels a long run.</param>
    /// <returns>The content: at most <see cref="ZstdDictionaryTrainingOptions.DictionarySize"/> bytes, at least 8.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null or holds a null.</exception>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes.</exception>
    public static byte[] SelectContent(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= ZstdDictionaryTrainingOptions.Default;
        ZstdTrainingSet set = Prepare(samples, options);
        ZstdCoverParameters parameters = Validate(new ZstdCoverParameters(options.SegmentSize, options.DmerSize, options.Score));
        ZstdDmerIndex index = ZstdDmerIndex.Build(set, parameters.DmerSize, options, cancellationToken);
        return SelectFrom(set, index, parameters, options.DictionarySize, cancellationToken);
    }

    /// <summary>
    /// Searches the candidate segment sizes, d-mer sizes and scores: each pair is trained on <see cref="ZstdDictionaryTrainingOptions.TrainingPercent"/>
    /// of the samples and scored by the total size of the evenly spread others compressed with it.
    /// </summary>
    /// <param name="samples">The training samples.</param>
    /// <param name="options">The options; null means the defaults. <see cref="ZstdDictionaryTrainingOptions.Optimize"/> is not consulted.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The pair with the smallest total; ties go to the smaller d, then the lower score value, then the smaller k.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null or holds a null.</exception>
    /// <exception cref="ArgumentException">An option is out of range, or no candidate pair has k at least d.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes.</exception>
    public static ZstdCoverParameters Optimize(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= ZstdDictionaryTrainingOptions.Default;
        return Search(Prepare(samples, options), options, cancellationToken);
    }

    private static ZstdTrainingSet Prepare(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions options)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ValidateOptions(options);
        ZstdTrainingSet set = ZstdTrainingSet.Create(samples, options.MaxTrainingBytes);
        if (set.Length < 8)
        {
            throw new CompressionDictionaryException("zstd", options.DictionaryId, $"Training needs at least 8 bytes of samples; found {set.Length}.");
        }

        return set;
    }

    private static void ValidateOptions(ZstdDictionaryTrainingOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DictionarySize, 256, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.DictionarySize, 1 << 30, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TrainingPercent, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.TrainingPercent, 100, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ScoringLevel, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ScoringLevel, 22, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxScoringSamples, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.HashBits, 8, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.HashBits, 24, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Acceleration, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Acceleration, 10, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxTrainingBytes, 16, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxTrainingBytes, 1L << 30, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxDegreeOfParallelism, nameof(options));
        ArgumentNullException.ThrowIfNull(options.SegmentSizeCandidates, nameof(options));
        ArgumentNullException.ThrowIfNull(options.DmerSizeCandidates, nameof(options));
        ArgumentNullException.ThrowIfNull(options.ScoreCandidates, nameof(options));
        if (options.DictionaryId != 0 && (options.DictionaryId < 32768 || options.DictionaryId >= 0x80000000u))
        {
            throw new ArgumentException("Dictionary IDs from 1 to 32767 and from 2^31 up are reserved by the format.", nameof(options));
        }

        if (!options.Optimize)
        {
            Validate(new ZstdCoverParameters(options.SegmentSize, options.DmerSize, options.Score));
        }
    }

    private static ZstdCoverParameters Validate(ZstdCoverParameters p)
    {
        if (p.DmerSize is < 2 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "The d-mer size must be from 2 to 16.");
        }

        if (p.SegmentSize < p.DmerSize || p.SegmentSize > 1 << 24)
        {
            throw new ArgumentOutOfRangeException(nameof(p), "The segment size must be at least the d-mer size and at most 16 MiB.");
        }

        return p;
    }

    private static ZstdCoverParameters Search(ZstdTrainingSet set, ZstdDictionaryTrainingOptions options, CancellationToken cancellationToken)
    {
        // Distinct candidates in a fixed order: d ascending, then score, then k ascending; the first of equal totals wins.
        int[] ds = options.DmerSizeCandidates.Distinct().OrderBy(x => x).ToArray();
        int[] ks = options.SegmentSizeCandidates.Distinct().OrderBy(x => x).ToArray();
        foreach (int d in ds)
        {
            if (d is < 2 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Every d-mer size candidate must be from 2 to 16.");
            }
        }

        ZstdSegmentScore[] scores = options.ScoreCandidates.Distinct().OrderBy(x => x).ToArray();
        var candidates = new List<ZstdCoverParameters>();
        foreach (int d in ds)
        {
            foreach (ZstdSegmentScore score in scores)
            {
                foreach (int k in ks)
                {
                    if (k >= d && k <= 1 << 24)
                    {
                        candidates.Add(new ZstdCoverParameters(k, d, score));
                    }
                }
            }
        }

        if (candidates.Count == 0)
        {
            throw new ArgumentException("No candidate has a segment size at least as large as its d-mer size.", nameof(options));
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // Hold out evenly spread blocks of samples: the samples are cut into 64 blocks, and with 75 percent every fourth block (block
        // number modulo 4) forms a fold that candidates are scored on after training on the rest. Samples next to each other are often
        // alike (ticks of one game, pieces of one file), so holding out single samples would let a candidate score well by memorizing
        // the neighbours of what it is tested on, while one block at the end would test it on data unlike what it trained on. Small
        // sample sets are noisy to score, so up to four folds are used and their totals added; large ones use the first fold only.
        int foldCount = options.TrainingPercent == 100 || set.Count < 2 ? 1 : (200 - options.TrainingPercent - 1) / (100 - options.TrainingPercent);
        int evaluated = foldCount == 1 ? 1 : set.Length <= CrossValidateBytes ? Math.Min(foldCount, 4) : 1;
        var folds = new Fold[evaluated];
        for (int f = 0; f < evaluated; f++)
        {
            folds[f] = MakeFold(set, foldCount, f, options);
        }

        int threads = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Math.Min(Environment.ProcessorCount, 8);
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = cancellationToken };
        int budget = Math.Max(options.DictionarySize - HeaderEstimate, 16);

        // One d-mer index per fold and d, shared by every k and score (and read-only, so candidates can run side by side).
        var indexes = new ZstdDmerIndex[evaluated * ds.Length];
        Parallel.For(0, indexes.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(threads, 4), CancellationToken = cancellationToken }, i =>
        {
            indexes[i] = ZstdDmerIndex.Build(folds[i / ds.Length].Train, ds[i % ds.Length], options, cancellationToken);
        });

        long[] foldTotals = new long[evaluated * candidates.Count];
        Parallel.For(0, foldTotals.Length, parallel, i =>
        {
            int f = i / candidates.Count;
            ZstdCoverParameters cp = candidates[i % candidates.Count];
            ZstdDmerIndex index = indexes[(f * ds.Length) + Array.IndexOf(ds, cp.DmerSize)];
            byte[] content = SelectFrom(folds[f].Train, index, cp, budget, cancellationToken);
            var stats = new ZstdDictionaryBuilder.Statistics();
            ZstdDictionaryBuilder.CollectStatistics(content, folds[f].StatSamples, stats);
            byte[] dictionary = ZstdDictionaryBuilder.Assemble(content, stats, 0);
            foldTotals[i] = TotalCompressedSize(dictionary, folds[f].Test, options.ScoringLevel, cancellationToken);
        });

        int best = 0;
        long bestTotal = long.MaxValue;
        for (int c = 0; c < candidates.Count; c++)
        {
            long total = 0;
            for (int f = 0; f < evaluated; f++)
            {
                total += foldTotals[(f * candidates.Count) + c];
            }

            if (total < bestTotal)
            {
                bestTotal = total;
                best = c;
            }
        }

        return candidates[best];
    }

    // The tables are measured on frames the content has not been chosen from. A dictionary chosen from the samples contains them, so
    // parsing them against it finds one long match each and the tables would describe frames that are nothing like new data (the
    // smaller the samples are against the dictionary, the worse). Instead the samples are cut into four folds; for each, content of the
    // same size is chosen from the other three and the fold is parsed against that. Large sample sets are far bigger than the
    // dictionary, where the effect is small, and are measured against the final content directly.
    private const int StatisticsFolds = 4;

    // The samples are cut into this many blocks of neighbours, dealt to the folds in turn.
    private const int FoldBlocks = 16;

    private static void CollectTableStatistics(ZstdTrainingSet set, ZstdCoverParameters parameters, byte[] content, ZstdDictionaryBuilder.Statistics stats, ZstdDictionaryTrainingOptions options, CancellationToken cancellationToken)
    {
        if (set.Length > CrossValidateBytes || set.Count < StatisticsFolds * 2)
        {
            ZstdDictionaryBuilder.CollectStatistics(content, set.Slice(0, set.Count, int.MaxValue, long.MaxValue), stats);
            return;
        }

        int blockSize = Math.Max(1, set.Count / FoldBlocks);
        for (int f = 0; f < StatisticsFolds; f++)
        {
            var trainList = new List<ReadOnlyMemory<byte>>();
            var testList = new List<ReadOnlyMemory<byte>>();
            for (int i = 0; i < set.Count; i++)
            {
                ((i / blockSize) % StatisticsFolds == f ? testList : trainList).Add(set.Sample(i));
            }

            ZstdTrainingSet train = trainList.Count == 0 || testList.Count == 0 ? set : ZstdTrainingSet.Create(trainList, long.MaxValue >> 2);
            if (train.Length < 8)
            {
                train = set;
            }

            ZstdDmerIndex index = ZstdDmerIndex.Build(train, parameters.DmerSize, options, cancellationToken);
            byte[] foldContent = SelectFrom(train, index, parameters, content.Length, cancellationToken);
            ZstdDictionaryBuilder.CollectStatistics(foldContent, testList.Count == 0 ? set.Slice(0, set.Count, int.MaxValue, long.MaxValue) : testList, stats);
        }
    }

    private sealed record Fold(ZstdTrainingSet Train, IReadOnlyList<ReadOnlyMemory<byte>> Test, IReadOnlyList<ReadOnlyMemory<byte>> StatSamples);

    private static Fold MakeFold(ZstdTrainingSet set, int foldCount, int fold, ZstdDictionaryTrainingOptions options)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> all = set.Slice(0, set.Count, options.MaxScoringSamples, long.MaxValue);
        if (foldCount == 1)
        {
            return new Fold(set, all, set.Slice(0, set.Count, CandidateStatSamples, CandidateStatBytes));
        }

        var trainList = new List<ReadOnlyMemory<byte>>();
        var testList = new List<ReadOnlyMemory<byte>>();
        int blockSize = Math.Max(1, set.Count / FoldBlocks);
        for (int i = 0; i < set.Count; i++)
        {
            ((i / blockSize) % foldCount == fold ? testList : trainList).Add(set.Sample(i));
        }

        if (trainList.Count == 0 || testList.Count == 0)
        {
            return new Fold(set, all, set.Slice(0, set.Count, CandidateStatSamples, CandidateStatBytes));
        }

        ZstdTrainingSet train = ZstdTrainingSet.Create(trainList, long.MaxValue >> 2);
        if (train.Length < 8)
        {
            return new Fold(set, all, set.Slice(0, set.Count, CandidateStatSamples, CandidateStatBytes));
        }

        return new Fold(train, Thin(testList, options.MaxScoringSamples), Thin(testList, CandidateStatSamples));
    }

    private static long TotalCompressedSize(byte[] dictionaryBytes, IReadOnlyList<ReadOnlyMemory<byte>> test, int level, CancellationToken cancellationToken)
    {
        var dictionary = ZstdCompressionDictionary.Parse(dictionaryBytes);
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = Math.Min(level, ZstdCompressionParameters.MaxLevel), Dictionary = dictionary, WriteContentSize = true });
        byte[] buffer = new byte[1024];
        long total = 0;
        for (int i = 0; i < test.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> sample = test[i].Span;
            int max = ZstdEncoder.GetMaxCompressedLength(sample.Length);
            if (buffer.Length < max)
            {
                buffer = new byte[max];
            }

            total += encoder.CompressFrame(sample, buffer);
        }

        return total;
    }

    private static List<ReadOnlyMemory<byte>> Thin(List<ReadOnlyMemory<byte>> list, int max)
    {
        if (list.Count <= max)
        {
            return list;
        }

        // Every n-th, n from the counts alone.
        int stride = (list.Count + max - 1) / max;
        var thinned = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < list.Count; i += stride)
        {
            thinned.Add(list[i]);
        }

        return thinned;
    }

    private readonly record struct Segment(int Start, int Length, long Score, int Epoch);

    private static byte[] SelectFrom(ZstdTrainingSet set, ZstdDmerIndex index, ZstdCoverParameters parameters, int dictionarySize, CancellationToken cancellationToken)
    {
        int k = parameters.SegmentSize;
        int d = index.DmerSize;
        int n = set.Length;
        int[] ids = index.Ids;
        uint[] weights = new uint[index.Frequencies.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = ZstdDmerIndex.Weight(index.Frequencies[i], parameters.Score);
        }

        int[] counts = new int[weights.Length];

        // RFC-independent but fixed: as many epochs as there is room for segments, but never an epoch shorter than a segment.
        int epochs = Math.Clamp((dictionarySize + k - 1) / k, 1, Math.Max(1, n / k));
        int epochLength = n / epochs;
        int remaining = dictionarySize;
        var chosen = new List<Segment>();
        for (int e = 0; e < epochs && remaining >= d; e++)
        {
            if ((e & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int e0 = e * epochLength;
            int e1 = e == epochs - 1 ? n : e0 + epochLength;
            int window = Math.Min(Math.Min(k, remaining), e1 - e0);
            if (window < d)
            {
                continue;
            }

            int span = window - d;
            long score = 0;
            long best = 0;
            int bestStart = -1;
            for (int hi = e0; hi + d <= e1; hi++)
            {
                int id = ids[hi];
                if (id >= 0 && counts[id]++ == 0)
                {
                    score += weights[id];
                }

                int lo = hi - span - 1;
                if (lo >= e0)
                {
                    int old = ids[lo];
                    if (old >= 0 && --counts[old] == 0)
                    {
                        score -= weights[old];
                    }
                }

                // A window is evaluated once it holds its full width; the first best (earliest) wins ties.
                if (hi - e0 >= span && score > best)
                {
                    best = score;
                    bestStart = hi - span;
                }
            }

            for (int p = e0; p + d <= e1; p++)
            {
                int id = ids[p];
                if (id >= 0)
                {
                    counts[id] = 0;
                }
            }

            if (bestStart < 0)
            {
                continue;
            }

            // The d-mers of the chosen segment are covered now: they stop counting for every later epoch.
            for (int p = bestStart; p + d <= bestStart + window; p++)
            {
                int id = ids[p];
                if (id >= 0)
                {
                    weights[id] = 0;
                }
            }

            chosen.Add(new Segment(bestStart, window, best, e));
            remaining -= window;
        }

        if (chosen.Count == 0)
        {
            // Nothing scored (for instance, no d-mer occurs in two samples under a zero weight): take the start of the samples.
            int length = Math.Min(dictionarySize, n);
            return set.Data.AsSpan(0, Math.Max(length, 0)).ToArray();
        }

        // Least valuable first, so the most valuable content ends up closest to the data it serves.
        chosen.Sort(static (a, b) => a.Score != b.Score ? a.Score.CompareTo(b.Score) : a.Epoch.CompareTo(b.Epoch));
        int total = 0;
        foreach (Segment s in chosen)
        {
            total += s.Length;
        }

        byte[] content = new byte[total];
        int w = 0;
        foreach (Segment s in chosen)
        {
            set.Data.AsSpan(s.Start, s.Length).CopyTo(content.AsSpan(w));
            w += s.Length;
        }

        return content.Length >= 8 ? content : set.Data.AsSpan(0, Math.Min(Math.Max(dictionarySize, 8), n)).ToArray();
    }
}
