using System.Runtime.Versioning;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cpio;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Round trips against GNU cpio, GNU ar and bsdtar, run as black boxes. They run only with ONEARCHIVE_INTEROP=1, and then a
/// missing tool fails the run. ONEARCHIVE_CPIO, ONEARCHIVE_AR and ONEARCHIVE_BSDTAR name the executables (defaults: cpio, ar, bsdtar on PATH).
/// ONEARCHIVE_DEB names a .deb package to read; without it the first package in /var/cache/apt/archives is used if there is one.
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class CpioArInteropTests
{
    private static string Cpio => Environment.GetEnvironmentVariable("ONEARCHIVE_CPIO") ?? "cpio";

    private static string GnuAr => Environment.GetEnvironmentVariable("ONEARCHIVE_AR") ?? "ar";

    private static string BsdTar => Environment.GetEnvironmentVariable("ONEARCHIVE_BSDTAR") ?? "bsdtar";

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("GNU cpio", Tools.Run(Cpio, ["--version"]), StringComparison.Ordinal);
        Assert.Contains("GNU ar", Tools.Run(GnuAr, ["--version"]), StringComparison.Ordinal);
        Assert.Contains("bsdtar", Tools.Run(BsdTar, ["--version"]), StringComparison.Ordinal);
    }

    private static readonly DateTimeOffset s_time = DateTimeOffset.FromUnixTimeSeconds(1700000000);

    private static byte[] Data(int n) => ContainerVectors.Big(n);

    private static void WriteTree(ArchiveWriter w, bool links)
    {
        ArchiveEntry E(string name, ArchiveEntryKind kind = ArchiveEntryKind.File, int size = 0, int mode = 0x1A4, string? link = null) =>
            new() { Name = name, Kind = kind, Size = size, Mode = (UnixFileMode)mode, ModifiedTime = s_time, LinkTarget = link };

        w.WriteEntry(E("dir", ArchiveEntryKind.Directory, mode: 0x1ED));
        w.WriteEntry(E("dir/a.txt", size: 6), "hello\n"u8.ToArray());
        w.WriteEntry(E("dir/big.bin", size: 100_000, mode: 0x180), Data(100_000));
        w.WriteEntry(E("dir/empty"));
        w.WriteEntry(E("dir/exec.sh", size: 10, mode: 0x1ED), "#!/bin/sh\n"u8.ToArray());
        w.WriteEntry(E("dir/sl", ArchiveEntryKind.Symlink, mode: 0x1FF, link: "a.txt"));
        w.WriteEntry(E("dir/\u00fcn\u00ef-\u65e5\u672c.txt", size: 7), "unicode"u8.ToArray());
        w.WriteEntry(E("dir/" + new string('n', 150), size: 3), "123"u8.ToArray());
        if (links && w is CpioWriter)
        {
            w.WriteEntry(new CpioEntry { Name = "dir/link1", Inode = 77, NLink = 2, Mode = (UnixFileMode)0x1A4, ModifiedTime = s_time });
            w.WriteEntry(new CpioEntry { Name = "dir/link2", Inode = 77, NLink = 2, Size = 4, Mode = (UnixFileMode)0x1A4, ModifiedTime = s_time }, "link"u8.ToArray());
        }
    }

    private static void CheckTree(string root, bool links, bool checkTimes)
    {
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(root, "dir/a.txt")));
        Assert.Equal(Data(100_000), File.ReadAllBytes(Path.Combine(root, "dir/big.bin")));
        Assert.Empty(File.ReadAllBytes(Path.Combine(root, "dir/empty")));
        Assert.Equal("unicode", File.ReadAllText(Path.Combine(root, "dir/\u00fcn\u00ef-\u65e5\u672c.txt")));
        Assert.Equal("123", File.ReadAllText(Path.Combine(root, "dir/" + new string('n', 150))));
        Assert.Equal("a.txt", new FileInfo(Path.Combine(root, "dir/sl")).LinkTarget);
        Assert.Equal(0x180, (int)File.GetUnixFileMode(Path.Combine(root, "dir/big.bin")) & 0x1FF);
        Assert.Equal(0x1ED, (int)File.GetUnixFileMode(Path.Combine(root, "dir/exec.sh")) & 0x1FF);
        if (checkTimes)
        {
            Assert.Equal(s_time, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(root, "dir/a.txt"))));
        }

        if (links)
        {
            Assert.Equal("link", File.ReadAllText(Path.Combine(root, "dir/link1")));
            Assert.Equal("link", File.ReadAllText(Path.Combine(root, "dir/link2")));
        }
    }

    [Theory]
    [InlineData("newc")]
    [InlineData("crc")]
    [InlineData("odc")]
    public void GnuCpioAndBsdtarReadOurCpio(string format)
    {
        RequireEnabled();
        using var t = new TempDir();
        string file = t.Combine("ours.cpio");
        using (var fs = File.Create(file))
        using (var w = new CpioWriter(fs, new CpioWriterOptions { Format = Enum.Parse<CpioFormat>(format, true) }))
        {
            WriteTree(w, links: format != "odc");
        }

        bool links = format != "odc";
        string gnu = t.Combine("gnu");
        Directory.CreateDirectory(gnu);
        Tools.Run("sh", ["-c", $"'{Cpio}' -i -d -m --quiet < '{file}'"], gnu);
        CheckTree(gnu, links, checkTimes: true);
        if (links)
        {
            // GNU cpio makes the two names one file.
            Assert.Equal(Tools.Run("stat", ["-c", "%i", Path.Combine(gnu, "dir/link1")]), Tools.Run("stat", ["-c", "%i", Path.Combine(gnu, "dir/link2")]));
        }

        string listing = Tools.Run(Cpio, ["-t", "--quiet", "-I", file]);
        Assert.Contains("dir/a.txt", listing, StringComparison.Ordinal);
        Assert.Contains("dir/" + new string('n', 150), listing, StringComparison.Ordinal);

        string bsd = t.Combine("bsd");
        Directory.CreateDirectory(bsd);
        Tools.Run(BsdTar, ["-xf", file, "-C", bsd]);
        CheckTree(bsd, links: false, checkTimes: true);
        if (links)
        {
            Assert.Equal("link", File.ReadAllText(Path.Combine(bsd, "dir/link2")));
        }
    }

    [Theory]
    [InlineData("newc")]
    [InlineData("crc")]
    [InlineData("odc")]
    public void WeReadWhatGnuCpioWrites(string format)
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "d/e"));
        File.WriteAllBytes(Path.Combine(src, "d/e/data.bin"), Data(300_000));
        File.WriteAllText(Path.Combine(src, "d/text"), "text");
        File.CreateSymbolicLink(Path.Combine(src, "d/sym"), "text");
        Tools.Run("ln", [Path.Combine(src, "d/text"), Path.Combine(src, "d/text2")]);
        string list = Tools.Run("find", ["d"], src);
        string archive = t.Combine("theirs.cpio");
        Tools.Run("sh", ["-c", $"find d | sort | '{Cpio}' -o -H {format} --quiet > '{archive}'"], src);
        _ = list;
        string dest = t.Combine("out");
        Archive.Extract(archive, dest);
        Assert.Equal(Data(300_000), File.ReadAllBytes(Path.Combine(dest, "d/e/data.bin")));
        Assert.Equal("text", File.ReadAllText(Path.Combine(dest, "d/text")));
        Assert.Equal("text", File.ReadAllText(Path.Combine(dest, "d/text2")));
        Assert.Equal("text", new FileInfo(Path.Combine(dest, "d/sym")).LinkTarget);
    }

    [Theory]
    [InlineData(ArFormat.Gnu)]
    [InlineData(ArFormat.Bsd)]
    public void GnuArAndBsdtarReadOurAr(ArFormat format)
    {
        RequireEnabled();
        using var t = new TempDir();
        string file = t.Combine("ours.a");
        string[] names = ["short.txt", "a-name-that-needs-a-long-name-entry.txt", "sp ace.txt", "empty", "odd-size"];
        byte[][] data = [Data(2000), Data(1001), "x"u8.ToArray(), [], "123"u8.ToArray()];
        using (var fs = File.Create(file))
        using (var w = new ArWriter(fs, new ArWriterOptions { Format = format }))
        {
            for (int i = 0; i < names.Length; i++)
            {
                w.WriteEntry(new ArchiveEntry { Name = names[i], Size = data[i].Length, Mode = (UnixFileMode)0x1A4 }, data[i]);
            }
        }

        string listed = Tools.Run(GnuAr, ["t", file]);
        if (format == ArFormat.Gnu)
        {
            // GNU ar does not take BSD "#1/" names with a space in them the same way on every build; only the GNU layout is required to list.
            Assert.Equal(names, listed.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }

        string bsdList = Tools.Run(BsdTar, ["-tf", file]);
        foreach (string n in names)
        {
            Assert.Contains(n, bsdList, StringComparison.Ordinal);
        }

        string bsd = t.Combine("bsd");
        Directory.CreateDirectory(bsd);
        // bsdtar hands out the GNU "//" long-name table as a member too and then complains about its name.
        Tools.Run(BsdTar, ["-xf", file, "-C", bsd], failOnError: format != ArFormat.Gnu);
        for (int i = 0; i < names.Length; i++)
        {
            Assert.Equal(data[i], File.ReadAllBytes(Path.Combine(bsd, names[i])));
        }

        if (format == ArFormat.Gnu)
        {
            string gnu = t.Combine("gnu");
            Directory.CreateDirectory(gnu);
            Tools.Run(GnuAr, ["x", file], gnu);
            for (int i = 0; i < names.Length; i++)
            {
                Assert.Equal(data[i], File.ReadAllBytes(Path.Combine(gnu, names[i])));
            }
        }
    }

    [Fact]
    public void WeReadWhatGnuArAndBsdtarWrite()
    {
        RequireEnabled();
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(src);
        string[] names = ["a", "twelve-chars!", "fifteen-chars-!", "sixteen-chars-ok", "seventeen-chars-ok", "a long name with spaces.bin"];
        for (int i = 0; i < names.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(src, names[i]), Data(i * 777 + 1));
        }

        string gnu = t.Combine("gnu.a");
        Tools.Run(GnuAr, ["rc", gnu, .. names], src);
        string bsd = t.Combine("bsd.a");
        Tools.Run(BsdTar, ["--format", "arbsd", "-cf", bsd, .. names], src);
        foreach (string archive in new[] { gnu, bsd })
        {
            using var reader = new ArReader(File.OpenRead(archive));
            int i = 0;
            while (reader.GetNextEntry() is { } e)
            {
                Assert.Equal(names[i], e.Name);
                using var ms = new MemoryStream();
                using (Stream s = reader.OpenEntryStream())
                {
                    s.CopyTo(ms);
                }

                Assert.Equal(Data(i * 777 + 1), ms.ToArray());
                i++;
            }

            Assert.Equal(names.Length, i);
        }
    }

    [Fact]
    public void ARealDebPackageListsAndExtracts()
    {
        RequireEnabled();
        string? deb = Environment.GetEnvironmentVariable("ONEARCHIVE_DEB");
        if (deb is null && Directory.Exists("/var/cache/apt/archives"))
        {
            deb = Directory.EnumerateFiles("/var/cache/apt/archives", "*.deb").Order(StringComparer.Ordinal).FirstOrDefault();
        }

        Assert.SkipWhen(deb is null, "no .deb package available");
        string[] expected = Tools.Run(GnuAr, ["t", deb!]).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var archive = Archive.Open(deb!);
        Assert.Equal(ArchiveFormat.Ar, archive.Format);
        Assert.Equal(expected, archive.Entries.Select(e => e.Name));
        foreach (ArchiveEntry e in archive.Entries)
        {
            using var ms = new MemoryStream();
            using (Stream s = archive.OpenEntry(e))
            {
                s.CopyTo(ms);
            }

            Assert.Equal(Tools.Run("sh", ["-c", $"'{GnuAr}' p '{deb}' '{e.Name}' | sha256sum"]).Split(' ')[0], Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray())));
        }
    }
}
