using System.Text;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Tests;

public class ArchiveTests
{
    private static void MakeTree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src/sub"));
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "src/a.txt"), "alpha\n");
        File.WriteAllBytes(Path.Combine(root, "src/sub/b.bin"), Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllText(Path.Combine(root, "z.txt"), string.Empty);
        File.SetLastWriteTimeUtc(Path.Combine(root, "src/a.txt"), new DateTime(2023, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(root, "src/sub/b.bin"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            File.CreateSymbolicLink(Path.Combine(root, "src/link"), "a.txt");
        }
    }

    internal static string Describe(string root)
    {
        var sb = new StringBuilder();
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, path);
            var info = new FileInfo(path);
            if (info.LinkTarget is { } target)
            {
                sb.Append(rel).Append(" -> ").Append(target).Append('\n');
            }
            else if (Directory.Exists(path))
            {
                sb.Append(rel).Append("/ ").Append(OperatingSystem.IsWindows() ? string.Empty : ((int)File.GetUnixFileMode(path)).ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
            else
            {
                sb.Append(rel).Append(' ').Append(info.Length).Append(' ')
                    .Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))[..16]).Append(' ')
                    .Append(new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()).Append(' ')
                    .Append(OperatingSystem.IsWindows() ? string.Empty : ((int)File.GetUnixFileMode(path)).ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData("a.tar", ArchiveCompression.None)]
    [InlineData("a.tar.zst", ArchiveCompression.Zstd)]
    [InlineData("a.tar.lz4", ArchiveCompression.Lz4)]
    [InlineData("a.tzst", ArchiveCompression.Zstd)]
    public void CreateExtractRoundTrip(string fileName, ArchiveCompression compression)
    {
        using var t = new TempDir();
        string src = t.Combine("tree");
        MakeTree(src);
        string archive = t.Combine(fileName);
        Archive.Create(archive).Add(src, "").Save();

        using (var opened = Archive.Open(archive))
        {
            Assert.Equal(ArchiveFormat.Tar, opened.Format);
            Assert.Equal(compression, opened.Compression);
            Assert.True(opened.IsRandomAccess);
            var names = opened.Entries.Select(e => e.Name).ToList();
            Assert.Contains("src/a.txt", names);
            Assert.Contains("empty", names);
            ArchiveEntry file = opened.Find("src/sub/b.bin")!;
            Assert.Equal(70_000, file.Size);
            using Stream s = opened.OpenEntry(file);
            var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(File.ReadAllBytes(Path.Combine(src, "src/sub/b.bin")), ms.ToArray());
            Assert.Equal(compression == ArchiveCompression.None, s.CanSeek);
        }

        string dest = t.Combine("out");
        var result = Archive.Extract(archive, dest);
        Assert.True(result.EntriesExtracted >= 6);
        Assert.Equal(Describe(src), Describe(dest));
    }

    [Fact]
    public void ForwardOnlySourcesAreReadOnce()
    {
        using var t = new TempDir();
        string src = t.Combine("tree");
        MakeTree(src);
        var bytes = new MemoryStream();
        Archive.Create(bytes, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true }).Add(src, "").Save();

        using var archive = Archive.Open(new TarTests.ForwardOnly(bytes.ToArray()), new ArchiveOpenOptions());
        Assert.False(archive.IsRandomAccess);
        Assert.Equal(ArchiveCompression.Zstd, archive.Compression);
        Assert.Throws<NotSupportedException>(() => archive.Entries);
        var r = archive.Extract(t.Combine("out"));
        Assert.True(r.EntriesExtracted > 5);
        Assert.Equal(Describe(src), Describe(t.Combine("out")));
        Assert.Throws<InvalidOperationException>(() => archive.OpenReader());
    }

    [Fact]
    public void ReadsEntriesSequentially()
    {
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "x.txt", Size = 3 }, "xyz"u8.ToArray())
            .Add(new ArchiveEntry { Name = "d", Kind = ArchiveEntryKind.Directory })
            .Save();
        ms.Position = 0;
        using var archive = Archive.Open(ms);
        using ArchiveReader reader = archive.OpenReader();
        var e = reader.GetNextEntry()!;
        Assert.Equal("x.txt", e.Name);
        using var sr = new StreamReader(reader.OpenEntryStream());
        Assert.Equal("xyz", sr.ReadToEnd());
        Assert.Equal("d", reader.GetNextEntry()!.Name);
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void NormalizeMakesTheSameBytesFromDifferentTrees()
    {
        using var t = new TempDir();
        string one = t.Combine("one");
        string two = t.Combine("two");
        foreach (string dir in new[] { one, two })
        {
            Directory.CreateDirectory(Path.Combine(dir, "d"));
        }

        File.WriteAllText(Path.Combine(one, "d/b"), "B");
        File.WriteAllText(Path.Combine(one, "a"), "A");
        File.WriteAllText(Path.Combine(two, "a"), "A");
        File.WriteAllText(Path.Combine(two, "d/b"), "B");
        File.SetLastWriteTimeUtc(Path.Combine(two, "a"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var options = new ArchiveCreateOptions { Normalize = true };
        string a = t.Combine("a.tar");
        string b = t.Combine("b.tar");
        Archive.Create(a, options).Add(one, "").Save();
        Archive.Create(b, options).Add(two, "").Save();
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        using var archive = Archive.Open(a);
        Assert.Equal(["a", "d", "d/b"], archive.Entries.Select(e => e.Name).ToArray());
        Assert.All(archive.Entries, e => Assert.Equal(DateTimeOffset.UnixEpoch, e.ModifiedTime));
    }

    [Fact]
    public void CompressedArchivesMatchTheirTarBytes()
    {
        var plain = new MemoryStream();
        var zst = new MemoryStream();
        byte[] payload = new byte[50_000];
        new Random(3).NextBytes(payload.AsSpan(0, 1000));
        var entry = new ArchiveEntry { Name = "p", Size = payload.Length, ModifiedTime = DateTimeOffset.UnixEpoch };
        Archive.Create(plain, new ArchiveCreateOptions { LeaveOpen = true }).Add(entry, payload).Save();
        Archive.Create(zst, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true }).Add(entry, payload).Save();
        Assert.Equal(plain.ToArray(), Zstd.Decompress(zst.ToArray()));
    }

    [Fact]
    public void ReferenceToolsCompressionLayerIsDetectedByMagicNotName()
    {
        byte[] tar = new RawTar().Entry("a", data: "1"u8.ToArray()).End();
        byte[] zst = Zstd.Compress(tar);
        using var archive = Archive.Open(new MemoryStream(zst), new ArchiveOpenOptions { FileName = "misnamed.tar.gz" });
        Assert.Equal(ArchiveCompression.Zstd, archive.Compression);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void UnknownAndUnsupportedInputs()
    {
        // Not an archive at all.
        var ex = Assert.Throws<CompressionFormatException>(() => Archive.Open(new MemoryStream(Encoding.ASCII.GetBytes("hello world"))));
        Assert.Equal("archive", ex.Format);

        // An empty tar has no magic: the name decides.
        using (var empty = Archive.Open(new MemoryStream(new byte[1024]), new ArchiveOpenOptions { FileName = "x.tar" }))
        {
            Assert.Empty(empty.Entries);
        }

        // gzip is read; a truncated stream is a typed format error. xz is recognised and refused with a clear error.
        byte[] gzip = [0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 0, 3, 0, 0];
        Assert.True(Assert.Throws<CompressionFormatException>(() => Archive.Open(new MemoryStream(gzip))).IsTruncated);
        byte[] xz = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0, 4];
        var unsupported = Assert.Throws<ArchiveUnsupportedException>(() => Archive.Open(new MemoryStream(xz)));
        Assert.Contains("Xz", unsupported.Message, StringComparison.Ordinal);
        Assert.Throws<ArchiveUnsupportedException>(() => Archive.Create(new MemoryStream(), new ArchiveCreateOptions { Compression = ArchiveCompression.Xz }));
        Assert.Throws<ArgumentException>(() => Archive.Create("/nonexistent/x.unknown"));
        Assert.Throws<ArchiveUnsupportedException>(() => Archive.Create("x.tar.gz"));
    }

    [Fact]
    public void DetectionByMagicAndName()
    {
        byte[] ustar = new RawTar().Entry("a").End();
        Assert.Equal(ArchiveFormat.Tar, ArchiveDetector.DetectFormat(ustar));
        // A version 7 header has no magic; a valid checksum is the evidence.
        byte[] v7 = RawTar.Header("old", '0', 0, seal: false);
        Array.Clear(v7, 257, 8);
        RawTar.Seal(v7);
        Assert.Equal(ArchiveFormat.Tar, ArchiveDetector.DetectFormat(v7));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.DetectFormat(new byte[512]));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.DetectFormat(Encoding.ASCII.GetBytes(new string('x', 600))));
        Assert.Equal(ArchiveCompression.Zstd, ArchiveDetector.DetectCompression([0x28, 0xB5, 0x2F, 0xFD, 0]));
        Assert.Equal(ArchiveCompression.Lz4, ArchiveDetector.DetectCompression([0x04, 0x22, 0x4D, 0x18]));
        Assert.Equal(ArchiveCompression.Xz, ArchiveDetector.DetectCompression([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Zstd), ArchiveDetector.FromFileName("/x/Y.TAR.ZST"));
        Assert.Equal((ArchiveFormat.Tar, ArchiveCompression.Gzip), ArchiveDetector.FromFileName("a.tgz"));
        Assert.Equal((ArchiveFormat.Unknown, ArchiveCompression.Zstd), ArchiveDetector.FromFileName("a.zst"));
        Assert.Equal((ArchiveFormat.Unknown, ArchiveCompression.None), ArchiveDetector.FromFileName("a.txt"));
    }

    [Fact]
    public void ExpansionRatioStopsCompressedBombs()
    {
        var ms = new MemoryStream();
        Archive.Create(ms, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "zeros", Size = 64 << 20 }, new byte[64 << 20])
            .Save();
        Assert.True(ms.Length < 64 << 10);
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionLimitException>(() => Archive.Extract(new MemoryStream(ms.ToArray()), t.Combine("out")));
        Assert.Equal("MaxExpansionRatio", ex.LimitName);

        // Raising the ratio lets the same archive through.
        var ok = Archive.Extract(new MemoryStream(ms.ToArray()), t.Combine("out2"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxExpansionRatio = 0 } });
        Assert.Equal(64 << 20, ok.BytesWritten);
    }

    [Fact]
    public void TruncatedCompressedArchiveIsAnError()
    {
        var ms = new MemoryStream();
        byte[] data = new byte[300_000];
        new Random(4).NextBytes(data);
        Archive.Create(ms, new ArchiveCreateOptions { Compression = ArchiveCompression.Zstd, LeaveOpen = true })
            .Add(new ArchiveEntry { Name = "r", Size = data.Length }, data).Save();
        byte[] full = ms.ToArray();
        using var t = new TempDir();
        foreach (int cut in new[] { 10, 100, full.Length / 2, full.Length - 20, full.Length - 1 })
        {
            Assert.ThrowsAny<CompressionException>(() => Archive.Extract(new MemoryStream(full[..cut]), t.Combine("o" + cut)));
        }
    }

    [Fact]
    public async Task ExtractAsyncAndCancellation()
    {
        using var t = new TempDir();
        string src = t.Combine("tree");
        MakeTree(src);
        string archive = t.Combine("a.tar.zst");
        await Task.Run(() => Archive.Create(archive).Add(src, "").Save());
        var result = await Archive.ExtractAsync(archive, t.Combine("out"));
        Assert.True(result.EntriesExtracted > 5);
        Assert.Equal(Describe(src), Describe(t.Combine("out")));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Archive.ExtractAsync(archive, t.Combine("out2"), cancellationToken: cts.Token));
    }

    [Fact]
    public void SparseFilesExtractWithHoles()
    {
        byte[] h = RawTar.Header("sparse.bin", 'S', 15, gnu: true, seal: false);
        RawTar.Octal(h, 386, 12, 100);
        RawTar.Octal(h, 398, 12, 10);
        RawTar.Octal(h, 410, 12, 500);
        RawTar.Octal(h, 422, 12, 5);
        RawTar.Octal(h, 483, 12, 1_000_000);
        RawTar.Seal(h);
        byte[] tar = new RawTar().Raw(h).Data(Encoding.ASCII.GetBytes("ABCDEFGHIJxyz12")).End();
        using var t = new TempDir();
        var result = Archive.Extract(new MemoryStream(tar), t.Combine("out"));
        Assert.Equal(1, result.EntriesExtracted);
        byte[] file = File.ReadAllBytes(t.Combine("out/sparse.bin"));
        Assert.Equal(1_000_000, file.Length);
        Assert.Equal("ABCDEFGHIJ", Encoding.ASCII.GetString(file, 100, 10));
        Assert.Equal("xyz12", Encoding.ASCII.GetString(file, 500, 5));
    }

    [Fact]
    public void BuilderRefusesMissingFilesAndSecondSave()
    {
        using var t = new TempDir();
        var b = Archive.Create(t.Combine("a.tar"));
        Assert.Throws<FileNotFoundException>(() => b.Add(t.Combine("nothing")));
        b.Save();
        Assert.Throws<ObjectDisposedException>(() => b.Save());
        Assert.True(new FileInfo(t.Combine("a.tar")).Length == 10240);
    }

    [Fact]
    public void ZstdSkippableAndConcatenatedFramesAreTolerated()
    {
        byte[] tar = new RawTar().Entry("a", data: "1"u8.ToArray()).End();
        byte[] a = Zstd.Compress(tar.AsSpan(0, 512));
        byte[] b = Zstd.Compress(tar.AsSpan(512));
        using var archive = Archive.Open(new MemoryStream([.. a, .. b]));
        Assert.Equal("a", archive.Entries.Single().Name);
        Assert.IsType<TarEntry>(archive.Entries[0]);
    }
}
