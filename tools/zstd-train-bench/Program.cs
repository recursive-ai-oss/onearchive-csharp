using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Zstd;

// Trains a Zstandard dictionary with this library and prints how long it took.
//
//   ZstdTrainBench <out.dict> --size N (--dir DIR [--files N] | --chunks BYTES FILE...) [--algo fast|cover] [--k N --d N]
//                  [--split PERCENT] [--threads N] [--level N] [--accel N] [--f BITS] [--score freq|sqrt] [--max-bytes N] [--rebuild TOOL.dict] [--exhaustive] [--report] [--select-only N]
//
// With --dir the samples are the files of the directory in name order (the first N). With --chunks every FILE is cut into pieces of
// that many bytes. Without --k and --d the parameters are searched. Prints one line of key=value pairs.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ZstdTrainBench <out.dict> --size N (--dir DIR | --chunks BYTES FILE...) [options]");
            return 2;
        }

        string output = args[0];
        int size = 112640;
        string? dir = null;
        int files = int.MaxValue;
        int chunk = 0;
        var chunkFiles = new List<string>();
        var algorithm = ZstdTrainingAlgorithm.FastCover;
        int k = 0, d = 0, split = 75, threads = 0, level = 3, accel = 1, hashBits = 20;
        var score = ZstdSegmentScore.SquareRoot;
        string? rebuild = null;
        int selectRepeat = 0;
        bool report = false, exhaustive = false;
        long maxBytes = 64L << 20;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--size": size = Int(args[++i]); break;
                case "--dir": dir = args[++i]; break;
                case "--files": files = Int(args[++i]); break;
                case "--chunks":
                    chunk = Int(args[++i]);
                    while (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        chunkFiles.Add(args[++i]);
                    }

                    break;
                case "--algo": algorithm = args[++i] == "cover" ? ZstdTrainingAlgorithm.Cover : ZstdTrainingAlgorithm.FastCover; break;
                case "--k": k = Int(args[++i]); break;
                case "--d": d = Int(args[++i]); break;
                case "--split": split = Int(args[++i]); break;
                case "--threads": threads = Int(args[++i]); break;
                case "--level": level = Int(args[++i]); break;
                case "--accel": accel = Int(args[++i]); break;
                case "--f": hashBits = Int(args[++i]); break;
                case "--max-bytes": maxBytes = long.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--rebuild": rebuild = args[++i]; break;
                case "--report": report = true; break;
                case "--exhaustive": exhaustive = true; break;
                case "--select-only": selectRepeat = Int(args[++i]); break;
                case "--score": score = args[++i] == "freq" ? ZstdSegmentScore.Frequency : ZstdSegmentScore.SquareRoot; break;
                default: Console.Error.WriteLine("unknown option " + args[i]); return 2;
            }
        }

        var samples = new List<ReadOnlyMemory<byte>>();
        if (dir is not null)
        {
            string[] names = Directory.GetFiles(dir);
            Array.Sort(names, StringComparer.Ordinal);
            foreach (string name in names.Take(files))
            {
                samples.Add(File.ReadAllBytes(name));
            }
        }
        else
        {
            foreach (string name in chunkFiles)
            {
                byte[] all = File.ReadAllBytes(name);
                for (int p = 0; p < all.Length; p += chunk)
                {
                    samples.Add(all.AsMemory(p, Math.Min(chunk, all.Length - p)));
                }
            }
        }

        if (rebuild is not null)
        {
            byte[] rebuilt = ZstdDictionaryBuilder.Build(File.ReadAllBytes(rebuild), samples, 0);
            File.WriteAllBytes(output, rebuilt);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"samples={samples.Count} dict_bytes={rebuilt.Length} k=0 d=0 seconds=0 peak_mb=0"));
            return 0;
        }

        if (selectRepeat > 0)
        {
            // Content selection alone with fixed k and d, repeated: the steady-state cost once the code is compiled.
            var selectOptions = new ZstdDictionaryTrainingOptions { DictionarySize = size, Algorithm = algorithm, Optimize = false, SegmentSize = k == 0 ? 256 : k, DmerSize = d == 0 ? 8 : d, Acceleration = accel, HashBits = hashBits, MaxTrainingBytes = maxBytes, Score = score, MaxDegreeOfParallelism = threads };
            for (int r = 0; r < selectRepeat; r++)
            {
                var timer = Stopwatch.StartNew();
                byte[] content = ZstdCoverTrainer.SelectContent(samples, selectOptions);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"select run={r} ms={timer.Elapsed.TotalMilliseconds:F1} content_bytes={content.Length}"));
            }

            return 0;
        }

        bool search = k == 0 && d == 0;
        var options = new ZstdDictionaryTrainingOptions
        {
            DictionarySize = size,
            Algorithm = algorithm,
            Optimize = search,
            SegmentSize = k == 0 ? 256 : k,
            DmerSize = d == 0 ? 8 : d,
            TrainingPercent = split,
            ScoringLevel = level,
            MaxDegreeOfParallelism = threads,
            Acceleration = accel,
            HashBits = hashBits,
            MaxTrainingBytes = maxBytes,
            Score = score,
            ScoreCandidates = [score],
            SearchMode = exhaustive ? ZstdTrainingSearch.Exhaustive : ZstdTrainingSearch.Guided,
        };
        var watch = Stopwatch.StartNew();
        byte[] dictionary = ZstdDictionary.Train(samples, options);
        watch.Stop();
        File.WriteAllBytes(output, dictionary);
        using Process self = Process.GetCurrentProcess();
        long peak = self.PeakWorkingSet64 / (1024 * 1024);

        // The chosen parameters are looked up afterwards (a second search) only when asked for: quality.sh needs them, timing runs do not.
        ZstdCoverParameters chosen = report && search ? ZstdCoverTrainer.Optimize(samples, options) : new ZstdCoverParameters(options.SegmentSize, options.DmerSize, score);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"samples={samples.Count} dict_bytes={dictionary.Length} k={chosen.SegmentSize} d={chosen.DmerSize} score={chosen.Score} seconds={watch.Elapsed.TotalSeconds:F2} peak_mb={peak}"));
        return 0;
    }

    private static int Int(string s) => int.Parse(s, CultureInfo.InvariantCulture);
}
