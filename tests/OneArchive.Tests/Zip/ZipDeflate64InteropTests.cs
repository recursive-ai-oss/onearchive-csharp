using System.Globalization;
using System.Runtime.Versioning;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Deflate64 (zip method 9) against 7-Zip, run as a black box. They run only with ONEARCHIVE_INTEROP=1; ONEARCHIVE_7Z names the
/// executable (default 7zz). ONEARCHIVE_CORPORA (default ~/onearchive/corpora) holds the Silesia files as x/&lt;name&gt;; a missing file fails the run.
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class ZipDeflate64InteropTests
{
    private static string SevenZip => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    // Appends to the file named by ONEARCHIVE_INTEROP_LOG, if set: the numbers are for the reader of a run, not assertions.
    private static void Report(string line)
    {
        if (Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_LOG") is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + "\n");
        }
    }

    private static void Enabled() => Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");

    // Files that exercise what Deflate64 adds: matches farther than 32 KiB, and runs longer than 258 bytes.
    private static void Inputs(string dir, bool silesia)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello deflate64\n");
        File.WriteAllBytes(Path.Combine(dir, "text.txt"), ZipTests.Text(150_000, 21));
        var rng = new Random(7);
        byte[] block = rng.GetBytes(48_000);
        byte[] gap = rng.GetBytes(12_000);
        File.WriteAllBytes(Path.Combine(dir, "far.bin"), [.. block, .. gap, .. block, .. gap.Take(5000), .. block.Take(20_000)]);
        File.WriteAllBytes(Path.Combine(dir, "runs.bin"), [.. new byte[200_000], .. Enumerable.Repeat((byte)'a', 100_000), .. ZipTests.Text(2000, 3), .. new byte[70_000]]);
        File.WriteAllBytes(Path.Combine(dir, "random.bin"), rng.GetBytes(30_000));
        File.WriteAllBytes(Path.Combine(dir, "one"), [1]);
        if (silesia)
        {
            foreach (string name in new[] { "dickens", "mr", "reymont" })
            {
                string path = Path.Combine(Corpora, "x", name);
                Assert.True(File.Exists(path), "missing corpus file " + path);
                File.Copy(path, Path.Combine(dir, name));
            }
        }
    }

    private static void AssertSameFiles(string expected, string actual)
    {
        foreach (string file in Directory.GetFiles(expected))
        {
            Assert.True(File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(actual, Path.GetFileName(file)))), file);
        }
    }

    [Fact]
    public void ZipsWrittenBy7ZipAtEveryLevelExtractExactly()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Inputs(tree, silesia: true);
        foreach (int mx in new[] { 1, 3, 5, 7, 9 })
        {
            string zip = temp.Combine($"mx{mx}.zip");
            Tools.Run(SevenZip, ["a", "-tzip", "-mm=Deflate64", $"-mx={mx}", zip, ".", "-bd"], tree);
            using (var reader = new ZipReader(File.OpenRead(zip)))
            {
                ZipEntry? e;
                while ((e = reader.GetNextEntry()) is not null)
                {
                    Assert.True(e.Method is ZipMethod.Deflate64 or ZipMethod.Store, e.Name + " " + e.Method);
                }
            }

            string output = temp.Combine("out" + mx.ToString(CultureInfo.InvariantCulture));
            Archive.Extract(zip, output);
            AssertSameFiles(tree, output);
        }
    }

    [Fact]
    public void ZipsWeWriteAreAcceptedBy7ZipAtEveryLevel()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Inputs(tree, silesia: true);
        var sizes = new List<string>();
        for (int level = 0; level <= 9; level++)
        {
            string zip = temp.Combine($"l{level}.zip");
            Archive.Create(zip, new ArchiveCreateOptions { Zip = new ZipWriterOptions { Method = ZipMethod.Deflate64, Level = level } }).Add(tree, "").Save();
            Tools.Run(SevenZip, ["t", zip]);
            string output = temp.Combine("x" + level.ToString(CultureInfo.InvariantCulture));
            Tools.Run(SevenZip, ["x", "-y", "-o" + output, zip, "-bd"]);
            AssertSameFiles(tree, output);

            string ours = temp.Combine("o" + level.ToString(CultureInfo.InvariantCulture));
            Archive.Extract(zip, ours);
            AssertSameFiles(tree, ours);
            sizes.Add($"{level}:{new FileInfo(zip).Length}");
        }

        Report("our deflate64 zip sizes (all inputs) by level: " + string.Join(' ', sizes));
    }

    [Fact]
    public void RatioAgainst7ZipDeflate64OnSilesiaIsReported()
    {
        Enabled();
        using var temp = new TempDir();
        foreach (string name in new[] { "dickens", "mr", "reymont" })
        {
            string tree = temp.Combine(name + "-tree");
            Directory.CreateDirectory(tree);
            File.Copy(Path.Combine(Corpora, "x", name), Path.Combine(tree, name));
            foreach ((int ours, int theirs) in new[] { (1, 1), (3, 3), (5, 5), (7, 7), (9, 9) })
            {
                string zip = temp.Combine($"{name}-{ours}.zip");
                Archive.Create(zip, new ArchiveCreateOptions { Zip = new ZipWriterOptions { Method = ZipMethod.Deflate64, Level = ours } }).Add(tree, "").Save();
                string zip7 = temp.Combine($"{name}-7z{theirs}.zip");
                Tools.Run(SevenZip, ["a", "-tzip", "-mm=Deflate64", $"-mx={theirs}", zip7, ".", "-bd"], tree);
                long a = new FileInfo(zip).Length;
                long b = new FileInfo(zip7).Length;
                Report($"ratio {name} level {ours}: ours {a} 7-Zip {b} ({a * 100.0 / b:F2}%)");
            }
        }
    }
}
