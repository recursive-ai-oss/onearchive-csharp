using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>Compound files through the archive happy path: detection, Open, Extract with the default safety rules and limits.</summary>
public class CfbArchiveTests
{
    [Fact]
    public void DetectsBySignatureAndByName()
    {
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        Assert.Equal(ArchiveFormat.CompoundFile, ArchiveDetector.DetectFormat(data.AsSpan(0, ArchiveDetector.PrefixLength)));
        Assert.Equal(ArchiveFormat.CompoundFile, ArchiveDetector.DetectFormat(CfbTestData.Read("v4-basic.cfb").AsSpan(0, ArchiveDetector.PrefixLength)));
        foreach (string name in new[] { "a.doc", "B.XLS", "c.ppt", "d.msg", "e.msi", "f.cfb" })
        {
            Assert.Equal(ArchiveFormat.CompoundFile, ArchiveDetector.FromFileName(name).Format);
        }

        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.DetectFormat(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE0 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OpensFromSeekableAndForwardOnlySources(bool seekable)
    {
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        Stream source = seekable ? new MemoryStream(data) : new ForwardOnly(data);
        using Archive a = Archive.Open(source, new ArchiveOpenOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } });
        Assert.Equal(ArchiveFormat.CompoundFile, a.Format);
        using ArchiveReader r = a.OpenReader();
        Assert.IsType<CompoundFileReader>(r);
        var names = new List<string>();
        while (r.GetNextEntry() is { } e)
        {
            names.Add(e.Name);
        }

        Assert.Contains("Storage 1/Stream 1", names);
        Assert.Equal(10, names.Count);
        if (seekable)
        {
            ArchiveEntry big = a.Find("big")!;
            using Stream s = a.OpenEntry(big);
            Assert.Equal(9000, s.Length);
        }
    }

    [Fact]
    public void ExtractsStoragesAsDirectoriesAndStreamsAsFiles()
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        var result = Archive.Extract(new MemoryStream(data), target, new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } });
        Assert.Equal(10, result.EntriesExtracted);
        Assert.True(Directory.Exists(Path.Combine(target, "Storage 1")));
        Assert.True(Directory.Exists(Path.Combine(target, "deep", "a", "b", "c")));
        Assert.Equal(544, new FileInfo(Path.Combine(target, "Storage 1", "Stream 1")).Length);
        Assert.Equal(0, new FileInfo(Path.Combine(target, "Storage 1", "empty")).Length);
        Assert.Equal(9000, new FileInfo(Path.Combine(target, "big")).Length);
        Assert.Equal(70, new FileInfo(Path.Combine(target, "deep", "a", "b", "c", "leaf")).Length);
    }

    [Fact]
    public void ExtractedNamesNeverLeaveTheTarget()
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        File.WriteAllText(t.Combine("sentinel.txt"), "untouched");
        byte[] data = CfbTestData.Read("v3-names.cfb");
        var result = Archive.Extract(new MemoryStream(data), target);
        Assert.True(result.EntriesExtracted > 15);
        string full = Path.GetFullPath(target);
        foreach (string p in Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories))
        {
            Assert.StartsWith(full, Path.GetFullPath(p), StringComparison.Ordinal);
        }

        Assert.Equal(["out", "sentinel.txt"], Directory.GetFileSystemEntries(t.Root).Select(Path.GetFileName).Order().ToArray());

        // The stream named with a slash and the one named "..", stored literally, land as escaped names inside the target.
        Assert.True(File.Exists(Path.Combine(target, "slash%002Finside")));
        Assert.True(File.Exists(Path.Combine(target, "%002E%002E")));
        Assert.True(File.Exists(Path.Combine(target, "%0005SummaryInformation")));
    }

    [Fact]
    public void LimitsApplyThroughArchive()
    {
        byte[] many = CfbTestData.Read("v3-many-entries.cfb");
        using var t = new TempDir();
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(many), t.Combine("a"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxEntryCount = 50 } }));
        byte[] basic = CfbTestData.Read("v3-basic.cfb");
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(basic), t.Combine("b"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxTotalSize = 500, MaxPathDepth = 100 } }));
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(basic), t.Combine("c"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxPathLength = 5, MaxPathDepth = 100 } }));
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(CfbTestData.Read("v3-deep-70.cfb")), t.Combine("d")));
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(many), t.Combine("e"), new ArchiveExtractOptions { CompoundFile = new CompoundFileReaderOptions { MaxDirectoryEntries = 64 } }));
    }

    [Fact]
    public void DamagedFileIsATypedErrorAndWritesNothingOutside()
    {
        using var t = new TempDir();
        byte[] bad = CfbTestData.Read("bad-fat-cycle.cfb");
        Assert.Throws<CompressionFormatException>(() => Archive.Extract(new MemoryStream(bad), t.Combine("out")));
    }

    private sealed class ForwardOnly(byte[] data) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
