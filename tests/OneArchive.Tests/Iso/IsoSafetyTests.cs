using System.Text;
using RecursiveAI.OneArchive.Iso;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Hostile images: loops, extents past the end, bad continuation areas, bad descriptors, limits and unsafe extraction.</summary>
public class IsoSafetyTests
{
    private const int S = RawIso.S;

    private static byte[] Rr(string name, uint mode = 0x81A4) => RawIso.Concat(RawIso.Px(mode), RawIso.Nm(name));

    // Lists everything, then opens every file; returns the names.
    private static List<string> ReadAll(byte[] image, IsoReaderOptions? options = null)
    {
        var names = new List<string>();
        using var reader = new IsoReader(new MemoryStream(image), options);
        while (reader.GetNextEntry() is { } e)
        {
            names.Add(e.Name);
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = reader.OpenEntryStream();
                s.CopyTo(Stream.Null);
            }
        }

        return names;
    }

    private static void ExpectFormatError(byte[] image, string mentions, IsoReaderOptions? options = null)
    {
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(image, options));
        Assert.Contains(mentions, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADirectoryThatContainsTheRootIsALoop()
    {
        byte[] image = RawIso.Image([RawIso.Rec("LOOP", 18, S, 2)]);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void TwoDirectoriesThatContainEachOtherAreALoop()
    {
        byte[] a = RawIso.SubDir(19, 18, RawIso.Rec("B", 20, S, 2));
        byte[] b = RawIso.SubDir(20, 19, RawIso.Rec("A", 19, S, 2));
        byte[] image = RawIso.Image([RawIso.Rec("A", 19, S, 2)], true, a, b);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void TheSameDirectoryTwiceIsRefused()
    {
        byte[] a = RawIso.SubDir(19, 18, RawIso.Rec("F.TXT;1", 25, 1, 0));
        byte[] image = RawIso.Image([RawIso.Rec("A", 19, S, 2), RawIso.Rec("B", 19, S, 2)], true, a);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void ADirectoryPastTheEndOfTheImageIsRefused()
    {
        byte[] image = RawIso.Image([RawIso.Rec("DIR", 90_000, S, 2)]);
        ExpectFormatError(image, "extent");
    }

    [Fact]
    public void AFilePastTheEndOfTheImageIsRefused()
    {
        byte[] image = RawIso.Image([RawIso.Rec("F.TXT;1", 90_000, 10, 0)]);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void AFileWhoseLengthRunsPastTheEndIsRefused()
    {
        byte[] image = RawIso.Image([RawIso.Rec("F.TXT;1", 18, 4_000_000_000L, 0)]);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void AContinuationAreaThatPointsAtItselfIsRefused()
    {
        // Sector 19 holds a single CE entry that names sector 19 again.
        byte[] self = RawIso.Sector(RawIso.Ce(19, 0, 28));
        byte[] child = RawIso.Rec("F.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(19, 0, 28)));
        byte[] image = RawIso.Image([child], true, self);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void TwoContinuationAreasThatChainIntoEachOtherAreRefused()
    {
        byte[] a = RawIso.Sector(RawIso.Ce(20, 0, 28));
        byte[] b = RawIso.Sector(RawIso.Ce(19, 0, 28));
        byte[] child = RawIso.Rec("F.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(19, 0, 28)));
        byte[] image = RawIso.Image([child], true, a, b);
        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void ContinuationAreasPastTheEndOrOutsideTheirSectorAreRefused()
    {
        byte[] past = RawIso.Image([RawIso.Rec("F.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(50_000, 0, 28)))]);
        Assert.Throws<CompressionFormatException>(() => ReadAll(past));
        byte[] crossing = RawIso.Image([RawIso.Rec("F.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(19, 2040, 100)))], true, RawIso.Sector([]));
        Assert.Throws<CompressionFormatException>(() => ReadAll(crossing));
    }

    [Fact]
    public void TheContinuationBudgetStopsRecordsThatShareOneArea()
    {
        // 40 records all continue into the same 600-byte area; each use counts against the budget.
        byte[] area = RawIso.Sector(RawIso.Concat(RawIso.Nm("ab"), new byte[560]));
        var records = new List<byte[]>();
        for (int i = 0; i < 40; i++)
        {
            records.Add(RawIso.Rec($"F{i:D2}.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(19, 0, 600))));
        }

        byte[] image = RawIso.Image([.. records], true, area);
        var options = new IsoReaderOptions { MaxContinuationBytes = 4096 };
        Assert.Throws<CompressionLimitException>(() => ReadAll(image, options));
    }

    [Fact]
    public void TheSystemUseBudgetStopsAHugeRecordTail()
    {
        byte[] area = RawIso.Sector(RawIso.Nm("x"));
        byte[] child = RawIso.Rec("F.TXT;1", 18, 0, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(19, 0, 2000)));
        byte[] image = RawIso.Image([child], true, area);
        Assert.Throws<CompressionLimitException>(() => ReadAll(image, new IsoReaderOptions { MaxSystemUseSize = 100 }));
    }

    [Fact]
    public void ADirectoryLargerThanTheLimitIsRefused()
    {
        byte[] image = RawIso.Image([RawIso.Rec("BIG", 19, 64L * S, 2)], true, RawIso.SubDir(19, 18));
        Assert.Throws<CompressionLimitException>(() => ReadAll(image, new IsoReaderOptions { MaxDirectorySize = 8 * S }));
    }

    [Fact]
    public void ANameWithASlashIsNotUsed()
    {
        byte[] child = RawIso.Rec("F.TXT;1", 18, 0, 0, Rr("a/b"));
        byte[] image = RawIso.Image([child]);
        var ex = Record.Exception(() => ReadAll(image));
        // Either refused outright or never reported with a separator inside one component.
        if (ex is null)
        {
            Assert.DoesNotContain(ReadAll(image), static n => n.Contains("a/b", StringComparison.Ordinal));
        }
        else
        {
            Assert.IsAssignableFrom<CompressionException>(ex);
        }
    }

    [Fact]
    public void ALogicalBlockSizeOtherThan2048IsUnsupported()
    {
        byte[] image = RawIso.Image([]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(16 * S + 128), 512);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(16 * S + 130), 512);
        Assert.Throws<ArchiveUnsupportedException>(() => ReadAll(image));
    }

    [Fact]
    public void BadDescriptorTerminatorsAreRefused()
    {
        byte[] image = RawIso.Image([]);

        byte[] none = (byte[])image.Clone();
        Array.Clear(none, 17 * S, 7);
        Assert.Throws<CompressionFormatException>(() => ReadAll(none));

        byte[] badId = (byte[])image.Clone();
        badId[17 * S + 3] = (byte)'X';
        Assert.Throws<CompressionFormatException>(() => ReadAll(badId));

        byte[] badVersion = (byte[])image.Clone();
        badVersion[17 * S + 6] = 9;
        Assert.Throws<CompressionFormatException>(() => ReadAll(badVersion));

        byte[] noDescriptor = (byte[])image.Clone();
        Array.Clear(noDescriptor, 16 * S, 7);
        Assert.Throws<CompressionFormatException>(() => ReadAll(noDescriptor));
    }

    [Fact]
    public void ADescriptorAreaThatNeverEndsIsRefused()
    {
        // Sectors 17 onward are all "boot record" descriptors: the reader must stop looking.
        byte[] image = new byte[(16 + 300) * S];
        for (int i = 16; i < 316; i++)
        {
            image[i * S] = 0;
            "CD001"u8.CopyTo(image.AsSpan(i * S + 1));
            image[(i * S) + 6] = 1;
        }

        Assert.Throws<CompressionFormatException>(() => ReadAll(image));
    }

    [Fact]
    public void EveryTruncationEndsInATypedError()
    {
        byte[] sub = RawIso.SubDir(19, 18, RawIso.Rec("IN.TXT;1", 22, 5, 0, Rr("in.txt")));
        byte[] data = RawIso.Sector("hello"u8.ToArray());
        byte[] ce = RawIso.Sector(RawIso.Nm("long-name-in-continuation"));
        byte[] image = RawIso.Image(
            [
                RawIso.Rec("DIR", 19, S, 2, Rr("dir", 0x41ED)),
                RawIso.Rec("F.TXT;1", 21, 5, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Ce(20, 0, 30))),
            ],
            true,
            sub,
            ce,
            data,
            data);
        // The reference answer, whole.
        Assert.NotEmpty(ReadAll(image));
        for (int cut = 0; cut < image.Length; cut += (cut < 20 * S && cut > 15 * S) ? 7 : 211)
        {
            byte[] part = image[..cut];
            try
            {
                ReadAll(part);
            }
            catch (CompressionException)
            {
            }
            catch (ArgumentException)
            {
            }
        }
    }

    [Fact]
    public void FlippedBytesNeverEscapeTheTypedErrors()
    {
        byte[] sub = RawIso.SubDir(19, 18, RawIso.Rec("IN.TXT;1", 22, 5, 0, Rr("in.txt")));
        byte[] image = RawIso.Image([RawIso.Rec("DIR", 19, S, 2, Rr("dir", 0x41ED)), RawIso.Rec("F.TXT;1", 21, 5, 0, Rr("f.txt"))], true, sub, RawIso.Sector("hello"u8.ToArray()), RawIso.Sector("hello"u8.ToArray()));
        var rnd = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            byte[] copy = (byte[])image.Clone();
            int region = rnd.Next(3) switch { 0 => 16 * S, 1 => 18 * S, _ => 19 * S };
            copy[region + rnd.Next(300)] ^= (byte)(1 << rnd.Next(8));
            try
            {
                ReadAll(copy, new IsoReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 50 } });
            }
            catch (CompressionException)
            {
            }
        }
    }

    [Fact]
    public void TooManyEntriesTooLongPathsAndTooDeepTreesAreLimits()
    {
        var files = Enumerable.Range(0, 8).Select(static i => RawIso.Rec($"F{i}.TXT;1", 18, 0, 0)).ToArray();
        byte[] image = RawIso.Image(files);
        Assert.Throws<CompressionLimitException>(() => ReadAll(image, new IsoReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 3 } }));

        using var ms = new MemoryStream();
        using (var writer = new IsoWriter(ms, new IsoWriterOptions(), leaveOpen: true))
        {
            string dir = string.Join('/', Enumerable.Range(0, 70).Select(static i => "d" + i));
            writer.WriteEntry(new ArchiveEntry { Name = dir + "/f.txt", Kind = ArchiveEntryKind.File, Size = 1, Mode = (UnixFileMode)0x1A4 }, "x"u8.ToArray());
        }

        byte[] deep = ms.ToArray();
        Assert.Throws<CompressionLimitException>(() => ReadAll(deep, new IsoReaderOptions { Limits = new ArchiveLimits { MaxPathDepth = 32 } }));
        Assert.Throws<CompressionLimitException>(() => ReadAll(deep, new IsoReaderOptions { Limits = new ArchiveLimits { MaxPathLength = 100 } }));
        Assert.EndsWith("d69/f.txt", ReadAll(deep, new IsoReaderOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } }).Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void LinksToOneExtentCannotMultiplyTheOutput()
    {
        // Twenty names for one 100 KB extent: 2 MB of output from a 50 KB image.
        byte[] data = new byte[100_000];
        var files = Enumerable.Range(0, 20).Select(static i => RawIso.Rec($"F{i:D2}.TXT;1", 19, 100_000, 0, Rr($"f{i:D2}"))).ToArray();
        byte[] image = RawIso.Image(files, true, data);
        using var temp = new TempDir();
        var options = new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxTotalSize = 500_000 } };
        Assert.Throws<CompressionLimitException>(() => Archive.Extract(new MemoryStream(image), temp.Combine("out"), options));
    }

    [Fact]
    public void ExtractionRefusesNamesAndLinksThatLeaveTheTarget()
    {
        byte[] sl(string target) => RawIso.Su("SL", [0, .. Encoding.ASCII.GetBytes(target).Prepend((byte)target.Length).Prepend((byte)0)]);
        byte[] image = RawIso.Image(
            [
                RawIso.Rec("A.TXT;1", 18, 0, 0, Rr("../escape.txt")),
                RawIso.Rec("B.TXT;1", 18, 0, 0, Rr("/abs.txt")),
                RawIso.Rec("L", 18, 0, 0, RawIso.Concat(RawIso.Px(0xA1FF), RawIso.Nm("out"), sl("../../x"))),
            ]);
        using var temp = new TempDir();
        string target = temp.Combine("out");
        try
        {
            Archive.Extract(new MemoryStream(image), target, new ArchiveExtractOptions());
        }
        catch (CompressionException)
        {
        }

        Assert.False(File.Exists(temp.Combine("escape.txt")));
        Assert.False(File.Exists("/abs.txt"));
        Assert.False(File.Exists(temp.Combine("x")));
    }
}
