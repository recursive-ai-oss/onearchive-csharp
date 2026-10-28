using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Xz;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>The xz and lzip layers: single files, tarballs, detection, limits, and the reference tools reading and writing them.</summary>
public class XzLzipArchiveTests
{
    private static string MakeTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllBytes(Path.Combine(root, "a.txt"), ContainerVectors.Big(60_000));
        File.WriteAllBytes(Path.Combine(root, "sub/b.bin"), ContainerVectors.Big(3000));
        File.WriteAllBytes(Path.Combine(root, "empty"), []);
        return root;
    }

    private static void AssertSameTree(string expected, string actual)
    {
        foreach (string file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(actual, Path.GetRelativePath(expected, file))));
        }
    }

    [Fact]
    public void DetectionByMagicAndName()
    {
        Assert.Equal(ArchiveCompression.Lzip, ArchiveDetector.DetectCompression("LZIP\x01\x0c"u8));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Xz), ArchiveDetector.FromFileName("a.tar.xz"));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Xz), ArchiveDetector.FromFileName("a.TXZ"));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Lzip), ArchiveDetector.FromFileName("a.tar.lz"));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Lzip), ArchiveDetector.FromFileName("a.tlz"));
        Assert.Equal((ArchiveFormat.Unknown, ArchiveCompression.Lzip), ArchiveDetector.FromFileName("a.lz"));
        Assert.Equal((ArchiveFormat.Unknown, ArchiveCompression.Xz), ArchiveDetector.FromFileName("a.xz"));
    }

    [Theory]
    [InlineData("x.tar.xz", ArchiveCompression.Xz)]
    [InlineData("x.txz", ArchiveCompression.Xz)]
    [InlineData("x.tar.lz", ArchiveCompression.Lzip)]
    [InlineData("x.tlz", ArchiveCompression.Lzip)]
    public void TarballRoundTrip(string name, ArchiveCompression compression)
    {
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        string path = temp.Combine(name);
        Archive.Create(path).Add(tree, "tree").Save();

        byte[] packed = File.ReadAllBytes(path);
        Assert.Equal(compression, ArchiveDetector.DetectCompression(packed));

        using (Archive archive = Archive.Open(path))
        {
            Assert.Equal(ArchiveFormat.Tar, archive.Format);
            Assert.Equal(compression, archive.Compression);
            Assert.Contains(archive.Entries, e => e.Name == "tree/sub/b.bin");
        }

        Archive.Extract(path, temp.Combine("o"));
        AssertSameTree(tree, temp.Combine("o/tree"));

        // Detected from content when the name says nothing.
        string renamed = temp.Combine("blob.bin");
        File.WriteAllBytes(renamed, packed);
        Archive.Extract(renamed, temp.Combine("o2"));
        AssertSameTree(tree, temp.Combine("o2/tree"));

        // Same inputs, same bytes.
        string again = temp.Combine("again-" + name);
        Archive.Create(again).Add(tree, "tree").Save();
        Assert.Equal(packed, File.ReadAllBytes(again));
    }

    [Theory]
    [InlineData("report.txt.xz", ArchiveCompression.Xz)]
    [InlineData("report.txt.lz", ArchiveCompression.Lzip)]
    public void SingleFileRoundTrip(string name, ArchiveCompression compression)
    {
        using var temp = new TempDir();
        string src = temp.Combine("report.txt");
        byte[] data = ContainerVectors.Big(40_000);
        File.WriteAllBytes(src, data);
        string path = temp.Combine(name);
        Archive.Create(path).Add(src).Save();

        using (Archive archive = Archive.Open(path))
        {
            Assert.Equal(ArchiveFormat.SingleStream, archive.Format);
            Assert.Equal(compression, archive.Compression);
            Assert.Equal("report.txt", Assert.Single(archive.Entries).Name);
        }

        Archive.Extract(path, temp.Combine("o"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("o/report.txt")));
    }

    [Fact]
    public void ReadsStreamsMadeByTheCodecs()
    {
        byte[] tar = new RawTar().Entry("f.txt").End();
        foreach (byte[] packed in new[] { Xz.Compress(tar), Lzip.Compress(tar) })
        {
            using Archive archive = Archive.Open(new MemoryStream(packed));
            Assert.Equal(ArchiveFormat.Tar, archive.Format);
            Assert.Equal("f.txt", Assert.Single(archive.Entries).Name);
        }
    }

    [Fact]
    public void DictionaryAboveTheWindowLimitIsRefused()
    {
        byte[] data = ContainerVectors.Big(20_000);
        byte[] xz = Xz.Compress(data, new XzCompressionOptions { Lzma = new RecursiveAI.OneCompress.Lzma.LzmaOptions { DictionarySize = 1 << 24 } });
        byte[] lz = Lzip.Compress(data, new LzipOptions { DictionarySize = 1 << 24 });
        var options = new ArchiveOpenOptions { Limits = new ArchiveLimits { MaxWindowSize = 1 << 20 } };
        foreach ((byte[] packed, string name) in new[] { (xz, "a.xz"), (lz, "a.lz") })
        {
            Assert.ThrowsAny<RecursiveAI.OneCompress.CompressionException>(() =>
            {
                using Archive archive = Archive.Open(new MemoryStream(packed), new ArchiveOpenOptions { FileName = name, Limits = options.Limits });
                using Stream s = archive.OpenEntry(archive.Entries[0]);
                s.CopyTo(Stream.Null);
            });
        }
    }

    [Fact]
    public void TruncatedStreamIsATypedError()
    {
        byte[] packed = Xz.Compress(ContainerVectors.Big(30_000));
        Assert.ThrowsAny<RecursiveAI.OneCompress.CompressionException>(() =>
        {
            using Archive archive = Archive.Open(new MemoryStream(packed, 0, packed.Length / 2), new ArchiveOpenOptions { FileName = "a.xz" });
            using Stream s = archive.OpenEntry(archive.Entries[0]);
            s.CopyTo(Stream.Null);
        });
    }

    private static string Tool(string env, string fallback) => Environment.GetEnvironmentVariable(env) ?? fallback;

    [Fact]
    [Trait("Category", "Interop")]
    public void XzTool_ReadsOursAndWeReadItsFiles()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string xz = Tool("ONEARCHIVE_XZ", "xz");
        string tar = Tool("ONEARCHIVE_GNUTAR", "tar");
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));

        Archive.Create(temp.Combine("ours.tar.xz")).Add(tree, "tree").Save();
        Tools.Run(xz, ["-t", temp.Combine("ours.tar.xz")]);
        Directory.CreateDirectory(temp.Combine("a"));
        Tools.Run(tar, ["-xJf", temp.Combine("ours.tar.xz"), "-C", temp.Combine("a"), "--use-compress-program", xz]);
        AssertSameTree(tree, temp.Combine("a/tree"));

        Tools.Run(tar, ["-cf", temp.Combine("theirs.tar"), "-C", temp.Combine("."), "tree"]);
        Tools.Run(xz, ["-k", "-6", temp.Combine("theirs.tar")]);
        Archive.Extract(temp.Combine("theirs.tar.xz"), temp.Combine("b"));
        AssertSameTree(tree, temp.Combine("b/tree"));

        string one = temp.Combine("one.txt");
        File.WriteAllBytes(one, ContainerVectors.Big(70_000));
        Archive.Create(temp.Combine("one.txt.xz")).Add(one).Save();
        File.Move(one, temp.Combine("one.orig"));
        Tools.Run(xz, ["-d", temp.Combine("one.txt.xz")]);
        Assert.Equal(ContainerVectors.Big(70_000), File.ReadAllBytes(one));
    }

    [Fact]
    [Trait("Category", "Interop")]
    public void LzipTool_ReadsOursAndWeReadItsFiles()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string lzip = Tool("ONEARCHIVE_LZIP", "lzip");
        string tar = Tool("ONEARCHIVE_GNUTAR", "tar");
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));

        Archive.Create(temp.Combine("ours.tar.lz")).Add(tree, "tree").Save();
        Tools.Run(lzip, ["-t", temp.Combine("ours.tar.lz")]);
        Directory.CreateDirectory(temp.Combine("a"));
        Tools.Run(tar, ["-xf", temp.Combine("ours.tar.lz"), "-C", temp.Combine("a"), "--use-compress-program", lzip]);
        AssertSameTree(tree, temp.Combine("a/tree"));

        Tools.Run(tar, ["-cf", temp.Combine("theirs.tar"), "-C", temp.Combine("."), "tree"]);
        Tools.Run(lzip, ["-k", "-6", temp.Combine("theirs.tar")]);
        Archive.Extract(temp.Combine("theirs.tar.lz"), temp.Combine("b"));
        AssertSameTree(tree, temp.Combine("b/tree"));
    }

    [Fact]
    [Trait("Category", "Interop")]
    public void Bsdtar_ReadsOurTarXz()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string bsdtar = Tool("ONEARCHIVE_BSDTAR", "bsdtar");
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        Archive.Create(temp.Combine("ours.tar.xz")).Add(tree, "tree").Save();
        Directory.CreateDirectory(temp.Combine("a"));
        Tools.Run(bsdtar, ["-xf", temp.Combine("ours.tar.xz"), "-C", temp.Combine("a")]);
        AssertSameTree(tree, temp.Combine("a/tree"));
    }
}
