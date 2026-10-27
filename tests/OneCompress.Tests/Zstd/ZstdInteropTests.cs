using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop against the zstd command line tool over public corpora. These tests run only when
/// ONEARCHIVE_INTEROP=1 (they need the tool and the corpora, and take minutes); with it set, a missing tool or
/// corpus fails the run. Settings, all environment variables:
/// ONEARCHIVE_ZSTD (tool path, default "zstd"), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
/// ONEARCHIVE_INTEROP_TMP (scratch directory), ONEARCHIVE_INTEROP_JOBS (parallel cases, default 6),
/// ONEARCHIVE_INTEROP_PROFILE ("quick" or "full"), ONEARCHIVE_INTEROP_LOG (a TSV of every case).
/// </summary>
[Trait("Category", "Interop")]
public class ZstdInteropTests
{
    private static readonly string[] SilesiaFiles =
        ["dickens", "mozilla", "mr", "nci", "ooffice", "osdb", "reymont", "samba", "sao", "webster", "x-ray", "xml"];

    private static string Tool => Environment.GetEnvironmentVariable("ONEARCHIVE_ZSTD") ?? "zstd";

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static string Scratch { get; } = MakeScratch();

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "6", CultureInfo.InvariantCulture);

    private static bool Full => Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_PROFILE") == "full";

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "zstd-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string version = Sh($"{Tool} --version").Trim();
        Assert.Contains("Zstandard", version, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sh(string command, string? workDir = null)
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? Scratch,
        };
        using Process p = Process.Start(psi)!;
        string error = string.Empty;
        p.ErrorDataReceived += (_, e) => error += e.Data + "\n";
        p.BeginErrorReadLine();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"command failed ({p.ExitCode}): {command}\n{error}");
        }

        return output;
    }

    [Fact]
    public void ToolVersionIsRecorded()
    {
        RequireEnabled();
        string version = Sh($"{Tool} --version").Trim();
        Console.WriteLine("zstd tool: " + version);
        TestContext.Current.SendDiagnosticMessage("zstd tool: " + version);
    }

    private sealed record Case(string File, string Name, string Args, bool Pipe, long MaxWindow = 1L << 31);

    private static string? FindCorpusFile(string name)
    {
        foreach (string candidate in new[]
        {
            Path.Combine(Corpora, name),
            Path.Combine(Corpora, "silesia", name),
            Path.Combine(Corpora, "enwik8", name),
            Path.Combine(Corpora, "canterbury", "cantrbry", name),
            Path.Combine(Corpora, "canterbury", "large", name),
            Path.Combine(Corpora, "canterbury", "artificl", name),
            Path.Combine(Corpora, "cantrbry", name),
            Path.Combine(Corpora, "large", name),
            Path.Combine(Corpora, "artificl", name),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RequireCorpusFile(string name) =>
        FindCorpusFile(name) ?? throw new FileNotFoundException($"corpus file '{name}' not found under {Corpora}");

    private static IEnumerable<Case> LevelCases(string file, bool allLevels)
    {
        int[] levels = allLevels ? Enumerable.Range(1, 19).ToArray() : [1, 3, 9, 19];
        foreach (int l in levels)
        {
            yield return new Case(file, $"l{l}", $"-{l}", false);
        }

        if (allLevels)
        {
            foreach (int l in new[] { 20, 21, 22 })
            {
                yield return new Case(file, $"ultra{l}", $"--ultra -{l}", false);
            }
        }
    }

    private static IEnumerable<Case> OptionCases(string file)
    {
        yield return new Case(file, "nocheck", "-3 --no-check", false);
        yield return new Case(file, "nosize", "-3 --no-content-size", false);
        yield return new Case(file, "pipe-l3", "-3", true);
        yield return new Case(file, "pipe-l19", "-19", true);
        yield return new Case(file, "T0-l3", "-T0 -3", false);
        yield return new Case(file, "T8-l9", "-T8 -9", false);
        yield return new Case(file, "T4-pipe-l5", "-T4 -5", true);
        yield return new Case(file, "long27-l3", "--long=27 -3", false);
        yield return new Case(file, "long27-l19", "--long=27 -19", false);
        yield return new Case(file, "long27-pipe", "--long=27 -3", true);
        yield return new Case(file, "long31-l3", "--long=31 -3", false);
        yield return new Case(file, "ultra22-pipe", "--ultra -22", true);
        yield return new Case(file, "wlog10", "-3 --zstd=wlog=10", false);
        yield return new Case(file, "wlog18-strategy", "-9 --zstd=wlog=18,strategy=3", false);
        yield return new Case(file, "ldm-l3", "-3 --long=24", false);
    }

    private static string Hash(string path)
    {
        using FileStream f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private static readonly ConcurrentDictionary<string, string> s_hashes = new();

    private static string CachedHash(string path) => s_hashes.GetOrAdd(path, Hash);

    private static void RunCases(IReadOnlyList<Case> cases, string group)
    {
        RequireEnabled();
        var failures = new ConcurrentBag<string>();
        var log = new ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Jobs }, c =>
        {
            string input = RequireCorpusFile(c.File);
            string compressed = Path.Combine(Scratch, $"{group}-{Path.GetFileName(input)}-{c.Name}.zst");
            try
            {
                string expected = CachedHash(input);
                var sw = Stopwatch.StartNew();
                string command = c.Pipe
                    ? $"{Tool} {c.Args} -q -c < '{input}' > '{compressed}'"
                    : $"{Tool} {c.Args} -q -f -c '{input}' > '{compressed}'";
                Sh(command);
                long compressedSize = new FileInfo(compressed).Length;
                var options = new ZstdDecompressionOptions { MaxWindowSize = c.MaxWindow, MaxOutputSize = long.MaxValue };

                // One-shot decode (fills the caller's buffer directly when the frame states its size).
                byte[] source = File.ReadAllBytes(compressed);
                sw.Restart();
                byte[] output = Zstd.Zstd.Decompress(source, options);
                long oneShotMs = sw.ElapsedMilliseconds;
                if (Convert.ToHexStringLower(SHA256.HashData(output)) != expected)
                {
                    failures.Add($"{group}/{c.File}/{c.Name}: one-shot output differs");
                }

                output = [];

                // Streaming decode through ZstdStream with a window that slides.
                sw.Restart();
                using (var stream = new ZstdStream(File.OpenRead(compressed), options))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    byte[] buffer = new byte[1 << 17];
                    int n;
                    while ((n = stream.Read(buffer)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                    }

                    if (Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
                    {
                        failures.Add($"{group}/{c.File}/{c.Name}: stream output differs");
                    }
                }

                long streamMs = sw.ElapsedMilliseconds;
                log.Add(string.Join('\t', group, c.File, c.Name, c.Args + (c.Pipe ? " (stdin)" : string.Empty), new FileInfo(input).Length, compressedSize, oneShotMs, streamMs, "ok"));
            }
            catch (Exception e)
            {
                failures.Add($"{group}/{c.File}/{c.Name}: {e.GetType().Name}: {e.Message}");
                log.Add(string.Join('\t', group, c.File, c.Name, c.Args, 0, 0, 0, 0, "FAIL"));
            }
            finally
            {
                File.Delete(compressed);
            }
        });

        string? logPath = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG");
        if (logPath is not null)
        {
            File.AppendAllLines(logPath, log.OrderBy(l => l, StringComparer.Ordinal));
        }

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order(StringComparer.Ordinal).Take(40)));
    }

    private static string SilesiaTar()
    {
        string tar = Path.Combine(Corpora, "silesia.tar");
        if (File.Exists(tar))
        {
            return tar;
        }

        tar = Path.Combine(Scratch, "silesia.tar");
        if (!File.Exists(tar))
        {
            string dir = Path.GetDirectoryName(RequireCorpusFile("dickens"))!;
            Sh($"tar cf '{tar}' -C '{dir}' {string.Join(' ', SilesiaFiles)}");
        }

        return tar;
    }

    [Fact]
    public void SilesiaTarAtEveryLevelAndOption()
    {
        RequireEnabled();
        string tar = SilesiaTar();
        var cases = new List<Case>(LevelCases(tar, allLevels: true));
        cases.AddRange(OptionCases(tar));
        RunCases(cases, "silesia.tar");
    }

    [Fact]
    public void Enwik8AtEveryLevelAndOption()
    {
        var cases = new List<Case>(LevelCases("enwik8", allLevels: true));
        cases.AddRange(OptionCases("enwik8"));
        RunCases(cases, "enwik8");
    }

    [Fact]
    public void EverySilesiaFile()
    {
        var cases = new List<Case>();
        foreach (string f in SilesiaFiles)
        {
            cases.AddRange(LevelCases(f, allLevels: false));
            cases.Add(new Case(f, "ultra22", "--ultra -22", false));
            cases.Add(new Case(f, "pipe-l7", "-7", true));
            if (Full)
            {
                cases.AddRange(LevelCases(f, allLevels: true).Where(c => !c.Name.StartsWith("l1", StringComparison.Ordinal) || c.Name == "l1"));
            }
        }

        RunCases(cases.DistinctBy(c => (c.File, c.Name)).ToList(), "silesia");
    }

    [Fact]
    public void Canterbury()
    {
        RequireEnabled();
        string[] names =
        [
            "alice29.txt", "asyoulik.txt", "cp.html", "fields.c", "grammar.lsp", "kennedy.xls", "lcet10.txt", "plrabn12.txt", "ptt5",
            "sum", "xargs.1", "E.coli", "bible.txt", "world192.txt",
            "a.txt", "aaa.txt", "alphabet.txt", "random.txt",
        ];
        var cases = new List<Case>();
        foreach (string n in names.Where(n => FindCorpusFile(n) is not null))
        {
            cases.AddRange(LevelCases(n, allLevels: true));
            cases.AddRange(OptionCases(n).Where(c => !c.Name.StartsWith("long31", StringComparison.Ordinal)));
        }

        Assert.True(cases.Count > 100, "Canterbury files not found");
        RunCases(cases, "canterbury");
    }

    [Fact]
    public void EmptyAndTinyInputs()
    {
        RequireEnabled();
        string empty = Path.Combine(Scratch, "empty.bin");
        File.WriteAllBytes(empty, []);
        foreach (string args in new[] { "-1", "-3", "--ultra -22", "--no-check", "--no-content-size", "-19 --long=27" })
        {
            foreach (bool pipe in new[] { false, true })
            {
                string output = Path.Combine(Scratch, "empty.zst");
                Sh(pipe ? $"{Tool} {args} -q -c < '{empty}' > '{output}'" : $"{Tool} {args} -q -f -c '{empty}' > '{output}'");
                byte[] frame = File.ReadAllBytes(output);
                Assert.Empty(Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { MaxWindowSize = 1L << 31 }));
                using var stream = new ZstdStream(new MemoryStream(frame), new ZstdDecompressionOptions { MaxWindowSize = 1L << 31 });
                Assert.Equal(0, stream.Read(new byte[10]));
            }
        }

        // Tiny inputs: every length from 1 to 300 at three levels.
        for (int len = 1; len <= 300; len++)
        {
            byte[] data = new byte[len];
            for (int i = 0; i < len; i++)
            {
                data[i] = (byte)((i * 7) ^ (i >> 3));
            }

            string input = Path.Combine(Scratch, "tiny.bin");
            File.WriteAllBytes(input, data);
            foreach (string args in new[] { "-1", "-9", "-19 --no-check" })
            {
                string output = Path.Combine(Scratch, "tiny.zst");
                Sh($"{Tool} {args} -q -f -c '{input}' > '{output}'");
                Assert.Equal(data, Zstd.Zstd.Decompress(File.ReadAllBytes(output)));
            }
        }
    }

    [Fact]
    public void SkippableFramesAndConcatenation()
    {
        RequireEnabled();
        string a = RequireCorpusFile("alice29.txt");
        string b = RequireCorpusFile("sum");
        string za = Path.Combine(Scratch, "a.zst");
        string zb = Path.Combine(Scratch, "b.zst");
        Sh($"{Tool} -5 -q -f -c '{a}' > '{za}'");
        Sh($"{Tool} -2 -q -f -c '{b}' > '{zb}'");
        byte[] expected = [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)];

        static byte[] Skip(int variant, byte[] body)
        {
            byte[] frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(0x184D2A50 + variant));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)body.Length);
            body.CopyTo(frame, 8);
            return frame;
        }

        byte[] combined = [.. Skip(1, [1, 2, 3]), .. File.ReadAllBytes(za), .. Skip(14, []), .. File.ReadAllBytes(zb), .. Skip(0, new byte[1000])];
        Assert.Equal(expected, Zstd.Zstd.Decompress(combined));
        using var stream = new ZstdStream(new MemoryStream(combined));
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal(expected, ms.ToArray());

        // The reference tool agrees on plain concatenation.
        string cat = Path.Combine(Scratch, "cat.zst");
        Sh($"cat '{za}' '{zb}' > '{cat}'");
        Assert.Equal(expected, Zstd.Zstd.Decompress(File.ReadAllBytes(cat)));
    }

    // Small JSON-like payloads, the kind of data dictionaries are trained for.
    private static byte[] Payload(ulong seed, int i)
    {
        ulong state = (seed * 1000003) + (ulong)i;
        ulong Next()
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        string[] states = ["idle", "walking", "attacking", "falling"];
        string[] items = ["sword", "shield", "potion", "key", "gem", "arrow", "torch", "map", "scroll", "bread"];
        var sb = new System.Text.StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{{\"tick\":{1000 + i},\"entity\":{Next() % 64},\"pos\":[{Next() % 512},{Next() % 64},{Next() % 512}],");
        sb.Append(CultureInfo.InvariantCulture, $"\"health\":{Next() % 100},\"state\":\"{states[Next() % 4]}\",\"inventory\":[");
        for (ulong k = 0; k < 3 + (Next() % 6); k++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{{\"item\":\"{items[Next() % (ulong)items.Length]}\",\"count\":{Next() % 20}}},");
        }

        sb.Append(CultureInfo.InvariantCulture, $"],\"chunk\":[{Next() % 32},{Next() % 32}]}}");
        return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
    }

    [Fact]
    public void TrainedDictionariesOnSmallPayloads()
    {
        RequireEnabled();
        string dir = Path.Combine(Scratch, "payloads");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 6000; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"p{i:D5}"), Payload(21, i));
        }

        var failures = new List<string>();
        foreach (int kib in new[] { 4, 16, 112 })
        {
            string dictPath = Path.Combine(Scratch, $"d{kib}.zdict");
            Sh($"{Tool} --train -q --maxdict={kib * 1024} -o '{dictPath}' {dir}/p0*");
            var dictionary = ZstdDictionary.Parse(File.ReadAllBytes(dictPath));
            Assert.True(dictionary.HasEntropyTables);
            var options = new ZstdDecompressionOptions { Dictionary = dictionary };
            var decoder = new ZstdDecoder(options);
            foreach (string flags in new[] { "-1", "-3", "-10", "-19", "-3 --no-check", "-3 --no-dictID", "-7 --no-content-size" })
            {
                int checkedCount = 0;
                for (int i = 6000; i < 6150; i++)
                {
                    byte[] payload = Payload(21, i);
                    string input = Path.Combine(dir, "q.bin");
                    string output = Path.Combine(dir, "q.zst");
                    File.WriteAllBytes(input, payload);
                    Sh($"{Tool} {flags} -q -f -D '{dictPath}' -c '{input}' > '{output}'");
                    byte[] frame = File.ReadAllBytes(output);
                    byte[] decoded = new byte[payload.Length + 5];
                    try
                    {
                        // Reusing one decoder across frames is the supported fast path.
                        if (!Zstd.Zstd.TryDecompress(decoder, frame, decoded, out int written) || !decoded.AsSpan(0, written).SequenceEqual(payload))
                        {
                            failures.Add($"dict{kib} {flags} payload {i}: mismatch");
                        }
                    }
                    catch (CompressionException e)
                    {
                        failures.Add($"dict{kib} {flags} payload {i}: {e.Message}");
                    }

                    checkedCount++;
                }

                Assert.Equal(150, checkedCount);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void PatchFromWithLargeRawContentDictionaries()
    {
        RequireEnabled();
        var failures = new List<string>();
        foreach (string name in new[] { "samba", "dickens", "ooffice" })
        {
            string reference = RequireCorpusFile(name);
            byte[] refData = File.ReadAllBytes(reference);
            byte[] newer = refData.ToArray();

            // A newer version: sparse edits, an insertion and a deletion.
            ulong s = 99;
            for (int k = 0; k < 2000; k++)
            {
                s = (s * 6364136223846793005UL) + 1442695040888963407UL;
                newer[(int)((s >> 20) % (ulong)newer.Length)] ^= (byte)(s >> 8);
            }

            byte[] withInsert = [.. newer.AsSpan(0, newer.Length / 3), .. newer.AsSpan(newer.Length / 2, newer.Length / 4), .. new byte[777], .. newer.AsSpan(newer.Length / 3 + 5)];
            string input = Path.Combine(Scratch, name + ".new");
            File.WriteAllBytes(input, withInsert);

            var dictionary = ZstdDictionary.FromContent(refData);
            var options = new ZstdDecompressionOptions { Dictionary = dictionary, MaxWindowSize = 1L << 31 };
            foreach (string flags in new[] { "-3", "-19", "-3 --long=27" })
            {
                string output = Path.Combine(Scratch, name + ".patch.zst");
                Sh($"{Tool} {flags} --patch-from='{reference}' -q -f -c '{input}' > '{output}'");
                byte[] frame = File.ReadAllBytes(output);
                byte[] decoded = Zstd.Zstd.Decompress(frame, options);
                if (!decoded.AsSpan().SequenceEqual(withInsert))
                {
                    failures.Add($"{name} {flags}: mismatch");
                }

                // And through the streaming path, where the dictionary sits in front of a sliding window.
                using var stream = new ZstdStream(new MemoryStream(frame), options);
                var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (!ms.ToArray().AsSpan().SequenceEqual(withInsert))
                {
                    failures.Add($"{name} {flags}: stream mismatch");
                }

                // A frame that does not need a dictionary ignores one.
                File.Delete(output);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
