using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cab;

public class CabReaderTests
{
    internal static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static CabTestBuilder.FolderSpec Folder(CabTestBuilder.Method method, int windowBits = 16, params (string Name, byte[] Data)[] files)
    {
        var f = new CabTestBuilder.FolderSpec { Method = method, WindowBits = windowBits };
        foreach ((string name, byte[] data) in files)
        {
            f.Files.Add(new CabTestBuilder.FileSpec { Name = name, Data = data });
        }

        return f;
    }

    private static CabReader Open(byte[] cab, CabReaderOptions? options = null) => new(new MemoryStream(cab), options);

    private static void AssertContents(byte[] cab, params (string Name, byte[] Data)[] expected)
    {
        using CabReader r = Open(cab);
        int i = 0;
        while (r.GetNextEntry() is { } e)
        {
            Assert.Equal(expected[i].Name, e.Name);
            Assert.Equal(expected[i].Data.Length, e.Size);
            Assert.Equal(expected[i].Data, ReadAll(r.OpenEntryStream()));
            i++;
        }

        Assert.Equal(expected.Length, i);

        // Random access, in reverse order.
        using CabReader r2 = Open(cab);
        var items = r2.Items;
        for (int k = items.Count - 1; k >= 0; k--)
        {
            Assert.Equal(expected[k].Data, ReadAll(r2.OpenEntryStream(items[k])));
        }
    }

    [Theory]
    [InlineData(CabTestBuilder.Method.None)]
    [InlineData(CabTestBuilder.Method.Mszip)]
    [InlineData(CabTestBuilder.Method.MszipNoHistory)]
    [InlineData(CabTestBuilder.Method.Lzx)]
    public void ReadsEveryMethodAcrossManyBlocks(CabTestBuilder.Method method)
    {
        (string, byte[])[] f1 = [("a.txt", TestData.Mixed(70_000, 1)), ("dir\\b.bin", TestData.Mixed(1, 2)), ("empty", []), ("c.dat", TestData.Mixed(100_000, 3, true))];
        (string, byte[])[] f2 = [("second/x.txt", TestData.Mixed(33_000, 4)), ("y", TestData.Mixed(500, 5))];
        var b = new CabTestBuilder();
        b.Folders.Add(Folder(method, 17, f1));
        b.Folders.Add(Folder(method, 15, f2));
        AssertContents(b.Build(), [.. f1.Select(x => (x.Item1.Replace('\\', '/'), x.Item2)), .. f2]);
    }

    [Fact]
    public void ExposesHeaderFolderAndFileFields()
    {
        var b = new CabTestBuilder
        {
            SetId = 77,
            CabinetIndex = 2,
            PreviousCabinet = "disk1.cab",
            PreviousDisk = "Disk 1",
            NextCabinet = "disk3.cab",
            NextDisk = "Disk 3",
            HeaderReserve = [9, 8, 7],
            FolderReserveSize = 4,
            DataReserveSize = 6,
            DataReserveFill = [1, 2, 3, 4, 5, 6],
        };
        var folder = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.Mszip, Reserve = [4, 3, 2, 1] };
        folder.Files.Add(new CabTestBuilder.FileSpec { Name = "n.txt", Data = TestData.Mixed(50_000, 9), Attributes = 0x01 | 0x40 | 0x20, Date = CabTestBuilder.DosDate(2001, 12, 31), Time = CabTestBuilder.DosTime(23, 59, 58) });
        b.Folders.Add(folder);
        using CabReader r = Open(b.Build());

        CabHeader h = r.Header;
        Assert.Equal(new Version(1, 3), h.FormatVersion);
        Assert.Equal(77, h.SetId);
        Assert.Equal(2, h.CabinetIndex);
        Assert.Equal(CabHeaderFeatures.PreviousCabinet | CabHeaderFeatures.NextCabinet | CabHeaderFeatures.ReservePresent, h.Flags);
        Assert.Equal("disk1.cab", h.PreviousCabinet);
        Assert.Equal("Disk 1", h.PreviousDisk);
        Assert.Equal("disk3.cab", h.NextCabinet);
        Assert.Equal("Disk 3", h.NextDisk);
        Assert.Equal(new byte[] { 9, 8, 7 }, h.HeaderReserve.ToArray());
        Assert.Equal(4, h.FolderReserveSize);
        Assert.Equal(6, h.DataReserveSize);
        Assert.Equal(1, h.FolderCount);
        Assert.Equal(1, h.FileCount);

        CabFolder fo = Assert.Single(r.Folders);
        Assert.Equal(CabCompression.MsZip, fo.Compression);
        Assert.Equal(2, fo.BlockCount);
        Assert.Equal(new byte[] { 4, 3, 2, 1 }, fo.Reserve.ToArray());

        CabEntry e = Assert.Single(r.Items);
        Assert.Equal(CabAttributes.ReadOnly | CabAttributes.Execute | CabAttributes.Archive, e.Attributes);
        Assert.Equal(new DateTimeOffset(2001, 12, 31, 23, 59, 58, TimeSpan.Zero), e.ModifiedTime);
        Assert.Equal(CabContinuation.None, e.Continuation);
        Assert.Equal(TestData.Mixed(50_000, 9), ReadAll(r.OpenEntryStream(e)));
    }

    [Fact]
    public void ReadsLzxAtEveryWindowSize()
    {
        foreach (int window in new[] { 15, 16, 17, 18, 19, 20, 21 })
        {
            byte[] data = TestData.Mixed(150_000, window);
            var b = new CabTestBuilder();
            b.Folders.Add(Folder(CabTestBuilder.Method.Lzx, window, ("f", data)));
            AssertContents(b.Build(), ("f", data));
        }
    }

    [Fact]
    public void DecodesNamesByTheirEncodingFlag()
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "\u00fcn\u00ef/\u65e5\u672c.txt", Data = [1], Utf8Name = true });
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "legacy", Data = [2], RawName = [0x61, 0xE9, 0x5C, 0x62] });
        b.Folders.Add(f);
        using CabReader r = Open(b.Build());
        Assert.Equal("\u00fcn\u00ef/\u65e5\u672c.txt", r.Items[0].Name);
        Assert.Equal("a\u00e9/b", r.Items[1].Name);
        Assert.Equal(new byte[] { 0x61, 0xE9, 0x5C, 0x62 }, r.Items[1].RawName.ToArray());

        using CabReader r2 = Open(b.Build(), new CabReaderOptions { LegacyNameEncoding = System.Text.Encoding.UTF8 });
        Assert.Equal("a\uFFFD/b", r2.Items[1].Name);
    }

    [Fact]
    public void FilesThatSpanCabinetsAreListedAndRefusedWithATypedError()
    {
        var b = new CabTestBuilder { NextCabinet = "next.cab", NextDisk = "n", PreviousCabinet = "prev.cab", PreviousDisk = "p" };
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "head", Data = [1, 2, 3], RawFolderIndex = 0xFFFD });
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "whole", Data = [4, 5, 6] });
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "tail", Data = [7], RawFolderIndex = 0xFFFE });
        b.Folders.Add(f);
        using CabReader r = Open(b.Build());
        Assert.Equal([CabContinuation.FromPrevious, CabContinuation.None, CabContinuation.ToNext], r.Items.Select(i => i.Continuation));
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => r.OpenEntryStream(r.Items[0]));
        Assert.Equal("cab", ex.Format);
        Assert.Contains("prev.cab", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 4, 5, 6 }, ReadAll(r.OpenEntryStream(r.Items[1])));
        Assert.Throws<ArchiveUnsupportedException>(() => r.OpenEntryStream(r.Items[2]));
    }

    [Fact]
    public void QuantumFoldersRaiseAnUnsupportedMethodError()
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None, RawType = 0x1502 };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "q", Data = [1, 2, 3] });
        b.Folders.Add(f);
        using CabReader r = Open(b.Build());
        Assert.Equal(CabCompression.Quantum, r.Folders[0].Compression);
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => r.OpenEntryStream(r.Items[0]));
        Assert.Contains("Quantum", ex.Feature, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockThatContinuesInTheNextCabinetIsRefused()
    {
        var b = new CabTestBuilder();
        b.Folders.Add(Folder(CabTestBuilder.Method.None, 16, ("a", [1, 2, 3, 4])));
        byte[] cab = b.Build();
        // The only data block starts after the file table; zero its cbUncomp field.
        using CabReader probe = Open(cab);
        long dataAt = probe.Folders[0].DataOffset;
        cab[dataAt + 6] = 0;
        cab[dataAt + 7] = 0;
        using CabReader r = Open(cab, new CabReaderOptions { VerifyChecksum = false });
        Assert.Throws<ArchiveUnsupportedException>(() => ReadAll(r.OpenEntryStream(r.Items[0])));
    }

    [Fact]
    public void ADataBlockChecksumMismatchIsAFormatErrorUnlessIgnored()
    {
        var b = new CabTestBuilder();
        b.Folders.Add(Folder(CabTestBuilder.Method.None, 16, ("a", TestData.Mixed(1000, 1))));
        byte[] cab = b.Build();
        using (CabReader probe = Open(cab))
        {
            cab[probe.Folders[0].DataOffset + 8 + 10] ^= 0x55;
        }

        using CabReader r = Open(cab);
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(r.OpenEntryStream(r.Items[0])));
        Assert.Equal("cab", ex.Format);
        Assert.Contains("checksum", ex.Expected, StringComparison.Ordinal);

        using CabReader lenient = Open(cab, new CabReaderOptions { VerifyChecksum = false });
        Assert.Equal(1000, ReadAll(lenient.OpenEntryStream(lenient.Items[0])).Length);
    }

    [Fact]
    public void ZeroChecksumsAreNotChecked()
    {
        var b = new CabTestBuilder { OmitChecksums = true };
        b.Folders.Add(Folder(CabTestBuilder.Method.Mszip, 16, ("a", TestData.Mixed(40_000, 1))));
        AssertContents(b.Build(), ("a", TestData.Mixed(40_000, 1)));
    }
}
