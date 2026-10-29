using System.Text;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneCompress;
using static RecursiveAI.OneArchive.Tests.Rar.RarTestBuilder;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>Hand-built RAR 5 archives: Windows hosts, time variants, and every kind of malformed header.</summary>
public class RarHostileTests
{
    private static readonly byte[] s_hello = Encoding.ASCII.GetBytes("hello rar");

    private static RarTestBuilder Good() => new RarTestBuilder().Main().File("a.txt", s_hello).File("b/c.txt", s_hello);

    private static List<RarEntry> ReadAll(byte[] archive, RarReaderOptions? options = null, bool extract = true)
    {
        using var r = new RarReader(new MemoryStream(archive), options);
        var list = new List<RarEntry>();
        while (r.GetNextEntry() is { } e)
        {
            list.Add(e);
            if (extract && e.Kind == ArchiveEntryKind.File)
            {
                RarReaderTests.ReadAll(r.OpenEntryStream());
            }
        }

        return list;
    }

    private static CompressionFormatException Bad(byte[] archive, RarReaderOptions? options = null)
    {
        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => ReadAll(archive, options));
        Assert.Equal("rar", ex.Format);
        return ex;
    }

    [Fact]
    public void ACleanArchiveReads()
    {
        List<RarEntry> entries = ReadAll(Good().End().ToArray());
        Assert.Equal(["a.txt", "b/c.txt"], entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void WindowsHostEntriesHaveNoUnixMode()
    {
        // Host OS 0, DOS attribute 0x20 (archive); a Windows symlink with backslashes in the target.
        byte[] rar = new RarTestBuilder().Main()
            .File("win.txt", s_hello, o => { o.HostOs = 0; o.Attributes = 0x20; })
            .File("dir", [], o => { o.HostOs = 0; o.Attributes = 0x10; o.Directory = true; })
            .File("lnk", [], o => { o.HostOs = 0; o.Attributes = 0x20; o.Extra = Redirection(2, "dir\\inner.txt"); o.WriteCrc = false; })
            .End().ToArray();
        List<RarEntry> e = ReadAll(rar);
        Assert.Equal(RarHostOs.Windows, e[0].HostOs);
        Assert.Null(e[0].Mode);
        Assert.Equal(0x20UL, e[0].Attributes);
        Assert.Equal(ArchiveEntryKind.Directory, e[1].Kind);
        Assert.Equal(ArchiveEntryKind.Symlink, e[2].Kind);
        Assert.Equal("dir/inner.txt", e[2].LinkTarget);
        Assert.Equal(RarRedirectionType.WindowsSymlink, e[2].Redirection);
    }

    [Fact]
    public void FileTimeRecordVariantsAreDecoded()
    {
        // Windows FILETIME: 2020-01-02 03:04:05.0000007 UTC.
        long ticks = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc).Ticks + 7 - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        byte[] winTimes = Record(3, Vint(0x2 | 0x4 | 0x8), U64((ulong)ticks), U64((ulong)ticks + 10_000_000), U64((ulong)ticks + 20_000_000));
        byte[] unixTimes = Record(3, Vint(0x1 | 0x2 | 0x10), U32(1_600_000_000), U32(123_456_700));
        byte[] version = Record(4, Vint(0), Vint(7));
        byte[] owner = Record(6, Vint(0xF), Vint(3), "bob"u8.ToArray(), Vint(5), "staff"u8.ToArray(), Vint(1000), Vint(20));
        byte[] rar = new RarTestBuilder().Main()
            .File("w", s_hello, o => o.Extra = Cat(winTimes, version, owner))
            .File("u", s_hello, o => o.Extra = unixTimes)
            .End().ToArray();
        List<RarEntry> e = ReadAll(rar);
        var expected = new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(7);
        Assert.Equal(expected, e[0].ModifiedTime);
        Assert.Equal(expected.AddSeconds(1), e[0].CreationTime);
        Assert.Equal(expected.AddSeconds(2), e[0].AccessTime);
        Assert.Equal(7, e[0].FileVersion);
        Assert.Equal("bob", e[0].UserName);
        Assert.Equal("staff", e[0].GroupName);
        Assert.Equal(1000, e[0].Uid);
        Assert.Equal(20, e[0].Gid);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000).AddTicks(1_234_567), e[1].ModifiedTime);
    }

    [Fact]
    public void DictionarySizeFollowsTheCompressionInformationBits()
    {
        // Bits 10-14 are N (128 KB << N); for algorithm version 1, bits 15-19 add a 1/32 fraction step.
        byte[] rar = new RarTestBuilder().Main()
            .File("v0", s_hello, o => o.CompressionInfo = 3UL << 10)
            .File("v1", s_hello, o => o.CompressionInfo = 1 | (3UL << 10) | (16UL << 15))
            .File("m4solid", s_hello, o => o.CompressionInfo = (4UL << 7) | 0x40)
            .End().ToArray();
        using var r = new RarReader(new MemoryStream(rar));
        var e = new List<RarEntry>();
        while (r.GetNextEntry() is { } x)
        {
            e.Add(x);
        }

        Assert.Equal(1L << 20, e[0].DictionarySize);
        Assert.Equal((1L << 20) + (1L << 19), e[1].DictionarySize);
        Assert.Equal(1, e[1].CompressionVersion);
        Assert.Equal(4, e[2].Method);
        Assert.True(e[2].IsSolid);
        Assert.False(e[2].IsStored);
    }

    [Fact]
    public void ArchiveWithoutAnEndHeaderIsTruncated()
    {
        CompressionFormatException ex = Bad(new RarTestBuilder().Main().File("a", s_hello).ToArray());
        Assert.True(ex.IsTruncated);
    }

    [Fact]
    public void HeaderCrcMismatchNamesTheOffset()
    {
        byte[] rar = Good().End().ToArray();
        int firstFileHeader = Signature.Length + Block(1, 0, Vint(0)).Length;
        rar[firstFileHeader + 6] ^= 1;
        CompressionFormatException ex = Bad(rar);
        Assert.Equal(firstFileHeader, ex.Offset);
        Assert.Contains("header CRC-32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VintOverflowIsRejected()
    {
        // An unpacked size of eleven continuation bytes, and one whose tenth byte has bits beyond 64.
        foreach (byte[] bad in new[] { Enumerable.Repeat((byte)0xFF, 10).Append((byte)0x01).ToArray(), [.. Enumerable.Repeat((byte)0xFF, 9), 0x03] })
        {
            byte[] fields = Cat(Vint(0x4), bad, Vint(0x81A4), U32(0), Vint(0), Vint(1), Vint(1), "a"u8.ToArray());
            byte[] rar = new RarTestBuilder().Main().Raw(Block(2, 0, fields, null, 0)).End().ToArray();
            CompressionFormatException ex = Bad(rar);
            Assert.Contains("vint", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PaddedVintsAreAccepted()
    {
        // rar pre-allocates some vints and pads them with 0x80 bytes (technote, "vint").
        byte[] fields = Cat(Vint(0x4), [0x89, 0x80, 0x00], Vint(0x81A4), U32(0), [0x80, 0x00], Vint(1), Vint(1), "a"u8.ToArray());
        byte[] rar = new RarTestBuilder().Main().Raw(Block(2, 0, fields, null, 9)).Raw(new byte[9]).End().ToArray();
        Assert.Equal(9, ReadAll(rar, new RarReaderOptions { VerifyChecksum = false })[0].Size);
    }

    [Fact]
    public void HeaderSizeLongerThanTheArchiveIsTruncated()
    {
        byte[] rar = Good().End().ToArray();
        byte[] cut = rar[..(Signature.Length + Block(1, 0, Vint(0)).Length + 20)];
        CompressionFormatException ex = Bad(cut);
        Assert.True(ex.IsTruncated);
    }

    [Fact]
    public void HeaderSizeBeyondTheTechnoteMaximumIsRejected()
    {
        // 3-byte size field (up to 2 MiB) is the limit: 0xFFFFFF would be 2 MiB - 1... use a 3-byte value above 2 MiB.
        byte[] rar = Cat(Signature, Block(1, 0, Vint(0)), U32(0), [0x80, 0x80, 0x81]);
        Assert.Contains("header size", Bad(rar).Message, StringComparison.Ordinal);

        // A size field of four bytes is refused outright.
        byte[] four = Cat(Signature, Block(1, 0, Vint(0)), U32(0), [0x80, 0x80, 0x80, 0x01]);
        Assert.Contains("at most 3 bytes", Bad(four).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraRecordRunningPastItsAreaIsRejected()
    {
        // The record claims 50 bytes but the extra area holds 5.
        byte[] extra = Cat(Vint(50), Vint(3), [1, 2, 3]);
        byte[] rar = new RarTestBuilder().Main().File("a", s_hello, o => o.Extra = extra).End().ToArray();
        CompressionFormatException ex = Bad(rar);
        Assert.Contains("extra record size", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecordFieldRunningPastItsRecordIsRejected()
    {
        // A file-time record that says "mtime present" but holds no bytes for it.
        byte[] rar = new RarTestBuilder().Main().File("a", s_hello, o => o.Extra = Record(3, Vint(0x2))).End().ToArray();
        CompressionFormatException ex = Bad(rar);
        Assert.Contains("mtime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtraAreaLargerThanTheHeaderIsRejected()
    {
        // Header flags say there is an extra area of 200 bytes in a header that is 20 bytes long.
        byte[] body = Cat(Vint(1), Vint(0x1), Vint(200), Vint(0));
        byte[] size = Vint((ulong)body.Length);
        byte[] block = Cat(U32(RecursiveAI.OneCompress.Checksums.Crc32.Update(RecursiveAI.OneCompress.Checksums.Crc32.Compute(size), body)), size, body);
        Assert.Contains("extra area", Bad(Cat(Signature, block)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DataAreaPastTheEndOfTheVolumeIsTruncated()
    {
        byte[] rar = new RarTestBuilder().Main().File("a", s_hello, o => { o.DataSizeOverride = 5000; o.UnpackedSize = 5000; o.WriteCrc = false; }).End().ToArray();
        CompressionFormatException ex = Bad(rar);
        Assert.True(ex.IsTruncated);
    }

    [Fact]
    public void StoredEntryWhoseSizesDisagreeIsRejected()
    {
        byte[] rar = new RarTestBuilder().Main().File("a", s_hello, o => o.UnpackedSize = 100).End().ToArray();
        Assert.Contains("stored entry", Bad(rar).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AbsurdUnpackedSizesAreLimitsOrFormatErrors()
    {
        // 2^62 bytes: over the total-size limit, a typed limit error before any data is read.
        byte[] huge = new RarTestBuilder().Main().File("a", s_hello, o => { o.UnpackedSize = 1UL << 62; o.CompressionInfo = 3UL << 7; }).End().ToArray();
        Assert.Throws<CompressionLimitException>(() => ReadAll(huge, extract: false));

        // Above 2^63: not representable.
        byte[] over = new RarTestBuilder().Main().File("a", s_hello, o => { o.UnpackedSize = ulong.MaxValue; o.CompressionInfo = 3UL << 7; }).End().ToArray();
        Assert.Contains("2^63", Bad(over).Message, StringComparison.Ordinal);

        // Many entries under a low entry limit.
        var many = new RarTestBuilder().Main();
        for (int i = 0; i < 20; i++)
        {
            many.File("f" + i, s_hello);
        }

        Assert.Throws<CompressionLimitException>(() => ReadAll(many.End().ToArray(), new RarReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 5 } }));
        Assert.Throws<CompressionLimitException>(() => ReadAll(many.ToArray(), new RarReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 50 } }));
        Assert.Throws<CompressionLimitException>(() => ReadAll(new RarTestBuilder().Main().File(new string('x', 5000), s_hello).End().ToArray()));
    }

    [Fact]
    public void WrongChecksumOnStoredDataIsReportedAtTheEnd()
    {
        byte[] rar = new RarTestBuilder().Main().File("a", s_hello, o => o.CrcOverride = 0x12345678).End().ToArray();
        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => ReadAll(rar));
        Assert.Contains("12345678", ex.Message, StringComparison.Ordinal);
        Assert.Single(ReadAll(rar, new RarReaderOptions { VerifyChecksum = false }));
    }

    [Fact]
    public void ContinuationThatDoesNotMatchIsRejected()
    {
        byte[] first = new RarTestBuilder().Main(1).File("a", s_hello, o => { o.ContinuesInNext = true; o.UnpackedSize = 18; }).End(true).ToArray();
        byte[] wrongName = new RarTestBuilder().Main(1, 1).File("zzz", s_hello, o => { o.Continued = true; o.UnpackedSize = 18; }).End().ToArray();
        byte[] notContinued = new RarTestBuilder().Main(1, 1).File("a", s_hello, o => o.UnpackedSize = 18).End().ToArray();
        foreach (byte[] second in new[] { wrongName, notContinued })
        {
            var options = new RarReaderOptions { Volumes = n => new MemoryStream(second) };
            using var r = new RarReader(new MemoryStream(first), options);
            r.GetNextEntry();
            Assert.Throws<CompressionFormatException>(() => RarReaderTests.ReadAll(r.OpenEntryStream()));
        }

        // A continuation that is right, for contrast; the per-part CRC covers each volume's data.
        byte[] p1 = Encoding.ASCII.GetBytes("12345678");
        byte[] p2 = Encoding.ASCII.GetBytes("90abcdef");
        byte[] a = new RarTestBuilder().Main(1).File("a", p1, o => { o.ContinuesInNext = true; o.UnpackedSize = 16; }).End(true).ToArray();
        byte[] b = new RarTestBuilder().Main(1, 1)
            .File("a", p2, o => { o.Continued = true; o.UnpackedSize = 16; o.CrcOverride = RecursiveAI.OneCompress.Checksums.Crc32.Compute(Encoding.ASCII.GetBytes("1234567890abcdef")); })
            .End().ToArray();
        using var good = new RarReader(new MemoryStream(a), new RarReaderOptions { Volumes = n => new MemoryStream(b) });
        good.GetNextEntry();
        Assert.Equal("1234567890abcdef", Encoding.ASCII.GetString(RarReaderTests.ReadAll(good.OpenEntryStream())));
    }

    [Fact]
    public void ASecondMainHeaderOrAnEncryptionHeaderMidArchiveIsRejected()
    {
        Assert.Contains("second main", Bad(new RarTestBuilder().Main().File("a", s_hello).Main().End().ToArray()).Message, StringComparison.Ordinal);
        byte[] enc = new RarTestBuilder().Main().File("a", s_hello).Raw(Block(4, 0, Cat(Vint(0), Vint(0), [15], new byte[16]))).End().ToArray();
        Assert.Contains("encryption header", Bad(enc).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownHeaderTypesAndDataAreSkipped()
    {
        byte[] rar = new RarTestBuilder().Main()
            .Raw(Block(77, 0x4, [1, 2, 3], null, 5)).Raw(new byte[5])
            .File("a", s_hello)
            .Raw(Block(3, 0, Cat(Vint(0), Vint(3), Vint(0), Vint(0), Vint(1), Vint(3), "STM"u8.ToArray()), null, 3)).Raw([9, 9, 9])
            .File("b", s_hello)
            .End().ToArray();
        Assert.Equal(["a", "b"], ReadAll(rar).Select(e => e.Name).ToArray());
    }

    [Fact]
    public void ShortSignatureAndGarbageAreFormatErrors()
    {
        Assert.Throws<CompressionFormatException>(() => ReadAll(Signature.AsSpan(0, 5).ToArray(), new RarReaderOptions { MaxSfxSize = 0 }));
        Assert.Throws<CompressionFormatException>(() => ReadAll(new byte[200]));
        Assert.Throws<CompressionFormatException>(() => ReadAll([]));
    }

    [Fact]
    public void EveryTruncationAndBitFlipEndsInATypedError()
    {
        byte[] good = new RarTestBuilder().Main(0, null, Record(2, Vint(1), Vint(5), "a.rar"u8.ToArray()))
            .File("a.txt", s_hello, o => o.Extra = Cat(Record(3, Vint(0x1 | 0x2 | 0x10), U32(1_600_000_000), U32(5)), Redirection(1, "t"), Record(6, Vint(3), Vint(1), "u"u8.ToArray(), Vint(1), "g"u8.ToArray())))
            .File("d", [], o => { o.Directory = true; })
            .End().ToArray();

        for (int len = 0; len < good.Length; len++)
        {
            byte[] cut = good[..len];
            try
            {
                ReadAll(cut);
            }
            catch (CompressionException)
            {
            }
        }

        for (int i = 0; i < good.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] flipped = (byte[])good.Clone();
                flipped[i] ^= (byte)(1 << bit);
                foreach (bool resign in new[] { false, true })
                {
                    byte[] candidate = (byte[])flipped.Clone();
                    if (resign)
                    {
                        Resign(candidate);
                    }

                    try
                    {
                        ReadAll(candidate);
                    }
                    catch (CompressionException)
                    {
                    }
                }
            }
        }
    }
}
