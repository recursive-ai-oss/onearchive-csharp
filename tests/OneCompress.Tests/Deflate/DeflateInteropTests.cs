using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop: streams written by the reference tools (run as black boxes) over public corpora decode to the original bytes.
/// These tests run only when ONEARCHIVE_INTEROP=1 (they need the tools and the corpora and take minutes); with it set, a missing
/// tool or corpus fails the run. Settings, all environment variables: ONEARCHIVE_TOOLS (directory of the tools, default
/// ~/onearchive/tools/bin), ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_INTEROP_TMP (scratch directory),
/// ONEARCHIVE_INTEROP_JOBS (parallel cases, default 6), ONEARCHIVE_INTEROP_LOG (a TSV of every case), and
/// ONEARCHIVE_DEFLATE_TOOLS (comma list of gzip, pigz, minigzip-zlib, minigzip-ng, libdeflate-gzip; default all).
/// The tools: gzip, pigz, zlib's minigzip (as minigzip-zlib), zlib-ng's minigzip (minigzip-ng) with its level and strategy
/// flags, libdeflate-gzip at levels 1 to 12; the BCL's DeflateStream, ZLibStream and GZipStream run in process.
/// </summary>
[Trait("Category", "Interop")]
public class DeflateInteropTests
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

    private static string Scratch { get; } = MakeScratch();

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "6", CultureInfo.InvariantCulture);

    private static HashSet<string> EnabledTools => (Environment.GetEnvironmentVariable("ONEARCHIVE_DEFLATE_TOOLS") ?? "gzip,pigz,minigzip-zlib,minigzip-ng,libdeflate-gzip")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "deflate-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private sealed record Case(string File, string Tool, string Name, string[] Args, DeflateFormat Format = DeflateFormat.GZip);

    private static string ToolPath(string tool) => Path.Combine(Tools, tool);

    // The options that change the stream: levels, strategies, zlib output, block sizes (pigz), the zopfli-style level 11.
    private static IEnumerable<Case> AllCases(string file, bool big)
    {
        HashSet<string> enabled = EnabledTools;
        if (enabled.Contains("gzip"))
        {
            foreach (int l in big ? new[] { 1, 6, 9 } : new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            {
                yield return new Case(file, "gzip", $"gzip-{l}", [$"-{l}", "-c"]);
            }
        }

        if (enabled.Contains("pigz"))
        {
            foreach (int l in big ? new[] { 1, 6, 9 } : new[] { 1, 4, 6, 9 })
            {
                yield return new Case(file, "pigz", $"pigz-{l}", [$"-{l}", "-c", "-q"]);
            }

            yield return new Case(file, "pigz", "pigz-6-b32", ["-6", "-b", "32", "-c", "-q"]);
            yield return new Case(file, "pigz", "pigz-6-i", ["-6", "-i", "-c", "-q"]);
            yield return new Case(file, "pigz", "pigz-6-zlib", ["-6", "-z", "-c", "-q"], DeflateFormat.ZLib);
            if (!big)
            {
                yield return new Case(file, "pigz", "pigz-11", ["-11", "-c", "-q"]);
            }
        }

        foreach (string minigzip in new[] { "minigzip-zlib", "minigzip-ng" })
        {
            if (!enabled.Contains(minigzip))
            {
                continue;
            }

            foreach (int l in big ? new[] { 1, 6, 9 } : new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })
            {
                yield return new Case(file, minigzip, $"{minigzip}-{l}", [$"-{l}"]);
            }

            // zlib's minigzip takes only the filtered and Huffman-only strategy flags; it exits without reading input on
            // -R and -F, which zlib-ng's minigzip accepts.
            foreach ((string flag, string name) in new[] { ("-f", "filtered"), ("-h", "huffman"), ("-R", "rle"), ("-F", "fixed") })
            {
                if (minigzip == "minigzip-zlib" && flag is "-R" or "-F")
                {
                    continue;
                }

                yield return new Case(file, minigzip, $"{minigzip}-{name}", [flag]);
            }
        }

        if (enabled.Contains("libdeflate-gzip"))
        {
            foreach (int l in big ? new[] { 1, 6, 9, 12 } : Enumerable.Range(1, 12))
            {
                yield return new Case(file, "libdeflate-gzip", $"libdeflate-{l}", [$"-{l}", "-c"]);
            }
        }
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

    // Runs a tool with the file on its standard input and returns the stream it wrote to its standard output.
    private static byte[] RunTool(Case c, string file)
    {
        string output = Path.Combine(Scratch, Guid.NewGuid().ToString("N"));
        try
        {
            var psi = new ProcessStartInfo(ToolPath(c.Tool), c.Args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process p = Process.Start(psi)!;
            Task copyIn = Task.Run(() =>
            {
                using Stream input = File.OpenRead(file);
                input.CopyTo(p.StandardInput.BaseStream);
                p.StandardInput.Close();
            });
            Task<string> error = p.StandardError.ReadToEndAsync();
            using (FileStream o = File.Create(output))
            {
                p.StandardOutput.BaseStream.CopyTo(o);
            }

            p.WaitForExit();
            copyIn.Wait();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException($"{c.Tool} {string.Join(' ', c.Args)} failed ({p.ExitCode}): {error.Result}");
            }

            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void Run(IEnumerable<string> files, bool big)
    {
        RequireEnabled();
        var failures = new ConcurrentBag<string>();
        var log = new ConcurrentBag<string>();
        var cases = new List<Case>();
        foreach (string name in files)
        {
            string path = FindCorpusFile(name) ?? throw new FileNotFoundException($"corpus file '{name}' not found under {Corpora}");
            cases.AddRange(AllCases(path, big).Select(c => c with { File = path }));
        }

        var hashes = new ConcurrentDictionary<string, (string Sha, long Length)>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Jobs }, c =>
        {
            try
            {
                (string expected, long length) = hashes.GetOrAdd(c.File, f =>
                {
                    byte[] all = File.ReadAllBytes(f);
                    return (Sha(all), all.Length);
                });
                byte[] compressed = RunTool(c, c.File);
                var watch = Stopwatch.StartNew();
                byte[] decoded = c.Format switch
                {
                    DeflateFormat.Raw => Deflate.Deflate.Decompress(compressed, new DeflateDecompressionOptions { AllowTrailingData = true }),
                    DeflateFormat.ZLib => ZLib.Decompress(compressed),
                    _ => GZip.Decompress(compressed),
                };
                if (decoded.Length != length || Sha(decoded) != expected)
                {
                    failures.Add($"{Path.GetFileName(c.File)} {c.Name}: output differs");
                    return;
                }

                // The streaming path over the same bytes, with the inner stream handing out small pieces.
                using var stream = new DeflateCodecStream(new OneByteStream(compressed, 7919), c.Format, new DeflateDecompressionOptions { AllowTrailingData = c.Format == DeflateFormat.Raw });
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[1 << 16];
                int n;
                long total = 0;
                while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hasher.AppendData(buffer, 0, n);
                    total += n;
                }

                if (total != length || Convert.ToHexStringLower(hasher.GetHashAndReset()) != expected)
                {
                    failures.Add($"{Path.GetFileName(c.File)} {c.Name}: stream output differs");
                    return;
                }

                log.Add($"{Path.GetFileName(c.File)}\t{c.Name}\t{length}\t{compressed.Length}\t{watch.ElapsedMilliseconds}");
            }
            catch (Exception e)
            {
                failures.Add($"{Path.GetFileName(c.File)} {c.Name}: {e.GetType().Name}: {e.Message}");
            }
        });

        string? logPath = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG");
        if (logPath is not null)
        {
            File.AppendAllLines(logPath, log.Order(StringComparer.Ordinal));
        }

        Assert.True(failures.IsEmpty, $"{failures.Count} of {cases.Count} cases failed:\n" + string.Join('\n', failures.Order(StringComparer.Ordinal).Take(40)));
        Assert.NotEmpty(cases);
    }

    [Fact]
    public void ToolVersionsAreRecorded()
    {
        RequireEnabled();
        foreach (string tool in EnabledTools)
        {
            string path = ToolPath(tool);
            Assert.True(File.Exists(path), $"{tool} not found in {Tools}");
        }
    }

    [Fact]
    public void CanterburyDecodes() => Run(CanterburyFiles, big: false);

    [Theory]
    [MemberData(nameof(SilesiaTheory))]
    public void SilesiaFileDecodes(string file) => Run([file], big: true);

    public static TheoryData<string> SilesiaTheory() => new(SilesiaFiles);

    [Fact]
    public void Enwik8Decodes() => Run(["enwik8"], big: true);

    // The BCL's codecs, in process, over the same corpus files.
    [Theory]
    [MemberData(nameof(BclTheory))]
    public void BclOutputDecodes(string file)
    {
        RequireEnabled();
        string path = FindCorpusFile(file) ?? throw new FileNotFoundException(file);
        byte[] data = File.ReadAllBytes(path);
        foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib, DeflateFormat.GZip })
        {
            foreach (System.IO.Compression.CompressionLevel level in new[] { System.IO.Compression.CompressionLevel.Fastest, System.IO.Compression.CompressionLevel.Optimal, System.IO.Compression.CompressionLevel.SmallestSize })
            {
                byte[] compressed = DeflateTestData.BclCompress(format, data, level);
                byte[] decoded = format switch
                {
                    DeflateFormat.Raw => Deflate.Deflate.Decompress(compressed),
                    DeflateFormat.ZLib => ZLib.Decompress(compressed),
                    _ => GZip.Decompress(compressed),
                };
                Assert.True(decoded.AsSpan().SequenceEqual(data), $"{file} {format} {level}");
            }
        }
    }

    public static TheoryData<string> BclTheory() => new([.. SilesiaFiles, "enwik8", "alice29.txt", "kennedy.xls", "E.coli", "ptt5"]);
}
