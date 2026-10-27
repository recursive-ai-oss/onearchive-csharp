using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Encoder interop: frames written by this library must be decoded by the zstd tool to the original bytes. These tests run only
/// when ONEARCHIVE_INTEROP=1 (they need the tool and the corpora); with it set, a missing tool or corpus fails the run. The
/// environment variables are those of <see cref="ZstdInteropTests"/> (ONEARCHIVE_ZSTD, ONEARCHIVE_CORPORA, ONEARCHIVE_INTEROP_TMP,
/// ONEARCHIVE_INTEROP_JOBS, ONEARCHIVE_INTEROP_PROFILE).
/// </summary>
[Trait("Category", "Interop")]
public class ZstdEncoderInteropTests
{
    private static readonly string[] SilesiaFiles =
        ["dickens", "mozilla", "mr", "nci", "ooffice", "osdb", "reymont", "samba", "sao", "webster", "x-ray", "xml"];

    private static readonly string[] CanterburyFiles =
        ["alice29.txt", "asyoulik.txt", "cp.html", "fields.c", "grammar.lsp", "kennedy.xls", "lcet10.txt", "plrabn12.txt", "ptt5", "sum", "xargs.1",
         "E.coli", "bible.txt", "world192.txt"];

    private static string Tool => Environment.GetEnvironmentVariable("ONEARCHIVE_ZSTD") ?? "zstd";

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static string Scratch { get; } = MakeScratch();

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "6", CultureInfo.InvariantCulture);

    private static bool Full => Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_PROFILE") == "full";

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "zstd-encoder-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static readonly int[] AllLevels = [-7, -6, -5, -4, -3, -2, -1, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22];

    private static string Sh(string command)
    {
        var psi = new ProcessStartInfo("/bin/sh", ["-c", command])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Scratch,
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

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("Zstandard", Sh($"{Tool} --version"), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Find(string name)
    {
        foreach (string candidate in new[]
        {
            Path.Combine(Corpora, name),
            Path.Combine(Corpora, "silesia", name),
            Path.Combine(Corpora, "enwik8", name),
            Path.Combine(Corpora, "canterbury", "cantrbry", name),
            Path.Combine(Corpora, "canterbury", "large", name),
            Path.Combine(Corpora, "canterbury", "artificl", name),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Require(string name) => Find(name) ?? throw new FileNotFoundException($"corpus file '{name}' not found under {Corpora}");

    // Decodes with the tool and returns the SHA-256 of what it produced.
    private static string ToolDecodedHash(string compressedPath, string? dictionaryPath)
    {
        string dict = dictionaryPath is null ? string.Empty : $"-D '{dictionaryPath}'";
        return Sh($"{Tool} -d -q -c {dict} '{compressedPath}' | sha256sum").Split(' ')[0];
    }

    private static string Sha(string path)
    {
        using FileStream f = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(f));
    }

    private sealed record Variant(string Name, Func<int, ZstdCompressionOptions> Options, bool Stream = false, bool Skippable = false, string? Dictionary = null);

    private static IEnumerable<Variant> Variants()
    {
        yield return new Variant("plain", l => new ZstdCompressionOptions { Level = l });
        yield return new Variant("checksum", l => new ZstdCompressionOptions { Level = l, Checksum = true });
        yield return new Variant("nosize", l => new ZstdCompressionOptions { Level = l, WriteContentSize = false });
        yield return new Variant("stream-unknown", l => new ZstdCompressionOptions { Level = l, Checksum = true }, Stream: true);
        yield return new Variant("skippable", l => new ZstdCompressionOptions { Level = l }, Skippable: true);
    }

    private static void Run(IReadOnlyList<(string File, int Level, Variant Variant)> cases, string group)
    {
        RequireEnabled();
        var failures = new ConcurrentBag<string>();
        var rows = new ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Jobs }, c =>
        {
            string input = Require(c.File);
            string output = Path.Combine(Scratch, $"{group}-{c.File}-{c.Level}-{c.Variant.Name}.zst");
            try
            {
                byte[] data = File.ReadAllBytes(input);
                ZstdCompressionOptions options = c.Variant.Options(c.Level);
                byte[] frame;
                if (c.Variant.Stream)
                {
                    var sink = new MemoryStream();
                    using (var s = new ZstdStream(sink, options, leaveOpen: true))
                    {
                        for (int p = 0; p < data.Length; p += 1_000_003)
                        {
                            s.Write(data, p, Math.Min(1_000_003, data.Length - p));
                        }
                    }

                    frame = sink.ToArray();
                }
                else
                {
                    frame = Zstd.Zstd.Compress(data, options);
                }

                if (c.Variant.Skippable)
                {
                    byte[] skip = new byte[8 + 11];
                    ZstdFrameWriter.WriteSkippableFrame(skip, 3, "hello world"u8);
                    frame = [.. skip, .. frame, .. skip];
                }

                File.WriteAllBytes(output, frame);
                string expected = Sha(input);
                if (ToolDecodedHash(output, c.Variant.Dictionary) != expected)
                {
                    failures.Add($"{group}/{c.File}/l{c.Level}/{c.Variant.Name}: tool output differs");
                }

                if (!Zstd.Zstd.Decompress(frame, new ZstdDecompressionOptions { MaxOutputSize = long.MaxValue, MaxWindowSize = 1L << 31, Dictionary = c.Variant.Dictionary is null ? null : ZstdDictionary.Parse(File.ReadAllBytes(c.Variant.Dictionary)) }).AsSpan().SequenceEqual(data))
                {
                    failures.Add($"{group}/{c.File}/l{c.Level}/{c.Variant.Name}: our decoder output differs");
                }

                rows.Add($"{c.File}\t{c.Level}\t{c.Variant.Name}\t{data.Length}\t{frame.Length}");
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException)
            {
                failures.Add($"{group}/{c.File}/l{c.Level}/{c.Variant.Name}: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                File.Delete(output);
            }
        });

        string? log = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG");
        if (log is not null)
        {
            File.AppendAllLines(log, rows.Order(StringComparer.Ordinal));
        }

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryLevelOnCanterbury()
    {
        var cases = new List<(string, int, Variant)>();
        foreach (string f in CanterburyFiles)
        {
            foreach (int l in AllLevels)
            {
                cases.Add((f, l, new Variant("plain", lv => new ZstdCompressionOptions { Level = lv })));
            }
        }

        foreach (string f in new[] { "alice29.txt", "kennedy.xls", "ptt5", "E.coli" })
        {
            foreach (int l in new[] { -5, -1, 1, 3, 4, 6, 9, 13, 16, 19 })
            {
                foreach (Variant v in Variants().Skip(1))
                {
                    cases.Add((f, l, v));
                }
            }
        }

        Run(cases, "canterbury");
    }

    [Fact]
    public void EveryLevelOnSilesiaFiles()
    {
        var cases = new List<(string, int, Variant)>();
        foreach (string f in SilesiaFiles)
        {
            foreach (int l in Full ? AllLevels : [-5, -1, 1, 2, 3, 4, 5, 6, 9, 12, 15, 16, 19, 22])
            {
                cases.Add((f, l, new Variant("plain", lv => new ZstdCompressionOptions { Level = lv })));
            }
        }

        foreach (string f in new[] { "dickens", "mozilla", "x-ray" })
        {
            foreach (int l in new[] { -5, 1, 3 })
            {
                foreach (Variant v in Variants().Skip(1))
                {
                    cases.Add((f, l, v));
                }
            }
        }

        Run(cases, "silesia");
    }

    [Fact]
    public void BigFilesAtEveryLevel()
    {
        var cases = new List<(string, int, Variant)>();
        foreach (string f in new[] { "silesia.tar", "enwik8" })
        {
            foreach (int l in AllLevels)
            {
                cases.Add((f, l, new Variant("plain", lv => new ZstdCompressionOptions { Level = lv })));
            }

            foreach (Variant v in Variants().Skip(1))
            {
                cases.Add((f, 3, v));
                cases.Add((f, -3, v));
            }
        }

        Run(cases, "big");
    }

    [Fact]
    public void FormattedAndRawDictionaries()
    {
        RequireEnabled();
        string formatted = ZstdTestData.Path("dict16.zdict");
        string raw = ZstdTestData.Path("rawdict.bin");
        var cases = new List<(string, int, Variant)>();
        foreach (string f in new[] { "alice29.txt", "xargs.1", "fields.c", "kennedy.xls", "dickens" })
        {
            foreach (int l in Full ? AllLevels : [-5, -1, 1, 2, 3, 4, 5, 7, 9, 12, 15, 16, 19, 22])
            {
                cases.Add((f, l, new Variant("zdict", lv => new ZstdCompressionOptions { Level = lv, Dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(formatted)) }, Dictionary: formatted)));
                cases.Add((f, l, new Variant("zdict-checksum", lv => new ZstdCompressionOptions { Level = lv, Checksum = true, Dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(formatted)) }, Dictionary: formatted)));
                cases.Add((f, l, new Variant("raw-noid", lv => new ZstdCompressionOptions { Level = lv, Dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(raw)) }, Dictionary: raw)));
                cases.Add((f, l, new Variant("zdict-stream", lv => new ZstdCompressionOptions { Level = lv, Dictionary = ZstdCompressionDictionary.Parse(File.ReadAllBytes(formatted)) }, Stream: true, Dictionary: formatted)));
            }
        }

        Run(cases, "dict");
    }
}
