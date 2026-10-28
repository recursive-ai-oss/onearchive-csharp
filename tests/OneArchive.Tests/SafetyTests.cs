using System.Runtime.Versioning;
using System.Text;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Hostile archives, built byte by byte by <see cref="RawTar"/>. Each rule has a case that must be refused with its
/// typed error and leave nothing outside the target directory, and a case showing the option that loosens it.
/// </summary>
public class SafetyTests
{
    private static ArchiveSafetyException Refused(byte[] tar, string rule, ArchiveExtractOptions? options = null, Action<string>? prepare = null)
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        Directory.CreateDirectory(target);
        File.WriteAllText(t.Combine("sentinel.txt"), "untouched");
        prepare?.Invoke(t.Root);
        string[] before = Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(target, StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(tar), target, options));
        Assert.Equal(rule, ex.Rule);
        Assert.Equal("tar", ex.Format);
        string[] after = Directory.GetFileSystemEntries(t.Root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(target, StringComparison.Ordinal)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.Equal(before, after);
        Assert.Equal("untouched", File.ReadAllText(t.Combine("sentinel.txt")));
        return ex;
    }

    private static byte[] One(string name, char type = '0', string? link = null, byte[]? data = null) =>
        new RawTar().Entry(name, type, data ?? "evil"u8.ToArray(), link).End();

    [Theory]
    [InlineData("../evil.txt", "path-traversal")]
    [InlineData("a/../../evil.txt", "path-traversal")]
    [InlineData("a/b/../../../evil.txt", "path-traversal")]
    [InlineData("..", "path-traversal")]
    [InlineData("..\\evil.txt", "path-traversal")]
    [InlineData("a\\..\\..\\evil.txt", "path-traversal")]
    [InlineData("/etc/evil.txt", "absolute-path")]
    [InlineData("//server/share/evil.txt", "absolute-path")]
    [InlineData("\\\\server\\share\\evil.txt", "absolute-path")]
    [InlineData("C:\\evil.txt", "absolute-path")]
    [InlineData("c:evil.txt", "absolute-path")]
    public void UnsafeNamesAreRefused(string name, string rule)
    {
        Refused(One(name), rule);
    }

    [Fact]
    public void NulInANameIsRefused()
    {
        byte[] tar = new RawTar().Pax(("path", "ok\0../../evil")).Entry("x", data: "a"u8.ToArray()).End();
        Refused(tar, "invalid-name");
    }

    [Fact]
    public void SanitizeKeepsEverythingInside()
    {
        using var t = new TempDir();
        byte[] tar = new RawTar()
            .Entry("../../up.txt", data: "1"u8.ToArray())
            .Entry("/abs/path.txt", data: "2"u8.ToArray())
            .Entry("C:\\drive\\x.txt", data: "3"u8.ToArray())
            .End();
        Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { UnsafePaths = UnsafePathHandling.Sanitize });
        Assert.Equal("1", File.ReadAllText(t.Combine("out/up.txt")));
        Assert.Equal("2", File.ReadAllText(t.Combine("out/abs/path.txt")));
        Assert.Equal("3", File.ReadAllText(t.Combine("out/drive/x.txt")));
        Assert.Equal(["out"], Directory.GetFileSystemEntries(t.Root).Select(Path.GetFileName).ToArray());
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/etc/passwd")]
    [InlineData("a/../../x")]
    [InlineData("b/..")]
    [InlineData("C:\\x")]
    public void SymlinksThatCouldEscapeAreRefused(string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        Refused(One("l", '2', target, []), "link-escape");
    }

    [Fact]
    public void EscapingSymlinksCanBeAllowedButNeverWrittenThrough()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("elsewhere"));
        byte[] tar = new RawTar()
            .Entry("escape", '2', link: "../elsewhere")
            .Entry("escape/file.txt", data: "boom"u8.ToArray())
            .End();
        var options = new ArchiveExtractOptions { Symlinks = LinkPolicy.Unsafe };
        var ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(tar), t.Combine("out"), options));
        Assert.Equal("link-write-through", ex.Rule);
        Assert.Empty(Directory.GetFileSystemEntries(t.Combine("elsewhere")));
        Assert.Equal("../elsewhere", new FileInfo(t.Combine("out/escape")).LinkTarget);
    }

    [Fact]
    public void WritingThroughASafeSymlinkIsRefusedByDefault()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        byte[] tar = new RawTar()
            .Entry("real/", '5')
            .Entry("l", '2', link: "real")
            .Entry("l/x.txt", data: "x"u8.ToArray())
            .End();
        Refused(tar, "link-write-through");

        using var t = new TempDir();
        Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { AllowWriteThroughSymlinks = true });
        Assert.Equal("x", File.ReadAllText(t.Combine("out/real/x.txt")));
    }

    [Fact]
    public void ADotDotAfterANameInASymlinkIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        // "b" points at "." so "b/.." is the directory above the target, although it looks like "." lexically.
        byte[] tar = new RawTar().Entry("b", '2', link: ".").Entry("a", '2', link: "b/..").End();
        Refused(tar, "link-escape");
    }

    [Fact]
    public void PreexistingLinkInTheTargetIsNotWrittenThrough()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        byte[] tar = One("link/file.txt");
        var ex = Refused(tar, "link-write-through", prepare: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            File.CreateSymbolicLink(Path.Combine(root, "out", "link"), Path.Combine(root, "outside"));
        });
        Assert.Equal("link/file.txt", ex.EntryName);
    }

    [Fact]
    public void ExistingFileLinkIsReplacedNotFollowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        using var t = new TempDir();
        string victim = t.Combine("victim.txt");
        File.WriteAllText(victim, "original");
        Directory.CreateDirectory(t.Combine("out"));
        File.CreateSymbolicLink(t.Combine("out/f.txt"), victim);
        byte[] tar = One("f.txt");
        Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(tar), t.Combine("out")));
        Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { Existing = ExistingFilePolicy.Overwrite });
        Assert.Equal("original", File.ReadAllText(victim));
        Assert.Null(new FileInfo(t.Combine("out/f.txt")).LinkTarget);
        Assert.Equal("evil", File.ReadAllText(t.Combine("out/f.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("missing.txt")]
    [InlineData("sub/../../outside.txt")]
    public void HardLinksMustNameAnExtractedFileInside(string target)
    {
        Refused(new RawTar().Entry("h", '1', link: target).End(), "hardlink-target");
    }

    [Fact]
    public void HardLinksAreExtractedAsCopies()
    {
        using var t = new TempDir();
        byte[] tar = new RawTar().Entry("orig.txt", data: "content"u8.ToArray()).Entry("sub/link.txt", '1', link: "orig.txt").End();
        Archive.Extract(new MemoryStream(tar), t.Combine("out"));
        Assert.Equal("content", File.ReadAllText(t.Combine("out/sub/link.txt")));
        Archive.Extract(new MemoryStream(tar), t.Combine("out2"), new ArchiveExtractOptions { HardLinks = LinkPolicy.Skip });
        Assert.False(File.Exists(t.Combine("out2/sub/link.txt")));
    }

    [Theory]
    [InlineData('3')]
    [InlineData('4')]
    [InlineData('6')]
    public void DevicesAndFifosAreRefusedOrSkipped(char type)
    {
        byte[] tar = new RawTar().Entry("node", type, data: []).Entry("after.txt", data: "a"u8.ToArray()).End();
        Refused(tar, "special-file");
        using var t = new TempDir();
        var r = Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { SpecialFiles = SpecialFilePolicy.Skip });
        Assert.Equal(1, r.EntriesSkipped);
        Assert.False(File.Exists(t.Combine("out/node")));
        Assert.True(File.Exists(t.Combine("out/after.txt")));
    }

    [Fact]
    public void DuplicatesAndExistingFilesFailUnlessAllowed()
    {
        byte[] tar = new RawTar().Entry("f", data: "one"u8.ToArray()).Entry("f", data: "two"u8.ToArray()).End();
        Refused(tar, "existing-file");
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(tar), t.Combine("a"), new ArchiveExtractOptions { Existing = ExistingFilePolicy.Overwrite });
        Assert.Equal("two", File.ReadAllText(t.Combine("a/f")));
        Archive.Extract(new MemoryStream(tar), t.Combine("b"), new ArchiveExtractOptions { Existing = ExistingFilePolicy.Skip });
        Assert.Equal("one", File.ReadAllText(t.Combine("b/f")));
    }

    [Fact]
    public void AFileCannotBecomeADirectoryOrBack()
    {
        Refused(new RawTar().Entry("x", data: "f"u8.ToArray()).Entry("x/y", data: "g"u8.ToArray()).End(), "existing-file");
        Refused(new RawTar().Entry("x/y", data: "g"u8.ToArray()).Entry("x", data: "f"u8.ToArray()).End(), "existing-file");
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void SpecialModeBitsAreDroppedByDefault()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix modes");
        using var t = new TempDir();
        byte[] tar = new RawTar().Entry("suid", mode: Convert.ToInt32("6755", 8), data: "x"u8.ToArray()).End();
        Archive.Extract(new MemoryStream(tar), t.Combine("a"));
        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), File.GetUnixFileMode(t.Combine("a/suid")));
        Archive.Extract(new MemoryStream(tar), t.Combine("b"), new ArchiveExtractOptions { KeepSpecialModeBits = true });
        Assert.True(File.GetUnixFileMode(t.Combine("b/suid")).HasFlag(UnixFileMode.SetUser));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void ReadOnlyDirectoriesDoNotBlockTheirContents()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "unix modes");
        using var t = new TempDir();
        byte[] tar = new RawTar().Entry("ro/", '5', mode: Convert.ToInt32("555", 8)).Entry("ro/f", data: "x"u8.ToArray()).End();
        Archive.Extract(new MemoryStream(tar), t.Combine("out"));
        Assert.Equal((UnixFileMode)Convert.ToInt32("555", 8), File.GetUnixFileMode(t.Combine("out/ro")));
        File.SetUnixFileMode(t.Combine("out/ro"), (UnixFileMode)Convert.ToInt32("755", 8));
    }

    [Fact]
    public void DotEntriesAndEmptyNames()
    {
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(new RawTar().Entry("./", '5').Entry("./a.txt", data: "a"u8.ToArray()).End()), t.Combine("out"));
        Assert.True(File.Exists(t.Combine("out/a.txt")));
        Refused(new RawTar().Entry(".", data: "x"u8.ToArray()).End(), "invalid-name");
    }

    // ---- bombs: every limit, each with a typed error ----

    private static CompressionLimitException Limited(byte[] tar, ArchiveLimits limits)
    {
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionLimitException>(() => Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { Limits = limits }));
        Assert.Equal("tar", ex.Format);
        return ex;
    }

    [Fact]
    public void EntryCountBomb()
    {
        var rt = new RawTar();
        for (int i = 0; i < 300; i++)
        {
            rt.Entry("d/" + i, '5');
        }

        Assert.Equal("MaxEntryCount", Limited(rt.End(), new ArchiveLimits { MaxEntryCount = 100 }).LimitName);
    }

    [Fact]
    public void TotalSizeBombFiresBeforeAnyDataIsRead()
    {
        // The header claims 2 GiB and the archive carries none of it: the limit is checked before the data is touched.
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionLimitException>(() => Archive.Extract(new MemoryStream(new RawTar().Entry("big", size: 2_000_000_000).End()), t.Combine("out")));
        Assert.Equal("MaxTotalSize", ex.LimitName);
        Assert.Equal(1L << 30, ex.Limit);
        Assert.Empty(Directory.GetFileSystemEntries(t.Combine("out")));
    }

    [Fact]
    public void SparseBombIsCountedAtItsFullSize()
    {
        byte[] h = RawTar.Header("hole", 'S', 0, gnu: true, seal: false);
        RawTar.Octal(h, 483, 12, 1L << 40);
        RawTar.Seal(h);
        Assert.Equal("MaxTotalSize", Limited(new RawTar().Raw(h).End(), ArchiveLimits.Default).LimitName);
    }

    [Fact]
    public void SparseRegionBomb()
    {
        var map = new StringBuilder("100000\n");
        for (int i = 0; i < 100_000; i++)
        {
            map.Append(i * 2).Append("\n1\n");
        }

        byte[] mapBytes = Encoding.ASCII.GetBytes(map.ToString());
        byte[] padded = new byte[(mapBytes.Length + 511) / 512 * 512];
        mapBytes.CopyTo(padded, 0);
        byte[] tar = new RawTar()
            .Pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"), ("GNU.sparse.name", "s"), ("GNU.sparse.realsize", "1000000"))
            .Entry("GNUSparseFile.0/s", data: [.. padded, .. new byte[100_000]]).End();
        using var reader = new TarReader(new MemoryStream(tar), new TarReaderOptions { MaxSparseRegions = 1000 });
        var ex = Assert.Throws<CompressionLimitException>(() => reader.GetNextEntry());
        Assert.Equal("MaxSparseRegions", ex.LimitName);
    }

    [Fact]
    public void PathLengthAndDepthBombs()
    {
        Assert.Equal("MaxPathLength", Limited(One(new string('a', 99)), new ArchiveLimits { MaxPathLength = 50 }).LimitName);
        Assert.Equal("MaxPathDepth", Limited(One("a/b/c/d/e/f"), new ArchiveLimits { MaxPathDepth = 3 }).LimitName);
    }

    [Fact]
    public void AnArchiveThatLiesAboutItsSizeIsTruncated()
    {
        // The header promises 10 MB, the archive ends after 100 bytes.
        byte[] tar = [.. RawTar.Header("liar", '0', 10_000_000), .. new byte[100]];
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionFormatException>(() => Archive.Extract(new MemoryStream(tar), t.Combine("out")));
        Assert.True(ex.IsTruncated);
        // A partly written file is not left looking like a result: the exception is the result.
    }

    [Fact]
    public void FilterSkipsEntries()
    {
        using var t = new TempDir();
        byte[] tar = new RawTar().Entry("keep.txt", data: "k"u8.ToArray()).Entry("drop.txt", data: "d"u8.ToArray()).End();
        var r = Archive.Extract(new MemoryStream(tar), t.Combine("out"), new ArchiveExtractOptions { Filter = e => e.Name.StartsWith("keep", StringComparison.Ordinal) });
        Assert.Equal(1, r.EntriesSkipped);
        Assert.True(File.Exists(t.Combine("out/keep.txt")));
        Assert.False(File.Exists(t.Combine("out/drop.txt")));
    }

    [Fact]
    public void BackslashesSeparateComponentsOnEveryPlatform()
    {
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(One("dir\\sub\\f.txt")), t.Combine("out"));
        Assert.True(File.Exists(t.Combine("out/dir/sub/f.txt")));
    }
}
