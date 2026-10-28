using RecursiveAI.OneArchive.Cpio;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Reads the committed vectors from the test output directory (see the Content items in the project file).</summary>
internal static class ContainerVectors
{
    public static byte[] Read(string area, string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", area, name));

    public static byte[] Big(int n)
    {
        byte[] b = new byte[n];
        for (int i = 0; i < n; i++)
        {
            b[i] = (byte)((i * 31) ^ (i >> 8));
        }

        return b;
    }
}

public class CpioTests
{
    private static readonly string s_long = "sub/" + new string('x', 120) + ".txt";

    // What testdata/cpio/generate.py puts in the tree: name, kind, content, permission bits, link target.
    private static readonly (string Name, ArchiveEntryKind Kind, byte[] Data, int Mode, string? Link)[] s_tree =
    [
        ("a.txt", ArchiveEntryKind.File, "hello\n"u8.ToArray(), 0x1A4, null),
        ("hard1", ArchiveEntryKind.File, "hello\n"u8.ToArray(), 0x1A4, null),
        ("sub", ArchiveEntryKind.Directory, [], 0x1ED, null),
        ("sub/b.bin", ArchiveEntryKind.File, "xyz"u8.ToArray(), 0x1A4, null),
        ("big.bin", ArchiveEntryKind.File, ContainerVectors.Big(70000), 0x180, null),
        ("empty", ArchiveEntryKind.File, [], 0x1A4, null),
        ("exec.sh", ArchiveEntryKind.File, "#!/bin/sh\n"u8.ToArray(), 0x1ED, null),
        ("\u00fcn\u00ef-\u65e5\u672c.txt", ArchiveEntryKind.File, "unicode"u8.ToArray(), 0x1A4, null),
        (s_long, ArchiveEntryKind.File, "long path"u8.ToArray(), 0x1A4, null),
        ("link", ArchiveEntryKind.Symlink, [], 0x1FF, "a.txt"),
    ];

    public static TheoryData<string> TreeVectors => new()
    {
        "tree.gnu-newc.cpio", "tree.gnu-crc.cpio", "tree.gnu-odc.cpio", "tree.bsdtar-newc.cpio", "tree.bsdtar-odc.cpio",
    };

    private static string Normalize(string name) => name.StartsWith("./", StringComparison.Ordinal) ? name[2..] : name;

    private static List<(CpioEntry Entry, byte[] Data)> ReadAll(Stream stream, CpioReaderOptions? options = null)
    {
        var list = new List<(CpioEntry, byte[])>();
        using var reader = new CpioReader(stream, options);
        while (reader.GetNextEntry() is { } e)
        {
            using var ms = new MemoryStream();
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = reader.OpenEntryStream();
                s.CopyTo(ms);
            }

            list.Add((e, ms.ToArray()));
        }

        return list;
    }

    private static void CheckTree(List<(CpioEntry Entry, byte[] Data)> items, bool forwardOnly)
    {
        var byName = items.Where(i => Normalize(i.Entry.Name) is not ("." or "")).ToDictionary(i => Normalize(i.Entry.Name), i => i);
        Assert.Equal(s_tree.Length, byName.Count);
        foreach (var (name, kind, data, mode, link) in s_tree)
        {
            var (e, got) = byName[name];
            Assert.Equal(kind, e.Kind);
            Assert.Equal(link, e.LinkTarget);
            Assert.Equal(1700000000, e.ModifiedTime!.Value.ToUnixTimeSeconds());
            if (kind != ArchiveEntryKind.Symlink)
            {
                Assert.Equal(mode, (int)e.Mode! & 0x1FF);
            }

            if (kind == ArchiveEntryKind.File)
            {
                Assert.Equal(data.Length, e.Size == 0 && forwardOnly && e.IsLinkStub ? data.Length : (int)e.Size);
                if (!(forwardOnly && e.IsLinkStub))
                {
                    Assert.True(data.AsSpan().SequenceEqual(got), name);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(TreeVectors))]
    public void ReadsReferenceToolArchivesOnASeekableStream(string vector)
    {
        byte[] bytes = ContainerVectors.Read("cpio", vector);
        CheckTree(ReadAll(new MemoryStream(bytes)), forwardOnly: false);
    }

    [Theory]
    [MemberData(nameof(TreeVectors))]
    public void ReadsReferenceToolArchivesOnAForwardOnlyStream(string vector)
    {
        byte[] bytes = ContainerVectors.Read("cpio", vector);
        CheckTree(ReadAll(new TarTests.ForwardOnly(bytes)), forwardOnly: true);
    }

    [Fact]
    public void HardLinkStubsGetTheDataOfTheMemberThatHoldsIt()
    {
        byte[] bytes = ContainerVectors.Read("cpio", "tree.gnu-newc.cpio");
        var items = ReadAll(new MemoryStream(bytes));
        var stubs = items.Where(i => i.Entry.IsLinkStub).ToList();
        Assert.Single(stubs);
        Assert.True(stubs[0].Entry.ContentFromLaterLink);
        Assert.Equal(0, stubs[0].Entry.StoredSize);
        Assert.Equal(6, stubs[0].Entry.Size);
        Assert.Equal("hello\n"u8.ToArray(), stubs[0].Data);

        // Forward-only: the stub is empty and says so.
        var forward = ReadAll(new TarTests.ForwardOnly(bytes));
        var stub = forward.Single(i => i.Entry.IsLinkStub);
        Assert.False(stub.Entry.ContentFromLaterLink);
        Assert.Empty(stub.Data);

        // Odc stores the data under every name.
        Assert.DoesNotContain(ReadAll(new MemoryStream(ContainerVectors.Read("cpio", "tree.gnu-odc.cpio"))), i => i.Entry.IsLinkStub);
    }

    [Fact]
    public void VariantAndHeaderFieldsAreExposed()
    {
        foreach (var (vector, format) in new[] { ("tree.gnu-newc.cpio", CpioFormat.Newc), ("tree.gnu-crc.cpio", CpioFormat.Crc), ("tree.gnu-odc.cpio", CpioFormat.Odc) })
        {
            using var reader = new CpioReader(new MemoryStream(ContainerVectors.Read("cpio", vector)));
            CpioEntry first = reader.GetNextEntry()!;
            Assert.Equal(format, first.Format);
            Assert.Equal(format, reader.Variant);
            Assert.Equal(0, first.HeaderOffset);
            Assert.True(first.Inode > 0);
            Assert.True(first.NLink >= 1);
            Assert.NotEqual(0u, first.RawMode & 0xF000);
        }
    }

    [Theory]
    [InlineData("devices.bsdtar-newc.cpio")]
    [InlineData("devices.bsdtar-odc.cpio")]
    public void DevicesAndFifosAreListed(string vector)
    {
        var items = ReadAll(new MemoryStream(ContainerVectors.Read("cpio", vector)));
        var c = items.Single(i => i.Entry.Name == "c1").Entry;
        var b = items.Single(i => i.Entry.Name == "b1").Entry;
        Assert.Equal(ArchiveEntryKind.CharacterDevice, c.Kind);
        Assert.Equal((8, 1), (c.DeviceMajor, c.DeviceMinor));
        Assert.Equal(ArchiveEntryKind.BlockDevice, b.Kind);
        Assert.Equal((259, 3), (b.DeviceMajor, b.DeviceMinor));
        Assert.Equal(ArchiveEntryKind.Fifo, items.Single(i => i.Entry.Name == "f1").Entry.Kind);
    }

    [Fact]
    public void DevicesAreNeverCreatedByExtraction()
    {
        using var t = new TempDir();
        byte[] bytes = ContainerVectors.Read("cpio", "devices.bsdtar-newc.cpio");
        var ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(bytes), t.Combine("out")));
        Assert.Equal("special-file", ex.Rule);
        var result = Archive.Extract(new MemoryStream(bytes), t.Combine("out2"), new ArchiveExtractOptions { SpecialFiles = SpecialFilePolicy.Skip });
        Assert.Equal(3, result.EntriesSkipped);
    }

    // ---- writer ----

    private static byte[] Write(CpioFormat format, Action<CpioWriter> fill, int blockSize = 512)
    {
        var ms = new MemoryStream();
        using (var w = new CpioWriter(ms, new CpioWriterOptions { Format = format, BlockSize = blockSize }, leaveOpen: true))
        {
            fill(w);
        }

        return ms.ToArray();
    }

    private static void FillTree(CpioWriter w)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        foreach (var (name, kind, data, mode, link) in s_tree.Where(t => t.Name != "hard1"))
        {
            w.WriteEntry(new ArchiveEntry { Name = name, Kind = kind, Size = data.Length, Mode = (UnixFileMode)mode, ModifiedTime = time, LinkTarget = link }, data);
        }
    }

    [Theory]
    [InlineData(CpioFormat.Newc)]
    [InlineData(CpioFormat.Crc)]
    [InlineData(CpioFormat.Odc)]
    public void RoundTripsOurOwnOutput(CpioFormat format)
    {
        byte[] bytes = Write(format, FillTree);
        Assert.Equal(0, bytes.Length % 512);
        var items = ReadAll(new MemoryStream(bytes));
        Assert.Equal(s_tree.Length - 1, items.Count);
        foreach (var (e, data) in items)
        {
            var expected = s_tree.Single(t => t.Name == e.Name);
            Assert.Equal(expected.Kind, e.Kind);
            Assert.Equal(expected.Link, e.LinkTarget);
            Assert.Equal(format, e.Format);
            Assert.Equal(expected.Mode, (int)e.Mode!);
            if (e.Kind == ArchiveEntryKind.File)
            {
                Assert.Equal(expected.Data, data);
            }
        }

        // The same bytes through the forward-only path.
        Assert.Equal(items.Count, ReadAll(new TarTests.ForwardOnly(bytes)).Count);
    }

    [Fact]
    public void WritingIsDeterministicAndPadsToTheBlockSize()
    {
        Assert.Equal(Write(CpioFormat.Newc, FillTree), Write(CpioFormat.Newc, FillTree));
        Assert.Equal(0, Write(CpioFormat.Newc, FillTree, 10240).Length % 10240);
        byte[] one = Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = "f", Size = 1 }, new byte[] { 7 }), 1);
        // 110 + "f\0" (112, padded to 112) + 1 data byte padded to 4 + trailer 110 + 11 padded to 124.
        Assert.Equal(112 + 4 + 124, one.Length);
        byte[] odc = Write(CpioFormat.Odc, w => w.WriteEntry(new ArchiveEntry { Name = "f", Size = 1 }, new byte[] { 7 }), 1);
        Assert.Equal(76 + 2 + 1 + 76 + 11, odc.Length);
    }

    [Fact]
    public void NewcInodesComeFromACounter()
    {
        byte[] bytes = Write(CpioFormat.Newc, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = "a" });
            w.WriteEntry(new ArchiveEntry { Name = "b" });
        });
        var items = ReadAll(new MemoryStream(bytes));
        Assert.Equal([1L, 2L], items.Select(i => i.Entry.Inode).ToArray());
        Assert.Equal(1, items[0].Entry.NLink);
    }

    [Fact]
    public void HardLinksWrittenAsGnuCpioDoRoundTrip()
    {
        byte[] bytes = Write(CpioFormat.Newc, w =>
        {
            w.WriteEntry(new CpioEntry { Name = "first", Inode = 9, NLink = 2 });
            w.WriteEntry(new CpioEntry { Name = "second", Inode = 9, NLink = 2, Size = 3 }, "abc"u8.ToArray());
        });
        var items = ReadAll(new MemoryStream(bytes));
        Assert.True(items[0].Entry.IsLinkStub);
        Assert.Equal("abc"u8.ToArray(), items[0].Data);
        Assert.Equal("abc"u8.ToArray(), items[1].Data);
        Assert.Throws<ArchiveUnsupportedException>(() => Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = "x", Kind = ArchiveEntryKind.HardLink, LinkTarget = "y" })));
    }

    [Fact]
    public void CrcContentThatCannotSeekIsBufferedWithinTheLimit()
    {
        byte[] data = ContainerVectors.Big(5000);
        var ms = new MemoryStream();
        using (var w = new CpioWriter(ms, new CpioWriterOptions { Format = CpioFormat.Crc, MaxBufferedSize = 4999 }, leaveOpen: true))
        {
            Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "big", Size = data.Length }, new TarTests.ForwardOnly(data)));
        }

        byte[] ok = Write(CpioFormat.Crc, w => w.WriteEntry(new ArchiveEntry { Name = "big", Size = data.Length }, new TarTests.ForwardOnly(data)));
        Assert.Equal(data, ReadAll(new MemoryStream(ok))[0].Data);
    }

    [Fact]
    public void FieldsThatDoNotFitAreRefusedByName()
    {
        var odc = new CpioWriterOptions { Format = CpioFormat.Odc };
        using var w = new CpioWriter(new MemoryStream(), odc);
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "u", Uid = 1 << 20 }));
        Assert.Contains("uid", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "a\0b" }));
    }

    // ---- hostile input ----

    [Fact]
    public void ChecksumMismatchIsReported()
    {
        byte[] bytes = ContainerVectors.Read("cpio", "tree.gnu-crc.cpio");
        int at = bytes.AsSpan().IndexOf("hello\n"u8);
        bytes[at] ^= 1;
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(bytes)));
        Assert.Equal("cpio", ex.Format);
        // Without the check the data reads back.
        ReadAll(new MemoryStream(bytes), new CpioReaderOptions { VerifyChecksum = false });
    }

    [Fact]
    public void BadMagicAndBinaryFormatAreTypedErrors()
    {
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream("070700000000"u8.ToArray())));
        Assert.Equal(0, ex.Offset);
        Assert.Throws<ArchiveUnsupportedException>(() => ReadAll(new MemoryStream([0xC7, 0x71, 0, 0, 0, 0, 0, 0])));
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream([])));
    }

    [Fact]
    public void HugeNameAndSizeFieldsFailBeforeAllocating()
    {
        byte[] bytes = Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = "abc" }));
        // name size 0xFFFFFFFF
        byte[] huge = (byte[])bytes.Clone();
        "FFFFFFFF"u8.CopyTo(huge.AsSpan(94));
        var ex = Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(huge)));
        Assert.Equal(nameof(ArchiveLimits.MaxPathLength), ex.LimitName);

        // file size 0xFFFFFFFF: refused by the total-size limit, not by reading
        byte[] big = (byte[])bytes.Clone();
        "FFFFFFFF"u8.CopyTo(big.AsSpan(54));
        var limit = Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(big)));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), limit.LimitName);

        // a symlink whose data is huge
        byte[] link = (byte[])bytes.Clone();
        "0000A1FF"u8.CopyTo(link.AsSpan(14));
        "FFFFFFFF"u8.CopyTo(link.AsSpan(54));
        Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(link)));
    }

    [Fact]
    public void NonHexHeaderFieldNamesTheOffset()
    {
        byte[] bytes = Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = "abc" }));
        bytes[30] = (byte)'g';
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(bytes)));
        Assert.Equal(30, ex.Offset);
    }

    [Theory]
    [InlineData(CpioFormat.Newc)]
    [InlineData(CpioFormat.Crc)]
    [InlineData(CpioFormat.Odc)]
    public void TruncationAtEveryOffsetIsATypedError(CpioFormat format)
    {
        byte[] bytes = Write(format, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = "dir/file", Size = 5 }, "12345"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "dir/file" });
            w.WriteEntry(new ArchiveEntry { Name = "e" });
        }, 1);
        int fullEntries = ReadAll(new MemoryStream(bytes)).Count;
        Assert.Equal(3, fullEntries);
        for (int cut = 0; cut < bytes.Length; cut++)
        {
            byte[] part = bytes.AsSpan(0, cut).ToArray();
            Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(part)));
            Assert.Throws<CompressionFormatException>(() => ReadAll(new TarTests.ForwardOnly(part)));
        }
    }

    [Fact]
    public void TheTrailerIsOptionalOnlyWhenAsked()
    {
        byte[] bytes = Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = "e" }), 1);
        byte[] noTrailer = bytes.AsSpan(0, 112).ToArray();
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(noTrailer)));
        Assert.Single(ReadAll(new MemoryStream(noTrailer), new CpioReaderOptions { RequireTrailer = false }));
    }

    [Theory]
    [InlineData(CpioFormat.Newc)]
    [InlineData(CpioFormat.Crc)]
    [InlineData(CpioFormat.Odc)]
    public void BitFlipsNeverEscapeTheErrorHierarchy(CpioFormat format)
    {
        byte[] bytes = Write(format, w =>
        {
            w.WriteEntry(new CpioEntry { Name = "a", Inode = 4, NLink = 2 });
            w.WriteEntry(new CpioEntry { Name = "b", Inode = 4, NLink = 2, Size = 5 }, "12345"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "l", Kind = ArchiveEntryKind.Symlink, LinkTarget = "a" });
        }, 1);
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int bit = 0; bit < 8; bit += 3)
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= (byte)(1 << bit);
                try
                {
                    ReadAll(new MemoryStream(copy));
                }
                catch (CompressionException)
                {
                }
            }
        }
    }

    [Fact]
    public void PathEscapesAreRefusedAtExtraction()
    {
        foreach (string name in new[] { "../evil", "/abs/evil", "a/../../evil", "a\0b".Replace('\0', 'x') + "/../../evil" })
        {
            using var t = new TempDir();
            byte[] bytes = Write(CpioFormat.Newc, w => w.WriteEntry(new ArchiveEntry { Name = name, Size = 1 }, new byte[] { 1 }));
            Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(bytes), t.Combine("out")));
            Assert.False(File.Exists(t.Combine("evil")));
        }
    }

    [Fact]
    public void LimitsApply()
    {
        byte[] bytes = Write(CpioFormat.Newc, w =>
        {
            for (int i = 0; i < 5; i++)
            {
                w.WriteEntry(new ArchiveEntry { Name = "f" + i, Size = 10 }, new byte[10]);
            }
        });
        var count = new CpioReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 4 } };
        Assert.Equal(nameof(ArchiveLimits.MaxEntryCount), Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(bytes), count)).LimitName);
        var total = new CpioReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 49 } };
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(bytes), total)).LimitName);
    }

    // ---- the Archive happy path ----

    [Fact]
    public void ArchiveDetectsAndExtractsCpio()
    {
        using var t = new TempDir();
        foreach (string vector in new[] { "tree.gnu-newc.cpio", "tree.gnu-odc.cpio", "tree.bsdtar-newc.cpio" })
        {
            string target = t.Combine("out-" + vector);
            Archive.Extract(new MemoryStream(ContainerVectors.Read("cpio", vector)), target);
            Assert.Equal("hello\n", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal("hello\n", File.ReadAllText(Path.Combine(target, "hard1")));
            Assert.Equal(70000, new FileInfo(Path.Combine(target, "big.bin")).Length);
            Assert.Equal("unicode", File.ReadAllText(Path.Combine(target, "\u00fcn\u00ef-\u65e5\u672c.txt")));
        }

        using Archive a = Archive.Open(new MemoryStream(ContainerVectors.Read("cpio", "tree.gnu-newc.cpio")));
        Assert.Equal(ArchiveFormat.Cpio, a.Format);
    }

    [Fact]
    public void ArchiveCreatesCpioByExtensionAndCompressed()
    {
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "d"));
        File.WriteAllText(Path.Combine(src, "d", "x.txt"), "x contents");
        File.WriteAllText(Path.Combine(src, "y.txt"), "y");
        foreach (string name in new[] { "out.cpio", "out.cpio.gz", "out.cpio.zst" })
        {
            string path = t.Combine(name);
            Archive.Create(path).Add(src, "").Save();
            using (Archive a = Archive.Open(path))
            {
                Assert.Equal(ArchiveFormat.Cpio, a.Format);
                Assert.Equal(["d", "d/x.txt", "y.txt"], a.Entries.Select(e => e.Name).Order(StringComparer.Ordinal).ToArray());
            }

            Archive.Extract(path, t.Combine("x-" + name));
            Assert.Equal("x contents", File.ReadAllText(Path.Combine(t.Combine("x-" + name), "d", "x.txt")));
        }
    }
}
