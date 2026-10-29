using System.Runtime.Versioning;
using RecursiveAI.OneArchive.Rar;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>
/// Archives written by `rar`, run as a black box, read back and compared with the tree they were made from. They run only with
/// ONEARCHIVE_INTEROP=1, and then a missing tool fails the run. ONEARCHIVE_RAR names the executable (default: rar on PATH).
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class RarInteropTests
{
    private static string RarTool => Environment.GetEnvironmentVariable("ONEARCHIVE_RAR") ?? "rar";

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("RAR", Tools.Run(RarTool, ["-iver"], failOnError: false) + Tools.Run(RarTool, [], failOnError: false), StringComparison.Ordinal);
    }

    private static string Tree(TempDir t)
    {
        string root = t.Combine("src");
        Directory.CreateDirectory(Path.Combine(root, "a/b/c"));
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        File.WriteAllBytes(Path.Combine(root, "empty"), []);
        File.WriteAllBytes(Path.Combine(root, "one"), [42]);
        File.WriteAllBytes(Path.Combine(root, "a/text.txt"), System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("some text that repeats\n", 2000))));
        File.WriteAllBytes(Path.Combine(root, "a/b/c/random.bin"), ContainerVectors.Big(70_000));
        File.WriteAllBytes(Path.Combine(root, "big.bin"), ContainerVectors.Big(1_500_000));
        File.WriteAllText(Path.Combine(root, "\u00fcn\u00ef-\u6587\u4ef6-\U0001F642.txt"), "unicode name");
        File.SetUnixFileMode(Path.Combine(root, "a/text.txt"), (UnixFileMode)0x1ED);
        File.SetLastWriteTimeUtc(Path.Combine(root, "a/text.txt"), new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc).AddTicks(1_234_567));
        return root;
    }

    private static void Compare(string expectedRoot, string actualRoot, bool times)
    {
        foreach (string file in Directory.GetFiles(expectedRoot, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(expectedRoot, file);
            string other = Path.Combine(actualRoot, rel);
            Assert.True(File.Exists(other), rel);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(other));
            Assert.Equal(File.GetUnixFileMode(file), File.GetUnixFileMode(other));
            if (times)
            {
                Assert.Equal(File.GetLastWriteTimeUtc(file), File.GetLastWriteTimeUtc(other));
            }
        }

        foreach (string dir in Directory.GetDirectories(expectedRoot, "*", SearchOption.AllDirectories))
        {
            Assert.True(Directory.Exists(Path.Combine(actualRoot, Path.GetRelativePath(expectedRoot, dir))));
        }
    }

    [Theory]
    [InlineData("-m0")]
    [InlineData("-m0 -htb")]
    [InlineData("-m0 -s")]
    [InlineData("-m0 -rr3%")]
    [InlineData("-m0 -qo+")]
    [InlineData("-m0 -tsm4 -tsc4 -tsa4")]
    [InlineData("-m0 -md4m -ma5 -k")]
    public void StoredArchivesFromRarExtractIdentically(string switches)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = Tree(t);
        string archive = t.Combine("a.rar");
        Tools.Run(RarTool, ["a", "-ma5", "-ep1", "-r", "-idq", .. switches.Split(' '), archive, "."], src);
        Archive.Extract(archive, t.Combine("out"));
        Compare(src, t.Combine("out"), times: true);
    }

    [Theory]
    [InlineData(100_000)]
    [InlineData(400_000)]
    public void VolumeSetsFromRarExtractIdentically(int volumeSize)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = Tree(t);
        string archive = t.Combine("vols/set.rar");
        Directory.CreateDirectory(t.Combine("vols"));
        Tools.Run(RarTool, ["a", "-ma5", "-m0", "-ep1", "-r", "-idq", $"-v{volumeSize}b", archive, "."], src);
        string first = t.Combine("vols/set.part01.rar");
        Assert.True(File.Exists(first) || File.Exists(t.Combine("vols/set.part1.rar")));
        if (!File.Exists(first))
        {
            first = t.Combine("vols/set.part1.rar");
        }

        Archive.Extract(first, t.Combine("out"));
        Compare(src, t.Combine("out"), times: false);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void CompressedArchivesListLikeRarAndRefuseExtraction(int method)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = Tree(t);
        string archive = t.Combine("a.rar");
        Tools.Run(RarTool, ["a", "-ma5", $"-m{method}", "-ep1", "-r", "-idq", archive, "."], src);

        string[] expected = Tools.Run(RarTool, ["lb", archive]).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("RAR ", StringComparison.Ordinal) && !l.StartsWith("Trial", StringComparison.Ordinal) && !l.StartsWith("Archive", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        using Archive a = Archive.Open(archive);
        string[] ours = a.Entries.Select(e => e.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, ours);
        Assert.Contains(a.Entries, e => e is RarEntry { Method: var m } && m == method);
        Assert.Throws<RarUnsupportedException>(() => Archive.Extract(archive, t.Combine("out")));
    }
}
