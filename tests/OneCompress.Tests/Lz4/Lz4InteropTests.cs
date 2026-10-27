using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop against the lz4 command line tool over public corpora. These tests run only when ONEARCHIVE_INTEROP=1
/// (they need the tool and the corpora, and take minutes); with it set, a missing tool or corpus fails the run. Settings,
/// all environment variables: ONEARCHIVE_LZ4 (tool path, default "lz4"), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
/// ONEARCHIVE_INTEROP_TMP (scratch directory), ONEARCHIVE_INTEROP_JOBS (parallel cases, default 6),
/// ONEARCHIVE_INTEROP_LOG (a TSV of every case).
/// </summary>
[Trait("Category", "Interop")]
public class Lz4InteropTests
{
    private static readonly string[] SilesiaFiles =
        ["dickens", "mozilla", "mr", "nci", "ooffice", "osdb", "reymont", "samba", "sao", "webster", "x-ray", "xml"];

    private static string Tool => Environment.GetEnvironmentVariable("ONEARCHIVE_LZ4") ?? "lz4";

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static string Scratch { get; } = MakeScratch();

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "6", CultureInfo.InvariantCulture);

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "lz4-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string version = Sh($"{Tool} --version").Trim();
        Assert.Contains("LZ4", version, StringComparison.OrdinalIgnoreCase);
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
        Console.WriteLine("lz4 tool: " + version);
        TestContext.Current.SendDiagnosticMessage("lz4 tool: " + version);
    }

    private sealed record Case(string File, string Name, string Args, bool Pipe, string? Dictionary = null);

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

    // The options that change the stream: levels, the fast acceleration, block sizes, linked blocks, checksums, content size,
    // stdin, and the legacy format.
    private static IEnumerable<Case> AllCases(string file, bool big)
    {
        foreach (string level in new[] { "-1", "-9", "-12" })
        {
            yield return new Case(file, "l" + level, level, false);
        }

        foreach (int fast in new[] { 1, 3, 10, 100 })
        {
            yield return new Case(file, $"fast{fast}", $"--fast={fast}", false);
        }

        foreach (int b in new[] { 4, 5, 6, 7 })
        {
            yield return new Case(file, $"B{b}", $"-B{b} -3", false);
        }

        yield return new Case(file, "B4-BD", "-B4 -BD -1", false);
        yield return new Case(file, "B5-BD-l9", "-B5 -BD -9", false);
        yield return new Case(file, "B7-BD-l12", "-B7 -BD -12", false);
        yield return new Case(file, "BX", "-BX -1", false);
        yield return new Case(file, "B4-BD-BX-l9", "-B4 -BD -BX -9", false);
        yield return new Case(file, "nocrc", "--no-frame-crc -1", false);
        yield return new Case(file, "content-size", "--content-size -1", false);
        yield return new Case(file, "pipe-l1", "-1", true);
        yield return new Case(file, "pipe-BD-l9", "-BD -9", true);
        yield return new Case(file, "pipe-B4-BX", "-B4 -BX -3", true);
        yield return new Case(file, "legacy-l1", "-l -1", false);
        if (!big)
        {
            yield return new Case(file, "legacy-l12", "-l -12", false);
        }
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
            string compressed = Path.Combine(Scratch, $"{group}-{Path.GetFileName(input)}-{c.Name}.lz4");
            try
            {
                string expected = CachedHash(input);
                var sw = Stopwatch.StartNew();
                string command = c.Pipe
                    ? $"{Tool} {c.Args} -q -c < '{input}' > '{compressed}'"
                    : $"{Tool} {c.Args} -q -f -c '{input}' > '{compressed}'";
                Sh(command);
                long compressedSize = new FileInfo(compressed).Length;
                var options = new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue };

                // One-shot decode (a buffer sized from the frame's declared size, or grown, and blocks straight into it).
                byte[] source = File.ReadAllBytes(compressed);
                sw.Restart();
                byte[] output = Lz4.Lz4.Decompress(source, options);
                long oneShotMs = sw.ElapsedMilliseconds;
                if (Convert.ToHexStringLower(SHA256.HashData(output)) != expected)
                {
                    failures.Add($"{group}/{c.File}/{c.Name}: one-shot output differs");
                }

                output = [];

                // Streaming decode through Lz4Stream, through the window.
                sw.Restart();
                using (var stream = new Lz4Stream(File.OpenRead(compressed), options))
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
    public void SilesiaTarWithEveryOption()
    {
        RequireEnabled();
        string tar = SilesiaTar();
        RunCases([.. AllCases(tar, big: true)], "silesia.tar");
    }

    [Fact]
    public void Enwik8WithEveryOption()
    {
        RunCases([.. AllCases("enwik8", big: true)], "enwik8");
    }

    [Fact]
    public void EverySilesiaFile()
    {
        var cases = new List<Case>();
        foreach (string f in SilesiaFiles)
        {
            foreach (string level in new[] { "-1", "-9", "-12" })
            {
                cases.Add(new Case(f, "l" + level, level, false));
            }

            cases.Add(new Case(f, "fast1", "--fast=1", false));
            cases.Add(new Case(f, "fast20", "--fast=20", false));
            cases.Add(new Case(f, "B4-BD-BX", "-B4 -BD -BX -1", false));
            cases.Add(new Case(f, "pipe-B5-l3", "-B5 -3", true));
            cases.Add(new Case(f, "legacy-l1", "-l -1", false));
        }

        RunCases(cases, "silesia");
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
            cases.AddRange(AllCases(n, big: false));
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
        foreach (string args in new[] { "-1", "-9", "-12", "--fast=5", "--no-frame-crc", "-BX", "-BD", "--content-size", "-l" })
        {
            foreach (bool pipe in new[] { false, true })
            {
                string output = Path.Combine(Scratch, "empty.lz4");
                Sh(pipe ? $"{Tool} {args} -q -c < '{empty}' > '{output}'" : $"{Tool} {args} -q -f -c '{empty}' > '{output}'");
                byte[] frame = File.ReadAllBytes(output);
                Assert.Empty(Lz4.Lz4.Decompress(frame));
                using var stream = new Lz4Stream(new MemoryStream(frame));
                Assert.Equal(0, stream.Read(new byte[10]));
            }
        }

        // Tiny inputs: every length from 1 to 300 at three settings.
        for (int len = 1; len <= 300; len++)
        {
            byte[] data = new byte[len];
            for (int i = 0; i < len; i++)
            {
                data[i] = (byte)((i * 7) ^ (i >> 3));
            }

            string input = Path.Combine(Scratch, "tiny.bin");
            File.WriteAllBytes(input, data);
            foreach (string args in new[] { "-1", "-12", "-BD -BX" })
            {
                string output = Path.Combine(Scratch, "tiny.lz4");
                Sh($"{Tool} {args} -q -f -c '{input}' > '{output}'");
                Assert.Equal(data, Lz4.Lz4.Decompress(File.ReadAllBytes(output)));
            }
        }
    }

    [Fact]
    public void SmallPayloadsWithDictionaries()
    {
        RequireEnabled();
        string source = RequireCorpusFile("dickens");
        byte[] corpus = File.ReadAllBytes(source);
        var random = new Random(4242);

        // Dictionaries: a few KiB, exactly 64 KiB, and larger than 64 KiB (the tool uses the last 64 KiB).
        foreach (int dictionarySize in new[] { 4096, 65536, 150_000 })
        {
            string dictionaryPath = Path.Combine(Scratch, $"dict{dictionarySize}.bin");
            int dictionaryStart = random.Next(corpus.Length - dictionarySize);
            File.WriteAllBytes(dictionaryPath, corpus.AsSpan(dictionaryStart, dictionarySize).ToArray());
            var dictionary = new Lz4Dictionary(File.ReadAllBytes(dictionaryPath));
            var options = new Lz4DecompressionOptions { Dictionary = dictionary };
            foreach (string args in new[] { "-1", "-9", "-12", "-BD -3", "-BX -1" })
            {
                for (int round = 0; round < 40; round++)
                {
                    // Half the payloads come from near the dictionary's text, so matches into it are common.
                    int size = 50 + random.Next(2000);
                    int start = round % 2 == 0 ? Math.Min(corpus.Length - size, dictionaryStart + random.Next(dictionarySize)) : random.Next(corpus.Length - size);
                    byte[] payload = corpus.AsSpan(start, size).ToArray();
                    string input = Path.Combine(Scratch, "payload.bin");
                    string output = Path.Combine(Scratch, "payload.lz4");
                    File.WriteAllBytes(input, payload);
                    Sh($"{Tool} {args} -q -f -D '{dictionaryPath}' -c '{input}' > '{output}'");
                    byte[] frame = File.ReadAllBytes(output);
                    Assert.Equal(payload, Lz4.Lz4.Decompress(frame, options));
                    Assert.Equal(payload, Lz4Splits.Decode(frame, options, 7, 33));
                }
            }
        }
    }

    [Fact]
    public void ConcatenatedFramesDecodeLikeTheTool()
    {
        RequireEnabled();
        string input = RequireCorpusFile("dickens");
        string output = Path.Combine(Scratch, "concat.lz4");
        Sh($"{Tool} -1 -q -c '{input}' > '{output}'; {Tool} -9 -BD -q -c '{input}' >> '{output}'; {Tool} -l -q -c '{input}' >> '{output}'");
        byte[] expected = File.ReadAllBytes(input);
        byte[] all = Lz4.Lz4.Decompress(File.ReadAllBytes(output));
        Assert.Equal(expected.Length * 3, all.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(all.AsSpan(i * expected.Length, expected.Length).SequenceEqual(expected));
        }

        // The tool decodes two standard frames in a row to the same bytes (it does not accept a legacy frame after them).
        string two = Path.Combine(Scratch, "concat2.lz4");
        Sh($"{Tool} -1 -q -c '{input}' > '{two}'; {Tool} -9 -BD -q -c '{input}' >> '{two}'");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Lz4.Lz4.Decompress(File.ReadAllBytes(two)))), Convert.ToHexStringLower(SHA256.HashData(RunToolDecode(two))));
    }

    private static byte[] RunToolDecode(string path)
    {
        string decoded = path + ".out";
        Sh($"{Tool} -d -q -f -c '{path}' > '{decoded}'");
        return File.ReadAllBytes(decoded);
    }
}
