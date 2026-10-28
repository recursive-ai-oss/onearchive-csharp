using System.Diagnostics;
using RecursiveAI.OneCompress.Filters;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// The reference tools decode what the encoders write. These tests run only when ONEARCHIVE_INTEROP=1; with it set, a missing tool or
/// corpus fails the run. Settings, as in <see cref="LzmaInteropTests"/>: ONEARCHIVE_TOOLS (directory holding <c>xz</c>, <c>lzip</c> and
/// optionally <c>7z</c> or <c>7zz</c>, then the PATH) and ONEARCHIVE_CORPORA.
/// </summary>
[Trait("Category", "Interop")]
public class LzmaEncoderInteropTests
{
    private static readonly string[] Dirs = ["", "x/cantrbry", "cantrbry", "canterbury/cantrbry"];

    private static readonly string[] Files = ["alice29.txt", "kennedy.xls", "ptt5", "sum", "xargs.1"];

    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static string? TryFindTool(string name)
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

        return null;
    }

    private static string FindTool(string name) =>
        TryFindTool(name) ?? throw new FileNotFoundException($"the tool '{name}' is not in {Tools} or on the PATH");

    private static IEnumerable<byte[]> Inputs()
    {
        foreach (string name in Files)
        {
            string? found = Dirs.Select(d => Path.Combine(Corpora, d, name)).FirstOrDefault(File.Exists);
            Assert.True(found != null, $"corpus file {name} not found under {Corpora}");
            yield return File.ReadAllBytes(found!);
        }

        yield return [];
        yield return [42];
        yield return new byte[300_000];
        yield return LzmaEncoderTests.Random(100_000, 3);
        yield return LzmaEncoderTests.Structured(300_000, 4);
    }

    private static byte[] Run(string tool, string[] args, byte[] input)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        var feed = Task.Run(() =>
        {
            try
            {
                p.StandardInput.BaseStream.Write(input);
                p.StandardInput.Close();
            }
            catch (IOException)
            {
                // The tool stopped reading because it found an error; its exit code reports it.
            }
        });
        var err = p.StandardError.ReadToEndAsync();
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        feed.Wait();
        Assert.True(p.ExitCode == 0, $"{tool} {string.Join(' ', args)} exited with {p.ExitCode}: {err.Result}");
        return ms.ToArray();
    }

    public static TheoryData<int, bool> Presets()
    {
        var data = new TheoryData<int, bool>();
        for (int p = 0; p <= 9; p++)
        {
            data.Add(p, false);
            data.Add(p, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void XzDecodesEveryPreset(int preset, bool extreme)
    {
        RequireEnabled();
        string xz = FindTool("xz");
        foreach (byte[] input in Inputs())
        {
            byte[] packed = Xz.Xz.Compress(input, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = preset, Extreme = extreme } });
            Assert.Equal(input, Run(xz, ["-dc"], packed));
            Run(xz, ["-t"], packed);
        }
    }

    [Theory]
    [InlineData(XzCheck.None)]
    [InlineData(XzCheck.Crc32)]
    [InlineData(XzCheck.Crc64)]
    [InlineData(XzCheck.Sha256)]
    public void XzDecodesEveryCheckAndBlockLayout(XzCheck check)
    {
        RequireEnabled();
        string xz = FindTool("xz");
        string? sevenZip = TryFindTool("7zz") ?? TryFindTool("7z");
        byte[] input = Inputs().ElementAt(1);
        foreach (long blockSize in new long[] { 0, 4096, 100_000 })
        {
            var options = new XzCompressionOptions { Check = check, BlockSize = blockSize, Lzma = new LzmaOptions { Preset = 1 } };
            byte[] packed = Xz.Xz.Compress(input, options);
            Assert.Equal(input, Run(xz, ["-dc"], packed));
            string path = Path.Combine(Path.GetTempPath(), $"onearchive-{Guid.NewGuid():N}.xz");
            string listing;
            try
            {
                File.WriteAllBytes(path, packed);
                listing = System.Text.Encoding.ASCII.GetString(Run(xz, ["--robot", "--list", path], []));
                if (sevenZip is not null && blockSize == 100_000)
                {
                    Run(sevenZip, ["t", path], []);
                }
            }
            finally
            {
                File.Delete(path);
            }

            Assert.Contains("totals", listing, StringComparison.Ordinal);
            if (blockSize > 0)
            {
                long expectedBlocks = (input.Length + blockSize - 1) / blockSize;
                Assert.Contains($"\t{expectedBlocks}\t", listing, StringComparison.Ordinal);
            }
        }
    }

    public static TheoryData<int> FilterIds() => new(Enum.GetValues<BranchArchitecture>().Select(a => (int)a).Append(3));

    [Theory]
    [MemberData(nameof(FilterIds))]
    public void XzDecodesEveryFilter(int id)
    {
        RequireEnabled();
        string xz = FindTool("xz");
        byte[] input = [.. Inputs().ElementAt(3), .. LzmaEncoderTests.Structured(200_000, 8)];
        XzFilter filter = id == 3 ? XzFilter.Delta(4) : XzFilter.Branch((BranchArchitecture)id);
        foreach (long blockSize in new long[] { 0, 50_000 })
        {
            var options = new XzCompressionOptions { Filters = [filter], BlockSize = blockSize, Lzma = new LzmaOptions { Preset = 2 } };
            Assert.Equal(input, Run(xz, ["-dc"], Xz.Xz.Compress(input, options)));
        }

        var chain = new XzCompressionOptions { Filters = [XzFilter.Branch(BranchArchitecture.X86, 4096), XzFilter.Delta(2)], Lzma = new LzmaOptions { Preset = 2 } };
        Assert.Equal(input, Run(xz, ["-dc"], Xz.Xz.Compress(input, chain)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void XzDecodesLzmaAloneAndLzma2Parameters(int preset)
    {
        RequireEnabled();
        string xz = FindTool("xz");
        foreach (byte[] input in Inputs())
        {
            foreach (bool marker in new[] { false, true })
            {
                byte[] packed = Lzma.Lzma.Compress(input, new LzmaOptions { Preset = preset, WriteEndMarker = marker });
                Assert.Equal(input, Run(xz, ["--format=lzma", "-dc"], packed));
            }
        }

        byte[] data = Inputs().ElementAt(4);
        var odd = new LzmaOptions { Preset = preset, Lc = 0, Lp = 2, Pb = 0, DictionarySize = 1 << 14 };
        Assert.Equal(data, Run(xz, ["--format=lzma", "-dc"], Lzma.Lzma.Compress(data, odd)));
        Assert.Equal(data, Run(xz, ["-dc"], Xz.Xz.Compress(data, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = preset, Lc = 1, Lp = 3, Pb = 1 } })));

        // A streamed .lzma has an unknown length and an end marker.
        var sink = new MemoryStream();
        using (var stream = new LzmaEncoderStream(sink, new LzmaOptions { Preset = preset }, leaveOpen: true))
        {
            stream.Write(data);
        }

        Assert.Equal(data, Run(xz, ["--format=lzma", "-dc"], sink.ToArray()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void LzipDecodesEveryLevel(int level)
    {
        RequireEnabled();
        string lzip = FindTool("lzip");
        foreach (byte[] input in Inputs())
        {
            byte[] packed = Lzip.Lzip.Compress(input, new LzipOptions { Level = level });
            Assert.Equal(input, Run(lzip, ["-dc"], packed));
            Run(lzip, ["-t"], packed);
            byte[] members = Lzip.Lzip.Compress(input, new LzipOptions { Level = level, MemberSize = 70_000 });
            Assert.Equal(input, Run(lzip, ["-dc"], members));
        }
    }
}
