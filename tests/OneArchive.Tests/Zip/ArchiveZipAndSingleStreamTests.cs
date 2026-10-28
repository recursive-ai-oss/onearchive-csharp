using System.IO.Compression;
using System.Text;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive.Tests;

public class ArchiveZipAndSingleStreamTests
{
    private static string MakeTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "sub/deeper"));
        File.WriteAllBytes(Path.Combine(root, "a.txt"), ZipTests.Text(50_000, 5));
        File.WriteAllBytes(Path.Combine(root, "sub/b.bin"), ZipTests.Text(3000, 6));
        File.WriteAllBytes(Path.Combine(root, "sub/deeper/c.txt"), "c"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "empty"), []);
        return root;
    }

    private static void AssertSameTree(string expected, string actual)
    {
        foreach (string file in Directory.GetFiles(expected, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(expected, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(actual, rel)));
        }
    }

    [Theory]
    [InlineData(ZipMethod.Store)]
    [InlineData(ZipMethod.Deflate)]
    [InlineData(ZipMethod.Zstd)]
    public void CreateOpenAndExtractZip(ZipMethod method)
    {
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        string zip = temp.Combine("out.zip");
        var options = new ArchiveCreateOptions { Zip = new ZipWriterOptions { Method = method } };
        Archive.Create(zip, options).Add(tree, "tree").Save();

        using (Archive archive = Archive.Open(zip))
        {
            Assert.Equal(ArchiveFormat.Zip, archive.Format);
            Assert.Equal(ArchiveCompression.None, archive.Compression);
            Assert.True(archive.IsRandomAccess);
            Assert.Contains(archive.Entries, e => e.Name == "tree/sub/b.bin");
            using Stream s = archive.OpenEntry(archive.Find("tree/sub/b.bin")!);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(ZipTests.Text(3000, 6), ms.ToArray());
        }

        Archive.Extract(zip, temp.Combine("x"));
        AssertSameTree(tree, temp.Combine("x/tree"));

        // Forward-only source: no central directory.
        using var forward = new ForwardOnly(File.OpenRead(zip), 100);
        Archive.Extract(forward, temp.Combine("y"));
        AssertSameTree(tree, temp.Combine("y/tree"));
    }

    [Fact]
    public void NormalizedZipIsReproducible()
    {
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        var options = new ArchiveCreateOptions { Normalize = true, Zip = new ZipWriterOptions { Method = ZipMethod.Zstd } };
        Archive.Create(temp.Combine("1.zip"), options).Add(tree, "t").Save();
        File.SetLastWriteTimeUtc(Path.Combine(tree, "a.txt"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Archive.Create(temp.Combine("2.zip"), options).Add(tree, "t").Save();
        Assert.Equal(File.ReadAllBytes(temp.Combine("1.zip")), File.ReadAllBytes(temp.Combine("2.zip")));
    }

    [Fact]
    public void ZipToNonSeekableStream()
    {
        using var temp = new TempDir();
        string tree = MakeTree(temp.Combine("tree"));
        var sink = new MemoryStream();
        Archive.Create(new WriteOnly(sink), new ArchiveCreateOptions { Format = ArchiveFormat.Zip, Zip = new ZipWriterOptions { Method = ZipMethod.Zstd }, LeaveOpen = true }).Add(tree, "tree").Save();
        Archive.Extract(new MemoryStream(sink.ToArray()), temp.Combine("x"));
        AssertSameTree(tree, temp.Combine("x/tree"));
    }

    [Theory]
    [InlineData("note.txt.zst", ArchiveCompression.Zstd)]
    [InlineData("note.txt.lz4", ArchiveCompression.Lz4)]
    public void SingleCompressedFileIsAnArchiveOfOne(string fileName, ArchiveCompression compression)
    {
        using var temp = new TempDir();
        byte[] data = ZipTests.Text(200_000, 7);
        string source = temp.Combine("note.txt");
        File.WriteAllBytes(source, data);
        string packed = temp.Combine(fileName);
        Archive.Create(packed).Add(source).Save();

        using (Archive archive = Archive.Open(packed))
        {
            Assert.Equal(ArchiveFormat.SingleStream, archive.Format);
            Assert.Equal(compression, archive.Compression);
            ArchiveEntry e = Assert.Single(archive.Entries);
            Assert.Equal("note.txt", e.Name);
            using Stream s = archive.OpenEntry(e);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
        }

        Archive.Extract(packed, temp.Combine("out"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("out/note.txt")));

        // Detection by content, not by name.
        string renamed = temp.Combine("mystery");
        File.Copy(packed, renamed);
        Archive.Extract(renamed, temp.Combine("out2"));
        Assert.Equal(data, File.ReadAllBytes(temp.Combine("out2/mystery")));
    }

    [Fact]
    public void GzipSingleStreamIsWrittenWithTheEntryName()
    {
        using var temp = new TempDir();
        byte[] data = ZipTests.Text(50_000, 3);
        string src = temp.Combine("report.txt");
        File.WriteAllBytes(src, data);
        string gz = temp.Combine("out.gz");
        Archive.Create(gz).Add(src).Save();

        byte[] packed = File.ReadAllBytes(gz);
        Assert.Equal(8, packed[3] & 8);
        Assert.Contains("report.txt", System.Text.Encoding.Latin1.GetString(packed, 10, 16), StringComparison.Ordinal);
        using (var z = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
        {
            var ms = new MemoryStream();
            z.CopyTo(ms);
            Assert.Equal(data, ms.ToArray());
        }

        using (Archive archive = Archive.Open(gz))
        {
            Assert.Equal("report.txt", Assert.Single(archive.Entries).Name);
        }

        // Same inputs, same bytes.
        string again = temp.Combine("again.gz");
        Archive.Create(again).Add(src).Save();
        Assert.Equal(packed, File.ReadAllBytes(again));
    }

    [Fact]
    public void TarGzIsWrittenAndReadBack()
    {
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        MakeTree(tree);
        string path = temp.Combine("x.tar.gz");
        Archive.Create(path).Add(tree, "tree").Save();
        using (var z = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        {
            Assert.True(z.ReadByte() >= 0);
        }

        Archive.Extract(path, temp.Combine("o"));
        AssertSameTree(tree, temp.Combine("o/tree"));
    }

    [Fact]
    public void ZipEntryCompressedWithBzip2IsRead()
    {
        byte[] zip = Convert.FromBase64String("UEsDBC4AAAAMAMA4XF2eUQiZSgAAALgBAAAFAAAAYS50eHRCWmg5MUFZJlNZhghw2AAAWZmAQAAQABJl1DAgAFCAAAFKkADTonZPibE0JsTkmBMidE8JgmCeCZJ8TJOSZJ+LuSKcKEhDBDhsAFBLAQIuAy4AAAAMAMA4XF2eUQiZSgAAALgBAAAFAAAAAAAAAAAAAACAAQAAAABhLnR4dFBLBQYAAAAAAQABADMAAABtAAAAAAA=");
        byte[] expected = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello bzip2 zip entry ", 20)));
        foreach (bool forward in new[] { false, true })
        {
            using var reader = new ZipReader(forward ? new ForwardOnly(new MemoryStream(zip)) : new MemoryStream(zip));
            ArchiveEntry e = reader.GetNextEntry()!;
            Assert.Equal("a.txt", e.Name);
            using Stream s = reader.OpenEntryStream();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(expected, ms.ToArray());
        }
    }

    [Fact]
    public void SingleStreamWritesExactlyOneFile()
    {
        using var temp = new TempDir();
        string a = temp.Combine("a");
        string b = temp.Combine("b");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        var builder = Archive.Create(temp.Combine("x.zst")).Add(a).Add(b);
        Assert.Throws<ArchiveUnsupportedException>(() => builder.Save());
    }

    private static byte[] Gzip(byte[] data, string? name = null, uint mtime = 0)
    {
        var ms = new MemoryStream();
        byte flags = (byte)(name is null ? 0 : 8);
        ms.Write([0x1F, 0x8B, 8, flags]);
        ms.Write(BitConverter.GetBytes(mtime));
        ms.Write([0, 3]);
        if (name is not null)
        {
            ms.Write(Encoding.Latin1.GetBytes(name));
            ms.WriteByte(0);
        }

        ms.Write(RawZip.Deflate(data));
        ms.Write(BitConverter.GetBytes(RawZip.Crc(data)));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        return ms.ToArray();
    }

    [Fact]
    public void GzipSingleFileUsesHeaderNameAndReadsAllMembers()
    {
        using var temp = new TempDir();
        byte[] one = ZipTests.Text(30_000, 1);
        byte[] two = ZipTests.Text(30_000, 2);
        string path = temp.Combine("whatever.gz");
        File.WriteAllBytes(path, [.. Gzip(one, "original.txt", 1_700_000_000), .. Gzip(two)]);
        using Archive archive = Archive.Open(path);
        Assert.Equal(ArchiveFormat.SingleStream, archive.Format);
        Assert.Equal(ArchiveCompression.Gzip, archive.Compression);
        ArchiveEntry e = Assert.Single(archive.Entries);
        Assert.Equal("original.txt", e.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), e.ModifiedTime);
        using Stream s = archive.OpenEntry(e);
        var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal([.. one, .. two], ms.ToArray());

        string noName = temp.Combine("plain.dat.gz");
        File.WriteAllBytes(noName, Gzip(one));
        using Archive second = Archive.Open(noName);
        Assert.Equal("plain.dat", Assert.Single(second.Entries).Name);
    }

    [Fact]
    public void TruncatedGzipSingleFileIsATypedError()
    {
        using var temp = new TempDir();
        byte[] gz = Gzip(ZipTests.Text(30_000, 1), "x");
        string path = temp.Combine("t.gz");
        File.WriteAllBytes(path, gz[..(gz.Length - 20)]);
        Assert.ThrowsAny<RecursiveAI.OneCompress.CompressionException>(() => Archive.Extract(path, temp.Combine("o")));
    }

    [Fact]
    public void TarGzIsOneArchive()
    {
        using var temp = new TempDir();
        var tar = new MemoryStream();
        using (var w = new TarWriter(tar, null, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "f.txt", Size = 5 }, "hello"u8.ToArray());
        }

        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(tar.ToArray());
        }

        string path = temp.Combine("x.tgz");
        File.WriteAllBytes(path, gz.ToArray());
        using (Archive archive = Archive.Open(path))
        {
            Assert.Equal(ArchiveFormat.Tar, archive.Format);
            Assert.Equal(ArchiveCompression.Gzip, archive.Compression);
            Assert.Equal("f.txt", Assert.Single(archive.Entries).Name);
        }

        Archive.Extract(path, temp.Combine("o"));
        Assert.Equal("hello", File.ReadAllText(temp.Combine("o/f.txt")));
        Assert.Throws<ArchiveUnsupportedException>(() => Archive.Create(temp.Combine("new.tar.xz")));
    }

    [Fact]
    public void NotAnArchiveStillSaysSo()
    {
        using var temp = new TempDir();
        string path = temp.Combine("junk.bin");
        File.WriteAllBytes(path, new byte[100]);
        Assert.Throws<RecursiveAI.OneCompress.CompressionFormatException>(() => Archive.Open(path));
    }
}
