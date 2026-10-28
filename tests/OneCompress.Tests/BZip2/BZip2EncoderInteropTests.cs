using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneCompress.BZip2;
using BZ = RecursiveAI.OneCompress.BZip2.BZip2;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Encoder interop: what our encoder writes is decoded by the bzip2 tool (run as a black box) byte for byte. Runs only with
/// ONEARCHIVE_INTEROP=1; with it set, a missing tool or corpus file fails the run. Environment: ONEARCHIVE_TOOLS (directory with
/// <c>bzip2</c>, then the PATH), ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_INTEROP_TMP (scratch directory),
/// ONEARCHIVE_INTEROP_LOG (a file that receives one line of sizes per corpus case: name, level, input, ours, the tool's).
/// </summary>
[Trait("Category", "Interop")]
public class BZip2EncoderInteropTests
{
    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

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
        foreach (string sub in new[] { "", "silesia", "x", "cantrbry" })
        {
            string path = Path.Combine(Corpora, sub, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string Scratch(string name)
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"bz2enc-{Environment.ProcessId}-{name}");
    }

    private static byte[] ToolDecode(byte[] compressed, string name)
    {
        string path = Scratch(name + ".bz2");
        File.WriteAllBytes(path, compressed);
        try
        {
            var psi = new ProcessStartInfo(FindTool("bzip2")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-dc");
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"bzip2 -dc rejected {name}: {err.Result}");
            return ms.ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] ToolCompress(byte[] data, int level, string name)
    {
        string path = Scratch(name + ".in");
        File.WriteAllBytes(path, data);
        try
        {
            var psi = new ProcessStartInfo(FindTool("bzip2")) { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add($"-{level}");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(path);
            using Process p = Process.Start(psi)!;
            var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            return ms.ToArray();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckBoth(string name, byte[] data, int level)
    {
        byte[] packed = BZ.Compress(data, new BZip2Options { Level = level });
        Assert.True(BZ.Decompress(packed).AsSpan().SequenceEqual(data), $"{name} level {level}: our decoder");
        Assert.True(ToolDecode(packed, $"{name}-{level}").AsSpan().SequenceEqual(data), $"{name} level {level}: the tool's output differs");
    }

    [Fact]
    public void GeneratedInputsAtEveryLevel()
    {
        RequireEnabled();
        var cases = new List<(string, byte[])>
        {
            ("empty", []),
            ("one", [1]),
            ("text", BZip2EncoderTests.Text(300_000, 4)),
            ("random", BZip2EncoderTests.Random(120_000, 5)),
            ("periodic", Enumerable.Range(0, 70_000).Select(static i => (byte)("abcde"[i % 5])).ToArray()),
            ("zeros", new byte[2_500_000]),
        };
        foreach (int run in new[] { 3, 4, 5, 255, 256, 259, 260, 1000 })
        {
            cases.Add(($"run{run}", [.. "q"u8, .. Enumerable.Repeat((byte)'r', run), .. "s"u8]));
        }

        foreach ((string name, byte[] data) in cases)
        {
            for (int level = 1; level <= 9; level++)
            {
                CheckBoth(name, data, level);
            }
        }
    }

    [Fact]
    public void SeveralBlocksAndTheBoundary()
    {
        RequireEnabled();
        for (int level = 1; level <= 9; level++)
        {
            int capacity = (level * 100_000) - 19;
            foreach (int length in new[] { capacity, capacity + 1, (level * 100_000) + 1, (2 * capacity) + 7 })
            {
                if (level > 3 && length > capacity + 1 && length != (level * 100_000) + 1)
                {
                    continue;
                }

                CheckBoth($"noruns{length}", BZip2EncoderTests.NoRuns(length, length), level);
                CheckBoth($"text{length}", BZip2EncoderTests.Text(length, length), level);
            }
        }

        // A run across the end of a level-1 block.
        for (int head = 99_970; head <= 99_990; head++)
        {
            CheckBoth($"straddle{head}", [.. BZip2EncoderTests.NoRuns(head, head), .. Enumerable.Repeat((byte)'z', 3000), .. "end"u8], 1);
        }
    }

    [Fact]
    public void ConcatenatedStreamsAreDecodedByTheTool()
    {
        RequireEnabled();
        byte[] a = BZip2EncoderTests.Text(40_000, 1);
        byte[] b = BZip2EncoderTests.Random(3000, 2);
        byte[] joined = [.. BZ.Compress(a, new BZip2Options { Level = 3 }), .. BZ.Compress([]), .. BZ.Compress(b, new BZip2Options { Level = 9 })];
        Assert.True(ToolDecode(joined, "concat").AsSpan().SequenceEqual([.. a, .. b]));
    }

    [Fact]
    public void CodeLengthsUpToTwentyAreAccepted()
    {
        RequireEnabled();
        // 19 distinct bytes give a 21-symbol alphabet; lengths 1, 2, ..., 20, 20 fill the code space exactly.
        byte[] data = Enumerable.Range(0, 3000).Select(static i => (byte)((i * 7 % 19) + 1)).ToArray();
        byte[] lengths = [.. Enumerable.Range(1, 20).Select(static l => (byte)l), 20];
        byte[] packed = BZip2TestBuilder.Stream(data, 9, tamper: (m, _) =>
        {
            // Re-code the block's symbols so that all of them are legal under the new lengths (they are: every symbol has a length).
            m.Lengths = [lengths, lengths];
        });
        Assert.Equal(data, BZ.Decompress(packed));
        Assert.True(ToolDecode(packed, "len20").AsSpan().SequenceEqual(data));
    }

    [Fact]
    public void CorpusFilesAtEveryLevel()
    {
        RequireEnabled();
        string[] files = (Environment.GetEnvironmentVariable("ONEARCHIVE_BZIP2_ENCODE_FILES") ?? "dickens,mr,xml").Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[] levelText = (Environment.GetEnvironmentVariable("ONEARCHIVE_BZIP2_ENCODE_LEVELS") ?? "1,2,3,4,5,6,7,8,9").Split(',');
        string? log = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG");
        int maxLength = int.Parse(Environment.GetEnvironmentVariable("ONEARCHIVE_BZIP2_ENCODE_MAXLEN") ?? "17", CultureInfo.InvariantCulture);
        foreach (string file in files)
        {
            string path = FindCorpusFile(file) ?? throw new FileNotFoundException($"corpus file '{file}' not found under {Corpora}");
            byte[] data = File.ReadAllBytes(path);
            foreach (string l in levelText)
            {
                int level = int.Parse(l, CultureInfo.InvariantCulture);
                var sw = Stopwatch.StartNew();
                byte[] packed = BZ.Compress(data, new BZip2Options { Level = level, MaxCodeLength = maxLength });
                long ms = sw.ElapsedMilliseconds;
                Assert.True(BZ.Decompress(packed).AsSpan().SequenceEqual(data), $"{file} level {level}: our decoder");
                Assert.True(ToolDecode(packed, $"{file}-{level}").AsSpan().SequenceEqual(data), $"{file} level {level}: the tool's output differs");
                if (log is not null)
                {
                    File.AppendAllText(log, string.Join('\t', file, level, data.Length, packed.Length, ToolCompress(data, level, $"{file}-{level}").Length, ms) + "\n");
                }
            }
        }
    }
}
