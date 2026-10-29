using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>RAR through <see cref="Archive"/>: detection, extraction and the shared safety rules.</summary>
public class RarArchiveTests
{
    private static readonly byte[] s_data = Encoding.ASCII.GetBytes("payload");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public void DetectsBothSignaturesAndTheExtensions()
    {
        Assert.Equal(ArchiveFormat.Rar, ArchiveDetector.DetectFormat(RarTestBuilder.Signature));
        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.DetectFormat(RarTestBuilder.Signature.AsSpan(0, 6)));
        foreach (string name in new[] { "a.rar", "A.RAR", "a.part1.rar", "a.r00", "a.s12" })
        {
            Assert.Equal(ArchiveFormat.Rar, ArchiveDetector.FromFileName(name).Format);
        }

        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.FromFileName("a.r0x").Format);
    }

    [Fact]
    public void OpenListsAndExtractsTheStoredFiles()
    {
        using Archive a = Archive.Open(RarReaderTests.Fixture("stored-blake2.rar"));
        Assert.Equal(ArchiveFormat.Rar, a.Format);
        Assert.Equal(11, a.Entries.Count);
        Assert.All(a.Entries, e => Assert.IsType<RarEntry>(e));

        using var t = new TempDir();
        ArchiveExtractResult result = a.Extract(t.Combine("out"), new ArchiveExtractOptions { Symlinks = LinkPolicy.Skip, HardLinks = LinkPolicy.Skip });
        Assert.True(result.EntriesExtracted >= 8);
        string hello = Path.Combine(t.Combine("out"), "hello.txt");
        Assert.Equal(920, new FileInfo(hello).Length);
        Assert.True(Math.Abs((File.GetLastWriteTimeUtc(hello) - new DateTime(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc)).TotalSeconds) < 1);
        Assert.True(Directory.Exists(Path.Combine(t.Combine("out"), "empty-dir")));
        Assert.Equal(0, new FileInfo(Path.Combine(t.Combine("out"), "empty.txt")).Length);
        Assert.True(File.Exists(Path.Combine(t.Combine("out"), RarReaderTests.Unicode)));
    }

    [Fact]
    public void LinksFollowTheSharedRules()
    {
        using var t = new TempDir();
        string archive = RarReaderTests.Fixture("stored.rar");

        // The archive holds a symlink that points out of the target: refused by default, and nothing is written outside.
        ArchiveSafetyException ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(archive, t.Combine("out")));
        Assert.Equal("link-escape", ex.Rule);
        Assert.Equal("escaping-link", ex.EntryName);
        Assert.Equal("rar", ex.Format);

        string loose = t.Combine("skip");
        ArchiveExtractResult r = Archive.Extract(archive, loose, new ArchiveExtractOptions { Symlinks = LinkPolicy.Skip });
        Assert.True(r.EntriesSkipped >= 2);
        Assert.False(File.Exists(Path.Combine(loose, "escaping-link")));
    }

    [Fact]
    public void SafeLinksAreCreated()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        byte[] rar = new RarTestBuilder().Main()
            .File("real.txt", s_data)
            .File("ok-link", [], o => { o.Extra = RarTestBuilder.Redirection(1, "real.txt"); o.WriteCrc = false; })
            .File("ok-hard", [], o => { o.Extra = RarTestBuilder.Redirection(4, "real.txt"); o.WriteCrc = false; })
            .End().ToArray();
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(rar), t.Combine("out"));
        Assert.Equal("real.txt", new FileInfo(t.Combine("out/ok-link")).LinkTarget);
        Assert.Equal(s_data, File.ReadAllBytes(t.Combine("out/ok-hard")));
    }

    [Theory]
    [InlineData("../evil.txt", "path-traversal")]
    [InlineData("/abs/evil.txt", "absolute-path")]
    [InlineData("a/../../evil.txt", "path-traversal")]
    [InlineData("a\\..\\..\\evil.txt", "path-traversal")]
    public void HostileNamesAreRefused(string name, string rule)
    {
        byte[] rar = new RarTestBuilder().Main().File(name, s_data).End().ToArray();
        using var t = new TempDir();
        ArchiveSafetyException ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(rar), t.Combine("out")));
        Assert.Equal(rule, ex.Rule);
        Assert.False(File.Exists(t.Combine("evil.txt")));
    }

    [Fact]
    public void HardLinkOutOfTheTargetAndWriteThroughALinkAreRefused()
    {
        using var t = new TempDir();
        File.WriteAllText(t.Combine("secret.txt"), "secret");
        byte[] hard = new RarTestBuilder().Main()
            .File("h", [], o => { o.Extra = RarTestBuilder.Redirection(4, "../secret.txt"); o.WriteCrc = false; })
            .End().ToArray();
        Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(hard), t.Combine("out1")));

        // A link to a directory inside the target, then a file written through it.
        byte[] through = new RarTestBuilder().Main()
            .File("sub", [], o => { o.Directory = true; })
            .File("l", [], o => { o.Extra = RarTestBuilder.Redirection(1, "sub", 1); o.WriteCrc = false; })
            .File("l/x.txt", s_data)
            .End().ToArray();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(through), t.Combine("out2")));
            Assert.False(File.Exists(t.Combine("out2/sub/x.txt")));
        }

        Assert.Equal("secret", File.ReadAllText(t.Combine("secret.txt")));
    }

    [Fact]
    public void CompressedEntryStopsExtractionAndWritesNothing()
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        Assert.Throws<RarUnsupportedException>(() => Archive.Extract(RarReaderTests.Fixture("m3.rar"), target));
        Assert.Empty(Directory.Exists(target) ? Directory.GetFiles(target, "*", SearchOption.AllDirectories) : []);

        // The listing is complete, and a stored entry in the same archive could still be read through the reader.
        using Archive a = Archive.Open(RarReaderTests.Fixture("m3.rar"));
        Assert.Equal(11, a.Entries.Count);
        Assert.Contains(a.Entries, e => e is RarEntry { Method: 3 });
    }

    [Fact]
    public void EncryptedHeadersAreAnErrorFromOpenOrFirstEntry()
    {
        RarUnsupportedException ex = Assert.Throws<RarUnsupportedException>(() =>
        {
            using Archive a = Archive.Open(RarReaderTests.Fixture("encrypted-headers.rar"));
            _ = a.Entries;
        });
        Assert.Equal(RarUnsupportedKind.EncryptedHeaders, ex.Kind);
    }

    [Fact]
    public void FormatCanBeForcedForAnSfxStyleStream()
    {
        byte[] rar = File.ReadAllBytes(RarReaderTests.Fixture("stored.rar"));
        byte[] sfx = [.. new byte[2000], .. rar];
        using Archive a = Archive.Open(new MemoryStream(sfx), new ArchiveOpenOptions { Format = ArchiveFormat.Rar });
        Assert.Equal(11, a.Entries.Count);
    }

    [Fact]
    public async Task AsyncExtractionWorks()
    {
        using var t = new TempDir();
        ArchiveExtractResult r = await Archive.ExtractAsync(RarReaderTests.Fixture("stored-solid.rar"), t.Combine("out"), new ArchiveExtractOptions { Symlinks = LinkPolicy.Skip, HardLinks = LinkPolicy.Skip });
        Assert.True(r.BytesWritten > 50000);
    }

    [Fact]
    public void ExtractionAppliesTheTotalSizeLimit()
    {
        using var t = new TempDir();
        Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(RarReaderTests.Fixture("stored.rar"), t.Combine("out"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxTotalSize = 1000 }, Symlinks = LinkPolicy.Skip }));
    }
}
