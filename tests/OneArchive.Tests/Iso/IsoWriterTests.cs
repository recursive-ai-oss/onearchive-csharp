using System.Text;
using RecursiveAI.OneArchive.Iso;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Writes images with <see cref="IsoWriter"/> and reads them back with <see cref="IsoReader"/>.</summary>
public class IsoWriterTests
{
    private static readonly string[] s_orderNames = ["b.txt", "a.txt", "a1.txt", "a.c", "ab.txt", "a_.txt", "10.txt", "9.txt"];
    private static readonly DateTimeOffset s_time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private static ArchiveEntry File_(string name, long size, int mode = 0x1A4, long uid = 1000, long gid = 1000, DateTimeOffset? time = null) =>
        new() { Name = name, Kind = ArchiveEntryKind.File, Size = size, Mode = (UnixFileMode)mode, Uid = uid, Gid = gid, ModifiedTime = time ?? s_time };

    private static byte[] Write(Action<IsoWriter> fill, IsoWriterOptions? options = null)
    {
        var ms = new MemoryStream();
        using (var w = new IsoWriter(ms, options, leaveOpen: true))
        {
            fill(w);
        }

        return ms.ToArray();
    }

    private static void Put(IsoWriter w, string name, byte[] data, int mode = 0x1A4) => w.WriteEntry(File_(name, data.Length, mode), data);

    private static Dictionary<string, (IsoEntry Entry, byte[] Data)> ReadAll(byte[] image, IsoReaderOptions? options = null)
    {
        using var reader = new IsoReader(new MemoryStream(image), options);
        var map = new Dictionary<string, (IsoEntry, byte[])>();
        while (reader.GetNextEntry() is { } e)
        {
            using var ms = new MemoryStream();
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = reader.OpenEntryStream();
                s.CopyTo(ms);
            }

            map.Add(e.Name, (e, ms.ToArray()));
        }

        return map;
    }

    private static void Everything(IsoWriter w)
    {
        Put(w, "a.txt", "hello\n"u8.ToArray());
        Put(w, "sub/b.bin", "xyz"u8.ToArray());
        Put(w, "big.bin", ContainerVectors.Big(70000), 0x180);
        w.WriteEntry(File_("empty", 0), (Stream?)null);
        Put(w, "run.sh", "#!/bin/sh\n"u8.ToArray(), 0x1ED);
        Put(w, "\u00fcn\u00ef-\u65e5\u672c.txt", "unicode"u8.ToArray());
        w.WriteEntry(new ArchiveEntry { Name = "private", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0x1C0, ModifiedTime = s_time, Uid = 5, Gid = 6 });
        Put(w, "private/p.txt", "secret"u8.ToArray(), 0x180);
        w.WriteEntry(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "a.txt", ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "sub/up", Kind = ArchiveEntryKind.Symlink, LinkTarget = "../a.txt", ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "abs", Kind = ArchiveEntryKind.Symlink, LinkTarget = "/usr/bin/env", ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "dev/null", Kind = ArchiveEntryKind.CharacterDevice, DeviceMajor = 1, DeviceMinor = 3, Mode = (UnixFileMode)0x1B6, ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "dev/sda", Kind = ArchiveEntryKind.BlockDevice, DeviceMajor = 8, DeviceMinor = 0, Mode = (UnixFileMode)0x180, ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "dev/pipe", Kind = ArchiveEntryKind.Fifo, Mode = (UnixFileMode)0x1A4, ModifiedTime = s_time });
        w.WriteEntry(new ArchiveEntry { Name = "hard", Kind = ArchiveEntryKind.HardLink, LinkTarget = "a.txt", ModifiedTime = s_time });
    }

    [Fact]
    public void DefaultImageRoundTripsEveryKindWithItsMetadata()
    {
        byte[] image = Write(Everything);
        var all = ReadAll(image);
        Assert.Equal("hello\n"u8.ToArray(), all["a.txt"].Data);
        Assert.Equal("hello\n"u8.ToArray(), all["hard"].Data);
        Assert.Equal("xyz"u8.ToArray(), all["sub/b.bin"].Data);
        Assert.Equal(ContainerVectors.Big(70000), all["big.bin"].Data);
        Assert.Empty(all["empty"].Data);
        Assert.Equal("unicode"u8.ToArray(), all["\u00fcn\u00ef-\u65e5\u672c.txt"].Data);
        Assert.Equal((UnixFileMode)0x180, all["big.bin"].Entry.Mode);
        Assert.Equal((UnixFileMode)0x1ED, all["run.sh"].Entry.Mode);
        Assert.Equal((UnixFileMode)0x1C0, all["private"].Entry.Mode);
        Assert.Equal(5, all["private"].Entry.Uid);
        Assert.Equal(6, all["private"].Entry.Gid);
        Assert.Equal(1000, all["a.txt"].Entry.Uid);
        Assert.Equal((UnixFileMode)0x1ED, all["sub"].Entry.Mode);   // implied directory
        Assert.Equal(ArchiveEntryKind.Symlink, all["link"].Entry.Kind);
        Assert.Equal("a.txt", all["link"].Entry.LinkTarget);
        Assert.Equal("../a.txt", all["sub/up"].Entry.LinkTarget);
        Assert.Equal("/usr/bin/env", all["abs"].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.CharacterDevice, all["dev/null"].Entry.Kind);
        Assert.Equal(1, all["dev/null"].Entry.DeviceMajor);
        Assert.Equal(3, all["dev/null"].Entry.DeviceMinor);
        Assert.Equal(ArchiveEntryKind.BlockDevice, all["dev/sda"].Entry.Kind);
        Assert.Equal(8, all["dev/sda"].Entry.DeviceMajor);
        Assert.Equal(ArchiveEntryKind.Fifo, all["dev/pipe"].Entry.Kind);
        Assert.All(all.Where(static kv => kv.Key is not ("sub" or "dev")), static kv => Assert.Equal(1_700_000_000, kv.Value.Entry.ModifiedTime?.ToUnixTimeSeconds()));
        Assert.Equal(0, all["sub"].Entry.ModifiedTime?.ToUnixTimeSeconds());   // implied directories get the default time
        Assert.Equal(all["a.txt"].Entry.ExtentLocation, all["hard"].Entry.ExtentLocation);
        Assert.Equal(all["a.txt"].Entry.RockRidge!.SerialNumber, all["hard"].Entry.RockRidge!.SerialNumber);
        Assert.Equal(2, all["a.txt"].Entry.RockRidge!.Links);
        Assert.Equal(0, image.Length % 2048);
        Assert.True(all["sub"].Entry.RockRidge!.Links >= 2);
    }

    [Fact]
    public void OutputIsTheSameEveryTimeAndReadsWithoutAClock()
    {
        byte[] first = Write(Everything);
        byte[] second = Write(Everything);
        Assert.Equal(first, second);
        // No volume date unless asked: sixteen '0' digits and a zero offset.
        Assert.Equal(Enumerable.Repeat((byte)'0', 16).Append((byte)0).ToArray(), first.AsSpan((16 * 2048) + 813, 17).ToArray());
        byte[] dated = Write(Everything, new IsoWriterOptions { VolumeTime = s_time });
        using var reader = new IsoReader(new MemoryStream(dated));
        Assert.Equal(s_time, reader.Volume.CreationTime);
        Assert.NotEqual(first, dated);
    }

    [Fact]
    public async Task AsyncWritingGivesTheSameBytes()
    {
        var ms = new MemoryStream();
        await using (var w = new IsoWriter(ms, null, leaveOpen: true))
        {
            await w.WriteEntryAsync(File_("a.txt", 6), new MemoryStream("hello\n"u8.ToArray()));
            await w.FinishAsync();
        }

        byte[] small = Write(static w => Put(w, "a.txt", "hello\n"u8.ToArray()));
        Assert.Equal(small, ms.ToArray());
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(3, true, true)]
    [InlineData(3, false, true)]
    [InlineData(3, true, false)]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    public void EveryOptionCombinationReadsBackWithTheNamesOfItsHierarchy(int level, bool joliet, bool rockRidge)
    {
        byte[] image = Write(
            static w =>
            {
                Put(w, "readme.txt", "r"u8.ToArray());
                Put(w, "docs/Manual.PDF", "m"u8.ToArray());
                Put(w, "docs/a-rather-long-file-name-for-the-plain-hierarchy.markdown", "l"u8.ToArray());
                Put(w, "ALPHA/beta.c", "c"u8.ToArray());
            },
            new IsoWriterOptions { Level = level, Joliet = joliet, RockRidge = rockRidge });
        using var reader = new IsoReader(new MemoryStream(image));
        Assert.Equal(joliet, reader.Volume.HasJoliet);
        Assert.Equal(rockRidge, reader.Volume.HasRockRidge);
        var all = ReadAll(image);
        if (rockRidge || joliet)
        {
            Assert.Equal("m"u8.ToArray(), all["docs/Manual.PDF"].Data);
            Assert.Equal("l"u8.ToArray(), all["docs/a-rather-long-file-name-for-the-plain-hierarchy.markdown"].Data);
            Assert.Equal("c"u8.ToArray(), all["ALPHA/beta.c"].Data);
        }
        else
        {
            Assert.Equal("r"u8.ToArray(), all["README.TXT"].Data);
            Assert.Equal("m"u8.ToArray(), all["DOCS/MANUAL.PDF"].Data);
            Assert.Equal("c"u8.ToArray(), all["ALPHA/BETA.C"].Data);
            string longName = level == 1 ? "DOCS/A_RATHER.MAR" : "DOCS/A_RATHER_LONG_FILE_NAM.MARKDOWN";
            Assert.True(all.ContainsKey(longName), string.Join(", ", all.Keys));
        }

        // The other hierarchies stay reachable.
        if (joliet && rockRidge)
        {
            var j = ReadAll(image, new IsoReaderOptions { Tree = IsoTree.Joliet });
            Assert.Equal("m"u8.ToArray(), j["docs/Manual.PDF"].Data);
            Assert.All(j.Values, static v => Assert.Equal(IsoTree.Joliet, v.Entry.Tree));
        }

        var plain = ReadAll(image, new IsoReaderOptions { UseRockRidge = false, Tree = IsoTree.Primary });
        Assert.Equal("m"u8.ToArray(), plain["DOCS/MANUAL.PDF"].Data);
    }

    [Fact]
    public void NamesThatCollideAfterCuttingGetCountersInWritingOrder()
    {
        byte[] image = Write(
            static w =>
            {
                Put(w, "longfilename-one.txt", "1"u8.ToArray());
                Put(w, "longfilename-two.txt", "2"u8.ToArray());
                Put(w, "LONGFILE.TXT", "3"u8.ToArray());
                Put(w, "a:b?.txt", "j"u8.ToArray());
            },
            new IsoWriterOptions { Level = 1 });
        var all = ReadAll(image);
        Assert.Equal("1"u8.ToArray(), all["longfilename-one.txt"].Data);
        Assert.Equal("2"u8.ToArray(), all["longfilename-two.txt"].Data);
        Assert.Equal("3"u8.ToArray(), all["LONGFILE.TXT"].Data);
        string[] iso = [.. all.Values.Select(static v => v.Entry.IsoPath)];
        Assert.Equal(iso.Length, iso.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("LONGFILE.TXT", all["longfilename-one.txt"].Entry.IsoPath);
        Assert.Equal("LONGF001.TXT", all["longfilename-two.txt"].Entry.IsoPath);
        Assert.Equal("LONGF002.TXT", all["LONGFILE.TXT"].Entry.IsoPath);
        var j = ReadAll(image, new IsoReaderOptions { Tree = IsoTree.Joliet });
        Assert.Equal("a_b_.txt", j.Keys.Single(static k => k.StartsWith("a_", StringComparison.Ordinal)));
    }

    [Fact]
    public void RecordsAreInTheOrderClause93Asks()
    {
        byte[] image = Write(
            static w =>
            {
                foreach (string n in s_orderNames)
                {
                    Put(w, n, [1]);
                }
            });
        using var reader = new IsoReader(new MemoryStream(image), new IsoReaderOptions { UseRockRidge = false, Tree = IsoTree.Primary });
        // Names compare padded with spaces, so "A.TXT" comes before "A1.TXT" although "." sorts above nothing, and "_" sorts after letters.
        Assert.Equal(["10.TXT", "9.TXT", "A.C", "A.TXT", "A1.TXT", "AB.TXT", "A_.TXT", "B.TXT"], reader.Items.Select(static e => e.IsoPath));
    }

    [Fact]
    public void LongNamesAndTargetsUseContinuationAreas()
    {
        string name = new string('n', 250) + ".txt";
        string target = string.Join('/', Enumerable.Range(0, 80).Select(static i => $"segment-{i:D3}")) + "/" + new string('t', 300);
        byte[] image = Write(
            w =>
            {
                Put(w, name, "long"u8.ToArray());
                for (int i = 0; i < 20; i++)
                {
                    w.WriteEntry(new ArchiveEntry { Name = $"link{i:D2}", Kind = ArchiveEntryKind.Symlink, LinkTarget = target + i, ModifiedTime = s_time });
                }
            });
        var all = ReadAll(image);
        Assert.Equal("long"u8.ToArray(), all[name].Data);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(target + i, all[$"link{i:D2}"].Entry.LinkTarget);
        }
    }

    [Fact]
    public void ThousandsOfEntriesFillManyDirectorySectors()
    {
        byte[] image = Write(
            static w =>
            {
                for (int i = 0; i < 3000; i++)
                {
                    Put(w, $"dir{i % 7}/file-{i:D5}.dat", Encoding.ASCII.GetBytes(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            });
        var all = ReadAll(image, new IsoReaderOptions { Tree = IsoTree.Joliet });
        Assert.Equal(3000 + 7, all.Count);
        Assert.Equal("1234"u8.ToArray(), all["dir2/file-01234.dat"].Data);
        var rr = ReadAll(image);
        Assert.Equal(3007, rr.Count);
    }

    [Fact]
    public void ContentSpoolsToATemporaryFileBeyondTheMemoryBuffer()
    {
        byte[] a = ContainerVectors.Big(100_000);
        byte[] b = ContainerVectors.Big(50_000);
        byte[] image = Write(
            w =>
            {
                Put(w, "a.bin", a);
                Put(w, "b.bin", b);
            },
            new IsoWriterOptions { MaxMemoryBuffer = 64 * 1024 });
        var all = ReadAll(image);
        Assert.Equal(a, all["a.bin"].Data);
        Assert.Equal(b, all["b.bin"].Data);
    }

    [Fact]
    public void AnEmptyImageIsValid()
    {
        byte[] image = Write(static _ => { });
        using var reader = new IsoReader(new MemoryStream(image));
        Assert.Empty(reader.Items);
        Assert.Equal("CDROM", reader.Volume.VolumeId);
    }

    [Fact]
    public void VolumeFieldsAreWritten()
    {
        byte[] image = Write(static _ => { }, new IsoWriterOptions { VolumeId = "MY_DISC", SystemId = "LINUX", PublisherId = "Me", PreparerId = "Tool", ApplicationId = "Test", VolumeSetId = "Set" });
        using var reader = new IsoReader(new MemoryStream(image));
        Assert.Equal("MY_DISC", reader.Volume.VolumeId);
        Assert.Equal("LINUX", reader.Volume.SystemId);
        Assert.Equal("Me", reader.Volume.PublisherId);
        Assert.Equal("Tool", reader.Volume.PreparerId);
        Assert.Equal("Test", reader.Volume.ApplicationId);
        Assert.Equal("Set", reader.Volume.VolumeSetId);
        Assert.Equal(image.Length / 2048, reader.Volume.VolumeSpaceBlocks);
    }

    [Fact]
    public void BadEntriesAreRefusedWithTypedErrors()
    {
        Assert.Throws<ArgumentException>(() => Write(static w => Put(w, "../x", [1])));
        Assert.Throws<ArgumentException>(() => Write(static w => Put(w, "a/./x", [1])));
        Assert.Throws<ArgumentException>(() => Write(static w => Put(w, "", [1])));
        Assert.Throws<ArgumentException>(() => Write(static w => { Put(w, "x", [1]); Put(w, "x", [2]); }));
        Assert.Throws<ArgumentException>(() => Write(static w => { Put(w, "x", [1]); Put(w, "x/y", [2]); }));
        Assert.Throws<ArgumentException>(() => Write(static w => w.WriteEntry(new ArchiveEntry { Name = "h", Kind = ArchiveEntryKind.HardLink, LinkTarget = "missing" })));
        Assert.Throws<ArgumentException>(() => Write(static w => w.WriteEntry(File_("short", 10), new byte[3])));
        Assert.Throws<ArchiveUnsupportedException>(() => Write(static w => w.WriteEntry(new ArchiveEntry { Name = "s", Kind = ArchiveEntryKind.Symlink, LinkTarget = "t" }), new IsoWriterOptions { RockRidge = false }));
        Assert.Throws<ArchiveUnsupportedException>(() => Write(static w => Put(w, new string('x', 300), [1])));
        Assert.Throws<ArgumentException>(() => new IsoWriter(new MemoryStream(), new IsoWriterOptions { Level = 4 }));
        Assert.Throws<ArgumentException>(() => new IsoWriter(new MemoryStream(), new IsoWriterOptions { VolumeId = new string('v', 33) }));
        Assert.Throws<ArgumentException>(() => new IsoWriter(new MemoryStream([], false)));
    }

    [Fact]
    public void ExplicitDirectoryAfterItsContentsKeepsItsAttributes()
    {
        byte[] image = Write(
            static w =>
            {
                Put(w, "d/f.txt", [1]);
                w.WriteEntry(new ArchiveEntry { Name = "d", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0x1C0, ModifiedTime = s_time.AddDays(1) });
            });
        var all = ReadAll(image);
        Assert.Equal((UnixFileMode)0x1C0, all["d"].Entry.Mode);
        Assert.Equal(s_time.AddDays(1), all["d"].Entry.ModifiedTime);
    }

    [Fact]
    public void TheHappyPathCreatesAnIsoFromADirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onearchive-iso-" + Guid.NewGuid().ToString("N"));
        string iso = dir + ".iso";
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello\n");
            File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "bee\n");
            Archive.Create(iso, new ArchiveCreateOptions { Normalize = true }).Add(dir, string.Empty).Save();
            using Archive archive = Archive.Open(iso);
            Assert.Equal(ArchiveFormat.Iso, archive.Format);
            Assert.Equal(["a.txt", "sub", "sub/b.txt"], archive.Entries.Select(static e => e.Name));
            using Stream s = archive.OpenEntry(archive.Find("sub/b.txt")!);
            Assert.Equal("bee\n", new StreamReader(s).ReadToEnd());
            Assert.Equal(0, archive.Find("a.txt")!.ModifiedTime?.ToUnixTimeSeconds());
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            File.Delete(iso);
        }
    }

    [Fact]
    public void FilesOfFourGibOrMoreAreWrittenInSeveralExtents()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_LARGE") == "1", "set ONEARCHIVE_LARGE=1 to write a 4.5 GiB file (the spool needs that much disk)");
        const long Size = (4L << 30) + (512L << 20);
        var sink = new HeadKeepingStream(4 << 20);
        using (var w = new IsoWriter(sink, null, leaveOpen: true))
        {
            w.WriteEntry(File_("huge.bin", Size), new ZeroStream(Size));
            w.WriteEntry(File_("tail.txt", 4), "tail"u8.ToArray());
        }

        using var reader = new IsoReader(new SparseTailStream(sink.Head, sink.Length));
        IsoEntry huge = reader.Items.Single(static e => e.Name == "huge.bin");
        Assert.Equal(Size, huge.Size);
        Assert.Equal(2, huge.Extents.Count);
        Assert.Equal(0xFFFFF800L, huge.Extents[0].Length);
        Assert.Equal(huge.Extents[0].Location + (0xFFFFF800L / 2048), huge.Extents[1].Location);
        Assert.Equal(4, reader.Items.Single(static e => e.Name == "tail.txt").Size);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _at);
            Array.Clear(buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Keeps the first bytes written and counts the rest.</summary>
    private sealed class HeadKeepingStream(int keep) : Stream
    {
        private readonly byte[] _head = new byte[keep];
        private long _length;

        public byte[] Head => _head;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _length;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_length < keep)
            {
                int n = (int)Math.Min(count, keep - _length);
                Array.Copy(buffer, offset, _head, _length, n);
            }

            _length += count;
        }
    }
}
