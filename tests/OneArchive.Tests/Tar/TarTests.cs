using System.Text;
using RecursiveAI.OneArchive.Tar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

public class TarTests
{
    private static readonly DateTimeOffset s_time = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    internal static List<(TarEntry Entry, byte[] Content)> ReadAll(byte[] tar, TarReaderOptions? options = null, bool forwardOnly = false)
    {
        Stream source = forwardOnly ? new ForwardOnly(tar) : new MemoryStream(tar);
        using var reader = new TarReader(source, options);
        var list = new List<(TarEntry, byte[])>();
        while (reader.GetNextEntry() is { } e)
        {
            using var ms = new MemoryStream();
            reader.OpenEntryStream().CopyTo(ms);
            list.Add((e, ms.ToArray()));
        }

        return list;
    }

    internal sealed class ForwardOnly(byte[] data) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // Short reads on purpose: a reader must cope with any split.
            int n = Math.Min(Math.Min(count, 700), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Write(TarFormat format, Action<TarWriter> body, int recordSize = 10240)
    {
        var ms = new MemoryStream();
        using (var w = new TarWriter(ms, new TarWriterOptions { Format = format, RecordSize = recordSize }, leaveOpen: true))
        {
            body(w);
        }

        return ms.ToArray();
    }

    [Fact]
    public void UstarHeaderBytesAreExact()
    {
        byte[] tar = Write(TarFormat.Ustar, w => w.WriteEntry(
            new ArchiveEntry { Name = "hello.txt", Size = 2, Mode = (UnixFileMode)0b110_100_100, ModifiedTime = s_time },
            "hi"u8.ToArray()));
        Assert.Equal(10240, tar.Length);
        byte[] expected = RawTar.Header("hello.txt", '0', 2, uid: 0, gid: 0, uname: null, gname: null, seal: false);
        RawTar.Octal(expected, 329, 8, 0);
        RawTar.Octal(expected, 337, 8, 0);
        RawTar.Seal(expected);
        Assert.Equal(expected, tar[..512]);
        Assert.Equal((byte)'h', tar[512]);
        Assert.All(tar[514..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void RecordSizeCanBeTurnedOff()
    {
        byte[] tar = Write(TarFormat.Pax, w => w.WriteEntry(new ArchiveEntry { Name = "a", Kind = ArchiveEntryKind.Directory }), 512);
        Assert.Equal(512 + 1024, tar.Length);
    }

    [Fact]
    public void PaxRoundTripKeepsEveryField()
    {
        string longName = string.Concat(Enumerable.Repeat("directory/", 40)) + "file.txt";
        var time = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(1234567);
        var extras = new Dictionary<string, string> { ["SCHILY.xattr.user.k"] = "v", ["comment"] = "kept as extra? no" };
        byte[] tar = Write(TarFormat.Pax, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = "dir", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)0b111_101_101, ModifiedTime = s_time }, (Stream?)null);
            w.WriteEntry(new ArchiveEntry { Name = longName, Size = 5, ModifiedTime = time, AccessTime = time.AddSeconds(1), ChangeTime = time.AddSeconds(2), Uid = 5_000_000, Gid = 7, UserName = new string('u', 40), GroupName = "staff", Extras = extras, Mode = (UnixFileMode)0b110_100_000 }, "12345"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "ünïcödé/日本語.txt", Size = 0, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(-86400) });
            w.WriteEntry(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = new string('t', 150) });
            w.WriteEntry(new ArchiveEntry { Name = "hard", Kind = ArchiveEntryKind.HardLink, LinkTarget = longName });
            w.WriteEntry(new ArchiveEntry { Name = "dev", Kind = ArchiveEntryKind.CharacterDevice, DeviceMajor = 4, DeviceMinor = 64 });
            w.WriteEntry(new ArchiveEntry { Name = "pipe", Kind = ArchiveEntryKind.Fifo });
        });

        var all = ReadAll(tar);
        Assert.Equal(7, all.Count);
        Assert.Equal("dir", all[0].Entry.Name);
        Assert.Equal(ArchiveEntryKind.Directory, all[0].Entry.Kind);
        Assert.Equal((UnixFileMode)0b111_101_101, all[0].Entry.Mode);

        TarEntry f = all[1].Entry;
        Assert.Equal(longName, f.Name);
        Assert.Equal(TarFormat.Pax, f.Format);
        Assert.Equal(5, f.Size);
        Assert.Equal("12345", Encoding.ASCII.GetString(all[1].Content));
        Assert.Equal(time, f.ModifiedTime);
        Assert.Equal(time.AddSeconds(1), f.AccessTime);
        Assert.Equal(time.AddSeconds(2), f.ChangeTime);
        Assert.Equal(5_000_000, f.Uid);
        Assert.Equal(7, f.Gid);
        Assert.Equal(new string('u', 40), f.UserName);
        Assert.Equal("staff", f.GroupName);
        Assert.Equal((UnixFileMode)0b110_100_000, f.Mode);
        Assert.Equal("v", f.Extras["SCHILY.xattr.user.k"]);
        Assert.Equal("kept as extra? no", f.Extras["comment"]);
        Assert.True(f.PaxRecords.ContainsKey("path"));

        Assert.Equal("ünïcödé/日本語.txt", all[2].Entry.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(-86400), all[2].Entry.ModifiedTime);
        Assert.Equal(ArchiveEntryKind.Symlink, all[3].Entry.Kind);
        Assert.Equal(new string('t', 150), all[3].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.HardLink, all[4].Entry.Kind);
        Assert.Equal(longName, all[4].Entry.LinkTarget);
        Assert.Equal(ArchiveEntryKind.CharacterDevice, all[5].Entry.Kind);
        Assert.Equal((4, 64), (all[5].Entry.DeviceMajor, all[5].Entry.DeviceMinor));
        Assert.Equal(ArchiveEntryKind.Fifo, all[6].Entry.Kind);
    }

    [Fact]
    public void UstarSplitsLongNamesIntoPrefixAndName()
    {
        string name = string.Join('/', Enumerable.Repeat(new string('d', 30), 4)) + "/" + new string('f', 60);
        byte[] tar = Write(TarFormat.Ustar, w => w.WriteEntry(new ArchiveEntry { Name = name, Size = 1 }, "x"u8.ToArray()));
        var e = ReadAll(tar)[0].Entry;
        Assert.Equal(name, e.Name);
        Assert.Equal(TarFormat.Ustar, e.Format);
        Assert.NotEqual(0, tar[345]);
    }

    [Fact]
    public void UstarRefusesWhatDoesNotFit()
    {
        Assert.Throws<ArchiveUnsupportedException>(() => Write(TarFormat.Ustar, w => w.WriteEntry(new ArchiveEntry { Name = new string('a', 300) })));
        Assert.Throws<ArchiveUnsupportedException>(() => Write(TarFormat.Ustar, w => w.WriteEntry(new ArchiveEntry { Name = "a", Uid = 3_000_000 })));
        Assert.Throws<ArchiveUnsupportedException>(() => Write(TarFormat.Ustar, w => w.WriteEntry(new ArchiveEntry { Name = "a", UserName = new string('u', 40) })));
    }

    [Fact]
    public void WriterRejectsBadContent()
    {
        using var w = new TarWriter(new MemoryStream(), leaveOpen: true);
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "a", Size = 10 }, "short"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "" }));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "l", Kind = ArchiveEntryKind.Symlink }));
    }

    [Fact]
    public void WriterIsDeterministic()
    {
        byte[] Make() => Write(TarFormat.Pax, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = "a/b", Size = 3, ModifiedTime = s_time.AddTicks(5), Extras = new Dictionary<string, string> { ["z"] = "1", ["a"] = "2" } }, "abc"u8.ToArray());
        });
        Assert.Equal(Make(), Make());
    }

    [Fact]
    public void SizesOverEightGiBUsePaxSize()
    {
        long size = (8L << 30) + 5;
        var sink = new CaptureSink(4096);
        using (var w = new TarWriter(sink, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "big", Size = size }, new ZeroStream(size));
        }

        Assert.True(sink.Total >= size);
        var reader = new TarReader(new MemoryStream(sink.Head), new TarReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = long.MaxValue } });
        TarEntry e = reader.GetNextEntry()!;
        Assert.Equal(size, e.Size);
        Assert.Equal(new string('0', 11), Encoding.ASCII.GetString(sink.Head, 1024 + 124, 11));
    }

    private sealed class CaptureSink(int keep) : Stream
    {
        public byte[] Head { get; } = new byte[keep];

        public long Total { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Total;

        public override long Position { get => Total; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Total < Head.Length)
            {
                int n = (int)Math.Min(count, Head.Length - Total);
                Array.Copy(buffer, offset, Head, (int)Total, n);
            }

            Total += count;
        }
    }

    private sealed class ZeroStream : Stream
    {
        private readonly long _length;
        private long _left;

        public ZeroStream(long length)
        {
            _length = length;
            _left = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ReadsPaxOverridesAndGlobalHeaders()
    {
        byte[] tar = new RawTar()
            .GlobalPax(("uname", "globaluser"), ("comment", "hello"))
            .Pax(("path", "long/path/name.txt"), ("mtime", "1700000000.123456789"), ("uid", "123456789"), ("comment", ""))
            .Entry("short.txt", data: "abc"u8.ToArray())
            .Entry("second.txt")
            .End();
        var all = ReadAll(tar);
        TarEntry a = all[0].Entry;
        Assert.Equal("long/path/name.txt", a.Name);
        Assert.Equal("short.txt", a.StoredName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddTicks(1234567), a.ModifiedTime);
        Assert.Equal(123456789, a.Uid);
        Assert.Equal("globaluser", a.UserName);
        Assert.False(a.Extras.ContainsKey("comment"));
        // The local record applies to one entry only; the global one to all.
        TarEntry b = all[1].Entry;
        Assert.Equal("second.txt", b.Name);
        Assert.Equal("globaluser", b.UserName);
        Assert.Equal("hello", b.Extras["comment"]);
        Assert.Equal(1_700_000_000, b.ModifiedTime!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void ReadsGnuLongNamesAndLinks()
    {
        string name = string.Concat(Enumerable.Repeat("long-dir/", 30)) + "x";
        string target = string.Concat(Enumerable.Repeat("t/", 100));
        byte[] tar = new RawTar()
            .GnuLong('L', name).GnuLong('K', target)
            .Entry("trunc", '2', link: "trunc", gnu: true)
            .End();
        TarEntry e = ReadAll(tar)[0].Entry;
        Assert.Equal(name, e.Name);
        Assert.Equal(target, e.LinkTarget);
        Assert.Equal(TarFormat.Gnu, e.Format);
    }

    [Fact]
    public void TypeFlagsAndNames()
    {
        byte[] tar = new RawTar()
            .Entry("dir/", '5')
            .Entry("slash-file/", '0')
            .Entry("contig", '7', data: "c"u8.ToArray())
            .Entry("weird", 'Z', data: "zz"u8.ToArray())
            .Entry("v7style", '\0', data: "v"u8.ToArray())
            .End();
        var all = ReadAll(tar);
        Assert.Equal(ArchiveEntryKind.Directory, all[0].Entry.Kind);
        Assert.Equal("dir", all[0].Entry.Name);
        Assert.Equal("dir/", all[0].Entry.StoredName);
        Assert.Equal(ArchiveEntryKind.Directory, all[1].Entry.Kind);
        Assert.Equal("c", Encoding.ASCII.GetString(all[2].Content));
        Assert.Equal(ArchiveEntryKind.File, all[3].Entry.Kind);
        Assert.Equal("zz", Encoding.ASCII.GetString(all[3].Content));
        Assert.Equal((byte)'0', all[4].Entry.TypeFlag);
    }

    [Fact]
    public void ReadsBase256Numbers()
    {
        byte[] header = RawTar.Header("big", '0', 0, seal: false);
        // size field: base-256, 0x80 then big-endian value 3.
        Array.Clear(header, 124, 12);
        header[124] = 0x80;
        header[135] = 3;
        RawTar.Seal(header);
        byte[] tar = [.. header, .. new byte[512], .. new byte[1024]];
        var e = ReadAll(tar)[0].Entry;
        Assert.Equal(3, e.Size);
    }

    private static readonly byte[] s_sparseData = Encoding.ASCII.GetBytes("ABCDEFGHIJxyz12");

    private static byte[] ExpectedSparse()
    {
        byte[] full = new byte[1000];
        "ABCDEFGHIJ"u8.CopyTo(full.AsSpan(100));
        "xyz12"u8.CopyTo(full.AsSpan(500));
        return full;
    }

    [Fact]
    public void ReadsOldGnuSparse()
    {
        byte[] h = RawTar.Header("sparse.bin", 'S', s_sparseData.Length, gnu: true, seal: false);
        RawTar.Octal(h, 386, 12, 100);
        RawTar.Octal(h, 398, 12, 10);
        RawTar.Octal(h, 410, 12, 500);
        RawTar.Octal(h, 422, 12, 5);
        RawTar.Octal(h, 483, 12, 1000);
        RawTar.Seal(h);
        byte[] tar = new RawTar().Raw(h).Data(s_sparseData).End();
        var (e, content) = ReadAll(tar)[0];
        Assert.Equal(TarSparseFormat.OldGnu, e.SparseFormat);
        Assert.Equal(1000, e.Size);
        Assert.Equal(15, e.StoredSize);
        Assert.Equal([new TarSparseRegion(100, 10), new TarSparseRegion(500, 5)], e.SparseRegions);
        Assert.Equal(ExpectedSparse(), content);
        Assert.Equal(ExpectedSparse(), ReadAll(tar, forwardOnly: true)[0].Content);
    }

    [Fact]
    public void ReadsOldGnuSparseWithExtensionBlocks()
    {
        // Five regions: four in the header, the fifth in an extension block.
        byte[] data = Encoding.ASCII.GetBytes("12345");
        byte[] h = RawTar.Header("s", 'S', 5, gnu: true, seal: false);
        for (int i = 0; i < 4; i++)
        {
            RawTar.Octal(h, 386 + (i * 24), 12, i * 10);
            RawTar.Octal(h, 398 + (i * 24), 12, 1);
        }

        h[482] = 1;
        RawTar.Octal(h, 483, 12, 100);
        RawTar.Seal(h);
        byte[] ext = RawTar.Block();
        RawTar.Octal(ext, 0, 12, 40);
        RawTar.Octal(ext, 12, 12, 1);
        byte[] tar = new RawTar().Raw(h).Raw(ext).Data(data).End();
        var (e, content) = ReadAll(tar)[0];
        Assert.Equal(5, e.SparseRegions.Count);
        Assert.Equal(100, content.Length);
        Assert.Equal((byte)'5', content[40]);
        Assert.Equal((byte)'1', content[0]);
    }

    [Fact]
    public void ReadsPaxSparseForms()
    {
        string map = "100,10,500,5";
        byte[] tar01 = new RawTar()
            .Pax(("GNU.sparse.major", "0"), ("GNU.sparse.minor", "1"), ("GNU.sparse.name", "sparse.bin"), ("GNU.sparse.realsize", "1000"), ("GNU.sparse.map", map))
            .Entry("GNUSparseFile.0/sparse.bin", data: s_sparseData).End();
        byte[] tar00 = new RawTar()
            .Pax(("GNU.sparse.size", "1000"), ("GNU.sparse.numblocks", "2"), ("GNU.sparse.offset", "100"), ("GNU.sparse.numbytes", "10"), ("GNU.sparse.offset", "500"), ("GNU.sparse.numbytes", "5"), ("GNU.sparse.name", "sparse.bin"))
            .Entry("GNUSparseFile.0/sparse.bin", data: s_sparseData).End();
        byte[] mapBlock = new byte[512];
        Encoding.ASCII.GetBytes("2\n100\n10\n500\n5\n").CopyTo(mapBlock, 0);
        byte[] tar10 = new RawTar()
            .Pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"), ("GNU.sparse.name", "sparse.bin"), ("GNU.sparse.realsize", "1000"))
            .Entry("GNUSparseFile.0/sparse.bin", data: [.. mapBlock, .. s_sparseData]).End();

        foreach ((byte[] tar, TarSparseFormat format) in new[] { (tar00, TarSparseFormat.Pax00), (tar01, TarSparseFormat.Pax01), (tar10, TarSparseFormat.Pax10) })
        {
            foreach (bool forward in new[] { false, true })
            {
                var (e, content) = ReadAll(tar, forwardOnly: forward)[0];
                Assert.Equal(format, e.SparseFormat);
                Assert.Equal("sparse.bin", e.Name);
                Assert.Equal(1000, e.Size);
                Assert.Equal(15, e.StoredSize);
                Assert.Equal(ExpectedSparse(), content);
            }
        }
    }

    [Fact]
    public void SparseEntryStreamSeeks()
    {
        byte[] h = RawTar.Header("sparse.bin", 'S', s_sparseData.Length, gnu: true, seal: false);
        RawTar.Octal(h, 386, 12, 100);
        RawTar.Octal(h, 398, 12, 10);
        RawTar.Octal(h, 410, 12, 500);
        RawTar.Octal(h, 422, 12, 5);
        RawTar.Octal(h, 483, 12, 1000);
        RawTar.Seal(h);
        byte[] tar = new RawTar().Raw(h).Data(s_sparseData).Entry("after", data: "tail"u8.ToArray()).End();
        using var reader = new TarReader(new MemoryStream(tar));
        TarEntry e = reader.GetNextEntry()!;
        using Stream s = reader.OpenEntryStream(e);
        Assert.True(s.CanSeek);
        s.Seek(498, SeekOrigin.Begin);
        byte[] buf = new byte[10];
        s.ReadExactly(buf);
        Assert.Equal("\0\0xyz12\0\0\0", Encoding.ASCII.GetString(buf));
        s.Seek(-1, SeekOrigin.End);
        Assert.Equal(0, s.ReadByte());
        Assert.Equal(-1, s.ReadByte());
        // The sequential position was not disturbed.
        Assert.Equal("after", reader.GetNextEntry()!.Name);
    }

    [Fact]
    public void RejectsBadSparseMaps()
    {
        byte[] Make(long off, long len, long real)
        {
            byte[] h = RawTar.Header("s", 'S', 5, gnu: true, seal: false);
            RawTar.Octal(h, 386, 12, off);
            RawTar.Octal(h, 398, 12, len);
            RawTar.Octal(h, 483, 12, real);
            RawTar.Seal(h);
            return new RawTar().Raw(h).Data("12345"u8.ToArray()).End();
        }

        Assert.Throws<CompressionFormatException>(() => ReadAll(Make(0, 5, 3)));      // beyond the real size
        Assert.Throws<CompressionFormatException>(() => ReadAll(Make(0, 4, 10)));     // sizes do not add up
        Assert.Equal(10, ReadAll(Make(0, 5, 10))[0].Entry.Size);
    }

    [Fact]
    public void ForwardOnlyAndSeekableAgree()
    {
        byte[] tar = Write(TarFormat.Pax, w =>
        {
            for (int i = 0; i < 20; i++)
            {
                byte[] data = new byte[i * 311];
                new Random(i).NextBytes(data);
                w.WriteEntry(new ArchiveEntry { Name = $"d/f{i}", Size = data.Length, ModifiedTime = s_time }, data);
            }
        });
        var a = ReadAll(tar);
        var b = ReadAll(tar, forwardOnly: true);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Entry.Name, b[i].Entry.Name);
            Assert.Equal(a[i].Content, b[i].Content);
            Assert.Equal(i * 311, a[i].Content.Length);
        }
    }

    [Fact]
    public async Task AsyncReadMatchesSync()
    {
        byte[] tar = Write(TarFormat.Pax, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = new string('n', 150), Size = 3 }, "abc"u8.ToArray());
            w.WriteEntry(new ArchiveEntry { Name = "b", Size = 1 }, "z"u8.ToArray());
        });
        await using var reader = new TarReader(new ForwardOnly(tar));
        var names = new List<string>();
        while (await reader.GetNextEntryAsync() is { } e)
        {
            names.Add(e.Name);
            var ms = new MemoryStream();
            await reader.OpenEntryStream().CopyToAsync(ms);
            Assert.Equal((int)e.Size, ms.Length);
        }

        Assert.Equal([new string('n', 150), "b"], names);

        var output = new MemoryStream();
        await using (var writer = new TarWriter(output, leaveOpen: true))
        {
            await writer.WriteEntryAsync(new ArchiveEntry { Name = new string('n', 150), Size = 3 }, new MemoryStream("abc"u8.ToArray()));
            await writer.WriteEntryAsync(new ArchiveEntry { Name = "b", Size = 1 }, new MemoryStream("z"u8.ToArray()));
        }

        Assert.Equal(tar, output.ToArray());
    }

    [Fact]
    public void MissingEndMarkerIsTruncationUnlessAllowed()
    {
        byte[] tar = new RawTar().Entry("a", data: "x"u8.ToArray()).End(trailer: false);
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(tar));
        Assert.True(ex.IsTruncated);
        Assert.Equal(1024, ex.Offset);
        Assert.Equal("tar", ex.Format);
        Assert.Single(ReadAll(tar, new TarReaderOptions { RequireEndOfArchive = false }));
    }

    [Fact]
    public void ConcatenatedArchivesNeedIgnoreZeroBlocks()
    {
        byte[] one = new RawTar().Entry("a").End();
        byte[] two = new RawTar().Entry("b").End();
        byte[] both = [.. one, .. two];
        Assert.Single(ReadAll(both));
        Assert.Equal(["a", "b"], ReadAll(both, new TarReaderOptions { IgnoreZeroBlocks = true }).Select(x => x.Entry.Name));
    }

    [Fact]
    public void EveryTruncationAndBitFlipEndsInATypedError()
    {
        byte[] mixed = Write(TarFormat.Pax, w =>
        {
            w.WriteEntry(new ArchiveEntry { Name = new string('p', 130), Size = 700, ModifiedTime = s_time.AddTicks(7) }, new byte[700]);
            w.WriteEntry(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "x" });
        }, 512);
        byte[] mapBlock = new byte[512];
        Encoding.ASCII.GetBytes("2\n100\n10\n500\n5\n").CopyTo(mapBlock, 0);
        byte[] sparse10 = new RawTar()
            .Pax(("GNU.sparse.major", "1"), ("GNU.sparse.minor", "0"), ("GNU.sparse.name", "sparse.bin"), ("GNU.sparse.realsize", "1000"))
            .Entry("GNUSparseFile.0/sparse.bin", data: [.. mapBlock, .. s_sparseData]).End();
        byte[] gnu = new RawTar().GnuLong('L', new string('g', 300)).Entry("t", gnu: true).End();

        foreach (byte[] archive in new[] { mixed, sparse10, gnu })
        {
            for (int cut = 0; cut < archive.Length; cut++)
            {
                Probe(archive[..cut], mustFail: cut < archive.Length - 512);
            }

            for (int i = 0; i < archive.Length; i += 7)
            {
                byte[] copy = (byte[])archive.Clone();
                copy[i] ^= (byte)(1 << (i % 8));
                Probe(copy, mustFail: false);
            }
        }

        static void Probe(byte[] data, bool mustFail)
        {
            try
            {
                ReadAll(data);
                if (mustFail)
                {
                    Assert.Fail("a truncated archive was accepted");
                }
            }
            catch (CompressionException)
            {
            }
        }
    }

    [Fact]
    public void HugeDeclaredSizesAreLimitedBeforeReading()
    {
        byte[] tar = new RawTar().Entry("a", size: 5_000_000_000).End();
        var ex = Assert.Throws<CompressionLimitException>(() => ReadAll(tar));
        Assert.Equal("MaxTotalSize", ex.LimitName);

        byte[] pax = new RawTar().Extended('x', new byte[2 << 20]).Entry("a").End();
        Assert.Equal("MaxExtendedHeaderSize", Assert.Throws<CompressionLimitException>(() => ReadAll(pax)).LimitName);
    }

    [Fact]
    public void LimitsOnCountAndPaths()
    {
        var rt = new RawTar();
        for (int i = 0; i < 10; i++)
        {
            rt.Entry("f" + i);
        }

        byte[] tar = rt.End();
        Assert.Equal("MaxEntryCount", Assert.Throws<CompressionLimitException>(() => ReadAll(tar, new TarReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 5 } })).LimitName);
        byte[] deep = new RawTar().GnuLong('L', string.Join('/', Enumerable.Repeat("d", 70)) + "/f").Entry("t", gnu: true).End();
        Assert.Equal("MaxPathDepth", Assert.Throws<CompressionLimitException>(() => ReadAll(deep)).LimitName);
        byte[] longPath = new RawTar().GnuLong('L', new string('x', 5000)).Entry("t", gnu: true).End();
        Assert.Equal("MaxPathLength", Assert.Throws<CompressionLimitException>(() => ReadAll(longPath)).LimitName);
    }

    [Fact]
    public void RejectsGarbageWithTheOffset()
    {
        byte[] garbage = new byte[2048];
        new Random(1).NextBytes(garbage);
        var ex = Assert.Throws<CompressionFormatException>(() => ReadAll(garbage));
        Assert.Equal("tar", ex.Format);
        Assert.Equal(0, ex.Offset);
        Assert.Contains("tar: invalid data at byte offset 0: expected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiVolumeIsUnsupported()
    {
        byte[] tar = new RawTar().Entry("a", 'M').End();
        Assert.Throws<ArchiveUnsupportedException>(() => ReadAll(tar));
    }
}
