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
