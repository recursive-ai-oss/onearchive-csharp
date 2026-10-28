using System.Runtime.Versioning;
using RecursiveAI.OneArchive.SevenZip;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

/// <summary>
/// Reads archives written by 7-Zip, run as a black box, and compares with bsdtar's listing. They run only with
/// ONEARCHIVE_INTEROP=1, and then a missing tool fails the run. ONEARCHIVE_7Z and ONEARCHIVE_BSDTAR name the
/// executables (defaults: 7zz, bsdtar).
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class SevenZipInteropTests
{
    private static string Tool => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static string BsdTar => Environment.GetEnvironmentVariable("ONEARCHIVE_BSDTAR") ?? "bsdtar";

    private static void Tree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "dir/nested"));
        Directory.CreateDirectory(Path.Combine(root, "emptydir"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(root, "dir/nested/data.bin"), SevenZipFixtures.Repeat("compressible line of text\n", 20000));
        File.WriteAllBytes(Path.Combine(root, "dir/random.bin"), SevenZipFixtures.Lcg(3, 30000));
        File.WriteAllText(Path.Combine(root, "dir/run.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "\u00fcn\u00ef-\u65e5\u672c\u8a9e.txt"), "unicode");
        File.WriteAllText(Path.Combine(root, "zero"), string.Empty);
        for (int i = 0; i < 300; i++)
        {
            File.WriteAllText(Path.Combine(root, $"dir/f{i}.txt"), new string((char)('a' + i % 26), i + 1));
        }

        File.SetUnixFileMode(Path.Combine(root, "dir/run.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
        File.CreateSymbolicLink(Path.Combine(root, "link"), "hello.txt");
    }

    [Theory]
    [InlineData("-m0=Copy -ms=on -mhc=off")]
    [InlineData("-m0=Copy -ms=off -mhc=off")]
    [InlineData("-m0=Deflate -ms=on -mhc=off")]
    [InlineData("-m0=Deflate -ms=off -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=1 -ms=on -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=1 -ms=off -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=5 -ms=on -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=5 -ms=off -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=9 -ms=on -mhc=off")]
    [InlineData("-m0=Deflate64 -mx=9 -ms=off -mhc=off")]
    [InlineData("-m0=BZip2 -ms=on -mhc=off")]
    [InlineData("-m0=BZip2 -ms=e -mhc=off")]
    [InlineData("-m0=Copy -m1=Copy -ms=on -mhc=off")]
    [InlineData("")]
    [InlineData("-mx=9")]
    [InlineData("-mx=1")]
    [InlineData("-m0=LZMA -ms=on")]
    [InlineData("-m0=LZMA -ms=off -mhc=off")]
    [InlineData("-m0=LZMA2 -ms=e")]
    [InlineData("-m0=BCJ -m1=LZMA -ms=on")]
    [InlineData("-m0=Delta:4 -m1=LZMA2 -ms=on")]
    [InlineData("-mf=ARM64 -m1=LZMA2 -ms=on")]
    [InlineData("-mf=RISCV -m1=LZMA2 -ms=on")]
    [InlineData("-mf=ARM -m1=LZMA -ms=on")]
    [InlineData("-mf=PPC -m1=LZMA2 -ms=on")]
    [InlineData("-mf=SPARC -m1=LZMA2 -ms=on")]
    [InlineData("-mf=IA64 -m1=LZMA2 -ms=on")]
    [InlineData("-mf=ARMT -m1=LZMA2 -ms=on")]
    public void ReadsArchivesWrittenBy7Zip(string switches)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        string archive = tmp.Combine("a.7z");
        Tools.Run("bash", ["-c", $"cd '{src}' && '{Tool}' a -t7z -snl -bso0 -bsp0 -mmt=1 {switches} '{archive}' ."]);

        string outDir = tmp.Combine("out");
        Archive.Extract(archive, outDir);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            if (new FileInfo(file).LinkTarget is { } target)
            {
                Assert.Equal(target, new FileInfo(Path.Combine(outDir, rel)).LinkTarget);
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(outDir, rel)));
            }
        }

        Assert.Equal(File.GetUnixFileMode(Path.Combine(src, "dir/run.sh")), File.GetUnixFileMode(Path.Combine(outDir, "dir/run.sh")));

        using var fs = File.OpenRead(archive);
        using var r = new SevenZipReader(fs);
        foreach (SevenZipEntry e in r.Items)
        {
            names.Add(e.Name);
            string original = Path.Combine(src, e.Name);
            DateTime expected = e.Kind == ArchiveEntryKind.Symlink ? new FileInfo(original).LastWriteTimeUtc : File.GetLastWriteTimeUtc(original);
            if (e.Kind != ArchiveEntryKind.Symlink)
            {
                Assert.True(Math.Abs((e.ModifiedTime!.Value.UtcDateTime - expected).Ticks) < 2, e.Name);
            }
        }

        // bsdtar's listing names the same entries (directories with a trailing slash, a "./" prefix is not used by 7z).
        if (switches.Contains("-m1=Copy") || switches.Contains("Deflate64") || switches.Contains("Delta") || switches.Contains("-mf="))
        {
            return; // bsdtar cannot read these filter chains or Deflate64.
        }

        var listed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in Tools.Run(BsdTar, ["-tf", archive]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            listed.Add(line.TrimEnd('/'));
        }

        Assert.Equal(listed, names);
    }

    [Theory]
    [InlineData("LZMA")]
    [InlineData("xz")]
    [InlineData("LZMA:d=64k")]
    public void ReadsZipEntriesWrittenBy7Zip(string method)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        string archive = tmp.Combine("a.zip");
        Tools.Run("bash", ["-c", $"cd '{src}' && '{Tool}' a -tzip -snl -bso0 -bsp0 -mmt=1 -mm={method} '{archive}' ."]);
        string outDir = tmp.Combine("out");
        Archive.Extract(archive, outDir);
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file);
            if (new FileInfo(file).LinkTarget is { } target)
            {
                Assert.Equal(target, new FileInfo(Path.Combine(outDir, rel)).LinkTarget);
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(outDir, rel)));
            }
        }
    }

    [Theory]
    [InlineData("-m0=LZMA2:d=1m:c=256k -mmt=4")]
    [InlineData("-m0=LZMA:d=8m -mx=7")]
    [InlineData("-m0=LZMA2 -mx=9 -mmt=4")]
    public void ReadsABigMultiChunkArchive(string switches)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Directory.CreateDirectory(src);
        // 14 MB: words drawn from a small vocabulary (compressible), then noise, then a long repeat.
        byte[] noise = SevenZipFixtures.Lcg(11, 3_000_000);
        var words = new System.Text.StringBuilder();
        var rnd = new Random(42);
        string[] vocab = Enumerable.Range(0, 500).Select(i => "w" + (i * 7919 % 10007)).ToArray();
        while (words.Length < 9_000_000)
        {
            words.Append(vocab[rnd.Next(vocab.Length)]).Append(' ');
        }

        byte[] text = System.Text.Encoding.ASCII.GetBytes(words.ToString());
        File.WriteAllBytes(Path.Combine(src, "words.txt"), text);
        File.WriteAllBytes(Path.Combine(src, "noise.bin"), noise);
        File.WriteAllBytes(Path.Combine(src, "repeat.bin"), SevenZipFixtures.Repeat("0123456789abcdef", 100_000));
        string archive = tmp.Combine("big.7z");
        Tools.Run("bash", ["-c", $"cd '{src}' && '{Tool}' a -t7z -bso0 -bsp0 -ms=on {switches} '{archive}' ."]);
        string outDir = tmp.Combine("out");
        var limits = new ArchiveLimits { MaxTotalSize = 1L << 31 };
        Archive.Extract(archive, outDir, new ArchiveExtractOptions { Limits = limits });
        foreach (string file in Directory.GetFiles(src))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(outDir, Path.GetFileName(file))));
        }
    }
}
