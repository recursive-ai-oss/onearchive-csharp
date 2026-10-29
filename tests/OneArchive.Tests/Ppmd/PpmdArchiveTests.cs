using System.Runtime.Versioning;
using RecursiveAI.OneArchive.SevenZip;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.OneArchive.Tests.Ppmd;

/// <summary>PPMd in zip (method 98, variant I) and in 7z (variant H): round trips, limits, hostile input, and 7-Zip as a black box.</summary>
public class PpmdArchiveTests
{
    private static byte[] ZipOf(byte[] data, ZipWriterOptions options)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, options, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "a.txt", Size = data.Length }, data);
            w.WriteEntry(new ArchiveEntry { Name = "b.txt", Size = 5 }, "hello"u8.ToArray());
        }

        return ms.ToArray();
    }

    private static byte[] SevenOf(byte[] data, SevenZipWriterOptions options)
    {
        var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, options, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "a.txt", Size = data.Length }, data);
            w.WriteEntry(new ArchiveEntry { Name = "b.txt", Size = 5 }, "hello"u8.ToArray());
        }

        return ms.ToArray();
    }

    private static void DrainZip(byte[] zip, ZipReaderOptions? options = null)
    {
        using var r = new ZipReader(new MemoryStream(zip), options);
        while (r.GetNextEntry() is not null)
        {
            using var s = r.OpenEntryStream();
            s.CopyTo(Stream.Null);
        }
    }

    private static void DrainSeven(byte[] seven, SevenZipReaderOptions? options = null)
    {
        using var r = new SevenZipReader(new MemoryStream(seven), options);
        while (r.GetNextEntry() is not null)
        {
            using var s = r.OpenEntryStream();
            s.CopyTo(Stream.Null);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void ZipRoundTripsSeekableAndForwardOnly(int level)
    {
        byte[] data = ZipTests.Text(300_000, 3);
        byte[] zip = ZipOf(data, new ZipWriterOptions { Method = ZipMethod.Ppmd, Level = level });
        foreach (Stream s in new Stream[] { new MemoryStream(zip), new ForwardOnly(new MemoryStream(zip), 1) })
        {
            var all = ZipTests.ReadAll(s);
            Assert.Equal(data, all["a.txt"]);
            Assert.Equal("hello"u8.ToArray(), all["b.txt"]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SevenZipRoundTripsSolidAndNot(bool solid)
    {
        byte[] data = ZipTests.Text(300_000, 4);
        byte[] seven = SevenOf(data, new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Solid = solid });
        using var r = new SevenZipReader(new MemoryStream(seven));
        var got = new Dictionary<string, byte[]>();
        while (r.GetNextEntry() is { } e)
        {
            using var s = r.OpenEntryStream();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            got[e.Name] = ms.ToArray();
        }

        Assert.Equal(data, got["a.txt"]);
        Assert.Equal("hello"u8.ToArray(), got["b.txt"]);
    }

    // The level table and the small-input reduction as 7-Zip applies them (testdata/ppmd/levels/NOTES.md).
    [Theory]
    [InlineData(9, 4096, 32, 1 << 16, 12, 1 << 20)]
    [InlineData(9, 4097, 32, 1 << 17, 12, 1 << 20)]
    [InlineData(9, 65537, 32, 1 << 21, 12, 1 << 21)]
    [InlineData(9, 1048576, 32, 1 << 24, 12, 1 << 24)]
    [InlineData(9, 1048577, 32, 1 << 25, 12, 1 << 25)]
    [InlineData(8, 1048577, 24, 1 << 25, 11, 1 << 25)]
    [InlineData(7, 1048577, 16, 1 << 25, 10, 1 << 25)]
    [InlineData(5, 1048577, 6, 1 << 24, 8, 1 << 24)]
    [InlineData(1, 100, 4, 1 << 16, 4, 1 << 20)]
    public void LevelsRecordTheOrderAndMemory7ZipRecords(int level, int size, int order7z, int memory7z, int orderZip, int memoryZip)
    {
        // One entry of exactly `size` bytes, so the size the writer reduces by is the size in the table.
        byte[] data = ZipTests.Text(size, 4);
        var seven = new MemoryStream();
        using (var w = new SevenZipWriter(seven, new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Level = level, Solid = false, EncodeHeader = false }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "a", Size = size }, data);
        }

        (int o, long m) = PpmdRecorded.SevenZip(seven.ToArray());
        Assert.Equal((order7z, (long)memory7z), (o, m));
        var zip = new MemoryStream();
        using (var w = new ZipWriter(zip, new ZipWriterOptions { Method = ZipMethod.Ppmd, Level = level }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "a", Size = size }, data);
        }

        (int oz, long mz, _, _, _) = PpmdRecorded.Zip(zip.ToArray());
        Assert.Equal((orderZip, (long)memoryZip), (oz, mz));
    }

    [Fact]
    public void ZipModelMemoryAboveTheLimitIsRefused()
    {
        byte[] zip = ZipOf(ZipTests.Text(300_000, 5), new ZipWriterOptions { Method = ZipMethod.Ppmd, Ppmd = new PpmdIOptions { Order = 6, MemorySize = 8 << 20 } });
        DrainZip(zip);
        var tight = new ZipReaderOptions { Limits = new ArchiveLimits { MaxPpmdMemorySize = 1 << 20 } };
        Assert.Throws<CompressionLimitException>(() => DrainZip(zip, tight));
    }

    [Fact]
    public void SevenZipModelMemoryAboveTheLimitIsRefused()
    {
        byte[] seven = SevenOf(ZipTests.Text(300_000, 6), new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Ppmd = new PpmdHOptions { Order = 6, MemorySize = 8 << 20 } });
        DrainSeven(seven);
        var tight = new SevenZipReaderOptions { Limits = new ArchiveLimits { MaxPpmdMemorySize = 1 << 20 } };
        Assert.Throws<CompressionLimitException>(() => DrainSeven(seven, tight));
    }

    // Truncation at many offsets and single-bit flips must end in the library's own exceptions, never anything else.
    private static void Hostile(byte[] archive, Action<byte[]> drain)
    {
        void Try(byte[] bytes)
        {
            try
            {
                drain(bytes);
            }
            catch (CompressionException)
            {
            }
        }

        int step = Math.Max(1, archive.Length / 400);
        for (int n = 0; n < archive.Length; n += step)
        {
            Try(archive.AsSpan(0, n).ToArray());
        }

        var rng = new Random(99);
        for (int i = 0; i < 400; i++)
        {
            byte[] copy = (byte[])archive.Clone();
            copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
            Try(copy);
        }
    }

    [Fact]
    public void HostileZip() => Hostile(ZipOf(ZipTests.Text(20_000, 7), new ZipWriterOptions { Method = ZipMethod.Ppmd }), z => DrainZip(z));

    [Fact]
    public void HostileSevenZip() => Hostile(SevenOf(ZipTests.Text(20_000, 8), new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd }), z => DrainSeven(z));
}

/// <summary>Reads the PPMd parameters a writer recorded, for the level tests. The 7z header must be written uncompressed.</summary>
internal static class PpmdRecorded
{
    // The coder record in an uncompressed 7z header: flags 0x23, ID 03 04 01, properties size 5, order, memory size.
    public static (int Order, long Memory) SevenZip(byte[] archive)
    {
        int at = archive.AsSpan(32).LastIndexOf(new byte[] { 0x23, 0x03, 0x04, 0x01, 0x05 }) + 32;
        Assert.True(at >= 32, "no PPMd coder record in the header");
        return (archive[at + 5], BitConverter.ToUInt32(archive, at + 6));
    }

    // The first entry's data starts with the two-byte method 98 header (APPNOTE 5.10.4); read through the central directory.
    public static (int Order, long Memory, int Restore, int DataOffset, int CompressedSize) Zip(byte[] zip)
    {
        int eocd = zip.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4B, 5, 6 });
        int cd = BitConverter.ToInt32(zip, eocd + 16);
        int compressed = BitConverter.ToInt32(zip, cd + 20);
        int local = BitConverter.ToInt32(zip, cd + 42);
        int data = local + 30 + BitConverter.ToUInt16(zip, local + 26) + BitConverter.ToUInt16(zip, local + 28);
        Assert.Equal(98, BitConverter.ToUInt16(zip, local + 8));
        int h = BitConverter.ToUInt16(zip, data);
        return ((h & 15) + 1, (long)(((h >> 4) & 255) + 1) << 20, h >> 12, data, compressed);
    }
}

/// <summary>PPMd archives against 7-Zip, run as a black box in both directions. They run only with ONEARCHIVE_INTEROP=1.</summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class PpmdArchiveInteropTests
{
    private static string SevenZip => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static void Tree(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "d"));
        File.WriteAllBytes(Path.Combine(root, "d/text.txt"), ZipTests.Text(400_000, 12));
        File.WriteAllBytes(Path.Combine(root, "rand.bin"), new Random(3).GetBytes(30_000));
        File.WriteAllText(Path.Combine(root, "tiny"), "x");
        File.WriteAllText(Path.Combine(root, "zero"), string.Empty);
    }

    private static void AssertSame(string a, string b)
    {
        foreach (string f in Directory.GetFiles(a, "*", SearchOption.AllDirectories))
        {
            Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(b, Path.GetRelativePath(a, f))));
        }
    }

    [Theory]
    [InlineData("zip", "-mx=1")]
    [InlineData("zip", "-mx=5")]
    [InlineData("zip", "-mx=9")]
    [InlineData("7z", "-mx=1 -ms=on")]
    [InlineData("7z", "-mx=5 -ms=off")]
    [InlineData("7z", "-mx=9 -ms=on")]
    public void WeReadArchivesFrom7Zip(string type, string switches)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        string archive = tmp.Combine("a." + type);
        Tools.Run("bash", ["-c", $"cd '{src}' && '{SevenZip}' a -t{type} -m0=PPMd {switches} -mmt=1 -bso0 -bsp0 '{archive}' ."]);
        string o = tmp.Combine("out");
        Archive.Extract(archive, o);
        AssertSame(src, o);
        if (type == "zip")
        {
            // Streaming path as well.
            using var fwd = new ForwardOnly(File.OpenRead(archive), 1);
            using var r = new ZipReader(fwd);
            int n = 0;
            while (r.GetNextEntry() is { } e)
            {
                using var s = r.OpenEntryStream();
                s.CopyTo(Stream.Null);
                n++;
            }

            Assert.True(n >= 4);
        }
    }

    // Levels 1-9 on an input above the size where 7-Zip shrinks the model (8 MiB for level 9): the order and memory we record equal the ones
    // 7-Zip records, and at levels 1, 5 and 9 the compressed bytes are identical. ONEARCHIVE_PPMD_LARGE names a file to use (default: generated text).
    [Fact]
    public void LevelsMatch7ZipOnALargeInput()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string? file = Environment.GetEnvironmentVariable("ONEARCHIVE_PPMD_LARGE");
        byte[] data = file is null ? ZipTests.Text(8_500_000, 9) : File.ReadAllBytes(file);
        Assert.True(data.Length > (8 << 20), "the input must be above 8 MiB");
        using var tmp = new TempDir();
        string input = tmp.Combine("in.bin");
        File.WriteAllBytes(input, data);
        for (int level = 1; level <= 9; level++)
        {
            bool bytes = level is 1 or 5 or 9;

            // 7z
            string theirs7 = tmp.Combine($"t{level}.7z");
            Tools.Run(SevenZip, ["a", "-t7z", "-m0=PPMd", $"-mx={level}", "-mmt=1", "-mhc=off", "-bso0", "-bsp0", theirs7, input]);
            var ours7 = new MemoryStream();
            using (var w = new SevenZipWriter(ours7, new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Level = level, Solid = false, EncodeHeader = false }, leaveOpen: true))
            {
                w.WriteEntry(new ArchiveEntry { Name = "in.bin", Size = data.Length }, data);
            }

            byte[] a = File.ReadAllBytes(theirs7);
            byte[] b = ours7.ToArray();
            Assert.Equal(PpmdRecorded.SevenZip(a), PpmdRecorded.SevenZip(b));
            if (bytes)
            {
                Assert.Equal(a.AsSpan(32, (int)BitConverter.ToInt64(a, 12)).ToArray(), b.AsSpan(32, (int)BitConverter.ToInt64(b, 12)).ToArray());
            }

            // zip
            string theirsZip = tmp.Combine($"t{level}.zip");
            Tools.Run(SevenZip, ["a", "-tzip", "-mm=PPMd", $"-mx={level}", "-mmt=1", "-bso0", "-bsp0", theirsZip, input]);
            var oursZip = new MemoryStream();
            using (var w = new ZipWriter(oursZip, new ZipWriterOptions { Method = ZipMethod.Ppmd, Level = level }, leaveOpen: true))
            {
                w.WriteEntry(new ArchiveEntry { Name = "in.bin", Size = data.Length }, data);
            }

            byte[] za = File.ReadAllBytes(theirsZip);
            byte[] zb = oursZip.ToArray();
            var ra = PpmdRecorded.Zip(za);
            var rb = PpmdRecorded.Zip(zb);
            Assert.Equal((ra.Order, ra.Memory, ra.Restore), (rb.Order, rb.Memory, rb.Restore));
            if (bytes)
            {
                Assert.Equal(za.AsSpan(ra.DataOffset, ra.CompressedSize).ToArray(), zb.AsSpan(rb.DataOffset, rb.CompressedSize).ToArray());
            }
        }
    }

    [Theory]
    [InlineData(ZipMethod.Ppmd, 1)]
    [InlineData(ZipMethod.Ppmd, 5)]
    [InlineData(ZipMethod.Ppmd, 9)]
    public void SevenZipReadsOurZip(ZipMethod method, int level)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        foreach (bool seekable in new[] { true, false })
        {
            string zip = tmp.Combine($"o{seekable}.zip");
            using (var file = File.Create(zip))
            using (Stream sink = seekable ? file : new WriteOnly(file))
            {
                Archive.Create(sink, new ArchiveCreateOptions { Format = ArchiveFormat.Zip, Zip = new ZipWriterOptions { Method = method, Level = level }, LeaveOpen = true }).Add(src, "").Save();
            }

            Tools.Run(SevenZip, ["t", "-bso0", "-bsp0", zip]);
            string o = tmp.Combine("x" + seekable);
            Tools.Run(SevenZip, ["x", "-y", "-bso0", "-bsp0", "-o" + o, zip]);
            AssertSame(src, o);
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, false)]
    [InlineData(9, true)]
    public void SevenZipReadsOurSevenZip(int level, bool solid)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Tree(src);
        string archive = tmp.Combine("o.7z");
        Archive.Create(archive, new ArchiveCreateOptions { Format = ArchiveFormat.SevenZip, SevenZip = new SevenZipWriterOptions { Method = SevenZipMethod.Ppmd, Level = level, Solid = solid } }).Add(src, "").Save();
        Tools.Run(SevenZip, ["t", "-bso0", "-bsp0", archive]);
        string o = tmp.Combine("x");
        Tools.Run(SevenZip, ["x", "-y", "-bso0", "-bsp0", "-o" + o, archive]);
        AssertSame(src, o);
    }
}
