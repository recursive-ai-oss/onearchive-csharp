using System.Security.Cryptography;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>The compound file writer: every vector survives read, write and read again, generated trees of awkward shapes, and the name policy.</summary>
public class CompoundFileWriterTests
{
    private static readonly CompoundFileReaderOptions s_read = new() { Limits = new ArchiveLimits { MaxPathDepth = 200, MaxEntryCount = 100_000, MaxTotalSize = 1L << 30 } };

    private static byte[] Pattern(int length, int seed)
    {
        byte[] b = new byte[length];
        uint x = (uint)seed * 2654435761u + 1;
        for (int i = 0; i < b.Length; i++)
        {
            x = (x * 1103515245) + 12345;
            b[i] = (byte)(x >> 16);
        }

        return b;
    }

    // One line per entry (root included): everything the writer is meant to preserve.
    private static List<string> Snapshot(byte[] file)
    {
        using var r = new CompoundFileReader(file, s_read);
        var lines = new List<string>();
        void Add(CfbEntry e)
        {
            string digest = e.Kind == ArchiveEntryKind.File ? Convert.ToHexString(SHA256.HashData(Drain(r.OpenEntryStream(e)))) : "-";
            lines.Add($"{e.Name}|{e.RawName}|{e.Kind}|{e.Size}|{e.ClassId}|{e.StateBits}|{e.CreationFileTime}|{e.ModifiedFileTime}|{digest}");
        }

        Add(r.Root);
        while (r.GetNextEntry() is { } e)
        {
            Add(e);
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static byte[] Drain(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static byte[] Rewrite(byte[] file, int version, bool strict = false)
    {
        using var r = new CompoundFileReader(file, s_read);
        var output = new MemoryStream();
        using (var w = new CompoundFileWriter(output, new CompoundFileWriterOptions { MajorVersion = version, StrictNames = strict }, leaveOpen: true))
        {
            w.WriteEntry(r.Root);
            while (r.GetNextEntry() is { } e)
            {
                if (e.Kind == ArchiveEntryKind.File)
                {
                    using Stream s = r.OpenEntryStream(e);
                    w.WriteEntry(e, s);
                }
                else
                {
                    w.WriteEntry(e);
                }
            }
        }

        return output.ToArray();
    }

    public static IEnumerable<object[]> Files()
    {
        foreach (object[] g in CfbTestData.Good())
        {
            yield return g;
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryGoodVectorSurvivesRewriteInBothVersions(string file)
    {
        byte[] original = CfbTestData.Read(file);
        List<string> expected = Snapshot(original);
        foreach (int version in new[] { 3, 4 })
        {
            byte[] written = Rewrite(original, version);
            Assert.Equal(expected, Snapshot(written));
            using var r = new CompoundFileReader(written, s_read);
            Assert.Equal(version, r.Header.MajorVersion);
            Assert.Equal(Rewrite(original, version).AsSpan().ToArray(), written);
        }
    }

    [Fact]
    public void OfficeFilesSurviveRewrite()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "testdata", "cfb", "office");
        foreach (string f in Directory.GetFiles(dir).Where(f => f.EndsWith(".doc", StringComparison.Ordinal) || f.EndsWith(".xls", StringComparison.Ordinal) || f.EndsWith(".ppt", StringComparison.Ordinal)))
        {
            byte[] original = File.ReadAllBytes(f);
            List<string> expected = Snapshot(original);
            Assert.Equal(expected, Snapshot(Rewrite(original, 3)));
            Assert.Equal(expected, Snapshot(Rewrite(original, 4)));
        }
    }

    private static byte[] Build(int version, IEnumerable<(string Path, byte[]? Data)> items)
    {
        var output = new MemoryStream();
        using (var w = new CompoundFileWriter(output, new CompoundFileWriterOptions { MajorVersion = version }, leaveOpen: true))
        {
            foreach (var (path, data) in items)
            {
                if (data is null)
                {
                    w.WriteEntry(new ArchiveEntry { Name = path, Kind = ArchiveEntryKind.Directory });
                }
                else
                {
                    w.WriteEntry(new ArchiveEntry { Name = path, Kind = ArchiveEntryKind.File, Size = data.Length }, data);
                }
            }
        }

        return output.ToArray();
    }

    private static void AssertContains(byte[] file, IEnumerable<(string Path, byte[]? Data)> items)
    {
        var list = items.ToList();
        using var r = new CompoundFileReader(file, s_read);
        int streams = 0, storages = 0;
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.File)
            {
                streams++;
            }
            else
            {
                storages++;
            }
        }

        foreach (var (path, data) in list)
        {
            CfbEntry? e = r.Find(path);
            Assert.NotNull(e);
            if (data is null)
            {
                Assert.Equal(CfbObjectType.Storage, e.ObjectType);
            }
            else
            {
                Assert.Equal(data.Length, e.Size);
                Assert.Equal(data, Drain(r.OpenEntryStream(e)));
            }
        }

        Assert.Equal(list.Count(i => i.Data is not null), streams);
        Assert.True(storages >= list.Count(i => i.Data is null));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void SizesAroundTheMiniStreamCutoffAndSectorBoundaries(int version)
    {
        int[] sizes = [0, 1, 63, 64, 65, 127, 128, 511, 512, 513, 1023, 4094, 4095, 4096, 4097, 4607, 4608, 8191, 8192, 8193, 12288];
        var items = sizes.Select(n => ($"s{n}", (byte[]?)Pattern(n, n))).ToList();
        byte[] file = Build(version, items);
        AssertContains(file, items);
        using var r = new CompoundFileReader(file, s_read);
        Assert.True(r.Find("s4095")!.IsInMiniStream);
        Assert.False(r.Find("s4096")!.IsInMiniStream);
        Assert.Equal(0, file.Length % (version == 3 ? 512 : 4096));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void DeepNestingAndEmptyStorages(int version)
    {
        var items = new List<(string, byte[]?)>();
        string p = string.Empty;
        for (int i = 0; i < 150; i++)
        {
            p += (i == 0 ? "" : "/") + "d" + i;
            items.Add((p, null));
            items.Add((p + "/f", Pattern(i * 7, i)));
        }

        items.Add(("empty", null));
        byte[] file = Build(version, items);
        AssertContains(file, items);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(3, 4)]
    [InlineData(3, 7)]
    [InlineData(3, 8)]
    [InlineData(4, 100)]
    [InlineData(4, 2000)]
    [InlineData(3, 5000)]
    public void ManySiblingsFormAValidTree(int version, int count)
    {
        // Added in a scrambled order so the sorted tree is not the insertion order; names of mixed length and case.
        var items = new List<(string, byte[]?)>();
        for (int i = 0; i < count; i++)
        {
            int k = (int)((i * 2654435761L) % count);
            string name = (k % 3 == 0 ? "Item" : k % 3 == 1 ? "item" : "X") + k.ToString("D" + (1 + (k % 5)), System.Globalization.CultureInfo.InvariantCulture);
            if (items.Any(x => string.Equals(x.Item1, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            items.Add((name, k % 2 == 0 ? Pattern(k % 100, k) : null));
        }

        byte[] file = Build(version, items);
        AssertContains(file, items);
    }

    [Fact]
    public void ADifatIsWrittenWhenMoreThan109FatSectorsAreNeeded()
    {
        // 16 MB is about 31,300 sectors, so 245 FAT sectors and two DIFAT sectors.
        byte[] big = Pattern(16 << 20, 5);
        var items = new List<(string, byte[]?)> { ("big", big), ("small", Pattern(300, 1)), ("mid", Pattern(50_000, 2)) };
        byte[] file = Build(3, items);
        using (var r = new CompoundFileReader(file, s_read))
        {
            Assert.True(r.Header.DifatSectorCount >= 2);
            Assert.True(r.Header.FatSectorCount > 109);
        }

        AssertContains(file, items);
    }

    [Fact]
    public void RootAndEntryPropertiesAreStored()
    {
        var clsid = new Guid("0b0d0200-0000-0000-c000-000000000046");
        var output = new MemoryStream();
        using (var w = new CompoundFileWriter(output, null, leaveOpen: true))
        {
            w.WriteEntry(new CfbEntry { Name = string.Empty, RawName = string.Empty, Kind = ArchiveEntryKind.Directory, ClassId = clsid, StateBits = 7, ModifiedFileTime = 132_000_000_000_000_000UL });
            w.WriteEntry(new ArchiveEntry { Name = "a/b", Kind = ArchiveEntryKind.Directory, ModifiedTime = new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.Zero), ChangeTime = new DateTimeOffset(2019, 1, 2, 3, 4, 5, TimeSpan.Zero) });
            w.WriteEntry(new ArchiveEntry { Name = "a/b/s", Kind = ArchiveEntryKind.File, Size = 3, ModifiedTime = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero) }, new byte[] { 1, 2, 3 });
        }

        using var r = new CompoundFileReader(output.ToArray(), s_read);
        Assert.Equal(clsid, r.Root.ClassId);
        Assert.Equal(7u, r.Root.StateBits);
        Assert.Equal(132_000_000_000_000_000UL, r.Root.ModifiedFileTime);
        CfbEntry b = r.Find("a/b")!;
        Assert.Equal(new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.Zero), b.ModifiedTime);
        Assert.Equal(new DateTimeOffset(2019, 1, 2, 3, 4, 5, TimeSpan.Zero), b.CreationTime);
        Assert.Equal(new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero), r.Find("a/b/s")!.ModifiedTime);
        Assert.Equal(0UL, r.Find("a")!.ModifiedFileTime); // an implied parent has no times: the clock is never read
    }

    [Fact]
    public void NoClockAndDeterministic()
    {
        var items = new List<(string, byte[]?)> { ("x", Pattern(5000, 1)), ("y/z", Pattern(10, 2)), ("y", null) };
        byte[] a = Build(3, items);
        Thread.Sleep(20);
        Assert.Equal(a, Build(3, items));
        Assert.NotEqual(a, Build(4, items));
    }

    [Fact]
    public void NamesRoundTripThroughTheReadersMapping()
    {
        string[] names = ["plain", "a b", "with%percent", "100%25AB", "dots.", "..", ".", "sp ", "tab\t", "co:lon", "sl/ash", "back\\slash", "star*", "bang!", "\u00e9\u00c9x", "\ud83d\ude00", "\ud800lone", "\u0001ctl"];
        var output = new MemoryStream();
        var options = new CompoundFileWriterOptions { StrictNames = false };
        using (var w = new CompoundFileWriter(output, options, leaveOpen: true))
        {
            int i = 0;
            foreach (string n in names)
            {
                string safe = CompoundFileReader.SanitizeName(n);
                w.WriteEntry(new ArchiveEntry { Name = "st/" + safe, Kind = ArchiveEntryKind.File, Size = 1 }, new byte[] { (byte)i++ });
            }
        }

        using var r = new CompoundFileReader(output.ToArray(), s_read);
        var raw = new List<string>();
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind == ArchiveEntryKind.File)
            {
                raw.Add(e.RawName);
                Assert.Equal("st/" + CompoundFileReader.SanitizeName(e.RawName), e.Name);
            }
        }

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), raw.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("a%003Ab", CompoundFileReader.SanitizeName("a:b"));
        Assert.Equal("a:b", CompoundFileWriter.UnsanitizeName("a%003Ab"));
        Assert.Equal("%00zz", CompoundFileWriter.UnsanitizeName("%00zz"));
    }

    [Fact]
    public void BadEntriesAreRejectedAndLeaveTheWriterUsable()
    {
        var output = new MemoryStream();
        using var w = new CompoundFileWriter(output, null, leaveOpen: true);
        ArchiveEntry File(string n, int size = 0) => new() { Name = n, Kind = ArchiveEntryKind.File, Size = size };
        w.WriteEntry(File("ok"));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("OK")));                   // case-insensitive duplicate
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("a:b")));                  // strict names
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("a%0000b")));              // NUL
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File(new string('n', 32))));    // too long
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("../x")));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("ok/child")));             // below a stream
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("p/q%0021")));             // unsanitized ! is rejected too
        Assert.Throws<ArgumentException>(() => w.WriteEntry(File("short", 5), new byte[2]));
        Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "l", Kind = ArchiveEntryKind.Symlink, LinkTarget = "ok" }));
        w.WriteEntry(File(new string('n', 31)));
        w.Finish();
        using var r = new CompoundFileReader(output.ToArray(), s_read);
        Assert.Equal(2, r.GetChildren(r.Root).Count); // the failed entries left nothing behind, not even their parent storages
    }

    [Fact]
    public void BufferLimitIsATypedError()
    {
        using var w = new CompoundFileWriter(new MemoryStream(), new CompoundFileWriterOptions { MaxBufferedBytes = 1000 });
        w.WriteEntry(new ArchiveEntry { Name = "a", Kind = ArchiveEntryKind.File, Size = 900 }, new byte[900]);
        Assert.Throws<CompressionLimitException>(() => w.WriteEntry(new ArchiveEntry { Name = "b", Kind = ArchiveEntryKind.File, Size = 200 }, new byte[200]));
    }

    [Fact]
    public async Task AsyncWriteGivesTheSameBytes()
    {
        var items = new List<(string, byte[]?)> { ("x", Pattern(9000, 1)), ("d", null), ("d/y", Pattern(20, 2)) };
        byte[] sync = Build(4, items);
        var output = new MemoryStream();
        await using (var w = new CompoundFileWriter(output, new CompoundFileWriterOptions { MajorVersion = 4 }, leaveOpen: true))
        {
            foreach (var (p, d) in items)
            {
                if (d is null)
                {
                    await w.WriteEntryAsync(new ArchiveEntry { Name = p, Kind = ArchiveEntryKind.Directory });
                }
                else
                {
                    await w.WriteEntryAsync(new ArchiveEntry { Name = p, Kind = ArchiveEntryKind.File, Size = d.Length }, new MemoryStream(d));
                }
            }
        }

        Assert.Equal(sync, output.ToArray());
    }

    [Fact]
    public void WritesFilesForTheOlefileCheck()
    {
        // Run by tools/cfb/check-written.sh: it sets the output directory, then olefile reads what is written here.
        string? dir = Environment.GetEnvironmentVariable("ONEARCHIVE_CFB_WRITE_DIR");
        Assert.SkipUnless(!string.IsNullOrEmpty(dir), "ONEARCHIVE_CFB_WRITE_DIR is not set");
        Directory.CreateDirectory(Path.Combine(dir!, "office"));
        var good = new List<System.Text.Json.Nodes.JsonNode>();
        foreach (var g in CfbTestData.Manifest().GetProperty("good").EnumerateArray())
        {
            string file = g.GetProperty("file").GetString()!;
            byte[] original = CfbTestData.Read(file);
            foreach (int version in new[] { 3, 4 })
            {
                string name = file.Replace(".gz", string.Empty, StringComparison.Ordinal).Replace(".cfb", $".v{version}.cfb", StringComparison.Ordinal);
                File.WriteAllBytes(Path.Combine(dir!, name), Rewrite(original, version));
                good.Add(new System.Text.Json.Nodes.JsonObject { ["file"] = name, ["entries"] = System.Text.Json.Nodes.JsonNode.Parse(g.GetProperty("entries").GetRawText()) });
            }
        }

        File.WriteAllText(Path.Combine(dir!, "manifest.json"), new System.Text.Json.Nodes.JsonObject { ["good"] = new System.Text.Json.Nodes.JsonArray(good.ToArray()) }.ToJsonString());
        string office = Path.Combine(AppContext.BaseDirectory, "testdata", "cfb", "office");
        foreach (string f in Directory.GetFiles(office).Where(f => f.EndsWith(".doc", StringComparison.Ordinal) || f.EndsWith(".xls", StringComparison.Ordinal) || f.EndsWith(".ppt", StringComparison.Ordinal)))
        {
            foreach (int version in new[] { 3, 4 })
            {
                File.WriteAllBytes(Path.Combine(dir!, "office", Path.GetFileName(f) + $".v{version}"), Rewrite(File.ReadAllBytes(f), version));
            }
        }
    }

    [Fact]
    public void ArchiveCreateThenExtractGivesTheTreeBack()
    {
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "sub", "deeper"));
        File.WriteAllBytes(Path.Combine(src, "a.txt"), Pattern(100, 1));
        File.WriteAllBytes(Path.Combine(src, "big.bin"), Pattern(70_000, 2));
        File.WriteAllBytes(Path.Combine(src, "sub", "b.txt"), Pattern(5000, 3));
        File.WriteAllBytes(Path.Combine(src, "sub", "deeper", "empty"), []);
        string cfb = t.Combine("x.cfb");
        foreach (int version in new[] { 3, 4 })
        {
            Archive.Create(cfb, new ArchiveCreateOptions { CompoundFile = new CompoundFileWriterOptions { MajorVersion = version } }).Add(src, string.Empty).Save();
            Assert.Equal(ArchiveFormat.CompoundFile, ArchiveDetector.DetectFormat(File.ReadAllBytes(cfb).AsSpan(0, ArchiveDetector.PrefixLength)));
            string outDir = t.Combine("out" + version);
            Archive.Extract(cfb, outDir);
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                Assert.Equal(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(outDir, Path.GetRelativePath(src, f))));
            }

            Assert.True(Directory.Exists(Path.Combine(outDir, "sub", "deeper")));
        }
    }
}
