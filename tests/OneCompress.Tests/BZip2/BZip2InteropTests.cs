using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;
using RecursiveAI.OneCompress.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop: streams written by the bzip2 tool (run as a black box) over public corpora decode to the original bytes.
/// These tests run only when ONEARCHIVE_INTEROP=1 (they need the tool and the corpora and take minutes); with it set, a missing
/// tool or corpus fails the run. Settings, all environment variables: ONEARCHIVE_TOOLS (directory holding <c>bzip2</c>, default
/// ~/onearchive/tools/bin), ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_INTEROP_TMP (scratch directory),
/// ONEARCHIVE_INTEROP_JOBS (parallel cases, default 4), ONEARCHIVE_BZIP2_LEVELS (a comma list that narrows the levels), ONEARCHIVE_BZIP2_TOOLS (comma list of extra compressors to try on the
/// small inputs, for example <c>pbzip2,lbzip2</c>; each is looked up in ONEARCHIVE_TOOLS and then on the PATH).
/// </summary>
[Trait("Category", "Interop")]
public class BZip2InteropTests
{
    private static readonly string[] SilesiaFiles =
        ["dickens", "mozilla", "mr", "nci", "ooffice", "osdb", "reymont", "samba", "sao", "webster", "x-ray", "xml"];

    private static readonly string[] CanterburyFiles =
        ["alice29.txt", "asyoulik.txt", "cp.html", "fields.c", "grammar.lsp", "kennedy.xls", "lcet10.txt", "plrabn12.txt", "ptt5", "sum", "xargs.1",
         "E.coli", "bible.txt", "world192.txt", "aaa.txt", "alphabet.txt", "random.txt"];

    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "4", CultureInfo.InvariantCulture);

    private static string Scratch { get; } = MakeScratch();

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "bzip2-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static string Bzip2 => FindTool("bzip2");

    private static string FindTool(string name)
    {
        string pinned = Path.Combine(Tools, name);
        if (File.Exists(pinned))
        {
            return pinned;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"the tool '{name}' is not in {Tools} or on the PATH");
    }

    private static string? FindCorpusFile(string name)
    {
        foreach (string path in new[]
        {
            Path.Combine(Corpora, name), Path.Combine(Corpora, "silesia", name), Path.Combine(Corpora, "enwik8", name),
            Path.Combine(Corpora, "canterbury", "cantrbry", name), Path.Combine(Corpora, "canterbury", "large", name),
            Path.Combine(Corpora, "canterbury", "artificl", name), Path.Combine(Corpora, "cantrbry", name),
            Path.Combine(Corpora, "large", name), Path.Combine(Corpora, "artificl", name), Path.Combine(Corpora, "x", name),
            Path.Combine(Corpora, "x", "cantrbry", name), Path.Combine(Corpora, "x", "large", name), Path.Combine(Corpora, "x", "artificl", name),
        })
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static byte[] RunCompressor(string tool, string[] args, string inputPath)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardInput = false, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(inputPath);
        using Process p = Process.Start(psi)!;
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} {string.Join(' ', args)} {inputPath} exited with {p.ExitCode}");
        return ms.ToArray();
    }

    private static void CheckDecodes(string label, byte[] original, byte[] compressed, ConcurrentBag<string> failures)
    {
        try
        {
            byte[] decoded = BZ.Decompress(compressed, new BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });
            if (!decoded.AsSpan().SequenceEqual(original))
            {
                failures.Add(label + ": output differs");
                return;
            }

            // The same through the stream, with awkward read sizes.
            using var stream = new BZip2Stream(new BZip2TestData.SlowStream(compressed, 30011), new BZip2DecompressionOptions { MaxOutputSize = long.MaxValue });
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[100003];
            int n;
            long total = 0;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.AppendData(buffer, 0, n);
                total += n;
            }

            if (total != original.Length || !sha.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(original)))
            {
                failures.Add(label + ": stream output differs");
            }
        }
        catch (Exception e)
        {
            failures.Add($"{label}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static void RunAll(IEnumerable<string> files, int[] levels, string[] tools)
    {
        RequireEnabled();
        string? only = Environment.GetEnvironmentVariable("ONEARCHIVE_BZIP2_LEVELS"); // for example "1,9" to narrow a run
        if (!string.IsNullOrEmpty(only))
        {
            levels = only.Split(',').Select(static l => int.Parse(l, CultureInfo.InvariantCulture)).Intersect(levels).ToArray();
        }

        var failures = new ConcurrentBag<string>();
        var cases = new List<(string File, int Level, string Tool)>();
        foreach (string file in files)
        {
            foreach (string tool in tools)
            {
                foreach (int level in levels)
                {
                    cases.Add((file, level, tool));
                }
            }
        }

        string? log = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG");
        var lines = new ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Jobs }, c =>
        {
            string path = FindCorpusFile(c.File) ?? throw new FileNotFoundException($"corpus file '{c.File}' not found under {Corpora}");
            byte[] original = File.ReadAllBytes(path);
            byte[] compressed = RunCompressor(FindTool(c.Tool), [$"-{c.Level}"], path);
            var sw = Stopwatch.StartNew();
            CheckDecodes($"{c.Tool} -{c.Level} {c.File}", original, compressed, failures);
            lines.Add(string.Join('\t', c.Tool, c.Level, c.File, original.Length, compressed.Length, sw.ElapsedMilliseconds));
        });
        if (log is not null)
        {
            File.AppendAllLines(log, lines.OrderBy(static l => l, StringComparer.Ordinal));
        }

        Assert.True(failures.IsEmpty, string.Join('\n', failures.OrderBy(static f => f, StringComparer.Ordinal)));
    }

    private static string[] ExtraTools => (Environment.GetEnvironmentVariable("ONEARCHIVE_BZIP2_TOOLS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void CanterburyAtEveryLevel() => RunAll(CanterburyFiles, [1, 2, 3, 4, 5, 6, 7, 8, 9], ["bzip2"]);

    [Fact]
    public void SilesiaAtEveryLevel() => RunAll(SilesiaFiles, [1, 2, 3, 4, 5, 6, 7, 8, 9], ["bzip2"]);

    [Fact]
    public void Enwik8AtEveryLevel() => RunAll(["enwik8"], [1, 2, 3, 4, 5, 6, 7, 8, 9], ["bzip2"]);

    [Fact]
    public void ParallelCompressorsOnSmallInputs()
    {
        RequireEnabled();
        string[] tools = ExtraTools;
        Assert.SkipWhen(tools.Length == 0, "set ONEARCHIVE_BZIP2_TOOLS=pbzip2,lbzip2 to try parallel compressors");
        RunAll(["alice29.txt", "kennedy.xls", "E.coli", "bible.txt", "world192.txt", "dickens", "mr", "xml"], [1, 5, 9], tools);
    }

    private static readonly string[][] s_sevenZipFlags = [["-mx=1"], ["-mx=5"], ["-mx=9", "-mpass=7"], ["-mx=9", "-mmt=4"]];
    private static readonly string[] s_sevenZipBase = ["a", "-tbzip2", "-bd"];

    // 7-Zip has its own bzip2 encoder (different block sorting, several passes over the table choice), a second writer to test against.
    [Fact]
    public void SevenZipOutput()
    {
        RequireEnabled();
        string tool = Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? FindToolAny("7zz", "7z");
        var failures = new ConcurrentBag<string>();
        foreach (string file in new[] { "alice29.txt", "kennedy.xls", "E.coli", "world192.txt", "dickens", "mr", "xml", "sao" })
        {
            string path = FindCorpusFile(file) ?? throw new FileNotFoundException(file);
            byte[] original = File.ReadAllBytes(path);
            foreach (string[] flags in s_sevenZipFlags)
            {
                string archive = Path.Combine(Scratch, "7z-" + file + ".bz2");
                File.Delete(archive);
                var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string a in s_sevenZipBase.Concat(flags).Concat([archive, path]))
                {
                    psi.ArgumentList.Add(a);
                }

                using Process p = Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
                CheckDecodes($"7z {string.Join(' ', flags)} {file}", original, File.ReadAllBytes(archive), failures);
                File.Delete(archive);
            }
        }

        Assert.True(failures.IsEmpty, string.Join('\n', failures));
    }

    private static string FindToolAny(params string[] names)
    {
        foreach (string n in names)
        {
            try
            {
                return FindTool(n);
            }
            catch (FileNotFoundException)
            {
            }
        }

        throw new FileNotFoundException("none of " + string.Join(", ", names) + " found");
    }

    [Fact]
    public void CasesThatExerciseTheRunStages()
    {
        RequireEnabled();
        var failures = new ConcurrentBag<string>();
        string dir = Scratch;
        var inputs = new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["onebyte"] = [0x5A],
            ["zeros-4"] = new byte[4],
            ["zeros-5"] = new byte[5],
            ["zeros-255"] = new byte[255],
            ["zeros-259"] = new byte[259],
            ["zeros-260"] = new byte[260],
            ["zeros-1m"] = new byte[1_000_000],
            ["zeros-50m"] = new byte[50_000_000],
            ["ff-300k"] = Enumerable.Repeat((byte)0xFF, 300_000).ToArray(),
            ["runs"] = Enumerable.Range(0, 3000).SelectMany(static i => Enumerable.Repeat((byte)(i % 7), (i % 300) + 1)).ToArray(),
            ["all256x1000"] = Enumerable.Range(0, 256_000).Select(static i => (byte)i).ToArray(),
            ["random-2m"] = BZip2TestData.RandomBytes(2_000_000),
        };
        foreach ((string name, byte[] data) in inputs)
        {
            string path = Path.Combine(dir, name + ".raw");
            File.WriteAllBytes(path, data);
            foreach (int level in new[] { 1, 9 })
            {
                CheckDecodes($"{name} -{level}", data, RunCompressor(Bzip2, [$"-{level}"], path), failures);
            }

            File.Delete(path);
        }

        // Concatenated streams, as cat makes them.
        byte[] a = inputs["runs"];
        byte[] b = inputs["all256x1000"];
        string pa = Path.Combine(dir, "a.raw");
        string pb = Path.Combine(dir, "b.raw");
        File.WriteAllBytes(pa, a);
        File.WriteAllBytes(pb, b);
        byte[] joined = [.. RunCompressor(Bzip2, ["-3"], pa), .. RunCompressor(Bzip2, ["-9"], pb), .. RunCompressor(Bzip2, ["-1"], pa)];
        CheckDecodes("cat of three streams", [.. a, .. b, .. a], joined, failures);
        Assert.True(failures.IsEmpty, string.Join('\n', failures));
    }

    [Fact]
    public void RandomizedBlocksOverAFullCycleOfTheSchedule()
    {
        RequireEnabled();

        // The randomisation schedule was measured from the tool with this very construction (tools/bzip2); here the decoder is
        // compared with the tool over 899000 bytes, which cover more than three cycles of the 512 gaps.
        var data = new List<byte>();
        var r = new Random(5);
        int prev = -1;
        while (data.Count < 899_000)
        {
            int b = r.Next(256);
            if (prev >= 0 && (b >> 1) == (prev >> 1))
            {
                continue;
            }

            data.Add((byte)b);
            prev = b;
        }

        string path = Path.Combine(Scratch, "rnd.raw");
        File.WriteAllBytes(path, data.ToArray());
        byte[] packed = RunCompressor(Bzip2, ["-9"], path);
        packed[14] |= 0x80; // the randomised flag of the first block
        string flagged = Path.Combine(Scratch, "rnd-flagged.bz2");
        File.WriteAllBytes(flagged, packed);

        // The tool hands over the block's bytes and then fails on the CRC; ours does the same with checks off.
        var psi = new ProcessStartInfo(Bzip2) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-dc");
        psi.ArgumentList.Add(flagged);
        using Process p = Process.Start(psi)!;
        var expected = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(expected);
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.NotEqual(0, p.ExitCode);
        Assert.True(expected.Length > 800_000);

        byte[] ours = BZ.Decompress(packed, new BZip2DecompressionOptions { VerifyChecksum = false });
        Assert.Equal(data.Count, ours.Length);
        Assert.True(ours.AsSpan(0, (int)expected.Length).SequenceEqual(expected.ToArray()), "randomised output differs from the tool's");
        Assert.Throws<CompressionFormatException>(() => BZ.Decompress(packed));

        // Randomised runs: the flipped bits interact with the first run-length stage, which runs after them.
        byte[] runny = Enumerable.Range(0, 400_000).Select(static i => (byte)((i / 700) % 5 + (i % 9 == 0 ? 1 : 0))).ToArray();
        string rpath = Path.Combine(Scratch, "runny.raw");
        File.WriteAllBytes(rpath, runny);
        byte[] rpacked = RunCompressor(Bzip2, ["-9"], rpath);
        rpacked[14] |= 0x80;
        string rflagged = Path.Combine(Scratch, "runny-flagged.bz2");
        File.WriteAllBytes(rflagged, rpacked);
        psi = new ProcessStartInfo(Bzip2) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-dc");
        psi.ArgumentList.Add(rflagged);
        using Process q = Process.Start(psi)!;
        var rexpected = new MemoryStream();
        q.StandardOutput.BaseStream.CopyTo(rexpected);
        q.StandardError.ReadToEnd();
        q.WaitForExit();
        byte[] rours = BZ.Decompress(rpacked, new BZip2DecompressionOptions { VerifyChecksum = false });
        Assert.True(rexpected.Length > 0 && rours.AsSpan(0, (int)rexpected.Length).SequenceEqual(rexpected.ToArray()), "randomised output with runs differs from the tool's");
    }

    [Fact]
    public void ToolRejectsWhatWeReject()
    {
        RequireEnabled();

        // Streams the decoder calls corrupt are corrupt for the tool, too (bit flips of a real file; the tool's exit code is 2).
        BZip2Vector v = BZip2TestData.Vector("level1-small-l1");
        string path = Path.Combine(Scratch, "flip.bz2");
        int disagreements = 0;
        for (int i = 0; i < v.Compressed.Length; i++)
        {
            byte[] copy = v.Compressed.ToArray();
            copy[i] ^= 0x04;
            File.WriteAllBytes(path, copy);
            var psi = new ProcessStartInfo(Bzip2) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            bool toolOk = p.ExitCode == 0;
            bool oursOk;
            try
            {
                BZ.Decompress(copy);
                oursOk = true;
            }
            catch (CompressionException)
            {
                oursOk = false;
            }

            disagreements += toolOk != oursOk ? 1 : 0;
        }

        Assert.Equal(0, disagreements);
    }
}
