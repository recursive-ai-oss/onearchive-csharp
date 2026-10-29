using System.Security.Cryptography;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>Archives written by the pinned `rar` 7.23 (tools/rar-testdata/generate.sh), read back.</summary>
public class RarReaderTests
{
    internal const string Unicode = "\u00fcn\u00ef-c\u00f8d\u00e9-\u6587\u4ef6-\U0001F642.txt";

    internal static string Fixture(string relative) => Path.Combine(AppContext.BaseDirectory, "testdata", "rar", relative);

    private static Dictionary<string, string> Sources()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(Fixture("sources.sha256")))
        {
            int sp = line.IndexOf("  ", StringComparison.Ordinal);
            map[line[(sp + 2)..]] = line[..sp];
        }

        return map;
    }

    internal static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static RarReader Open(string name, RarReaderOptions? options = null) => new(File.OpenRead(Fixture(name)), options);

    private static List<RarEntry> List(string name)
    {
        using RarReader r = Open(name);
        var list = new List<RarEntry>();
        while (r.GetNextEntry() is { } e)
        {
            list.Add(e);
        }

        return list;
    }

    [Fact]
    public void ListsEveryEntryWithItsMetadata()
    {
        List<RarEntry> entries = List("stored.rar");
        Assert.Equal(
            ["hello.txt", "docs/sub/notes.txt", "empty.txt", "random.bin", Unicode, "link-to-hello", "escaping-link", "hard-to-hello", "docs/sub", "docs", "empty-dir"],
            entries.Select(e => e.Name).ToArray());

        RarEntry hello = entries[0];
        Assert.Equal(ArchiveEntryKind.File, hello.Kind);
        Assert.Equal(920, hello.Size);
        Assert.Equal(920, hello.PackedSize);
        Assert.Equal(0, hello.Method);
        Assert.True(hello.IsStored);
        Assert.Equal(0, hello.CompressionVersion);
        Assert.False(hello.IsSolid);
        Assert.Equal(128 * 1024, hello.DictionarySize);
        Assert.Equal(RarHostOs.Unix, hello.HostOs);
        Assert.Equal((UnixFileMode)0b110_100_100, hello.Mode);
        Assert.Equal(0x81A4UL, hello.Attributes);
        Assert.Equal(0x1DF9792Cu, hello.Crc32);
        Assert.Equal("root", hello.UserName);
        Assert.Equal("root", hello.GroupName);
        Assert.Equal(0, hello.Uid);

        // The file time record keeps nanoseconds; the entry model keeps 100 ns: 2024-03-05 06:07:08.123456789 UTC.
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 6, 7, 8, TimeSpan.Zero).AddTicks(1_234_567), hello.ModifiedTime);
        Assert.NotNull(hello.CreationTime);
        Assert.NotNull(hello.AccessTime);

        Assert.Equal(0, entries[2].Size);
        Assert.Equal(0u, entries[2].Crc32);
        Assert.Equal((UnixFileMode)0b110_000_000, entries[3].Mode);

        RarEntry link = entries[5];
        Assert.Equal(ArchiveEntryKind.Symlink, link.Kind);
        Assert.Equal("hello.txt", link.LinkTarget);
        Assert.Equal(RarRedirectionType.UnixSymlink, link.Redirection);
        Assert.Equal("../../outside/target", entries[6].LinkTarget);

        RarEntry hard = entries[7];
        Assert.Equal(ArchiveEntryKind.HardLink, hard.Kind);
        Assert.Equal("hello.txt", hard.LinkTarget);

        RarEntry dir = entries[8];
        Assert.Equal(ArchiveEntryKind.Directory, dir.Kind);
        Assert.Equal(0, dir.Size);
        Assert.Equal((UnixFileMode)0b111_101_101, dir.Mode);
        Assert.Equal(new DateTimeOffset(2021, 1, 2, 3, 4, 5, TimeSpan.Zero), dir.ModifiedTime);
    }

    [Theory]
    [InlineData("stored.rar")]
    [InlineData("stored-blake2.rar")]
    [InlineData("stored-solid.rar")]
    [InlineData("stored-comment.rar")]
    [InlineData("stored-recovery.rar")]
    [InlineData("stored-quickopen.rar")]
    [InlineData("stored-locked.rar")]
    [InlineData("encrypted-data.rar", true)]
    public void StoredEntriesExtractByteExact(string name, bool skipEncrypted = false)
    {
        Dictionary<string, string> sources = Sources();
        using RarReader r = Open(name);
        int files = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File || (skipEncrypted && e.IsEncrypted))
            {
                continue;
            }

            Assert.Equal(sources[e.Name], Sha(ReadAll(r.OpenEntryStream())));
            files++;
        }

        Assert.Equal(skipEncrypted ? 0 : 5, files);
    }

    [Fact]
    public void Blake2spHashesAreListedAndChecked()
    {
        List<RarEntry> entries = List("stored-blake2.rar");
        RarEntry hello = entries[0];
        Assert.Null(hello.Crc32);
        Assert.NotNull(hello.Blake2sp);
        Assert.Equal(32, hello.Blake2sp!.Value.Length);

        // The stored hash is the BLAKE2sp of the file, produced by rar and recomputed here by the reader.
        Dictionary<string, string> sources = Sources();
        using RarReader r = Open("stored-blake2.rar");
        r.GetNextEntry();
        Assert.Equal(sources["hello.txt"], Sha(ReadAll(r.OpenEntryStream())));
    }

    [Fact]
    public void ArchiveHeaderFieldsAreExposed()
    {
        using (RarReader r = Open("stored-comment.rar"))
        {
            Assert.Equal("An archive comment.\nSecond line.\n", r.Header.Comment);
            Assert.False(r.Header.IsVolume);
            Assert.Equal(0, r.Header.SignatureOffset);
            Assert.Equal("hello.txt", r.GetNextEntry()!.Name);
        }

        using (RarReader r = Open("stored.rar"))
        {
            Assert.Null(r.Header.Comment);
            Assert.True(r.Header.QuickOpenOffset > 0);
        }

        // rar ignores -s for stored data and does not mark the archive solid; a compressed one is (see SolidFlagIsReported).
        using (RarReader r = Open("stored-solid.rar"))
        {
            Assert.False(r.Header.IsSolid);
        }

        using (RarReader r = Open("stored-locked.rar"))
        {
            Assert.True(r.Header.IsLocked);
        }

        using (RarReader r = Open("stored-recovery.rar"))
        {
            Assert.True(r.Header.HasRecoveryRecord);
            Assert.True(r.Header.RecoveryRecordOffset > 0);
        }
    }

    [Theory]
    [InlineData("m1.rar", 1)]
    [InlineData("m2.rar", 2)]
    [InlineData("m3.rar", 3)]
    [InlineData("m4.rar", 4)]
    [InlineData("m5.rar", 5)]
    [InlineData("m3-solid.rar", 3)]
    [InlineData("m3-blake2.rar", 3)]
    public void CompressedEntriesListWithMetadataAndFailOnOpen(string name, int method)
    {
        using RarReader r = Open(name);
        // Solid archives reorder entries, so take the first file that is compressed.
        RarEntry hello = r.GetNextEntry()!;
        while (hello.Kind != ArchiveEntryKind.File || hello.IsStored || hello.Size == 0)
        {
            hello = r.GetNextEntry()!;
        }

        Assert.Equal(method, hello.Method);
        Assert.False(hello.IsStored);
        Assert.True(hello.PackedSize < hello.Size);
        Assert.True(hello.ModifiedTime is not null);

        RarUnsupportedException ex = Assert.Throws<RarUnsupportedException>(() => r.OpenEntryStream());
        Assert.Equal(RarUnsupportedKind.CompressedEntry, ex.Kind);
        Assert.Contains("RAR compressed entries are not supported yet", ex.Message, StringComparison.Ordinal);
        Assert.Contains(hello.Name, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"method {method}", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"dictionary size {hello.DictionarySize} bytes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(hello.Name, ex.EntryName);
        Assert.Equal(method, ex.Method);
        Assert.Equal(hello.DictionarySize, ex.DictionarySize);
        Assert.IsAssignableFrom<ArchiveUnsupportedException>(ex);

        // The rest of the archive still lists after the failed open.
        int more = 0;
        while (r.GetNextEntry() is not null)
        {
            more++;
        }

        Assert.True(more >= 1);
    }

    [Fact]
    public void SolidFlagIsReported()
    {
        using (RarReader r = Open("m3-solid.rar"))
        {
            Assert.True(r.Header.IsSolid);
            var entries = new List<RarEntry>();
            while (r.GetNextEntry() is { } e)
            {
                entries.Add(e);
            }

            Assert.False(entries[0].IsSolid);
            Assert.Contains(entries, e => e.IsSolid);
        }
    }

    [Fact]
    public void EncryptedHeadersAreATypedError()
    {
        using RarReader r = Open("encrypted-headers.rar");
        RarUnsupportedException ex = Assert.Throws<RarUnsupportedException>(() => r.GetNextEntry());
        Assert.Equal(RarUnsupportedKind.EncryptedHeaders, ex.Kind);
        Assert.Contains("encrypted headers are not supported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptedDataListsAndFailsOnOpen()
    {
        using RarReader r = Open("encrypted-data.rar");
        RarEntry e = r.GetNextEntry()!;
        Assert.Equal("hello.txt", e.Name);
        Assert.True(e.IsEncrypted);
        RarUnsupportedException ex = Assert.Throws<RarUnsupportedException>(() => r.OpenEntryStream());
        Assert.Equal(RarUnsupportedKind.EncryptedEntry, ex.Kind);
    }

    [Fact]
    public void ReadsFromASourceThatCannotSeek()
    {
        Dictionary<string, string> sources = Sources();
        using var r = new RarReader(new ForwardOnlyStream(File.ReadAllBytes(Fixture("stored-blake2.rar"))));
        int files = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.File)
            {
                Assert.Equal(sources[e.Name], Sha(ReadAll(r.OpenEntryStream())));
                files++;
            }
        }

        Assert.Equal(5, files);
    }

    [Fact]
    public async Task AsyncReadMatchesSyncRead()
    {
        Dictionary<string, string> sources = Sources();
        await using var r = new RarReader(File.OpenRead(Fixture("stored.rar")));
        while (await r.GetNextEntryAsync() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            await using Stream s = r.OpenEntryStream();
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            Assert.Equal(sources[e.Name], Sha(ms.ToArray()));
        }
    }

    [Fact]
    public void SkippingEntriesWithoutReadingThemWorks()
    {
        using RarReader r = Open("stored.rar");
        r.GetNextEntry();
        r.GetNextEntry();
        RarEntry third = r.GetNextEntry()!;
        Assert.Equal("empty.txt", third.Name);
        RarEntry random = r.GetNextEntry()!;
        Stream s = r.OpenEntryStream();
        Assert.Equal(random.Size, ReadAll(s).Length);
        r.GetNextEntry();
        Assert.Throws<InvalidOperationException>(() => s.ReadByte());
    }

    [Fact]
    public void CorruptStoredDataFailsTheChecksumUnlessVerificationIsOff()
    {
        byte[] data = File.ReadAllBytes(Fixture("stored.rar"));
        RarEntry first;
        using (RarReader r = new(new MemoryStream(data)))
        {
            first = r.GetNextEntry()!;
        }

        data[first.DataOffset + 10] ^= 0x40;
        using (RarReader r = new(new MemoryStream(data)))
        {
            r.GetNextEntry();
            Stream s = r.OpenEntryStream();
            CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => ReadAll(s));
            Assert.Equal("rar", ex.Format);
            Assert.Contains("CRC-32", ex.Message, StringComparison.Ordinal);
        }

        using (RarReader r = new(new MemoryStream(data), new RarReaderOptions { VerifyChecksum = false }))
        {
            r.GetNextEntry();
            Assert.Equal(920, ReadAll(r.OpenEntryStream()).Length);
        }

        // The same flip in a BLAKE2sp-checked archive.
        byte[] b2 = File.ReadAllBytes(Fixture("stored-blake2.rar"));
        using (RarReader r = new(new MemoryStream(b2)))
        {
            first = r.GetNextEntry()!;
        }

        b2[first.DataOffset + 10] ^= 0x40;
        using (RarReader r = new(new MemoryStream(b2)))
        {
            r.GetNextEntry();
            Stream s = r.OpenEntryStream();
            CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => ReadAll(s));
            Assert.Contains("BLAKE2sp", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SelfExtractingPrefixIsSkipped()
    {
        byte[] rar = File.ReadAllBytes(Fixture("stored-comment.rar"));
        long plainOffset;
        using (RarReader plain = Open("stored-comment.rar"))
        {
            plainOffset = plain.GetNextEntry()!.HeaderOffset;
        }

        byte[] sfx = [.. new byte[3000].Select((_, i) => (byte)(i * 7)), .. rar];
        foreach (bool seekable in new[] { true, false })
        {
            Stream s = seekable ? new MemoryStream(sfx) : new ForwardOnlyStream(sfx);
            using var r = new RarReader(s);
            Assert.Equal(3000, r.Header.SignatureOffset);
            Assert.Equal("An archive comment.\nSecond line.\n", r.Header.Comment);
            RarEntry e = r.GetNextEntry()!;
            Assert.Equal("hello.txt", e.Name);
            Assert.Equal(plainOffset + 3000, e.HeaderOffset);
            Assert.Equal(920, ReadAll(r.OpenEntryStream()).Length);
        }

        using var strict = new RarReader(new MemoryStream(sfx), new RarReaderOptions { MaxSfxSize = 0 });
        Assert.Throws<CompressionFormatException>(() => strict.GetNextEntry());
        using var small = new RarReader(new MemoryStream(sfx), new RarReaderOptions { MaxSfxSize = 100 });
        Assert.Throws<CompressionFormatException>(() => small.GetNextEntry());
    }

    [Fact]
    public void Rar4IsATypedErrorEverywhere()
    {
        byte[] rar4 = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0xCF, 0x90, 0x73, 0x00, 0x00, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        Assert.Equal(ArchiveFormat.Rar, ArchiveDetector.DetectFormat(rar4));
        using var r = new RarReader(new MemoryStream(rar4));
        RarUnsupportedException ex = Assert.Throws<RarUnsupportedException>(() => r.GetNextEntry());
        Assert.Equal(RarUnsupportedKind.Rar4Archive, ex.Kind);
        Assert.Contains("RAR 4 archives are not supported yet", ex.Message, StringComparison.Ordinal);

        ex = Assert.Throws<RarUnsupportedException>(() => Archive.Open(new MemoryStream(rar4)));
        Assert.Equal(RarUnsupportedKind.Rar4Archive, ex.Kind);
    }
}

/// <summary>A readable stream that cannot seek, handing out a few bytes at a time.</summary>
internal sealed class ForwardOnlyStream(byte[] data, int maxRead = 1000) : Stream
{
    private int _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = Math.Min(Math.Min(count, maxRead), data.Length - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
