using System.Buffers.Binary;
using System.Text;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Hostile zip archives: every one must end in a typed error (or be refused by the extractor), and nothing may be written outside the target.</summary>
public class ZipSafetyTests
{
    private static byte[] Simple()
    {
        var raw = new RawZip();
        raw.Add(RawZip.Stored("a.txt", "hello"u8.ToArray()));
        raw.Add(RawZip.Deflated("b.txt", ZipTests.Text(2000)));
        return raw.Finish();
    }

    private static void Extract(byte[] zip, string dir, ArchiveExtractOptions? options = null) =>
        Archive.Extract(new MemoryStream(zip), dir, options);

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("a\\..\\..\\evil.txt")]
    public void UnsafeNamesAreRejectedByDefaultAndWriteNothingOutside(string name)
    {
        using var temp = new TempDir();
        string target = temp.Combine("target");
        var raw = new RawZip();
        raw.Add(RawZip.Stored(name, "x"u8.ToArray()));
        byte[] zip = raw.Finish();
        Assert.Throws<ArchiveSafetyException>(() => Extract(zip, target));
        Assert.Empty(Directory.GetFiles(temp.Root, "evil.txt", SearchOption.AllDirectories));

        // Sanitize keeps the file inside.
        Extract(zip, target, new ArchiveExtractOptions { UnsafePaths = UnsafePathHandling.Sanitize });
        Assert.Single(Directory.GetFiles(target, "evil.txt", SearchOption.AllDirectories));
        Assert.False(File.Exists(Path.Combine(temp.Root, "evil.txt")));
    }

    [Fact]
    public void NulInNameIsRejected()
    {
        using var temp = new TempDir();
        var raw = new RawZip();
        var e = RawZip.Stored("x", [1]);
        e.RawName = "a\0b"u8.ToArray();
        raw.Add(e);
        Assert.Throws<ArchiveSafetyException>(() => Extract(raw.Finish(), temp.Combine("t")));
    }

    [Fact]
    public void SymlinkEscapesAreRefusedUnlessAllowed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        foreach (string target in new[] { "../../etc/passwd", "/etc/passwd" })
        {
            using var temp = new TempDir();
            var raw = new RawZip();
            var e = RawZip.Stored("link", Encoding.UTF8.GetBytes(target));
            e.VersionMadeBy = (3 << 8) | 20;
            e.ExternalAttributes = 0xA1FFu << 16;
            raw.Add(e);
            byte[] zip = raw.Finish();
            Assert.Throws<ArchiveSafetyException>(() => Extract(zip, temp.Combine("t")));
            Extract(zip, temp.Combine("u"), new ArchiveExtractOptions { Symlinks = LinkPolicy.Unsafe });
            Assert.Equal(target, new FileInfo(temp.Combine("u/link")).LinkTarget);
        }
    }

    [Fact]
    public void WritingThroughASymlinkFromTheSameArchiveIsRefused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks");
        using var temp = new TempDir();
        string outside = temp.Combine("outside");
        Directory.CreateDirectory(outside);
        var raw = new RawZip();
        var link = RawZip.Stored("sub", "../outside"u8.ToArray());
        link.VersionMadeBy = (3 << 8) | 20;
        link.ExternalAttributes = 0xA1FFu << 16;
        raw.Add(link);
        raw.Add(RawZip.Stored("sub/pwned.txt", "x"u8.ToArray()));
        byte[] zip = raw.Finish();
        Assert.ThrowsAny<ArchiveSafetyException>(() => Extract(zip, temp.Combine("t"), new ArchiveExtractOptions { Symlinks = LinkPolicy.Unsafe }));
        Assert.Empty(Directory.GetFiles(outside));
    }

    [Fact]
    public void EntriesSharingALocalHeaderAreRejected()
    {
        var raw = new RawZip();
        var e = RawZip.Deflated("one", ZipTests.Text(5000));
        raw.Add(e);
        var twin = RawZip.Deflated("two", ZipTests.Text(5000));
        raw.Add(twin);
        twin.OffsetOverride = e.LocalOffset;
        var ex = Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(raw.Finish())));
        Assert.Contains("overlap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyEntriesPointingAtOneBlobAreRejectedBeforeAnyIsRead()
    {
        // The classic "quoted overlap" bomb: one small compressed blob that thousands of directory entries share.
        var raw = new RawZip();
        var e = RawZip.Deflated("blob", new byte[10_000_000]);
        raw.Add(e);
        for (int i = 0; i < 200; i++)
        {
            var twin = RawZip.Deflated("n" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), []);
            twin.Data = e.Data;
            twin.Plain = e.Plain;
            raw.Add(twin);
            twin.OffsetOverride = e.LocalOffset;
        }

        byte[] zip = raw.Finish();
        using var reader = new ZipReader(new MemoryStream(zip));
        Assert.Throws<CompressionFormatException>(() => reader.GetNextEntry());
    }

    [Fact]
    public void CompressedSizeThatRunsIntoTheNextEntryIsRejected()
    {
        var raw = new RawZip();
        var first = RawZip.Stored("first", new byte[100]);
        first.DeclaredCompressed = 100_000;
        first.DeclaredSize = 100_000;
        raw.Add(first);
        raw.Add(RawZip.Stored("second", new byte[100]));
        Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(raw.Finish())));
    }

    [Fact]
    public void LocalHeaderThatDisagreesWithTheDirectoryIsRejected()
    {
        var raw = new RawZip();
        raw.Add(RawZip.Stored("name", "abcd"u8.ToArray()));
        byte[] zip = raw.Finish();
        zip[30] ^= 1;   // first byte of the local header's name
        Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(zip)));
        byte[] zip2 = new RawZip().Add(RawZip.Stored("name", "abcd"u8.ToArray())).Finish();
        zip2[8] = 8; // local method differs
        Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(zip2)));
    }

    [Fact]
    public void EntryCountThatDoesNotFitTheDirectoryIsRejected()
    {
        byte[] zip = new RawZip().Add(RawZip.Stored("a", [1])).Finish(countOverride: 60_000);
        var ex = Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(zip)));
        Assert.Contains("entries", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryCountOverTheLimitIsATypedLimitError()
    {
        var raw = new RawZip();
        for (int i = 0; i < 50; i++)
        {
            raw.Add(RawZip.Stored("f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), []));
        }

        byte[] zip = raw.Finish();
        var options = new ZipReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 10 } };
        Assert.Throws<CompressionLimitException>(() => ZipTests.ReadAll(new MemoryStream(zip), options));
        Assert.Throws<CompressionLimitException>(() => ZipTests.ReadAll(new ForwardOnly(new MemoryStream(zip)), options));
    }

    [Fact]
    public void DeclaredTotalSizeOverTheLimitIsRefusedBeforeReading()
    {
        var raw = new RawZip();
        var e = RawZip.Deflated("big", new byte[1000]);
        e.DeclaredSize = 5_000_000_000 > uint.MaxValue ? 4_000_000_000 : 5_000_000_000;
        raw.Add(e);
        var options = new ZipReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 1 << 20 } };
        using var reader = new ZipReader(new MemoryStream(raw.Finish()), options);
        Assert.Throws<CompressionLimitException>(() => reader.GetNextEntry());
    }

    [Fact]
    public void ExpansionBombIsStoppedByTheRatioLimit()
    {
        using var temp = new TempDir();
        var raw = new RawZip();
        raw.Add(RawZip.Deflated("zeros", new byte[200 << 20]));
        byte[] zip = raw.Finish();
        Assert.True(zip.Length < 400_000);
        var ex = Assert.Throws<CompressionLimitException>(() => Extract(zip, temp.Combine("t")));
        Assert.Contains("MaxExpansionRatio", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Combine("t/zeros")));
    }

    [Fact]
    public void DataDescriptorEntriesCannotHideASizeBomb()
    {
        var raw = new RawZip();
        var e = RawZip.Deflated("zeros", new byte[64 << 20]);
        e.Descriptor = true;
        raw.Add(e);
        byte[] zip = raw.Finish();
        var options = new ZipReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 1 << 20 } };
        using var reader = new ZipReader(new ForwardOnly(new MemoryStream(zip), 4096), options);
        Assert.NotNull(reader.GetNextEntry());
        using Stream s = reader.OpenEntryStream();
        byte[] buffer = new byte[65536];
        Assert.Throws<CompressionLimitException>(() =>
        {
            while (s.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        });
    }

    [Fact]
    public void TruncatedAtEveryOffsetOnlyTypedErrorsEscape()
    {
        byte[] zip = Simple();
        for (int cut = 0; cut < zip.Length; cut++)
        {
            byte[] part = zip[..cut];
            AssertTyped(() => ZipTests.ReadAll(new MemoryStream(part)));
            AssertTyped(() => ZipTests.ReadAll(new ForwardOnly(new MemoryStream(part), 11)));
        }
    }

    [Fact]
    public void ByteFlipsOnlyGiveTypedErrors()
    {
        byte[] zip = Simple();
        var rng = new Random(42);
        for (int i = 0; i < 1500; i++)
        {
            byte[] bad = (byte[])zip.Clone();
            int flips = 1 + rng.Next(3);
            for (int k = 0; k < flips; k++)
            {
                bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
            }

            AssertTyped(() => ZipTests.ReadAll(new MemoryStream(bad)));
            AssertTyped(() => ZipTests.ReadAll(new ForwardOnly(new MemoryStream(bad), 13)));
        }
    }

    private static void AssertTyped(Action action)
    {
        try
        {
            action();
        }
        catch (CompressionException)
        {
        }
    }

    [Fact]
    public void SpannedArchivesAreUnsupported()
    {
        byte[] zip = new RawZip().Add(RawZip.Stored("a", [1])).Finish();
        zip[zip.Length - 22 + 4] = 1;   // this disk number
        Assert.Throws<ArchiveUnsupportedException>(() => ZipTests.ReadAll(new MemoryStream(zip)));
    }

    [Fact]
    public void ZipInsideZipIsJustAnEntryAndLongNamesAreLimited()
    {
        using var temp = new TempDir();
        byte[] inner = Simple();
        var raw = new RawZip();
        raw.Add(RawZip.Stored("inner.zip", inner));
        raw.Add(RawZip.Stored(new string('x', 5000), [1]));
        byte[] zip = raw.Finish();
        Assert.Throws<CompressionLimitException>(() => Extract(zip, temp.Combine("t")));
        var raw2 = new RawZip().Add(RawZip.Stored("inner.zip", inner));
        Extract(raw2.Finish(), temp.Combine("u"));
        Assert.Equal(inner, File.ReadAllBytes(temp.Combine("u/inner.zip")));
    }

    [Fact]
    public void ZipWithPrependedDataStillOpens()
    {
        byte[] stub = new byte[1000];
        new Random(1).NextBytes(stub);
        byte[] zip = new RawZip().Add(RawZip.Stored("a", "payload"u8.ToArray())).Finish();
        byte[] sfx = [.. stub, .. zip];
        // The offsets in the directory are relative to the start of the zip data, not of the file.
        var got = ZipTests.ReadAll(new MemoryStream(sfx));
        Assert.Equal("payload"u8.ToArray(), got["a"]);
    }

    [Fact]
    public void CentralDirectoryOffsetIsValidated()
    {
        byte[] zip = new RawZip().Add(RawZip.Stored("a", [1])).Finish();
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(zip.Length - 22 + 16), 0x7fffffff);
        Assert.Throws<CompressionFormatException>(() => ZipTests.ReadAll(new MemoryStream(zip)));
    }
}
