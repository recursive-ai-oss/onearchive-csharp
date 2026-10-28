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
    [InlineData("-m0=Copy -ms=on")]
    [InlineData("-m0=Copy -ms=off")]
    [InlineData("-m0=Deflate -ms=on")]
    [InlineData("-m0=Deflate -ms=off")]
    [InlineData("-m0=BZip2 -ms=on")]
    [InlineData("-m0=BZip2 -ms=e")]
    [InlineData("-m0=Copy -m1=Copy -ms=on")]
    public void ReadsArchivesWrittenBy7Zip(string switches)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        string archive = tmp.Combine("a.7z");
        Tools.Run("bash", ["-c", $"cd '{src}' && '{Tool}' a -t7z -snl -bso0 -bsp0 -mmt=1 -mhc=off {switches} '{archive}' ."]);

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
        if (switches.Contains("-m1="))
        {
            return; // bsdtar cannot read a chained Copy folder.
        }

        var listed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in Tools.Run(BsdTar, ["-tf", archive]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            listed.Add(line.TrimEnd('/'));
        }

        Assert.Equal(listed, names);
    }
}
