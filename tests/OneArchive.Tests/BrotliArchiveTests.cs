using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Brotli;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// The brotli layer: single files, tarballs, name-only detection (RFC 7932 section 9.1 defines no magic number), limits, and the
/// brotli tool reading and writing it.
/// </summary>
public class BrotliArchiveTests
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

    [Theory]
    [InlineData("a.br", ArchiveFormat.Unknown)]
    [InlineData("A.BR", ArchiveFormat.Unknown)]
    [InlineData("dir/a.txt.br", ArchiveFormat.Unknown)]
    [InlineData("a.tar.br", ArchiveFormat.Tar)]
    [InlineData("a.tbr", ArchiveFormat.Tar)]
    [InlineData("a.cpio.br", ArchiveFormat.Cpio)]
    public void NamesSelectBrotli(string name, ArchiveFormat format) =>
        Assert.Equal((format, ArchiveCompression.Brotli), ArchiveDetector.FromFileName(name));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.tar")]
    [InlineData("a.brx")]
    [InlineData("abr")]
    [InlineData("a.br.txt")]
    [InlineData("a.tar.gz")]
    [InlineData("a.zip")]
    [InlineData("a.bar")]
    public void OtherNamesNeverSelectBrotli(string name) =>
        Assert.NotEqual(ArchiveCompression.Brotli, ArchiveDetector.FromFileName(name).Compression);

    [Fact]
    public void ContentAloneNeverSelectsBrotli()
    {
        // A real brotli stream, and assorted other bytes: magic detection never answers Brotli.
        byte[] stream = BR.Compress(ContainerVectors.Big(5000), new BrotliOptions { Quality = 5 });
        Assert.Equal(ArchiveCompression.None, ArchiveDetector.DetectCompression(stream));
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] junk = new byte[64];
            random.NextBytes(junk);
            Assert.NotEqual(ArchiveCompression.Brotli, ArchiveDetector.DetectCompression(junk));
        }

        // Without a name the stream is not an archive, and with a non-brotli name it is not read as one.
        Assert.ThrowsAny<CompressionException>(() => Archive.Open(new MemoryStream(stream)));
        Assert.ThrowsAny<CompressionException>(() => Archive.Open(new MemoryStream(stream), new ArchiveOpenOptions { FileName = "a.dat" }));
        using Archive named = Archive.Open(new MemoryStream(stream), new ArchiveOpenOptions { FileName = "a.br" });
        Assert.Equal(ArchiveCompression.Brotli, named.Compression);
    }

    [Fact]
    public void UnknownFileWithBrotliNameButContainerBytesIsReadAsTheContainer()
    {
        // Magic wins over the name: a zip called .br is a zip.
        using var temp = new TempDir();
        string zip = temp.Combine("a.zip");
        Archive.Create(zip).Add(new ArchiveEntry { Name = "f.txt", Size = 3 }, "abc"u8.ToArray()).Save();
        string renamed = temp.Combine("a.br");
        File.Copy(zip, renamed);
        using Archive archive = Archive.Open(renamed);
        Assert.Equal(ArchiveFormat.Zip, archive.Format);
        Assert.Equal(ArchiveCompression.None, archive.Compression);
    }

    [Theory]
    [InlineData("x.tar.br")]
    [InlineData("x.tbr")]
    public void TarballRoundTrip(string name)
    {
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        string path = temp.Combine(name);
        Archive.Create(path).Add(tree, "tree").Save();

        using (Archive archive = Archive.Open(path))
        {
            Assert.Equal(ArchiveFormat.Tar, archive.Format);
            Assert.Equal(ArchiveCompression.Brotli, archive.Compression);
            Assert.Contains(archive.Entries, e => e.Name == "tree/sub/b.bin");
        }

        Archive.Extract(path, temp.Combine("o"));
        AssertSameTree(tree, temp.Combine("o/tree"));

        string again = temp.Combine("again-" + name);
        Archive.Create(again).Add(tree, "tree").Save();
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(again));
    }

    [Fact]
    public void SingleFileRoundTripAndLevel()
    {
        using var temp = new TempDir();
        string src = temp.Combine("report.txt");
        byte[] data = ContainerVectors.Big(40_000);
        File.WriteAllBytes(src, data);
        string path = temp.Combine("report.txt.br");
        Archive.Create(path).Add(src).Save();

        using (Archive archive = Archive.Open(path))
        {
            Assert.Equal(ArchiveFormat.SingleStream, archive.Format);
            Assert.Equal(ArchiveCompression.Brotli, archive.Compression);
            Assert.Equal("report.txt", Assert.Single(archive.Entries).Name);
        }

        Archive.Extract(path, temp.Combine("o"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("o/report.txt")));

        // The default is quality 5 at a 4 MiB window.
        Assert.Equal(BR.Compress(data, new BrotliOptions { Quality = 5, WindowBits = 22 }), File.ReadAllBytes(path));
    }

    [Fact]
    public void ExplicitCompressionOptionNeedsNoName()
    {
        byte[] data = ContainerVectors.Big(10_000);
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { Compression = ArchiveCompression.Brotli, Format = ArchiveFormat.SingleStream, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "d", Size = data.Length }, data).Save();
        Assert.Equal(data, BR.Decompress(ms.ToArray()));
        using Archive archive = Archive.Open(new MemoryStream(ms.ToArray()), new ArchiveOpenOptions { Compression = ArchiveCompression.Brotli });
        Assert.Equal(ArchiveFormat.SingleStream, archive.Format);
    }

    [Fact]
    public void ExpansionRatioStopsABrotliBomb()
    {
        byte[] bomb = BR.Compress(new byte[64 << 20], new BrotliOptions { Quality = 1 });
        Assert.True(bomb.Length < 64 << 10);
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(bomb), t.Combine("out"), new ArchiveExtractOptions { FileName = "bomb.br" }));
        Assert.Equal("MaxExpansionRatio", ex.LimitName);

        var ok = Archive.Extract(new MemoryStream(bomb), t.Combine("out2"),
            new ArchiveExtractOptions { FileName = "bomb.br", Limits = new ArchiveLimits { MaxExpansionRatio = 0 } });
        Assert.Equal(64 << 20, ok.BytesWritten);
    }

    [Fact]
    public void TotalSizeLimitAppliesToBrotli()
    {
        byte[] packed = BR.Compress(ContainerVectors.Big(200_000), new BrotliOptions { Quality = 1 });
        using var t = new TempDir();
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(packed), t.Combine("out"),
                new ArchiveExtractOptions { FileName = "a.br", Limits = new ArchiveLimits { MaxTotalSize = 1000, MaxExpansionRatio = 0 } }));
    }

    [Fact]
    public void WindowAboveTheLimitIsRefused()
    {
        byte[] packed = BR.Compress(ContainerVectors.Big(20_000), new BrotliOptions { Quality = 1, WindowBits = 24 });
        using var t = new TempDir();
        Assert.ThrowsAny<CompressionException>(() =>
            Archive.Extract(new MemoryStream(packed), t.Combine("out"),
                new ArchiveExtractOptions { FileName = "a.br", Limits = new ArchiveLimits { MaxWindowSize = 1 << 20 } }));
    }

    [Fact]
    public void TruncatedStreamIsATypedError()
    {
        byte[] packed = BR.Compress(ContainerVectors.Big(30_000), new BrotliOptions { Quality = 5 });
        using var t = new TempDir();
        foreach (int cut in new[] { 1, 5, packed.Length / 2, packed.Length - 1 })
        {
            Assert.ThrowsAny<CompressionException>(() =>
                Archive.Extract(new MemoryStream(packed[..cut]), t.Combine("o" + cut), new ArchiveExtractOptions { FileName = "a.br" }));
        }
    }

    // ---- reference tool ----

    private static string Brotli => Environment.GetEnvironmentVariable("ONEARCHIVE_BROTLI")
        ?? (Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS") is { } dir ? Path.Combine(dir, "brotli") : "brotli");

    private static string Tar => Environment.GetEnvironmentVariable("ONEARCHIVE_GNUTAR") ?? "tar";

    [Fact]
    [Trait("Category", "Interop")]
    public void BrotliTool_DecodesOursAndTarListsThem()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        Archive.Create(temp.Combine("ours.tar.br")).Add(tree, "tree").Save();
        Tools.Run(Brotli, ["-d", "-f", "-o", temp.Combine("ours.tar"), temp.Combine("ours.tar.br")]);
        string listing = Tools.Run(Tar, ["-tf", temp.Combine("ours.tar")]);
        Assert.Contains("tree/sub/b.bin", listing);
        Directory.CreateDirectory(temp.Combine("a"));
        Tools.Run(Tar, ["-xf", temp.Combine("ours.tar"), "-C", temp.Combine("a")]);
        AssertSameTree(tree, temp.Combine("a/tree"));

        string one = temp.Combine("one.txt");
        byte[] data = ContainerVectors.Big(70_000);
        File.WriteAllBytes(one, data);
        Archive.Create(temp.Combine("one.txt.br")).Add(one).Save();
        Tools.Run(Brotli, ["-d", "-f", "-o", temp.Combine("one.out"), temp.Combine("one.txt.br")]);
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("one.out")));
    }

    [Theory]
    [Trait("Category", "Interop")]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(11)]
    public void WeReadBrotliToolFiles(int quality)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        string q = quality.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Tools.Run(Tar, ["-cf", temp.Combine("theirs.tar"), "-C", temp.Root, "tree"]);
        Tools.Run(Brotli, ["-q", q, "-f", "-o", temp.Combine("theirs.tar.br"), temp.Combine("theirs.tar")]);
        Archive.Extract(temp.Combine("theirs.tar.br"), temp.Combine("b"));
        AssertSameTree(tree, temp.Combine("b/tree"));

        string one = temp.Combine("one.txt");
        byte[] data = ContainerVectors.Big(70_000);
        File.WriteAllBytes(one, data);
        Tools.Run(Brotli, ["-q", q, "-f", "-o", temp.Combine("one.txt.br"), one]);
        Archive.Extract(temp.Combine("one.txt.br"), temp.Combine("c"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("c/one.txt")));
    }
}
