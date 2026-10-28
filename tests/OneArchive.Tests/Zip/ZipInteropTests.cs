using System.Runtime.Versioning;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Round trips against Info-ZIP zip/unzip, 7-Zip, bsdtar, gzip, pigz, zstd and lz4, run as black boxes. They run only with
/// ONEARCHIVE_INTEROP=1, and then a missing tool fails the run. ONEARCHIVE_ZIP, ONEARCHIVE_UNZIP, ONEARCHIVE_7Z and
/// ONEARCHIVE_BSDTAR name the executables (defaults: zip, unzip, 7zz, bsdtar). The over-4-GiB case also needs ONEARCHIVE_BIG=1.
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class ZipInteropTests
{
    private static string Zip => Environment.GetEnvironmentVariable("ONEARCHIVE_ZIP") ?? "zip";

    private static string Unzip => Environment.GetEnvironmentVariable("ONEARCHIVE_UNZIP") ?? "unzip";

    private static string SevenZip => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static string BsdTar => Environment.GetEnvironmentVariable("ONEARCHIVE_BSDTAR") ?? "bsdtar";

    private static void Enabled() => Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static string Sh(string script, string? cwd = null) => Tools.Run("bash", ["-c", "set -o pipefail; " + script], cwd);

    private static void Tree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "dir/nested"));
        Directory.CreateDirectory(Path.Combine(root, "emptydir"));
        File.WriteAllText(Path.Combine(root, "hello.txt"), "hello\n");
        File.WriteAllBytes(Path.Combine(root, "dir/nested/data.bin"), ZipTests.Text(300_000, 11));
        File.WriteAllBytes(Path.Combine(root, "dir/random.bin"), new Random(5).GetBytes(20_000));
        File.WriteAllText(Path.Combine(root, "dir/run.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(root, "ünïcödé-日本語.txt"), "unicode");
        File.WriteAllText(Path.Combine(root, "zero"), string.Empty);
        File.SetUnixFileMode(Path.Combine(root, "dir/run.sh"), (UnixFileMode)Convert.ToInt32("755", 8));
        File.CreateSymbolicLink(Path.Combine(root, "link"), "hello.txt");
    }

    // Forward-only reads do not see Unix modes or symbolic links (they are in the central directory only): a link arrives as
    // a file holding its target, and modes are the extractor defaults.
    private static void AssertSame(string expected, string actual, bool forward = false)
    {
        foreach (string file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(expected, file);
            string other = Path.Combine(actual, rel);
            if (new FileInfo(file).LinkTarget is { } target)
            {
                if (forward)
                {
                    Assert.Equal(target, File.ReadAllText(other));
                }
                else
                {
                    Assert.Equal(target, new FileInfo(other).LinkTarget);
                }
            }
            else
            {
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(other));
            }
        }

        Assert.True(Directory.Exists(Path.Combine(actual, "emptydir")));
        if (!forward)
        {
            Assert.Equal(File.GetUnixFileMode(Path.Combine(expected, "dir/run.sh")), File.GetUnixFileMode(Path.Combine(actual, "dir/run.sh")));
        }
    }

    [Theory]
    [InlineData("zip -qry")]
    [InlineData("zip -qry -0")]
    [InlineData("zip -qry -9")]
    [InlineData("zip -qry -fz")]
    public void WeReadInfoZipArchives(string command)
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Tree(tree);
        Sh($"{command.Replace("zip", Zip, StringComparison.Ordinal)} ../out.zip . ", tree);
        Archive.Extract(temp.Combine("out.zip"), temp.Combine("x"));
        AssertSame(tree, temp.Combine("x"));
        using var forward = new ForwardOnly(File.OpenRead(temp.Combine("out.zip")), 4096);
        Archive.Extract(forward, temp.Combine("y"));
        AssertSame(tree, temp.Combine("y"), forward: true);
    }

    [Fact]
    public void WeReadInfoZipStreamingOutputWithDescriptors()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Tree(tree);
        Sh($"{Zip} -qry - . > ../piped.zip", tree);
        using (var f = File.OpenRead(temp.Combine("piped.zip")))
        {
            using var reader = new ZipReader(new ForwardOnly(f, 1000));
            Assert.Contains(Enumerable.Range(0, 100).Select(_ => reader.GetNextEntry()).TakeWhile(e => e is not null), e => e!.HasDataDescriptor || e.Size == 0);
        }

        Archive.Extract(temp.Combine("piped.zip"), temp.Combine("x"));
        AssertSame(tree, temp.Combine("x"));
        using var forward = new ForwardOnly(File.OpenRead(temp.Combine("piped.zip")), 777);
        Archive.Extract(forward, temp.Combine("y"));
        AssertSame(tree, temp.Combine("y"), forward: true);
    }

    [Fact]
    public void InfoZipAndSevenZipAndBsdtarReadOurStoredZip()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Tree(tree);
        foreach (bool seekable in new[] { true, false })
        {
            string zip = temp.Combine(seekable ? "seekable.zip" : "streamed.zip");
            using (var file = File.Create(zip))
            using (Stream sink = seekable ? file : new WriteOnly(file))
            {
                Archive.Create(sink, new ArchiveCreateOptions { Format = ArchiveFormat.Zip, Zip = new ZipWriterOptions { Method = ZipMethod.Store }, LeaveOpen = true }).Add(tree, "").Save();
            }

            Tools.Run(Unzip, ["-tq", zip]);
            Tools.Run(SevenZip, ["t", zip]);
            Sh($"{Unzip} -q {zip} -d {temp.Combine("u" + seekable)}");
            AssertSame(tree, temp.Combine("u" + seekable));
            Sh($"{SevenZip} x -y -o{temp.Combine("s" + seekable)} {zip} > /dev/null");
            Assert.Equal(File.ReadAllBytes(Path.Combine(tree, "dir/nested/data.bin")), File.ReadAllBytes(temp.Combine("s" + seekable + "/dir/nested/data.bin")));
            Sh($"mkdir -p {temp.Combine("b" + seekable)} && {BsdTar} -xf {zip} -C {temp.Combine("b" + seekable)}");
            AssertSame(tree, temp.Combine("b" + seekable));
        }
    }

    [Fact]
    public void ZstdEntriesAreReadByOurselvesAndReportedByTheReferenceTools()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Tree(tree);
        string zip = temp.Combine("zstd.zip");
        Archive.Create(zip, new ArchiveCreateOptions { Zip = new ZipWriterOptions { Method = ZipMethod.Zstd } }).Add(tree, "").Save();
        Archive.Extract(zip, temp.Combine("x"));
        AssertSame(tree, temp.Combine("x"));

        // Method 93 is not in Info-ZIP, p7zip 16 or bsdtar; record what the installed 7-Zip says about it.
        string listing = Tools.Run(SevenZip, ["l", "-slt", zip], failOnError: false);
        Assert.Contains("data.bin", listing, StringComparison.Ordinal);
        Console.WriteLine("7-Zip method for zstd entries: " + string.Join(' ', listing.Split('\n').Where(l => l.StartsWith("Method", StringComparison.Ordinal)).Distinct()));
    }

    [Fact]
    public void ManyEntriesInterop()
    {
        Enabled();
        using var temp = new TempDir();
        string zip = temp.Combine("many.zip");
        using (var f = File.Create(zip))
        using (var w = new ZipWriter(f, new ZipWriterOptions { Method = ZipMethod.Store }))
        {
            for (int i = 0; i < 70_000; i++)
            {
                w.WriteEntry(new ArchiveEntry { Name = "d/f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Size = 1 }, new byte[] { (byte)i });
            }
        }

        Tools.Run(Unzip, ["-tq", zip]);
        Assert.Contains("70000", Sh($"{SevenZip} l {zip} | tail -3"), StringComparison.Ordinal);

        // Info-ZIP writes the same many entries; we read them back.
        string tree = temp.Combine("tree");
        Directory.CreateDirectory(tree);
        for (int i = 0; i < 70_000; i++)
        {
            File.WriteAllBytes(Path.Combine(tree, "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), [(byte)i]);
        }

        Sh($"{Zip} -qr ../info.zip .", tree);
        int count = 0;
        var limits = new ArchiveLimits { MaxEntryCount = 100_000 };
        using (Archive a = Archive.Open(temp.Combine("info.zip"), new ArchiveOpenOptions { Limits = limits }))
        {
            count = a.Entries.Count;
        }

        Assert.Equal(70_000, count);
    }

    [Fact]
    public void ZipOver4GiBInterop()
    {
        Enabled();
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_BIG") == "1", "set ONEARCHIVE_BIG=1 for the over-4-GiB case");
        using var temp = new TempDir();
        const long size = (4L << 30) + (300L << 20);
        var limits = new ArchiveLimits { MaxTotalSize = 16L << 30, MaxExpansionRatio = 0 };

        // Ours: stored zeros (needs zip64 in the local header and the directory); read by the reference tools.
        string zip = temp.Combine("big-store.zip");
        using (var f = File.Create(zip))
        using (var w = new ZipWriter(f, new ZipWriterOptions { Method = ZipMethod.Store }))
        {
            w.WriteEntry(new ArchiveEntry { Name = "small", Size = 3 }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "big", Size = size }, new ZeroStream(size));
            w.WriteEntry(new ArchiveEntry { Name = "after", Size = 3 }, "xyz"u8.ToArray());
        }

        Tools.Run(Unzip, ["-tq", zip]);
        Tools.Run(SevenZip, ["t", zip]);
        using (Archive a = Archive.Open(zip, new ArchiveOpenOptions { Limits = limits }))
        {
            Assert.Equal(size, a.Find("big")!.Size);
            Assert.True(((ZipEntry)a.Find("after")!).LocalHeaderOffset > 4L << 30);
            using Stream s = a.OpenEntry(a.Find("after")!);
            Assert.Equal((byte)'x', s.ReadByte());
        }

        // Theirs: deflated zeros, 4.3 GiB; read by us, both ways.
        string big = temp.Combine("zeros.bin");
        Sh($"head -c {size} /dev/zero > {big}");
        string info = temp.Combine("info.zip");
        Sh($"{Zip} -q {info} zeros.bin", temp.Root);
        using (Archive a = Archive.Open(info, new ArchiveOpenOptions { Limits = limits }))
        {
            Assert.Equal(size, Assert.Single(a.Entries).Size);
            using Stream s = a.OpenEntry(a.Entries[0]);
            long total = 0;
            byte[] buffer = new byte[1 << 20];
            int n;
            while ((n = s.Read(buffer)) > 0)
            {
                total += n;
            }

            Assert.Equal(size, total);
        }
    }

    private sealed class ZeroStream : Stream
    {
        private readonly long _size;
        private long _left;

        public ZeroStream(long size)
        {
            _size = size;
            _left = size;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _size;

        public override long Position { get => _size - _left; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void WeReadGzipAndPigzOutputAndTheirZstdAndLz4()
    {
        Enabled();
        using var temp = new TempDir();
        byte[] data = ZipTests.Text(700_000, 21);
        File.WriteAllBytes(temp.Combine("doc.txt"), data);
        foreach (string tool in new[] { "gzip", "pigz" })
        {
            Sh($"{tool} -k -c doc.txt > {tool}.gz", temp.Root);
            Archive.Extract(temp.Combine(tool + ".gz"), temp.Combine("o-" + tool));
            Assert.Equal(data, File.ReadAllBytes(Directory.GetFiles(temp.Combine("o-" + tool)).Single()));
        }

        // gzip -N-style header name: compress the file itself so the tool records its name.
        Sh("cp doc.txt named.txt && gzip -9 named.txt", temp.Root);
        using (Archive a = Archive.Open(temp.Combine("named.txt.gz")))
        {
            Assert.Equal("named.txt", Assert.Single(a.Entries).Name);
        }

        // Concatenated members decode as one stream.
        Sh("gzip -c doc.txt > multi.gz && gzip -c doc.txt >> multi.gz", temp.Root);
        Archive.Extract(temp.Combine("multi.gz"), temp.Combine("o-multi"));
        Assert.Equal([.. data, .. data], File.ReadAllBytes(Directory.GetFiles(temp.Combine("o-multi")).Single()));

        Sh("zstd -q -19 -c doc.txt > doc.txt.zst && lz4 -q -9 -c doc.txt > doc.txt.lz4", temp.Root);
        Archive.Extract(temp.Combine("doc.txt.zst"), temp.Combine("o-zst"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("o-zst/doc.txt")));
        Archive.Extract(temp.Combine("doc.txt.lz4"), temp.Combine("o-lz4"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("o-lz4/doc.txt")));
    }

    [Fact]
    public void ReferenceToolsReadOurSingleStreams()
    {
        Enabled();
        using var temp = new TempDir();
        byte[] data = ZipTests.Text(400_000, 22);
        File.WriteAllBytes(temp.Combine("doc.txt"), data);
        Archive.Create(temp.Combine("ours.txt.zst")).Add(temp.Combine("doc.txt")).Save();
        Archive.Create(temp.Combine("ours.txt.lz4")).Add(temp.Combine("doc.txt")).Save();
        Sh("zstd -dc ours.txt.zst > from-zst && lz4 -dc ours.txt.lz4 > from-lz4", temp.Root);
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("from-zst")));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("from-lz4")));
    }

    [Fact]
    public void TarGzFromGnuTarAndBsdtarIsOneArchive()
    {
        Enabled();
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Tree(tree);
        Sh("tar czf ../gnu.tar.gz .", tree);
        Sh($"{BsdTar} czf ../bsd.tgz .", tree);
        foreach (string name in new[] { "gnu.tar.gz", "bsd.tgz" })
        {
            using (Archive a = Archive.Open(temp.Combine(name)))
            {
                Assert.Equal(ArchiveFormat.Tar, a.Format);
                Assert.Equal(ArchiveCompression.Gzip, a.Compression);
            }

            Archive.Extract(temp.Combine(name), temp.Combine("o-" + name));
            AssertSame(tree, temp.Combine("o-" + name));
        }
    }
}

internal static class RandomExtensions
{
    public static byte[] GetBytes(this Random random, int length)
    {
        byte[] b = new byte[length];
        random.NextBytes(b);
        return b;
    }
}
