using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Lz4;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Encoder interop against the lz4 command line tool over public corpora: frames and blocks written by our encoders must
/// decode with the tool (and with our decoder) to the original bytes, at every level and with the options that change the
/// stream. Like the decoder interop tests, these run only when ONEARCHIVE_INTEROP=1 (they need the tool and the corpora and
/// take minutes) and use the same environment variables; with it set, a missing tool or corpus fails the run.
/// ONEARCHIVE_INTEROP_LOG gets a TSV of every case, with our compressed size.
/// </summary>
[Trait("Category", "Interop")]
public class Lz4EncoderInteropTests
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
        string dir = Path.Combine(root, "lz4-enc-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("LZ4", Sh($"{Tool} --version").Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string Sh(string command)
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", command]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Scratch };
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

    private sealed record Case(string File, string Name, Lz4CompressionOptions Options, bool Dictionary = false);

    private static readonly ConcurrentDictionary<string, string> s_hashes = new();

    private static string FileHash(string path) => s_hashes.GetOrAdd(path, p =>
    {
        using FileStream f = File.OpenRead(p);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    });

    private static string ReferenceDictionary()
    {
        string path = Path.Combine(Scratch, "dictionary.bin");
        if (!File.Exists(path))
        {
            // The last 32 KiB of a text file: any bytes do for a raw dictionary.
            byte[] bytes = File.ReadAllBytes(RequireCorpusFile("dickens"));
            File.WriteAllBytes(path, bytes.AsSpan(bytes.Length - 32768 - 100_000, 32768).ToArray());
        }

        return path;
    }

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
                string expected = FileHash(input);
                byte[] data = File.ReadAllBytes(input);
                Lz4CompressionOptions options = c.Dictionary
                    ? new Lz4CompressionOptions
                    {
                        Level = c.Options.Level,
                        BlockSize = c.Options.BlockSize,
                        BlocksIndependent = c.Options.BlocksIndependent,
                        BlockChecksum = c.Options.BlockChecksum,
                        ContentChecksum = c.Options.ContentChecksum,
                        IncludeContentSize = c.Options.IncludeContentSize,
                        Dictionary = new Lz4Dictionary(File.ReadAllBytes(ReferenceDictionary())),
                    }
                    : c.Options;
                var sw = Stopwatch.StartNew();
                byte[] frame = Lz4.Lz4.Compress(data, options);
                long ms = sw.ElapsedMilliseconds;
                File.WriteAllBytes(compressed, frame);
                data = [];

                // The reference tool decodes it: the bytes through a pipe, so nothing is held twice.
                string dict = c.Dictionary ? $" -D '{ReferenceDictionary()}'" : string.Empty;
                string toolHash = Sh($"{Tool} -d -q -c{dict} '{compressed}' | sha256sum").Split(' ')[0];
                if (toolHash != expected)
                {
                    failures.Add($"{group}/{c.File}/{c.Name}: lz4 decodes our frame to different bytes");
                }

                Sh($"{Tool} -t -q{dict} '{compressed}'");

                // And so does our decoder, one-shot.
                byte[] back = Lz4.Lz4.Decompress(frame, new Lz4DecompressionOptions { MaxOutputSize = long.MaxValue, Dictionary = options.Dictionary });
                if (Convert.ToHexStringLower(SHA256.HashData(back)) != expected)
                {
                    failures.Add($"{group}/{c.File}/{c.Name}: our decoder differs");
                }

                log.Add(string.Join('\t', group, c.File, c.Name, c.Options.Level, new FileInfo(input).Length, frame.Length, ms, "ok"));
            }
            catch (Exception e)
            {
                failures.Add($"{group}/{c.File}/{c.Name}: {e.GetType().Name}: {e.Message}");
                log.Add(string.Join('\t', group, c.File, c.Name, c.Options.Level, 0, 0, 0, "FAIL"));
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

    // Every level, with the defaults the lz4 tool has (independent 4 MiB blocks, content checksum), then the options that
    // change the stream at a few levels.
    private static IEnumerable<Case> AllCases(string file)
    {
        foreach (int level in new[] { -65536, -100, -10, -5, -3, -2, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
        {
            yield return new Case(file, "l" + level, new Lz4CompressionOptions { Level = level });
        }

        foreach (int level in new[] { 1, 4, 9, 12 })
        {
            yield return new Case(file, $"linked-l{level}", new Lz4CompressionOptions { Level = level, BlocksIndependent = false });
            yield return new Case(file, $"64k-linked-checksums-l{level}", new Lz4CompressionOptions { Level = level, BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB, BlockChecksum = true });
            yield return new Case(file, $"256k-nocrc-l{level}", new Lz4CompressionOptions { Level = level, BlockSize = Lz4BlockSize.Size256KiB, ContentChecksum = false, IncludeContentSize = false });
            yield return new Case(file, $"1m-l{level}", new Lz4CompressionOptions { Level = level, BlockSize = Lz4BlockSize.Size1MiB });
            yield return new Case(file, $"dict-l{level}", new Lz4CompressionOptions { Level = level }, Dictionary: true);
            yield return new Case(file, $"dict-linked-64k-l{level}", new Lz4CompressionOptions { Level = level, BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB }, Dictionary: true);
        }
    }

    [Fact]
    public void ToolVersionIsRecorded()
    {
        RequireEnabled();
        TestContext.Current.SendDiagnosticMessage("lz4 tool: " + Sh($"{Tool} --version").Trim());
    }

    [Fact]
    public void SilesiaTarWithEveryOption()
    {
        RequireEnabled();
        RunCases([.. AllCases(SilesiaTar())], "silesia.tar");
    }

    [Fact]
    public void Enwik8WithEveryOption() => RunCases([.. AllCases("enwik8")], "enwik8");

    [Fact]
    public void EverySilesiaFile()
    {
        var cases = new List<Case>();
        foreach (string file in SilesiaFiles)
        {
            foreach (int level in new[] { -3, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })
            {
                cases.Add(new Case(file, "l" + level, new Lz4CompressionOptions { Level = level }));
            }

            cases.Add(new Case(file, "linked-64k-l5", new Lz4CompressionOptions { Level = 5, BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB, BlockChecksum = true }));
            cases.Add(new Case(file, "dict-l1", new Lz4CompressionOptions { Level = 1 }, Dictionary: true));
        }

        RunCases(cases, "silesia");
    }

    [Fact]
    public void CanterburyCorpora()
    {
        RequireEnabled();
        var cases = new List<Case>();
        foreach (string file in new[] { "alice29.txt", "asyoulik.txt", "cp.html", "fields.c", "grammar.lsp", "kennedy.xls", "lcet10.txt", "plrabn12.txt", "ptt5", "sum", "xargs.1", "E.coli", "bible.txt", "world192.txt", "a.txt", "aaa.txt", "alphabet.txt", "random.txt" })
        {
            if (FindCorpusFile(file) is null)
            {
                continue;
            }

            foreach (int level in new[] { -3, 1, 2, 3, 6, 9, 10, 12 })
            {
                cases.Add(new Case(file, "l" + level, new Lz4CompressionOptions { Level = level }));
            }

            cases.Add(new Case(file, "linked-64k-l9", new Lz4CompressionOptions { Level = 9, BlocksIndependent = false, BlockSize = Lz4BlockSize.Size64KiB }));
            cases.Add(new Case(file, "dict-l5", new Lz4CompressionOptions { Level = 5 }, Dictionary: true));
        }

        Assert.NotEmpty(cases);
        RunCases(cases, "canterbury");
    }
}
