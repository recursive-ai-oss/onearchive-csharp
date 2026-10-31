using System.Text;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneArchive.Cpio;
using RecursiveAI.OneArchive.Iso;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>A time a header can't express as a date is unknown (null); the entry stays readable.</summary>
public class TimestampTests
{
    private static string TestData(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "testdata")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? string.Empty, "testdata", relative);
    }

    [Fact]
    public void ArMtimeBeyondYear9999IsNull()
    {
        using var reader = new ArReader(new MemoryStream(File.ReadAllBytes(TestData("ar/mtime-out-of-range.a"))));
        ArEntry? e = reader.GetNextEntry();
        Assert.NotNull(e);
        Assert.Null(e.ModifiedTime);
        Assert.Equal(52, e.Size);
    }

    [Theory]
    [InlineData("333333333333")]
    [InlineData("999999999999")]
    public void ArMtimeVariantsAreNull(string mtime)
    {
        byte[] data = Encoding.ASCII.GetBytes("!<arch>\n" + "a/".PadRight(16) + mtime + "      " + "      " + "100644  " + "1         " + "`\nx\n");
        using var reader = new ArReader(new MemoryStream(data));
        ArEntry? e = reader.GetNextEntry();
        Assert.NotNull(e);
        Assert.Null(e.ModifiedTime);
    }

    [Fact]
    public void CpioLargestMtimeFieldIsReadable()
    {
        // odc: magic, dev, ino, mode, uid, gid, nlink, rdev, mtime (11 octal digits), namesize (6), filesize (11), name.
        string rec = "070707" + "000000" + "000001" + "100644" + "000000" + "000000" + "000001" + "000000" + "77777777777" + "000002" + "00000000000" + "a\0";
        string trailer = "070707" + "000000" + "000000" + "000000" + "000000" + "000000" + "000001" + "000000" + "00000000000" + "000013" + "00000000000" + "TRAILER!!!\0";
        using var reader = new CpioReader(new MemoryStream(Encoding.Latin1.GetBytes(rec + trailer)));
        CpioEntry? e = reader.GetNextEntry();
        Assert.NotNull(e);
        Assert.Equal("a", e.Name);
        // The largest 11-digit octal value is the year 2242: the field can never leave the representable range.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(0x1FFFFFFFFL), e.ModifiedTime);
    }

    [Fact]
    public void ZipInvalidDosFieldsAreNull()
    {
        Assert.Null(ZipFormat.FromDos((ushort)((1 << 5) | 0), 0));   // day 0
        Assert.Null(ZipFormat.FromDos((ushort)((13 << 5) | 1), 0));  // month 13
        Assert.Null(ZipFormat.FromDos((ushort)((2 << 5) | 31), 0));  // 31 February
        Assert.Null(ZipFormat.FromDos((ushort)((1 << 5) | 1), (ushort)((31 << 11) | (63 << 5)))); // 31:63
        Assert.Null(ZipFormat.FromFileTime(ulong.MaxValue));
    }

    [Fact]
    public void SevenZipAndRarFileTimesOutOfRangeAreNull()
    {
        Assert.Null(SevenZipFormat.FromFileTime(ulong.MaxValue));
        Assert.Null(SevenZipFormat.FromFileTime((ulong)long.MaxValue));
        Assert.Null(RarText.FromFileTime(ulong.MaxValue));
        Assert.Null(RarText.FromUnix(ulong.MaxValue, 0));
        Assert.Null(RarText.FromUnix(0, 1_000_000_000));
    }

    private static byte[] Date17(string digits, sbyte offset) => [.. Encoding.ASCII.GetBytes(digits), (byte)offset];

    [Fact]
    public void IsoInvalidAndEdgeDatesAreNull()
    {
        Assert.Null(IsoFormat.ParseDate7([100, 13, 1, 0, 0, 0, 0]));
        Assert.Null(IsoFormat.ParseDate7([100, 2, 30, 0, 0, 0, 0]));
        Assert.Null(IsoFormat.ParseDate7([100, 1, 1, 24, 0, 0, 0]));
        Assert.Null(IsoFormat.ParseDate17(Date17("2020133100000000", 0)));
        // The first and last instants of the representable range, shifted out of it by the zone offset.
        Assert.Null(IsoFormat.ParseDate17(Date17("0001010100000000", 52)));
        Assert.Null(IsoFormat.ParseDate17(Date17("9999123123595999", -48)));
    }
}
