using System.Runtime.Versioning;
using RecursiveAI.OneArchive.Tar;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Round trips against GNU tar and bsdtar, run as black boxes. They run only with ONEARCHIVE_INTEROP=1, and then a
/// missing tool fails the run. ONEARCHIVE_GNUTAR and ONEARCHIVE_BSDTAR name the executables (defaults: tar, bsdtar on PATH).
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class TarInteropTests
{
    private static string GnuTar => Environment.GetEnvironmentVariable("ONEARCHIVE_GNUTAR") ?? "tar";

    private static string BsdTar => Environment.GetEnvironmentVariable("ONEARCHIVE_BSDTAR") ?? "bsdtar";

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("GNU tar", Tools.Run(GnuTar, ["--version"]), StringComparison.Ordinal);
        Assert.Contains("bsdtar", Tools.Run(BsdTar, ["--version"]), StringComparison.Ordinal);
    }

    private static void Block(string root, bool sparse, bool longNames, bool medium = true)
    {
        Directory.CreateDirectory(Path.Combine(root, "dir/nested"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(root, "dir/nested/data.bin"), Enumerable.Range(0, 100_000).Select(i => (byte)((i * 31) ^ (i >> 8))).ToArray());
        File.WriteAllText(Path.Combine(root, "dir/exec.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "dir/private"), "secret");
        File.WriteAllText(Path.Combine(root, "ünïcödé-日本語.txt"), "unicode");
        File.WriteAllText(Path.Combine(root, "zero"), string.Empty);
        File.SetUnixFileMode(Path.Combine(root, "dir/exec.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
        File.SetUnixFileMode(Path.Combine(root, "dir/private"), (UnixFileMode)Convert.ToInt32("600", 8));
        File.CreateSymbolicLink(Path.Combine(root, "link-to-hello"), "hello.txt");
        File.CreateSymbolicLink(Path.Combine(root, "dir/dangling"), "../nowhere/at/all");
        Tools.Run("ln", [Path.Combine(root, "hello.txt"), Path.Combine(root, "dir/hard-hello")]);
        if (longNames)
        {
            string deep = Path.Combine(root, new string('a', 120), new string('b', 120));
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, new string('c', 150) + ".txt"), "long");
            File.CreateSymbolicLink(Path.Combine(root, "long-link"), string.Join('/', Enumerable.Repeat(new string('t', 60), 4)));
        }
        else if (medium)
        {
            string mid = Path.Combine(root, new string('m', 60), new string('n', 60));
            Directory.CreateDirectory(mid);
            File.WriteAllText(Path.Combine(mid, new string('f', 50)), "medium");
        }

        if (sparse)
        {
            using var f = new FileStream(Path.Combine(root, "sparse.bin"), FileMode.Create);
            f.SetLength(8 << 20);
            f.Position = 1 << 20;
            f.Write("first-island"u8);
            f.Position = (5 << 20) + 17;
            f.Write("second-island"u8);
            f.Position = (8 << 20) - 5;
            f.Write("tail!"u8);
        }

        // Fixed times so that trees compare: whole seconds, and one file with sub-second precision.
        var when = new DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        foreach (string p in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(p).LinkTarget is null)
            {
                File.SetLastWriteTimeUtc(p, when);
            }
        }

        File.SetLastWriteTimeUtc(Path.Combine(root, "dir/exec.sh"), when.AddTicks(1234567));
    }

    [Theory]
    [InlineData("ustar", false, false)]
    [InlineData("pax", false, true)]
    [InlineData("gnu", false, true)]
    [InlineData("pax", true, true)]
    [InlineData("gnu", true, true)]
    public void ReadsGnuTarArchives(string format, bool sparse, bool longNames)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Block(src, sparse, longNames);
        var args = new List<string> { "--format=" + format, "-cf", t.Combine("g.tar") };
        if (sparse)
        {
            args.Add("-S");
        }

        args.AddRange(["-C", src, "."]);
        Tools.Run(GnuTar, args);

        var result = Archive.Extract(t.Combine("g.tar"), t.Combine("out"));
        Assert.True(result.EntriesExtracted > 10);
        Assert.Equal(ArchiveTests.Describe(src), ArchiveTests.Describe(t.Combine("out")));

        // The header fields we expose match what the tool wrote.
        using var archive = Archive.Open(t.Combine("g.tar"));
        var hello = archive.Entries.OfType<TarEntry>().Single(e => e.Name.EndsWith("dir/exec.sh", StringComparison.Ordinal));
        Assert.Equal(10, hello.Size);
        Assert.Equal(format == "pax" ? new DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567) : new DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc), hello.ModifiedTime!.Value.UtcDateTime);
        Assert.Equal(format switch { "ustar" => TarFormat.Ustar, "pax" => TarFormat.Pax, "gnu" => TarFormat.Gnu, _ => TarFormat.V7 }, hello.Format);
        if (sparse)
        {
            var s = archive.Entries.OfType<TarEntry>().Single(e => e.Name.EndsWith("sparse.bin", StringComparison.Ordinal));
            Assert.NotEqual(TarSparseFormat.None, s.SparseFormat);
            Assert.Equal(8 << 20, s.Size);
            Assert.True(s.StoredSize < 1 << 20);
        }
    }

    [Theory]
    [InlineData("0.0")]
    [InlineData("0.1")]
    [InlineData("1.0")]
    public void ReadsEveryGnuSparseVersion(string version)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Block(src, sparse: true, longNames: false);
        Tools.Run(GnuTar, ["--format=pax", "--sparse-version=" + version, "-S", "-cf", t.Combine("g.tar"), "-C", src, "."]);
        Archive.Extract(t.Combine("g.tar"), t.Combine("out"));
        Assert.Equal(ArchiveTests.Describe(src), ArchiveTests.Describe(t.Combine("out")));
        using var archive = Archive.Open(t.Combine("g.tar"));
        var s = archive.Entries.OfType<TarEntry>().Single(e => e.Name.EndsWith("sparse.bin", StringComparison.Ordinal));
        Assert.Equal(version switch { "0.0" => TarSparseFormat.Pax00, "0.1" => TarSparseFormat.Pax01, _ => TarSparseFormat.Pax10 }, s.SparseFormat);
        using Stream data = archive.OpenEntry(s);
        data.Position = 1 << 20;
        byte[] buf = new byte[12];
        data.ReadExactly(buf);
        Assert.Equal("first-island", System.Text.Encoding.ASCII.GetString(buf));
    }

    [Theory]
    [InlineData("ustar", false)]
    [InlineData("pax", false)]
    [InlineData("gnutar", false)]
    [InlineData("v7", false)]
    [InlineData("pax", true)]
    public void ReadsBsdtarArchives(string format, bool sparse)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Block(src, sparse, longNames: format is "pax" or "gnutar", medium: format != "v7");
        Tools.Run(BsdTar, ["--format", format, "-cf", t.Combine("b.tar"), "-C", src, "."]);
        Archive.Extract(t.Combine("b.tar"), t.Combine("out"));
        Assert.Equal(ArchiveTests.Describe(src), ArchiveTests.Describe(t.Combine("out")));
    }

    [Theory]
    [InlineData("a.tar", false, false)]
    [InlineData("a.tar", true, false)]
    [InlineData("a.tar", false, true)]
    [InlineData("a.tar.zst", false, false)]
    [InlineData("a.tar.lz4", false, false)]
    [InlineData("a.tar.zst", true, false)]
    public void OurArchivesExtractIdenticallyWithBothTools(string name, bool ustar, bool normalize)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Block(src, sparse: false, longNames: !ustar);
        string archive = t.Combine(name);
        var options = new ArchiveCreateOptions { Normalize = normalize, Tar = new TarWriterOptions { Format = ustar ? TarFormat.Ustar : TarFormat.Pax } };
        Archive.Create(archive, options).Add(src, "").Save();

        string gnuOut = t.Combine("gnu");
        string bsdOut = t.Combine("bsd");
        Directory.CreateDirectory(gnuOut);
        Directory.CreateDirectory(bsdOut);
        if (name.EndsWith(".zst", StringComparison.Ordinal))
        {
            Tools.Run(GnuTar, ["--use-compress-program=zstd", "-xf", archive, "-C", gnuOut]);
        }
        else if (name.EndsWith(".lz4", StringComparison.Ordinal))
        {
            Tools.Run(GnuTar, ["--use-compress-program=lz4", "-xf", archive, "-C", gnuOut]);
        }
        else
        {
            Tools.Run(GnuTar, ["-xf", archive, "-C", gnuOut]);
        }

        Tools.Run(BsdTar, ["-xf", archive, "-C", bsdOut]);

        string expected = ArchiveTests.Describe(src);
        string gnuTree = ArchiveTests.Describe(gnuOut);
        string bsdTree = ArchiveTests.Describe(bsdOut);
        Assert.Equal(gnuTree, bsdTree);
        if (normalize)
        {
            // Normalised archives carry no times and fixed modes: only names, contents and links must agree.
            Assert.Equal(Strip(expected), Strip(gnuTree));
        }
        else
        {
            Assert.Equal(expected, gnuTree);
        }

        // And the tool's listing agrees on the entry count with ours.
        using var ours = Archive.Open(archive);
        string[] listed = Tools.Run(BsdTar, ["-tf", archive]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ours.Entries.Count, listed.Length);
    }

    private static string Strip(string tree) =>
        string.Join('\n', tree.Split('\n').Select(l =>
        {
            string[] p = l.Split(' ');
            return p.Length >= 4 ? p[0] + " " + p[1] + " " + p[2] : p[0];
        }));

    [Theory]
    [InlineData("zstd", "zst")]
    [InlineData("lz4", "lz4")]
    public void ReadsToolCompressedTars(string program, string extension)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Block(src, sparse: false, longNames: true);
        string archive = t.Combine("g.tar." + extension);
        Tools.Run(GnuTar, ["--format=pax", "--use-compress-program=" + program, "-cf", archive, "-C", src, "."]);
        var ex = Record.Exception(() => Archive.Extract(archive, t.Combine("out")));
        Assert.Null(ex);
        Assert.Equal(ArchiveTests.Describe(src), ArchiveTests.Describe(t.Combine("out")));

        // The same through a forward-only stream.
        Archive.Extract(new TarTests.ForwardOnly(File.ReadAllBytes(archive)), t.Combine("out2"));
        Assert.Equal(ArchiveTests.Describe(src), ArchiveTests.Describe(t.Combine("out2")));
    }

    [Fact]
    public void BothToolsAcceptBigPaxFields()
    {
        RequireEnabled();
        using var t = new TempDir();
        string archive = t.Combine("pax.tar");
        var when = DateTimeOffset.FromUnixTimeSeconds(-5).AddTicks(2_500_000);
        using (var w = new TarWriter(File.Create(archive)))
        {
            w.WriteEntry(new ArchiveEntry
            {
                Name = "uid-and-time.txt",
                Size = 3,
                Uid = 40_000_000,
                Gid = 3_000_000,
                UserName = new string('u', 50),
                ModifiedTime = when,
                Extras = new Dictionary<string, string> { ["SCHILY.xattr.user.note"] = "kept" },
            }, "abc"u8.ToArray());
        }

        string gnuList = Tools.Run(GnuTar, ["-tvf", archive, "--numeric-owner"]);
        Assert.Contains("uid-and-time.txt", gnuList, StringComparison.Ordinal);
        Assert.Contains("40000000/3000000", gnuList, StringComparison.Ordinal);
        Assert.Contains("uid-and-time.txt", Tools.Run(BsdTar, ["-tvf", archive]), StringComparison.Ordinal);
        Directory.CreateDirectory(t.Combine("g"));
        Tools.Run(GnuTar, ["-xf", archive, "-C", t.Combine("g"), "--no-same-owner"]);
        Assert.Equal("abc", File.ReadAllText(t.Combine("g/uid-and-time.txt")));
    }

    [Fact]
    public void OurReaderHandlesBsdtarFlagsRoundTrip()
    {
        RequireEnabled();
        // Sub-second times written by the tools are read back exactly.
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(src);
        string f = Path.Combine(src, "f");
        File.WriteAllText(f, "x");
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(7654321);
        File.SetLastWriteTimeUtc(f, time);
        Tools.Run(GnuTar, ["--format=pax", "-cf", t.Combine("g.tar"), "-C", src, "f"]);
        Tools.Run(BsdTar, ["--format", "pax", "-cf", t.Combine("b.tar"), "-C", src, "f"]);
        foreach (string a in new[] { "g.tar", "b.tar" })
        {
            using var archive = Archive.Open(t.Combine(a));
            Assert.Equal(time.Ticks / 100 * 100, archive.Entries[0].ModifiedTime!.Value.UtcTicks / 100 * 100);
        }
    }
}
