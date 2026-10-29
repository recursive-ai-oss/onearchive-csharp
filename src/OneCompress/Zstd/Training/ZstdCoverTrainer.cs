using System.Diagnostics.CodeAnalysis;
namespace RecursiveAI.OneCompress.Zstd;

/// <summary>A segment size and d-mer size for COVER-style training.</summary>
/// <param name="SegmentSize">The segment size k in bytes.</param>
/// <param name="DmerSize">The d-mer size d in bytes.</param>
/// <param name="Score">How d-mer frequencies are combined into a segment's score.</param>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
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
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public static class ZstdCoverTrainer
{
    // A dictionary file's header (magic, ID, tables, repeat offsets) is usually 150 to 350 bytes; this much is set aside for it
    // when the content is first chosen, and the real size is used when the content is chosen again.
    private const int HeaderEstimate = 350;

    // The held-out samples a candidate is scored on are thinned to this many bytes, so that a search costs a few parses of a bounded
    // amount of data, not one per candidate over all of it. The samples the tables are measured on are thinned less.
    private const long ScoreBytes = 1L << 20;
    private const int StatSamples = 20000;
    private const long StatBytes = 8L << 20;

    // The samples are cut into this many blocks of neighbours, dealt to the folds in turn. Samples next to each other are often alike
    // (ticks of one game, pieces of one file), so holding out single samples would let a candidate score well by memorizing the
    // neighbours of what it is tested on, while one block at the end would test it on data unlike what it trained on.
    private const int FoldBlocks = 16;

    // Small sample sets are noisy to score and cheap to train, so they use up to four folds (held-out parts, scored in turn and added);
    // larger ones use fewer, and the largest one.
    private const long FourFoldBytes = 1L << 20;
    private const long TwoFoldBytes = 4L << 20;
    private const long StatFoldBytes = 24L << 20;

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
        int size = options.DictionarySize;
        List<ZstdCoverParameters> candidates = options.Optimize
            ? Narrow(Candidates(options), options, set)
            : [Validate(new ZstdCoverParameters(options.SegmentSize, options.DmerSize, options.Score))];

        // Each fold: content chosen from the samples outside it, and the tables measured by parsing the samples inside it. A frame
        // compressed with the finished dictionary is new data to it, and the tables should describe that, not frames the content
        // was cut from (which parse as one long match each).
        using var run = new Run(set, options, cancellationToken);
        run.Count(candidates);
        Fold[] folds = run.MakeFolds();
        run.AttachIndexes(folds, candidates);
        int budget = Math.Max(size - HeaderEstimate, 16);
        Evaluation best = options.Optimize && candidates.Count > 1
            ? RunSearch(run, folds, candidates, budget)
            : EvaluateCells(run, folds, candidates, [0], budget, false)[0];
        ZstdCoverParameters parameters = candidates[best.Candidate];

        // The tables are measured on more folds than the search scored, when the samples are few enough for that to be cheap. Each
        // fold chooses its content from the samples outside it (the folds the search scored already have it) and parses the samples
        // inside it; the histograms are added up in fold order.
        int statFolds = Math.Max(run.StatFoldCount, folds.Length);
        var statContents = new byte[statFolds][];
        var partial = new ZstdDictionaryBuilder.Statistics[statFolds];
        var statParallel = new ParallelOptions { MaxDegreeOfParallelism = run.Threads, CancellationToken = cancellationToken };
        Parallel.For(0, statFolds, statParallel, run.Rent, (f, _, scratch) =>
        {
            byte[] content;
            IReadOnlyList<ReadOnlyMemory<byte>> held;
            if (f < folds.Length)
            {
                content = best.Contents[f];
                held = folds[f].StatSamples;
            }
            else
            {
                Fold fold = run.MakeFold(f);
                ZstdDmerIndex foldIndex = run.IndexOf(fold, parameters);
                content = ZstdSegmentSelector.Select(foldIndex, parameters.Score, parameters.SegmentSize, budget, scratch, cancellationToken);
                foldIndex.Release();
                held = fold.StatSamples;
            }

            statContents[f] = content;
            partial[f] = new ZstdDictionaryBuilder.Statistics();
            ZstdDictionaryBuilder.CollectStatistics(content, held, partial[f]);
            return scratch;
        }, run.Return);
        foreach (Fold fold in folds)
        {
            fold.Release();
        }

        var stats = new ZstdDictionaryBuilder.Statistics();
        foreach (ZstdDictionaryBuilder.Statistics p in partial)
        {
            stats.Add(p);
        }

        // The header is as large as the tables just measured need; the content fills the rest.
        byte[] first = statContents[0];
        int header = ZstdDictionaryBuilder.Assemble(first, stats, 0).Length - first.Length;
        budget = Math.Max(size - header, 16);
        byte[] content;
        if (folds[0].Group < 0 && budget == first.Length)
        {
            content = first;
        }
        else
        {
            ZstdDmerIndex index = run.IndexOf(run.WholeFold(), parameters);
            ZstdTrainingScratch scratch = run.Rent();
            content = ZstdSegmentSelector.Select(index, parameters.Score, parameters.SegmentSize, budget, scratch, cancellationToken);
            run.Return(scratch);
            index.Release();
        }
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
        var scratch = new ZstdTrainingScratch();
        using var run = new Run(set, options, cancellationToken, noFolds: true);
        run.Count([parameters]);
        ZstdDmerIndex index = run.IndexOf(run.WholeFold(), parameters);
        byte[] content = ZstdSegmentSelector.Select(index, parameters.Score, parameters.SegmentSize, options.DictionarySize, scratch, cancellationToken);
        index.Release();
        scratch.Release();
        return content;
    }

    /// <summary>
    /// Searches the candidate segment sizes, d-mer sizes and scores: each pair is trained on <see cref="ZstdDictionaryTrainingOptions.TrainingPercent"/>
    /// of the samples and scored by the total size of the evenly spread others compressed with it.
    /// </summary>
    /// <param name="samples">The training samples.</param>
    /// <param name="options">The options; null means the defaults. <see cref="ZstdDictionaryTrainingOptions.Optimize"/> is not consulted;
    /// <see cref="ZstdDictionaryTrainingOptions.SearchMode"/> chooses how many of the pairs are tried.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The pair with the smallest total; ties go to the smaller d, then the lower score value, then the smaller k.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null or holds a null.</exception>
    /// <exception cref="ArgumentException">An option is out of range, or no candidate pair has k at least d.</exception>
    /// <exception cref="CompressionDictionaryException">The samples hold fewer than 8 bytes.</exception>
    public static ZstdCoverParameters Optimize(IReadOnlyList<ReadOnlyMemory<byte>> samples, ZstdDictionaryTrainingOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= ZstdDictionaryTrainingOptions.Default;
        ZstdTrainingSet set = Prepare(samples, options);
        List<ZstdCoverParameters> candidates = Narrow(Candidates(options), options, set);
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        using var run = new Run(set, options, cancellationToken);
        run.Count(candidates);
        Fold[] folds = run.MakeFolds();
        run.AttachIndexes(folds, candidates);
        int best = RunSearch(run, folds, candidates, Math.Max(options.DictionarySize - HeaderEstimate, 16)).Candidate;
        foreach (Fold fold in folds)
        {
            fold.Release();
        }

        return candidates[best];
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

    // The candidates in a fixed order: d ascending, then score, then k ascending; the first of equal totals wins.
    private static List<ZstdCoverParameters> Candidates(ZstdDictionaryTrainingOptions options)
    {
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

        return candidates;
    }

    /// <summary>One held-out part of the samples: the rest to train on, and what to score and measure tables on.</summary>
    private sealed class Fold
    {
        private readonly Dictionary<int, ZstdDmerIndex> _indexes = [];

        public Fold(int[] runs, int trainLength, int trainCount, int group, IReadOnlyList<ReadOnlyMemory<byte>> scoreSamples, IReadOnlyList<ReadOnlyMemory<byte>> statSamples)
        {
            Runs = runs;
            TrainLength = trainLength;
            TrainCount = trainCount;
            Group = group;
            ScoreSamples = scoreSamples;
            StatSamples = statSamples;
        }

        /// <summary>Gets the stretches of the samples content is chosen from (see <see cref="ZstdDmerIndex.Runs"/>).</summary>
        public int[] Runs { get; }

        public int TrainLength { get; }

        public int TrainCount { get; }

        /// <summary>Gets the group of samples held out (-1 when none is).</summary>
        public int Group { get; }

        public IReadOnlyList<ReadOnlyMemory<byte>> ScoreSamples { get; }

        public IReadOnlyList<ReadOnlyMemory<byte>> StatSamples { get; }

        public ZstdDmerIndex Index(int d) => _indexes[d];

        public void Set(int d, ZstdDmerIndex index) => _indexes[d] = index;

        public void Release()
        {
            foreach (ZstdDmerIndex index in _indexes.Values)
            {
                index.Release();
            }

            _indexes.Clear();
        }
    }

    /// <summary>The state of one training: the samples, how they are cut into folds, and the d-mer counts.</summary>
    private sealed class Run : IDisposable
    {
        private readonly ZstdTrainingSet _set;
        private readonly ZstdDictionaryTrainingOptions _options;
        private readonly Dictionary<int, ZstdDmerCounts> _counts = [];
        private readonly int _blockSize;
        private readonly System.Collections.Concurrent.ConcurrentBag<ZstdTrainingScratch> _pool = [];
        private ZstdSegmentScore[] _scores = [];

        public Run(ZstdTrainingSet set, ZstdDictionaryTrainingOptions options, CancellationToken cancellationToken, bool noFolds = false)
        {
            _set = set;
            _options = options;
            CancellationToken = cancellationToken;
            Threads = options.MaxDegreeOfParallelism > 0 ? options.MaxDegreeOfParallelism : Math.Min(Environment.ProcessorCount, 8);
            FoldCount = noFolds ? 1 : CutIntoFolds(set, options, out _blockSize);
            if (FoldCount == 1)
            {
                _blockSize = 1;
            }
        }

        public CancellationToken CancellationToken { get; }

        /// <summary>Takes a scratch set from the pool (or makes one); the arrays in it are reused by the next candidate, not reallocated.</summary>
        public ZstdTrainingScratch Rent() => _pool.TryTake(out ZstdTrainingScratch? scratch) ? scratch : new ZstdTrainingScratch();

        public void Return(ZstdTrainingScratch scratch) => _pool.Add(scratch);

        public void Dispose()
        {
            foreach (ZstdDmerCounts counts in _counts.Values)
            {
                counts.Release();
            }

            _counts.Clear();
            while (_pool.TryTake(out ZstdTrainingScratch? scratch))
            {
                scratch.Release();
            }
        }

        public int Threads { get; }

        public ZstdDictionaryTrainingOptions Options => _options;

        /// <summary>Gets how many parts the samples are cut into; a part is held out in turn. 1 means no part is held out.</summary>
        public int FoldCount { get; }

        /// <summary>Gets how many folds the search scores on: all of them for small sample sets, fewer as the set grows.</summary>
        public int EvaluatedFolds => FoldCount == 1 ? 1 : Math.Min(FoldCount, _set.Length <= FourFoldBytes ? 4 : _set.Length <= TwoFoldBytes ? 2 : 1);

        /// <summary>Gets how many folds the tables are measured on: all of them for sample sets up to 24 MiB, one above that (where the
        /// dictionary is a small part of the data and one fold has plenty of samples).</summary>
        public int StatFoldCount => FoldCount == 1 ? 1 : _set.Length <= StatFoldBytes ? FoldCount : 1;

        // Samples next to each other are often alike (ticks of one game, pieces of one file), so holding out single samples would let a
        // candidate score well by memorizing the neighbours of what it is tested on, while one block at the end would test it on data
        // unlike what it trained on: the samples are cut into blocks of neighbours that are dealt to the folds in turn.
        private static int CutIntoFolds(ZstdTrainingSet set, ZstdDictionaryTrainingOptions options, out int blockSize)
        {
            blockSize = Math.Max(1, set.Count / FoldBlocks);
            int folds = options.TrainingPercent == 100 || set.Count < 2 ? 1 : (200 - options.TrainingPercent - 1) / (100 - options.TrainingPercent);
            if (folds > 1)
            {
                // A fold needs samples to hold out and at least a few bytes to train on.
                var groupBytes = new long[folds];
                var groupSamples = new int[folds];
                for (int i = 0; i < set.Count; i++)
                {
                    int g = (i / blockSize) % folds;
                    groupBytes[g] += set.Starts[i + 1] - set.Starts[i];
                    groupSamples[g]++;
                }

                for (int g = 0; g < folds; g++)
                {
                    if (groupSamples[g] == 0 || set.Length - groupBytes[g] < 8)
                    {
                        return 1;
                    }
                }
            }

            return folds;
        }

        /// <summary>Counts the d-mers for every d-mer size the candidates use.</summary>
        public void Count(List<ZstdCoverParameters> candidates)
        {
            int[] ds = candidates.Select(c => c.DmerSize).Distinct().OrderBy(x => x).ToArray();
            _scores = candidates.Select(c => c.Score).Distinct().ToArray();
            var counts = new ZstdDmerCounts[ds.Length];
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Threads, 2), CancellationToken = CancellationToken };
            Parallel.For(0, ds.Length, parallel, Rent, (i, _, scratch) =>
            {
                counts[i] = ZstdDmerCounts.Build(_set, ds[i], FoldCount, _blockSize, _options, scratch, CancellationToken);
                return scratch;
            }, Return);
            for (int i = 0; i < ds.Length; i++)
            {
                _counts[ds[i]] = counts[i];
            }
        }

        /// <summary>The folds the search scores on.</summary>
        public Fold[] MakeFolds()
        {
            var folds = new Fold[EvaluatedFolds];
            for (int f = 0; f < folds.Length; f++)
            {
                folds[f] = MakeFold(f);
            }

            return folds;
        }

        /// <summary>The fold that holds out nothing: content is chosen from all the samples.</summary>
        public Fold WholeFold() => new([0, 0, _set.Length], _set.Length, _set.Count, -1, [], []);

        public Fold MakeFold(int fold)
        {
            if (FoldCount == 1)
            {
                return new Fold([0, 0, _set.Length], _set.Length, _set.Count, -1, _set.Slice(0, _set.Count, _options.MaxScoringSamples, ScoreBytes), _set.Slice(0, _set.Count, StatSamples, StatBytes));
            }

            // The samples outside the fold are a few stretches of the original data: each is a (compact start, start, length) triple.
            var runs = new List<int>();
            var testList = new List<ReadOnlyMemory<byte>>();
            int compact = 0;
            int trainCount = 0;
            for (int i = 0; i < _set.Count; i++)
            {
                int length = _set.Starts[i + 1] - _set.Starts[i];
                if ((i / _blockSize) % FoldCount == fold)
                {
                    testList.Add(_set.Sample(i));
                    continue;
                }

                int count = runs.Count;
                if (count > 0 && runs[count - 2] + runs[count - 1] == _set.Starts[i])
                {
                    runs[count - 1] += length;
                }
                else
                {
                    runs.Add(compact);
                    runs.Add(_set.Starts[i]);
                    runs.Add(length);
                }

                compact += length;
                trainCount++;
            }

            return new Fold([.. runs], compact, trainCount, fold, Thin(testList, _options.MaxScoringSamples, ScoreBytes), Thin(testList, StatSamples, StatBytes));
        }

        /// <summary>The index of the samples a fold trains on, for the candidate's d-mer size and score.</summary>
        public ZstdDmerIndex IndexOf(Fold fold, ZstdCoverParameters candidate) =>
            _counts[candidate.DmerSize].IndexFor(fold.Runs, fold.TrainLength, fold.TrainCount, fold.Group, [candidate.Score]);

        public void AttachIndexes(Fold[] folds, List<ZstdCoverParameters> candidates)
        {
            int[] ds = candidates.Select(c => c.DmerSize).Distinct().OrderBy(x => x).ToArray();
            var built = new ZstdDmerIndex[folds.Length * ds.Length];
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = Threads, CancellationToken = CancellationToken };
            Parallel.For(0, built.Length, parallel, i =>
            {
                Fold fold = folds[i / ds.Length];
                built[i] = _counts[ds[i % ds.Length]].IndexFor(fold.Runs, fold.TrainLength, fold.TrainCount, fold.Group, _scores);
            });
            for (int i = 0; i < built.Length; i++)
            {
                folds[i / ds.Length].Set(ds[i % ds.Length], built[i]);
            }
        }
    }

    // Every n-th sample, n from the counts and sizes alone.
    private static List<ReadOnlyMemory<byte>> Thin(List<ReadOnlyMemory<byte>> list, int maxSamples, long maxBytes)
    {
        long bytes = 0;
        foreach (ReadOnlyMemory<byte> s in list)
        {
            bytes += s.Length;
        }

        long stride = Math.Max(Math.Max(1, (list.Count + maxSamples - 1) / maxSamples), (bytes + maxBytes - 1) / maxBytes);
        if (stride <= 1)
        {
            return list;
        }

        var thinned = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < list.Count; i += (int)stride)
        {
            thinned.Add(list[i]);
        }

        return thinned;
    }

    /// <summary>What one candidate came to on every fold: the content each fold chose, and the total size of the held-out samples.</summary>
    private sealed class Evaluation
    {
        public int Candidate;
        public long Total;
        public byte[][] Contents = [];
    }

    private static Evaluation[] EvaluateCells(Run run, Fold[] folds, List<ZstdCoverParameters> candidates, int[] cells, int budget, bool score)
    {
        var results = new Evaluation[cells.Length];
        for (int i = 0; i < cells.Length; i++)
        {
            results[i] = new Evaluation { Candidate = cells[i], Contents = new byte[folds.Length][] };
        }

        long[] totals = new long[cells.Length * folds.Length];
        CancellationToken cancellationToken = run.CancellationToken;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = run.Threads, CancellationToken = cancellationToken };
        Parallel.For(0, totals.Length, parallel, run.Rent, (i, _, scratch) =>
        {
            int cell = i / folds.Length;
            int f = i % folds.Length;
            ZstdCoverParameters cp = candidates[cells[cell]];
            ZstdDmerIndex index = folds[f].Index(cp.DmerSize);
            byte[] content = ZstdSegmentSelector.Select(index, cp.Score, cp.SegmentSize, budget, scratch, cancellationToken);
            results[cell].Contents[f] = content;
            totals[i] = score ? TotalCompressedSize(content, folds[f].ScoreSamples, run.Options.ScoringLevel, scratch, cancellationToken) : 0;
            return scratch;
        }, run.Return);
        for (int cell = 0; cell < cells.Length; cell++)
        {
            for (int f = 0; f < folds.Length; f++)
            {
                results[cell].Total += totals[(cell * folds.Length) + f];
            }
        }

        return results;
    }

    // Finds the candidate with the smallest total. Candidates are laid out as variants (d, score) by segment size, k ascending. The
    // guided search walks one variant's segment sizes on a coarse grid, then climbs from the best one to its neighbours until neither
    // is better, and finally tries the other variants at the best segment size; the exhaustive search tries them all. The cost of a
    // dictionary against segment size is smooth (it falls, flattens, and rises again), so the climb ends next to the best.
    // The variant searched first: the largest d up to 8, which is the d-mer size the zstd tool starts with.
    private static int PrimaryVariant(List<ZstdCoverParameters> candidates, List<int> starts)
    {
        int primary = 0;
        for (int v = 0; v < starts.Count - 1; v++)
        {
            if (candidates[starts[v]].DmerSize <= 8)
            {
                primary = v;
            }
        }

        return primary;
    }

    private static List<int> VariantStarts(List<ZstdCoverParameters> candidates)
    {
        var starts = new List<int>();
        for (int c = 0; c < candidates.Count; c++)
        {
            if (c == 0 || candidates[c].DmerSize != candidates[c - 1].DmerSize || candidates[c].Score != candidates[c - 1].Score)
            {
                starts.Add(c);
            }
        }

        starts.Add(candidates.Count);
        return starts;
    }

    // On a large sample set another d-mer size gains little (a few tenths of a percent) and costs a counting pass, a selection and a
    // scoring, so the guided search keeps only the first variant there; small sets are cheap enough to try the others.
    private static List<ZstdCoverParameters> Narrow(List<ZstdCoverParameters> candidates, ZstdDictionaryTrainingOptions options, ZstdTrainingSet set)
    {
        if (options.SearchMode == ZstdTrainingSearch.Exhaustive || candidates.Count <= 6 || set.Length <= TwoFoldBytes)
        {
            return candidates;
        }

        List<int> starts = VariantStarts(candidates);
        int primary = PrimaryVariant(candidates, starts);
        return candidates.GetRange(starts[primary], starts[primary + 1] - starts[primary]);
    }

    private static Evaluation RunSearch(Run run, Fold[] folds, List<ZstdCoverParameters> candidates, int budget)
    {
        ZstdDictionaryTrainingOptions options = run.Options;
        int count = candidates.Count;
        var done = new Evaluation?[count];
        void Run(IEnumerable<int> wanted)
        {
            int[] cells = wanted.Where(c => c >= 0 && c < count && done[c] is null).Distinct().OrderBy(c => c).ToArray();
            if (cells.Length == 0)
            {
                return;
            }

            Evaluation[] results = EvaluateCells(run, folds, candidates, cells, budget, true);
            foreach (Evaluation r in results)
            {
                done[r.Candidate] = r;
            }
        }

        Evaluation Best() => done.Where(e => e is not null).OrderBy(e => e!.Total).ThenBy(e => e!.Candidate).First()!;

        List<int> starts = VariantStarts(candidates);
        int variants = starts.Count - 1;
        if (options.SearchMode == ZstdTrainingSearch.Exhaustive || count <= 6)
        {
            Run(Enumerable.Range(0, count));
            return Best();
        }

        int primary = PrimaryVariant(candidates, starts);
        int from = starts[primary];
        int length = starts[primary + 1] - from;
        var grid = new SortedSet<int>();
        foreach (int third in new[] { 1, 3, 5 })
        {
            grid.Add(from + ((length - 1) * third / 6));
        }

        Run(grid);

        // Climb from the best of the grid while a neighbour is better by more than a quarter of a percent: the cost is flat near its
        // minimum, and a step that gains less is not worth another round of selecting and scoring.
        while (true)
        {
            Evaluation current = Best();
            Run(new[] { current.Candidate - 1, current.Candidate + 1 }.Where(c => c >= from && c < from + length));
            Evaluation next = Best();
            if (next.Candidate == current.Candidate || (current.Total - next.Total) * 400 < current.Total)
            {
                break;
            }
        }

        // The other variants, at the best segment size (or the nearest one they have).
        int bestSize = candidates[Best().Candidate].SegmentSize;
        var others = new List<int>();
        for (int v = 0; v < variants; v++)
        {
            if (v == primary)
            {
                continue;
            }

            int pick = starts[v];
            for (int c = starts[v]; c < starts[v + 1]; c++)
            {
                if (Math.Abs(candidates[c].SegmentSize - bestSize) < Math.Abs(candidates[pick].SegmentSize - bestSize))
                {
                    pick = c;
                }
            }

            others.Add(pick);
        }

        Run(others);
        return Best();
    }

    private static long TotalCompressedSize(byte[] content, IReadOnlyList<ReadOnlyMemory<byte>> test, int level, ZstdTrainingScratch scratch, CancellationToken cancellationToken)
    {
        ZstdCompressionDictionary dict = ZstdCompressionDictionary.FromContent(content);
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = Math.Min(level, ZstdCompressionParameters.MaxLevel), Dictionary = dict, WriteContentSize = true });
        long total = 0;
        for (int i = 0; i < test.Count; i++)
        {
            if ((i & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> sample = test[i].Span;
            ZstdTrainingScratch.Fit(ref scratch.Output, ZstdEncoder.GetMaxCompressedLength(sample.Length));
            total += encoder.CompressFrame(sample, scratch.Output);
        }

        return total;
    }
}
