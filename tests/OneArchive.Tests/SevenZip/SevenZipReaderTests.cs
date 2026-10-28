using System.IO.Compression;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;
using static RecursiveAI.OneArchive.Tests.SevenZip.SevenZipFixtures;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

public class SevenZipReaderTests
{
    public static TheoryData<string> Vectors() =>
    [
        "copy-solid", "copy-nonsolid", "copy-multifolder", "copy-times", "copy-chain",
        "deflate-solid", "deflate-nonsolid", "bzip2-solid", "bzip2-nonsolid",
        "default", "lzma-solid", "lzma-header", "lzma2-nonsolid", "bcj-lzma", "delta-lzma2", "arm64-lzma2",
    ];

    private static byte[] ReadAll(Stream s)
    {
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReadsEveryEntryByteExact(string name)
    {
        var expected = Files();
        var seenDirs = new HashSet<string>();
        using var fs = File.OpenRead(Vector(name));
        using var r = new SevenZipReader(fs);
        int files = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.Directory)
            {
                seenDirs.Add(e.Name);
                Assert.Equal(Mtime, e.ModifiedTime!.Value.ToUnixTimeSeconds());
                Assert.Equal(0, e.Size);
            }
            else if (e.Kind == ArchiveEntryKind.Symlink)
            {
                Assert.Equal("link-to-a", e.Name);
                Assert.Equal("a.txt", e.LinkTarget);
            }
            else
            {
                byte[] data = expected[e.Name];
                Assert.Equal(data.Length, e.Size);
                Assert.Equal(data, ReadAll(r.OpenEntryStream()));
                Assert.Equal(Mtime, e.ModifiedTime!.Value.ToUnixTimeSeconds());
                files++;
            }
        }

        Assert.Equal(expected.Count, files);
        Assert.Equal(Directories.Order(StringComparer.Ordinal), seenDirs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReportsModeAttributesAndCreationTime()
    {
        using var fs = File.OpenRead(Vector("copy-times"));
        using var r = new SevenZipReader(fs);
        var all = r.Items.ToDictionary(e => e.Name);
        SevenZipEntry a = all["a.txt"];
        Assert.Equal((UnixFileMode)0b110_100_000, a.Mode);
        Assert.NotNull(a.CreationTime);
        Assert.NotNull(a.AccessTime);
        Assert.True((a.Attributes!.Value & 0x8000) != 0);
        Assert.Equal((UnixFileMode)0b111_101_000, all["dir"].Mode);
        Assert.Equal(ArchiveEntryKind.Directory, all["dir"].Kind);
        Assert.Equal(ArchiveEntryKind.Symlink, all["link-to-a"].Kind);
        Assert.True(all["empty.txt"].IsEmptyStream);
        Assert.Equal(ArchiveEntryKind.File, all["empty.txt"].Kind);
        Assert.Equal(Crc32.Compute("hello 7z\n"u8), a.Crc32);
    }

    [Fact]
    public void ExposesFolderGraphs()
    {
        using var fs = File.OpenRead(Vector("copy-chain"));
        using var r = new SevenZipReader(fs);
        Assert.False(r.IsHeaderEncoded);
        SevenZipFolder f = r.Folders[0];
        Assert.Equal(2, f.Coders.Count);
        Assert.Single(f.BindPairs);
        Assert.All(f.Coders, c => Assert.Equal(0UL, c.MethodId));
        using var fs2 = File.OpenRead(Vector("copy-nonsolid"));
        using var r2 = new SevenZipReader(fs2);
        Assert.True(r2.Folders.Count > 100);
    }

    [Fact]
    public void Bcj2GraphIsParsedAndItsContentRefusedByName()
    {
        using var fs = File.OpenRead(Vector("bcj2-graph"));
        using var r = new SevenZipReader(fs);
        SevenZipFolder f = r.Folders[0];
        Assert.Equal(4, f.Coders.Count);
        SevenZipCoder bcj2 = f.Coders.Single(c => c.MethodId == 0x0303011BUL);
        Assert.Equal(4, bcj2.NumInStreams);
        Assert.Equal(3, f.BindPairs.Count);
        Assert.Equal(4, f.PackedStreams.Count);
        Assert.Equal(60000, r.GetNextEntry()!.Size);
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => r.OpenEntryStream().ReadByte());
        Assert.Contains("0x303011B", ex.Message);
    }

    [Fact]
    public void DefaultOutputHasAnEncodedLzmaHeaderAndLzma2Folder()
    {
        using var fs = File.OpenRead(Vector("default"));
        using var r = new SevenZipReader(fs);
        Assert.Equal(0x21UL, r.Folders[0].Coders[^1].MethodId);
        Assert.True(r.IsHeaderEncoded);
        using var fs2 = File.OpenRead(Vector("bcj-lzma"));
        using var r2 = new SevenZipReader(fs2);
        Assert.Contains(r2.Folders[0].Coders, c => c.MethodId == 0x030101);
        Assert.Contains(r2.Folders[0].Coders, c => c.MethodId is 0x04 or 0x03030103);
    }

    [Fact]
    public void AFailedEntryDoesNotBreakLaterReads()
    {
        byte[] full = File.ReadAllBytes(Vector("deflate-solid"));
        // Damage the packed data near its end: the last entries of the solid folder fail, the first ones do not.
        full[32 + 20000] ^= 0xFF;
        var expected = Files();
        using var r = new SevenZipReader(new MemoryStream(full));
        var files = r.Items.Where(e => e.Kind == ArchiveEntryKind.File).ToList();
        SevenZipEntry first = files.First(e => e.Name == "a.txt");
        SevenZipEntry? bad = null;
        foreach (SevenZipEntry e in files.OrderBy(e => e.FolderOffset))
        {
            try
            {
                ReadAll(r.OpenEntryStream(e));
            }
            catch (CompressionException)
            {
                bad = e;
                break;
            }
        }

        Assert.NotNull(bad);
        Assert.ThrowsAny<CompressionException>(() => ReadAll(r.OpenEntryStream(bad)));
        Assert.Equal(expected[first.Name], ReadAll(r.OpenEntryStream(first)));
        Assert.ThrowsAny<CompressionException>(() => ReadAll(r.OpenEntryStream(bad)));
        Assert.Equal(expected[first.Name], ReadAll(r.OpenEntryStream(first)));
    }

    [Fact]
    public void ForwardRandomAccessReusesTheFolderDecoder()
    {
        var expected = Files();
        using var fs = File.OpenRead(Vector("lzma-solid"));
        using var r = new SevenZipReader(fs);
        foreach (SevenZipEntry e in r.Items.Where(e => e.Kind == ArchiveEntryKind.File).OrderBy(e => e.FolderOffset))
        {
            using Stream s = r.OpenEntryStream(e);
            Assert.Equal(expected[e.Name], ReadAll(s));
        }
    }

    [Theory]
    [InlineData("deflate-solid")]
    [InlineData("bzip2-nonsolid")]
    [InlineData("copy-multifolder")]
    public void RandomAccessInAnyOrder(string name)
    {
        var expected = Files();
        using var fs = File.OpenRead(Vector(name));
        using var r = new SevenZipReader(fs);
        var items = r.Items.Where(e => e.Kind == ArchiveEntryKind.File).Reverse().ToList();
        foreach (SevenZipEntry e in items)
        {
            using Stream s = r.OpenEntryStream(e);
            Assert.Equal(expected[e.Name], ReadAll(s));
        }

        // Seeking inside an entry.
        SevenZipEntry big = r.Items.First(e => e.Name == "dir/b.bin");
        using Stream seek = r.OpenEntryStream(big);
        seek.Seek(15000, SeekOrigin.Begin);
        var buf = new byte[100];
        seek.ReadExactly(buf);
        Assert.Equal(expected["dir/b.bin"].AsSpan(15000, 100).ToArray(), buf);
        seek.Seek(10, SeekOrigin.Begin);
        seek.ReadExactly(buf);
        Assert.Equal(expected["dir/b.bin"].AsSpan(10, 100).ToArray(), buf);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ArchiveExtractsTheTree(string name)
    {
        using var tmp = new TempDir();
        Archive.Extract(Vector(name), tmp.Root);
        foreach ((string rel, byte[] data) in Files())
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(tmp.Root, rel)));
        }

        foreach (string d in Directories)
        {
            Assert.True(Directory.Exists(Path.Combine(tmp.Root, d)), d);
        }
    }

    [Fact]
    public void ArchiveOpenDetectsAndListsFromANonSeekableStream()
    {
        byte[] bytes = File.ReadAllBytes(Vector("deflate-solid"));
        using var forward = new ForwardOnlyStream(bytes);
        using Archive a = Archive.Open(forward);
        Assert.Equal(ArchiveFormat.SevenZip, a.Format);
        using ArchiveReader rd = a.OpenReader();
        Assert.Equal(Files().Count, Count(rd));
    }

    private static int Count(ArchiveReader r)
    {
        int n = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.File)
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void ArchiveEntriesAndOpenEntry()
    {
        using Archive a = Archive.Open(Vector("bzip2-solid"));
        Assert.Equal(ArchiveFormat.SevenZip, a.Format);
        ArchiveEntry e = a.Find("dir/sub/c.txt")!;
        using Stream s = a.OpenEntry(e);
        Assert.Equal(Files()["dir/sub/c.txt"], ReadAll(s));
    }

    [Fact]
    public void EmptyArchiveAndLoneEmptyFile()
    {
        var empty = Build([], []);
        Assert.Equal(32, empty.Length);
        using (var r = new SevenZipReader(new MemoryStream(empty)))
        {
            Assert.Null(r.GetNextEntry());
        }

        using var fs = File.OpenRead(Vector("one-empty-file"));
        using var r2 = new SevenZipReader(fs);
        SevenZipEntry e = r2.GetNextEntry()!;
        Assert.Equal("only-empty.txt", e.Name);
        Assert.Equal(0, e.Size);
        Assert.Empty(ReadAll(r2.OpenEntryStream()));
        Assert.Null(r2.GetNextEntry());
    }

    // ---- hand-assembled archives ----

    private static byte[] CopyArchive(byte[] data, string name = "f.txt")
    {
        byte[] streams = CopyStreams(data, data);
        byte[] header = Header(streams, 1, Names(name));
        return Build(data, header);
    }

    [Fact]
    public void HandAssembledArchiveReads()
    {
        using var r = new SevenZipReader(new MemoryStream(CopyArchive("abc"u8.ToArray())));
        SevenZipEntry e = r.GetNextEntry()!;
        Assert.Equal("f.txt", e.Name);
        Assert.Equal("abc"u8.ToArray(), ReadAll(r.OpenEntryStream()));
    }

    [Fact]
    public void EncodedHeaderWithCopyAndDeflate()
    {
        byte[] data = "payload"u8.ToArray();
        byte[] plainHeader = Header(CopyStreams(data, data), 1, Names("x.bin"));
        foreach (bool deflate in new[] { false, true })
        {
            byte[] stored = plainHeader;
            if (deflate)
            {
                using var m = new MemoryStream();
                using (var d = new DeflateStream(m, CompressionLevel.Optimal, true))
                {
                    d.Write(plainHeader);
                }

                stored = m.ToArray();
            }

            // Packed: data, then the (compressed) header. The encoded header's folder uses pack position = data length.
            byte[] coder = deflate ? Cat(0x03, 0x04, 0x01, 0x08) : Cat(0x01, 0x00);
            byte[] enc = Cat(0x17, 0x06, (ulong)data.Length, 1UL, 0x09, (ulong)stored.Length, 0x00,
                0x07, 0x0B, 1UL, 0, 1UL, coder, 0x0C, (ulong)plainHeader.Length, 0x0A, 1, Crc32.Compute(plainHeader), 0x00, 0x00);
            byte[] archive = Build([.. data, .. stored], enc);
            using var r = new SevenZipReader(new MemoryStream(archive));
            SevenZipEntry e = r.GetNextEntry()!;
            Assert.True(r.IsHeaderEncoded);
            Assert.Equal("x.bin", e.Name);
            Assert.Equal(data, ReadAll(r.OpenEntryStream()));
        }
    }

    [Fact]
    public void AntiItemsAreListedButNotReturned()
    {
        byte[] data = "abc"u8.ToArray();
        byte[] header = Header(CopyStreams(data, data), 2,
            Prop(0x0E, Bits(false, true)), Prop(0x10, Bits(true)), Names("keep.txt", "gone.txt"));
        using var r = new SevenZipReader(new MemoryStream(Build(data, header)));
        Assert.Equal(2, r.Items.Count);
        Assert.True(r.Items[1].IsAnti);
        Assert.Equal("keep.txt", r.GetNextEntry()!.Name);
        Assert.Null(r.GetNextEntry());
    }

    [Fact]
    public void BadSignatureAndCrcsAreTypedErrors()
    {
        byte[] good = CopyArchive("abc"u8.ToArray());
        foreach (int pos in new[] { 0, 6, 8, 12, 20, 28, good.Length - 1, good.Length - 5, 32 + 3 + 2 })
        {
            byte[] bad = (byte[])good.Clone();
            bad[pos] ^= 0x01;
            try
            {
                using var r = new SevenZipReader(new MemoryStream(bad));
                while (r.GetNextEntry() is not null)
                {
                    ReadAll(r.OpenEntryStream());
                }
            }
            catch (CompressionException)
            {
            }
        }

        byte[] sig = (byte[])good.Clone();
        sig[0] = (byte)'8';
        var ex = Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(sig)).GetNextEntry());
        Assert.Equal("7z", ex.Format);
        Assert.Equal(0, ex.Offset);

        byte[] crc = (byte[])good.Clone();
        crc[good.Length - 1] ^= 0xFF; // header body
        Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(crc)).GetNextEntry());
    }

    [Fact]
    public void ContentCrcMismatchIsReportedAtTheEnd()
    {
        byte[] good = CopyArchive("abc"u8.ToArray());
        byte[] bad = (byte[])good.Clone();
        bad[32] ^= 0x20; // a packed content byte
        using var r = new SevenZipReader(new MemoryStream(bad));
        r.GetNextEntry();
        Assert.Throws<CompressionFormatException>(() => ReadAll(r.OpenEntryStream()));
        using var r2 = new SevenZipReader(new MemoryStream(bad), new SevenZipReaderOptions { VerifyChecksum = false });
        r2.GetNextEntry();
        Assert.Equal(3, ReadAll(r2.OpenEntryStream()).Length);
    }

    [Fact]
    public void TruncationAtEveryOffsetIsATypedError()
    {
        foreach (string vec in new[] { "deflate-solid", "one-empty-file", "default", "delta-lzma2" })
        {
            byte[] full = File.ReadAllBytes(Vector(vec));
            int step = full.Length > 4000 ? 23 : 1;
            for (int n = 0; n < full.Length; n += step)
            {
                try
                {
                    using var r = new SevenZipReader(new MemoryStream(full, 0, n));
                    while (r.GetNextEntry() is not null)
                    {
                    }
                }
                catch (CompressionException)
                {
                }
            }
        }
    }

    [Fact]
    public void BitFlipsEndInTypedErrors()
    {
        foreach (string vec in new[] { "deflate-solid", "lzma-solid", "bcj-lzma", "default" })
        {
            FlipBits(File.ReadAllBytes(Vector(vec)));
        }
    }

    private static void FlipBits(byte[] full)
    {
        for (int i = 0; i < full.Length; i += 7)
        {
            byte[] bad = (byte[])full.Clone();
            bad[i] ^= (byte)(1 << (i % 8));
            try
            {
                using var r = new SevenZipReader(new MemoryStream(bad));
                while (r.GetNextEntry() is not null)
                {
                    ReadAll(r.OpenEntryStream());
                }
            }
            catch (CompressionException)
            {
            }
        }
    }

    [Fact]
    public void CoderGraphProblemsAreRefused()
    {
        // Three coders: coder 0 is the result and reads the packed stream; coders 1 and 2 feed each other.
        byte[] cyclic = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00,
            0x07, 0x0B, 1UL, 0, 3UL, Cat(0x01, 0x00), Cat(0x01, 0x00), Cat(0x01, 0x00),
            1UL, 2UL, 2UL, 1UL, // bonds: input 1 <- output 2, input 2 <- output 1
            0x0C, 3UL, 3UL, 3UL, 0x00, 0x00);
        byte[] header = Header(cyclic, 1, Names("a"));
        var ex = Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(Build("abc"u8.ToArray(), header))).GetNextEntry());
        Assert.Contains("coder", ex.Expected);

        // More coders than the limit.
        var coders = new List<byte>();
        for (int i = 0; i < 40; i++)
        {
            coders.AddRange(Cat(0x01, 0x00));
        }

        byte[] many = Cat(0x07, 0x0B, 1UL, 0, 40UL, coders.ToArray());
        byte[] h2 = Header(Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00, many), 1, Names("a"));
        Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(Build("abc"u8.ToArray(), h2))).GetNextEntry());
    }

    [Fact]
    public void HugeDeclaredSizesFailBeforeAllocating()
    {
        byte[] data = "abc"u8.ToArray();

        // A folder claiming 2^40 bytes: over MaxTotalSize.
        byte[] big = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00, 0x07, 0x0B, 1UL, 0, 1UL, Cat(0x01, 0x00), 0x0C, 1UL << 40, 0x00, 0x00);
        byte[] h = Header(big, 1, Names("a"));
        var ex = Assert.Throws<CompressionLimitException>(() => new SevenZipReader(new MemoryStream(Build(data, h))).GetNextEntry());
        Assert.Equal("MaxTotalSize", ex.LimitName);

        // A next-header size of 4 GiB.
        byte[] a = CopyArchive(data);
        BitConverter.GetBytes(1UL << 32).CopyTo(a, 20);
        BitConverter.GetBytes(Crc32.Compute(a.AsSpan(12, 20))).CopyTo(a, 8);
        var ex2 = Assert.Throws<CompressionLimitException>(() => new SevenZipReader(new MemoryStream(a)).GetNextEntry());
        Assert.Equal("MaxHeaderSize", ex2.LimitName);

        // A file count over MaxEntryCount.
        byte[] h3 = Header([], 1_000_000, Names("a"));
        var ex3 = Assert.Throws<CompressionLimitException>(() => new SevenZipReader(new MemoryStream(Build(data, h3)), new SevenZipReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 1000 } }).GetNextEntry());
        Assert.Equal("MaxEntryCount", ex3.LimitName);

        // A packed stream longer than the file.
        byte[] pk = Cat(0x06, 0UL, 1UL, 0x09, 1UL << 40, 0x00, 0x07, 0x0B, 1UL, 0, 1UL, Cat(0x01, 0x00), 0x0C, 3UL, 0x00, 0x00);
        var ex4 = Assert.Throws<CompressionFormatException>(() => new SevenZipReader(new MemoryStream(Build(data, Header(pk, 1, Names("a"))))).GetNextEntry());
        Assert.True(ex4.IsTruncated);
    }

    [Fact]
    public void HugeLzmaDictionaryIsRefusedBeforeAllocating()
    {
        byte[] data = "abc"u8.ToArray();
        byte[] streams = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00, 0x07, 0x0B, 1UL, 0, 1UL, Cat(0x23, 0x03, 0x01, 0x01, 5UL, 0x5D, 0, 0, 0, 0xFF), 0x0C, 3UL, 0x00, 0x00);
        byte[] h = Header(streams, 1, Names("a"));
        using var r = new SevenZipReader(new MemoryStream(Build(data, h)));
        r.GetNextEntry();
        var ex = Assert.Throws<CompressionLimitException>(() => r.OpenEntryStream().ReadByte());
        Assert.Contains("Dictionary", ex.LimitName);
        // Bad coder properties are a format error naming the coder.
        byte[] bad = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00, 0x07, 0x0B, 1UL, 0, 1UL, Cat(0x23, 0x03, 0x01, 0x01, 2UL, 0x5D, 0), 0x0C, 3UL, 0x00, 0x00);
        using var r2 = new SevenZipReader(new MemoryStream(Build(data, Header(bad, 1, Names("a")))));
        r2.GetNextEntry();
        Assert.Throws<CompressionFormatException>(() => r2.OpenEntryStream().ReadByte());
    }

    [Fact]
    public void UnsupportedMethodNamesTheId()
    {
        byte[] data = "abc"u8.ToArray();
        // PPMd (03 04 01), whose decoder is not available: listing works, opening the content is refused.
        byte[] streams = Cat(0x06, 0UL, 1UL, 0x09, 3UL, 0x00, 0x07, 0x0B, 1UL, 0, 1UL, Cat(0x03, 0x03, 0x04, 0x01), 0x0C, 3UL, 0x00, 0x00);
        byte[] h = Header(streams, 1, Names("a"));
        using var r = new SevenZipReader(new MemoryStream(Build(data, h)));
        r.GetNextEntry();
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => r.OpenEntryStream().ReadByte());
        Assert.Contains("0x30401", ex.Message);
    }

    [Fact]
    public void UnsafeNamesAreRefusedAtExtraction()
    {
        byte[] data = "abc"u8.ToArray();
        byte[] a = CopyArchive(data, "../evil.txt");
        using var tmp = new TempDir();
        string target = Path.Combine(tmp.Root, "out");
        Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(a), target));
        Assert.False(File.Exists(Path.Combine(tmp.Root, "evil.txt")));
    }

    [Fact]
    public void ZstdCoderDecodes()
    {
        byte[] plain = Repeat("zstd in 7z ", 100);
        byte[] frame = RecursiveAI.OneCompress.Zstd.Zstd.Compress(plain);
        byte[] streams = Cat(0x06, 0UL, 1UL, 0x09, (ulong)frame.Length, 0x00,
            0x07, 0x0B, 1UL, 0, 1UL, Cat(0x03, 0xF7, 0x11, 0x01), 0x0C, (ulong)plain.Length, 0x00,
            0x08, 0x0D, 1UL, 0x0A, 1, Crc32.Compute(plain), 0x00, 0x00);
        byte[] h = Header(streams, 1, Names("z"));
        using var r = new SevenZipReader(new MemoryStream(Build(frame, h)));
        r.GetNextEntry();
        Assert.Equal(plain, ReadAll(r.OpenEntryStream()));
    }
}

internal sealed class ForwardOnlyStream(byte[] data) : Stream
{
    private readonly MemoryStream _m = new(data);

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

    public override int Read(byte[] buffer, int offset, int count) => _m.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
