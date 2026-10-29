using RecursiveAI.OneArchive.Iso;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Round trips against xorriso and bsdtar, run as black boxes. They run only with ONEARCHIVE_INTEROP=1, and then a missing tool fails
/// the run. ONEARCHIVE_XORRISO and ONEARCHIVE_BSDTAR name the executables (defaults: xorriso and bsdtar on PATH); ONEARCHIVE_ISOINFO
/// optionally names isoinfo, which then checks the Joliet and Rock Ridge listings as well.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class IsoInteropTests
{
    private static string Xorriso => Environment.GetEnvironmentVariable("ONEARCHIVE_XORRISO") ?? "xorriso";

    private static string BsdTar => Environment.GetEnvironmentVariable("ONEARCHIVE_BSDTAR") ?? "bsdtar";

    private static string? IsoInfo => Environment.GetEnvironmentVariable("ONEARCHIVE_ISOINFO");

    private const long Mtime = 1_700_000_000;

    private static readonly string s_unicode = "\u00fcn\u00ef-\u65e5\u672c.txt";

    private static void Skip() => Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");

    // A tree on disk with a symlink, modes and fixed times, then an ISO made from it by the happy path.
    private static string BuildTree(TempDir temp)
    {
        string root = temp.Combine("tree");
        Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(root, "big.bin"), ContainerVectors.Big(3_000_000));
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "bee\n");
        File.WriteAllText(Path.Combine(root, "sub", "deeper", "c.txt"), "sea\n");
        File.WriteAllText(Path.Combine(root, "run.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, s_unicode), "unicode");
        File.WriteAllText(Path.Combine(root, new string('l', 200) + ".txt"), "long");
        File.WriteAllBytes(Path.Combine(root, "empty"), []);
        File.SetUnixFileMode(Path.Combine(root, "run.sh"), (UnixFileMode)0x1ED);
        File.SetUnixFileMode(Path.Combine(root, "big.bin"), (UnixFileMode)0x180);
        File.CreateSymbolicLink(Path.Combine(root, "link"), "a.txt");
        File.CreateSymbolicLink(Path.Combine(root, "sub", "up"), "../a.txt");
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(f).LinkTarget is null)
            {
                File.SetLastWriteTimeUtc(f, DateTimeOffset.FromUnixTimeSeconds(Mtime).UtcDateTime);
            }
        }

        return root;
    }

    private static string MakeIso(TempDir temp, string tree, IsoWriterOptions? options = null)
    {
        string iso = temp.Combine("ours.iso");
        Archive.Create(iso, new ArchiveCreateOptions { Iso = options }).Add(tree, string.Empty).Save();
        return iso;
    }

    private static void CompareTrees(string expected, string actual, bool checkModes = true)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(expected, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(expected, path);
            string other = Path.Combine(actual, rel);
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target)
            {
                Assert.Equal(target, new FileInfo(other).LinkTarget);
                continue;
            }

            if (Directory.Exists(path))
            {
                Assert.True(Directory.Exists(other), "missing directory " + rel);
                continue;
            }

            Assert.True(File.Exists(other), "missing file " + rel);
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(other));
            if (checkModes)
            {
                Assert.Equal(File.GetUnixFileMode(path), File.GetUnixFileMode(other));
                Assert.Equal(Mtime, new DateTimeOffset(File.GetLastWriteTimeUtc(other), TimeSpan.Zero).ToUnixTimeSeconds());
            }
        }
    }

    [Fact]
    public void XorrisoReadsAndExtractsOurImage()
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree);
        string listing = Tools.Run(Xorriso, ["-indev", iso, "-find"]);
        Assert.Contains("'./sub/deeper/c.txt'", listing, StringComparison.Ordinal);
        Assert.Contains("'./" + s_unicode + "'", listing, StringComparison.Ordinal);
        Assert.Contains("'./" + new string('l', 200) + ".txt'", listing, StringComparison.Ordinal);
        string outDir = temp.Combine("xorriso-out");
        Tools.Run(Xorriso, ["-osirrox", "on", "-indev", iso, "-extract", "/", outDir]);
        CompareTrees(tree, outDir);
        string report = Tools.Run(Xorriso, ["-indev", iso, "-pwd", "-lsl", "/"]);
        Assert.Contains("link -> a.txt", report, StringComparison.Ordinal);
    }

    [Fact]
    public void XorrisoReadsTheJolietHierarchyOfOurImage()
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree);
        string outDir = temp.Combine("joliet-out");
        Tools.Run(Xorriso, ["-joliet", "on", "-rockridge", "off", "-osirrox", "on", "-indev", iso, "-extract", "/", outDir]);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal(ContainerVectors.Big(3_000_000), File.ReadAllBytes(Path.Combine(outDir, "big.bin")));
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(outDir, s_unicode)));
        Assert.Equal("sea\n", File.ReadAllText(Path.Combine(outDir, "sub", "deeper", "c.txt")));
    }

    [Fact]
    public void XorrisoReadsPlainIso9660OfOurImage()
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree, new IsoWriterOptions { Joliet = false, RockRidge = false, Level = 1 });
        string outDir = temp.Combine("plain-out");
        Tools.Run(Xorriso, ["-joliet", "off", "-rockridge", "off", "-osirrox", "on", "-indev", iso, "-extract", "/", outDir]);
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(outDir, "A.TXT")));
        Assert.Equal("bee\n", File.ReadAllText(Path.Combine(outDir, "SUB", "B.TXT")));
        Assert.Equal(ContainerVectors.Big(3_000_000), File.ReadAllBytes(Path.Combine(outDir, "BIG.BIN")));
    }

    [Fact]
    public void BsdtarListsAndExtractsOurImage()
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree);
        string listing = Tools.Run(BsdTar, ["-tvf", iso]);
        Assert.Contains("sub/deeper/c.txt", listing, StringComparison.Ordinal);
        Assert.Contains("link -> a.txt", listing, StringComparison.Ordinal);
        string outDir = temp.Combine("bsdtar-out");
        Directory.CreateDirectory(outDir);
        Tools.Run(BsdTar, ["-xf", iso, "-C", outDir]);
        CompareTrees(tree, outDir);
    }

    [Fact]
    public void BsdtarReadsTheJolietNamesWhenRockRidgeIsAbsent()
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree, new IsoWriterOptions { RockRidge = false });
        string outDir = temp.Combine("bsdtar-joliet-out");
        Directory.CreateDirectory(outDir);
        Tools.Run(BsdTar, ["-xf", iso, "-C", outDir]);
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(outDir, s_unicode)));
        Assert.Equal("sea\n", File.ReadAllText(Path.Combine(outDir, "sub", "deeper", "c.txt")));
        Assert.Equal(ContainerVectors.Big(3_000_000), File.ReadAllBytes(Path.Combine(outDir, "big.bin")));
    }

    [Fact]
    public void IsoinfoAcceptsOurJolietAndRockRidgeListings()
    {
        Skip();
        Assert.SkipWhen(IsoInfo is null, "set ONEARCHIVE_ISOINFO to run isoinfo checks");
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = MakeIso(temp, tree);
        string rr = Tools.Run(IsoInfo!, ["-R", "-l", "-i", iso]);
        Assert.Contains("run.sh", rr, StringComparison.Ordinal);
        string joliet = Tools.Run(IsoInfo!, ["-J", "-l", "-i", iso]);
        Assert.Contains("run.sh", joliet, StringComparison.Ordinal);
        string info = Tools.Run(IsoInfo!, ["-d", "-i", iso]);
        Assert.Contains("Rock Ridge", info, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WeReadWhatXorrisoWritesAtEveryLevel(int level)
    {
        Skip();
        using var temp = new TempDir();
        string tree = BuildTree(temp);
        string iso = temp.Combine("theirs.iso");
        Tools.Run(Xorriso, ["-as", "mkisofs", "-R", "-J", "-iso-level", level.ToString(System.Globalization.CultureInfo.InvariantCulture), "-no-pad", "-o", iso, tree]);
        string outDir = temp.Combine("our-out");
        Archive.Extract(iso, outDir, new ArchiveExtractOptions());
        CompareTrees(tree, outDir);
        using var reader = new IsoReader(File.OpenRead(iso));
        Assert.True(reader.Volume.HasRockRidge);
        Assert.Contains(reader.Items, static e => e.Name == "sub/deeper/c.txt");
    }
}
