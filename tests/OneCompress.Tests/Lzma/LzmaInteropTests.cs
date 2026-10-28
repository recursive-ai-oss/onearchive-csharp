using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
using LzipApi = RecursiveAI.OneCompress.Lzip.Lzip;
using LzmaApi = RecursiveAI.OneCompress.Lzma.Lzma;
using XzApi = RecursiveAI.OneCompress.Xz.Xz;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop: files written by xz (xz, lzma-alone, every check, BCJ and delta filters, block sizes, concatenated streams)
/// and lzip (levels 0-9, concatenated members) over public corpora decode to the original bytes, in one call and through the
/// stream class. These tests run only when ONEARCHIVE_INTEROP=1; with it set, a missing tool or corpus fails the run.
/// Settings, all environment variables: ONEARCHIVE_TOOLS (directory holding <c>xz</c> and <c>lzip</c>, default
/// ~/onearchive/tools/bin, then the PATH), ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_INTEROP_JOBS (parallel
/// cases, default 4), ONEARCHIVE_LZMA_BIG=1 (also run the Silesia files and enwik8 at every level; by default the large files
/// run at levels 1, 6 and 9 only, and ONEARCHIVE_LZMA_LARGE=0 leaves them out).
/// </summary>
[Trait("Category", "Interop")]
public class LzmaInteropTests
{
    private static readonly string[] Small =
        ["alice29.txt", "asyoulik.txt", "cp.html", "fields.c", "grammar.lsp", "kennedy.xls", "lcet10.txt", "plrabn12.txt", "ptt5", "sum", "xargs.1",
         "E.coli", "bible.txt", "world192.txt", "aaa.txt", "alphabet.txt", "random.txt"];

    private static readonly string[] Large = ["dickens", "mr", "nci", "ooffice", "osdb", "reymont", "sao", "webster", "x-ray", "xml", "mozilla", "samba"];

    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static int Jobs => int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_JOBS") ?? "4", CultureInfo.InvariantCulture);

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

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
        foreach (string dir in new[] { "", "silesia", "enwik8", "canterbury/cantrbry", "canterbury/large", "canterbury/artificl", "cantrbry", "large", "artificl", "x", "x/cantrbry", "x/large", "x/artificl" })
        {
            string path = Path.Combine(Corpora, dir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static List<(string Name, byte[] Data)> Inputs(bool large, int maxLargeBytes = 12 << 20)
    {
        var list = new List<(string, byte[])>();
        foreach (string n in large ? Large.Append("enwik8") : Small)
        {
            string? path = FindCorpusFile(n);
            Assert.True(path != null, $"corpus file {n} not found under {Corpora}");
            byte[] data = File.ReadAllBytes(path!);
            list.Add((n, data.Length > maxLargeBytes && large ? data[..maxLargeBytes] : data));
        }

        return list;
    }

    private static byte[] Run(string tool, string[] args, byte[] input)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        var feed = Task.Run(() =>
        {
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
        });
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        feed.Wait();
        Assert.True(p.ExitCode == 0, $"{tool} {string.Join(' ', args)} exited with {p.ExitCode}");
        return ms.ToArray();
    }

    private static readonly LzmaDecompressionOptions BigLzma = new() { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 };
    private static readonly XzDecompressionOptions BigXz = new() { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 };
    private static readonly LzipDecompressionOptions BigLzip = new() { MaxOutputSize = long.MaxValue, MaxDictionarySize = 1L << 30 };

    private static void Check(string label, byte[] original, Func<byte[]> decode, ConcurrentBag<string> failures)
    {
        try
        {
            if (!decode().AsSpan().SequenceEqual(original))
            {
                failures.Add(label + ": output differs");
            }
        }
        catch (Exception e)
        {
            failures.Add($"{label}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static byte[] ThroughStream(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static void RunCases(IEnumerable<(string Label, byte[] Original, Func<byte[]> Compressed, bool Stream, int Kind)> cases)
    {
        var failures = new ConcurrentBag<string>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Jobs }, c =>
        {
            byte[] packed;
            try
            {
                packed = c.Compressed();
            }
            catch (Exception e)
            {
                failures.Add($"{c.Label}: tool: {e.Message}");
                return;
            }

            switch (c.Kind)
            {
                case 0:
                    Check(c.Label + " oneshot", c.Original, () => XzApi.Decompress(packed, BigXz), failures);
                    Check(c.Label + " stream", c.Original, () => ThroughStream(new XzStream(new MemoryStream(packed), BigXz)), failures);
                    break;
                case 1:
                    Check(c.Label + " oneshot", c.Original, () => LzmaApi.Decompress(packed, BigLzma), failures);
                    Check(c.Label + " stream", c.Original, () => ThroughStream(new LzmaStream(new MemoryStream(packed), BigLzma)), failures);
                    break;
                default:
                    Check(c.Label + " oneshot", c.Original, () => LzipApi.Decompress(packed, BigLzip), failures);
                    Check(c.Label + " stream", c.Original, () => ThroughStream(new LzipStream(new MemoryStream(packed), BigLzip)), failures);
                    break;
            }
        });
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.OrderBy(x => x).Take(30)));
    }

    private static bool Big => Environment.GetEnvironmentVariable("ONEARCHIVE_LZMA_BIG") == "1";

    private static List<(string Name, byte[] Data)> LargeAt(int level) =>
        Environment.GetEnvironmentVariable("ONEARCHIVE_LZMA_LARGE") == "0" ? [] : Big || level is 1 or 6 or 9 ? Inputs(true) : [];

    [Fact]
    public void XzPresets()
    {
        RequireEnabled();
        string xz = FindTool("xz");
        var cases = new List<(string, byte[], Func<byte[]>, bool, int)>();
        foreach (string preset in new[] { "-0", "-1", "-2", "-3", "-4", "-5", "-6", "-7", "-8", "-9", "-9e", "-6e" })
        {
            int level = preset[1] - '0';
            foreach ((string name, byte[] data) in Inputs(false).Concat(LargeAt(level)))
            {
                cases.Add(($"xz {preset} {name}", data, () => Run(xz, [preset, "-T1", "-c"], data), false, 0));
            }
        }

        RunCases(cases);
    }

    [Fact]
    public void XzChecksFiltersAndBlocks()
    {
        RequireEnabled();
        string xz = FindTool("xz");
        var cases = new List<(string, byte[], Func<byte[]>, bool, int)>();
        foreach ((string name, byte[] data) in Inputs(false))
        {
            foreach (string check in new[] { "none", "crc32", "crc64", "sha256" })
            {
                cases.Add(($"xz --check={check} {name}", data, () => Run(xz, [$"--check={check}", "-c"], data), false, 0));
            }

            foreach (string filter in new[] { "--x86", "--powerpc", "--ia64", "--arm", "--armthumb", "--arm64", "--sparc", "--riscv" })
            {
                cases.Add(($"xz {filter} {name}", data, () => Run(xz, [filter, "--lzma2=preset=4", "-c"], data), false, 0));
            }

            foreach (int distance in new[] { 1, 2, 4, 16, 256 })
            {
                cases.Add(($"xz --delta=dist={distance} {name}", data, () => Run(xz, [$"--delta=dist={distance}", "--lzma2=preset=4", "-c"], data), false, 0));
            }

            cases.Add(($"xz --x86 --delta {name}", data, () => Run(xz, ["--x86", "--delta=dist=4", "--lzma2=preset=3", "-c"], data), false, 0));
            foreach (string size in new[] { "10000", "65536", "1MiB" })
            {
                cases.Add(($"xz --block-size={size} {name}", data, () => Run(xz, [$"--block-size={size}", "-T1", "-3", "-c"], data), false, 0));
            }

            // Concatenated streams are one xz file for the decoder, and the tool agrees.
            cases.Add(($"xz two streams {name}", [.. data, .. data], () => [.. Run(xz, ["-1", "-c"], data), .. Run(xz, ["-6", "--check=sha256", "-c"], data)], false, 0));
        }

        RunCases(cases);
    }

    [Fact]
    public void LzmaAlone()
    {
        RequireEnabled();
        string xz = FindTool("xz");
        var cases = new List<(string, byte[], Func<byte[]>, bool, int)>();
        foreach (string preset in new[] { "-0", "-3", "-6", "-9" })
        {
            foreach ((string name, byte[] data) in Inputs(false))
            {
                cases.Add(($"lzma {preset} {name}", data, () => Run(xz, ["--format=lzma", preset, "-c"], data), false, 1));
            }
        }

        RunCases(cases);
    }

    [Fact]
    public void LzipLevels()
    {
        RequireEnabled();
        string lzip = FindTool("lzip");
        var cases = new List<(string, byte[], Func<byte[]>, bool, int)>();
        for (int level = 0; level <= 9; level++)
        {
            string flag = "-" + level.ToString(CultureInfo.InvariantCulture);
            foreach ((string name, byte[] data) in Inputs(false).Concat(LargeAt(level)))
            {
                cases.Add(($"lzip {flag} {name}", data, () => Run(lzip, [flag, "-c"], data), false, 2));
            }
        }

        foreach ((string name, byte[] data) in Inputs(false))
        {
            cases.Add(($"lzip two members {name}", [.. data, .. data], () => [.. Run(lzip, ["-1", "-c"], data), .. Run(lzip, ["-9", "-c"], data)], false, 2));
        }

        RunCases(cases);
    }
}
