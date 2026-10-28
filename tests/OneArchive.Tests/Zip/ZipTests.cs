using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>A stream that only reads forward, in small odd pieces (to shake out buffering mistakes).</summary>
internal sealed class ForwardOnly(Stream inner, int piece = 7) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, piece));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A write-only stream that cannot seek.</summary>
internal sealed class WriteOnly(Stream inner) : Stream
{
    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}

/// <summary>Builds zip structures byte by byte, independently of the library's writer, so tests can make unusual and hostile archives.</summary>
internal sealed class RawZip
{
    public sealed class Entry
    {
        public string Name = "";
        public byte[] RawName = [];
        public ushort Method;
        public ushort Flags;
        public byte[] Data = [];            // bytes as stored (already compressed)
        public byte[] Plain = [];           // uncompressed content, for the CRC
        public uint? Crc;
        public long? DeclaredSize;
        public long? DeclaredCompressed;
        public byte[] LocalExtra = [];
        public byte[] CentralExtra = [];
        public uint ExternalAttributes;
        public ushort VersionMadeBy = 20;
        public bool Descriptor;
        public bool DescriptorSignature = true;
        public bool DescriptorZip64;
        public long? OffsetOverride;        // central directory offset
        public long LocalOffset;
    }

    private readonly List<byte> _out = [];
    private readonly List<Entry> _entries = [];

    public long Position => _out.Count;

    public static byte[] Deflate(byte[] data)
    {
        var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            d.Write(data);
        }

        return ms.ToArray();
    }

    public static uint Crc(byte[] data) => RecursiveAI.OneCompress.Checksums.Crc32.Compute(data);

    public static Entry Stored(string name, byte[] data) => new() { Name = name, Method = 0, Data = data, Plain = data };

    public static Entry Deflated(string name, byte[] data) => new() { Name = name, Method = 8, Data = Deflate(data), Plain = data };

    public RawZip Add(Entry e)
    {
        e.LocalOffset = _out.Count;
        byte[] name = e.RawName.Length > 0 ? e.RawName : Encoding.UTF8.GetBytes(e.Name);
        uint crc = e.Crc ?? Crc(e.Plain);
        ushort flags = (ushort)(e.Flags | (e.Descriptor ? 8 : 0) | (name.Any(static b => b >= 0x80) && e.RawName.Length == 0 ? 0x800 : 0));
        e.Flags = flags;
        var h = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(h, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), e.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(12), 0x21);
        if (!e.Descriptor)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(18), (uint)(e.DeclaredCompressed ?? e.Data.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(22), (uint)(e.DeclaredSize ?? e.Plain.Length));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)e.LocalExtra.Length);
        _out.AddRange(h);
        _out.AddRange(name);
        _out.AddRange(e.LocalExtra);
        _out.AddRange(e.Data);
        if (e.Descriptor)
        {
            var d = new List<byte>();
            if (e.DescriptorSignature)
            {
                d.AddRange(BitConverter.GetBytes(0x08074b50u));
            }

            d.AddRange(BitConverter.GetBytes(crc));
            if (e.DescriptorZip64)
            {
                d.AddRange(BitConverter.GetBytes((long)e.Data.Length));
                d.AddRange(BitConverter.GetBytes((long)e.Plain.Length));
            }
            else
            {
                d.AddRange(BitConverter.GetBytes((uint)e.Data.Length));
                d.AddRange(BitConverter.GetBytes((uint)e.Plain.Length));
            }

            _out.AddRange(d);
        }

        e.RawName = name;
        _entries.Add(e);
        return this;
    }

    public RawZip AddBytes(byte[] raw)
    {
        _out.AddRange(raw);
        return this;
    }

    public byte[] Finish(string comment = "", long? countOverride = null, long? directoryOffsetOverride = null, long? directorySizeOverride = null)
    {
        long dirStart = _out.Count;
        foreach (Entry e in _entries)
        {
            uint crc = e.Crc ?? Crc(e.Plain);
            var h = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(4), e.VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(6), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(8), e.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(10), e.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(14), 0x21);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(20), (uint)(e.DeclaredCompressed ?? e.Data.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)(e.DeclaredSize ?? e.Plain.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(28), (ushort)e.RawName.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(30), (ushort)e.CentralExtra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(38), e.ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(42), (uint)(e.OffsetOverride ?? e.LocalOffset));
            _out.AddRange(h);
            _out.AddRange(e.RawName);
            _out.AddRange(e.CentralExtra);
        }

        long dirSize = _out.Count - dirStart;
        byte[] c = Encoding.UTF8.GetBytes(comment);
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        long count = countOverride ?? _entries.Count;
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)count);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), (uint)(directorySizeOverride ?? dirSize));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), (uint)(directoryOffsetOverride ?? dirStart));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(20), (ushort)c.Length);
        _out.AddRange(end);
        _out.AddRange(c);
        return [.. _out];
    }

    /// <summary>The bytes of a zip with local headers only (no directory), as a streaming writer would end before its trailer.</summary>
    public byte[] WithoutDirectory() => [.. _out];

    public static byte[] Extra(ushort id, byte[] data)
    {
        var b = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, id);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)data.Length);
        data.CopyTo(b, 4);
        return b;
    }
}

public class ZipTests
{
    internal static byte[] Text(int length, int seed = 1)
    {
        var rng = new Random(seed);
        string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa"];
        var sb = new StringBuilder();
        while (sb.Length < length)
        {
            sb.Append(words[rng.Next(words.Length)]).Append(rng.Next(3) == 0 ? "\n" : " ");
        }

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    internal static Dictionary<string, byte[]> ReadAll(Stream s, ZipReaderOptions? options = null, bool leaveOpen = true)
    {
        var result = new Dictionary<string, byte[]>();
        using var reader = new ZipReader(s, options, leaveOpen);
        while (reader.GetNextEntry() is { } e)
        {
            using var content = reader.OpenEntryStream();
            var ms = new MemoryStream();
            content.CopyTo(ms);
            result[e.Name] = ms.ToArray();
        }

        return result;
    }

    private static List<ZipEntry> List(byte[] zip, ZipReaderOptions? options = null)
    {
        var list = new List<ZipEntry>();
        using var reader = new ZipReader(new MemoryStream(zip), options);
        while (reader.GetNextEntry() is { } e)
        {
            list.Add(e);
        }

        return list;
    }

    private static byte[] Write(Action<ZipWriter> fill, ZipWriterOptions? options = null, bool seekable = true)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(seekable ? ms : new WriteOnly(ms), options, leaveOpen: true))
        {
            fill(w);
        }

        return ms.ToArray();
    }

    private static readonly DateTimeOffset s_time = new(2024, 5, 17, 12, 34, 56, TimeSpan.Zero);

    private static void Standard(ZipWriter w)
    {
        w.WriteEntry(new ArchiveEntry { Name = "docs", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_time, Mode = (UnixFileMode)0b111_101_101 }, (Stream?)null);
        byte[] a = Text(100_000, 1);
        w.WriteEntry(new ArchiveEntry { Name = "docs/a.txt", Size = a.Length, ModifiedTime = s_time, Mode = (UnixFileMode)0b110_100_100 }, a);
        w.WriteEntry(new ArchiveEntry { Name = "empty", Size = 0, ModifiedTime = s_time }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "docs/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "a.txt", ModifiedTime = s_time }, (Stream?)null);
        byte[] u = Text(1000, 2);
        w.WriteEntry(new ArchiveEntry { Name = "ünïcödé-日本語.txt", Size = u.Length, ModifiedTime = s_time }, u);
        byte[] tiny = "x"u8.ToArray();
        w.WriteEntry(new ArchiveEntry { Name = "tiny", Size = 1 }, tiny);
    }

    [Theory]
    [InlineData(ZipMethod.Store, true)]
    [InlineData(ZipMethod.Store, false)]
    [InlineData(ZipMethod.Deflate64, true)]
    [InlineData(ZipMethod.Deflate64, false)]
    [InlineData(ZipMethod.BZip2, true)]
    [InlineData(ZipMethod.BZip2, false)]
    [InlineData(ZipMethod.Zstd, true)]
    [InlineData(ZipMethod.Zstd, false)]
    public void WriterOutputReadsBackSeekableAndForwardOnly(ZipMethod method, bool seekableOutput)
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = method }, seekableOutput);
        var expect = new Dictionary<string, byte[]> { ["docs"] = [], ["docs/a.txt"] = Text(100_000, 1), ["empty"] = [], ["docs/link"] = "a.txt"u8.ToArray(), ["ünïcödé-日本語.txt"] = Text(1000, 2), ["tiny"] = "x"u8.ToArray() };

        var viaDirectory = ReadAll(new MemoryStream(zip));
        Assert.Equal(expect.Keys.OrderBy(k => k, StringComparer.Ordinal), viaDirectory.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var kv in expect)
        {
            if (kv.Key != "docs/link")
            {
                Assert.Equal(kv.Value, viaDirectory[kv.Key]);
            }
        }

        var forward = ReadAll(new ForwardOnly(new MemoryStream(zip)));
        foreach (var kv in expect)
        {
            Assert.Equal(kv.Value, forward[kv.Key]);
        }

        var forced = ReadAll(new MemoryStream(zip), new ZipReaderOptions { ForceStreaming = true });
        Assert.Equal(forward.Count, forced.Count);
    }

    [Fact]
    public void DirectoryExposesMetadata()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Store });
        List<ZipEntry> entries = List(zip);
        ZipEntry link = entries.Single(e => e.Name == "docs/link");
        Assert.Equal(ArchiveEntryKind.Symlink, link.Kind);
        Assert.Equal("a.txt", link.LinkTarget);
        ZipEntry dir = entries.Single(e => e.Name == "docs");
        Assert.Equal(ArchiveEntryKind.Directory, dir.Kind);
        Assert.Equal((UnixFileMode)0b111_101_101, dir.Mode);
        ZipEntry file = entries.Single(e => e.Name == "docs/a.txt");
        Assert.Equal(s_time, file.ModifiedTime);
        Assert.Equal(100_000, file.Size);
        Assert.Equal(ZipMethod.Store, file.Method);
        Assert.Equal(3, file.HostSystem);
        ZipEntry uni = entries.Single(e => e.Name.StartsWith("ün", StringComparison.Ordinal));
        Assert.True(uni.IsUtf8Name);
        Assert.Equal((UnixFileMode)0b110_100_100, file.Mode);
    }

    [Fact]
    public void SubSecondTimesUseNtfsField()
    {
        DateTimeOffset t = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
        byte[] zip = Write(w => w.WriteEntry(new ArchiveEntry { Name = "f", Size = 1, ModifiedTime = t, AccessTime = t.AddDays(1) }, "a"u8.ToArray()), new ZipWriterOptions { Method = ZipMethod.Store });
        ZipEntry e = List(zip).Single();
        Assert.Equal(t, e.ModifiedTime);
        Assert.Equal(t.AddDays(1), e.AccessTime);
        Assert.Contains(e.ExtraFields, f => f.Id == 0x000a);
    }

    [Fact]
    public void OutputIsTheSameForAnyChunkingAndStreamKind()
    {
        byte[] a = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Zstd, DataDescriptors = ZipDataDescriptorMode.Always }, seekable: true);
        byte[] b = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Zstd, DataDescriptors = ZipDataDescriptorMode.Always }, seekable: false);
        Assert.Equal(a, b);
        byte[] c = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Zstd }, seekable: false);
        Assert.Equal(a, c);
    }

    [Fact]
    public void DescriptorsAreReadForwardWithAndWithoutSignature()
    {
        byte[] data = Text(5000, 3);
        foreach (bool sig in new[] { true, false })
        {
            var raw = new RawZip();
            var e = RawZip.Deflated("d.txt", data);
            e.Descriptor = true;
            e.DescriptorSignature = sig;
            raw.Add(e);
            raw.Add(RawZip.Deflated("after.txt", [1, 2, 3]));
            byte[] zip = raw.Finish();
            var got = ReadAll(new ForwardOnly(new MemoryStream(zip), 5));
            Assert.Equal(data, got["d.txt"]);
            Assert.Equal(new byte[] { 1, 2, 3 }, got["after.txt"]);
            Assert.Equal(data, ReadAll(new MemoryStream(zip))["d.txt"]);
        }
    }

    [Fact]
    public void Descriptor64IsRead()
    {
        byte[] data = Text(300, 4);
        var raw = new RawZip();
        var e = RawZip.Deflated("z.txt", data);
        e.Descriptor = true;
        e.DescriptorZip64 = true;
        e.LocalExtra = RawZip.Extra(1, new byte[16]);
        raw.Add(e);
        byte[] zip = raw.Finish();
        Assert.Equal(data, ReadAll(new ForwardOnly(new MemoryStream(zip)))["z.txt"]);
    }

    [Fact]
    public void ForwardReadListsEntriesWithoutDirectory()
    {
        var raw = new RawZip();
        raw.Add(RawZip.Stored("a", [1, 2, 3]));
        raw.Add(RawZip.Deflated("b", Text(400)));
        byte[] cut = raw.WithoutDirectory();
        using var reader = new ZipReader(new ForwardOnly(new MemoryStream(cut)));
        Assert.Equal("a", reader.GetNextEntry()!.Name);
        Assert.Equal("b", reader.GetNextEntry()!.Name);
        var ex = Assert.Throws<CompressionFormatException>(() => reader.GetNextEntry());
        Assert.True(ex.IsTruncated);
    }

    [Fact]
    public void Cp437AndUnicodePathNames()
    {
        var raw = new RawZip();
        var e = RawZip.Stored("x", [1]);
        e.RawName = [0x82, (byte)'a'];     // CP437: é a
        raw.Add(e);
        var e2 = RawZip.Stored("x2", [1]);
        byte[] oldName = "caf\u00e9"u8.ToArray();
        e2.RawName = oldName;
        // Info-ZIP Unicode Path field: version 1, CRC-32 of the header name, UTF-8 name.
        byte[] field = [1, .. BitConverter.GetBytes(RawZip.Crc(oldName)), .. "naïve"u8.ToArray()];
        e2.CentralExtra = RawZip.Extra(0x7075, field);
        raw.Add(e2);
        List<ZipEntry> list = List(raw.Finish());
        Assert.Equal("éa", list[0].Name);
        Assert.Equal("naïve", list[1].Name);
    }

    [Fact]
    public void UnsupportedMethodIsListedAndRefusedWhenOpened()
    {
        var raw = new RawZip();
        var e = RawZip.Stored("ppmd.bin", [9, 9, 9]);
        e.Method = 98;
        raw.Add(e);
        raw.Add(RawZip.Stored("ok", [1]));
        byte[] zip = raw.Finish();
        using var reader = new ZipReader(new MemoryStream(zip));
        ZipEntry first = reader.GetNextEntry()!;
        Assert.Equal(ZipMethod.Ppmd, first.Method);
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => reader.OpenEntryStream());
        Assert.Contains("98", ex.Message, StringComparison.Ordinal);
        Assert.Equal("ok", reader.GetNextEntry()!.Name);

        // Forward-only: the unsupported entry has a known length, so it is skipped.
        using var forward = new ZipReader(new ForwardOnly(new MemoryStream(zip)));
        Assert.Equal("ppmd.bin", forward.GetNextEntry()!.Name);
        Assert.Equal("ok", forward.GetNextEntry()!.Name);
    }

    [Fact]
    public void ChecksumMismatchIsDetected()
    {
        var raw = new RawZip();
        var e = RawZip.Stored("bad", [1, 2, 3]);
        e.Crc = 12345;
        raw.Add(e);
        byte[] zip = raw.Finish();
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(zip)));
        Assert.Throws<CompressionFormatException>(() => ReadAll(new ForwardOnly(new MemoryStream(zip))));
        Assert.Equal(new byte[] { 1, 2, 3 }, ReadAll(new MemoryStream(zip), new ZipReaderOptions { VerifyChecksum = false })["bad"]);
    }

    [Fact]
    public void LyingSizeIsRejected()
    {
        var raw = new RawZip();
        var e = RawZip.Deflated("lie", Text(1000));
        e.DeclaredSize = 10;
        raw.Add(e);
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(raw.Finish())));
        var raw2 = new RawZip();
        var e2 = RawZip.Deflated("lie", Text(1000));
        e2.DeclaredSize = 5000;
        raw2.Add(e2);
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(raw2.Finish())));
    }

    [Fact]
    public void EmptyArchiveHasNoEntries()
    {
        byte[] zip = Write(_ => { });
        Assert.Equal(22, zip.Length);
        Assert.Empty(List(zip));
        using var forward = new ZipReader(new ForwardOnly(new MemoryStream(zip)));
        Assert.Null(forward.GetNextEntry());
    }

    [Fact]
    public void ArchiveCommentRoundTrips()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Store, Comment = "héllo" });
        using var reader = new ZipReader(new MemoryStream(zip));
        reader.GetNextEntry();
        Assert.Equal("héllo", reader.ArchiveComment);
    }

    [Fact]
    public void Zip64AlwaysRoundTrips()
    {
        foreach (bool seekable in new[] { true, false })
        {
            byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Zstd, Zip64 = ZipZip64Mode.Always }, seekable);
            Assert.Equal(Text(100_000, 1), ReadAll(new MemoryStream(zip))["docs/a.txt"]);
            Assert.Equal(Text(100_000, 1), ReadAll(new ForwardOnly(new MemoryStream(zip)))["docs/a.txt"]);
            Assert.True(List(zip).All(e => e.VersionNeeded >= 45));
        }
    }

    [Fact]
    public void ManyEntriesUseZip64EndRecord()
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, new ZipWriterOptions { Method = ZipMethod.Store }, leaveOpen: true))
        {
            for (int i = 0; i < 70_000; i++)
            {
                w.WriteEntry(new ArchiveEntry { Name = "f" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) }, (Stream?)null);
            }
        }

        byte[] zip = ms.ToArray();
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(zip.Length - 22)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(zip.Length - 22 + 10)));
        var limits = new ArchiveLimits { MaxEntryCount = 100_000 };
        int count = 0;
        using var reader = new ZipReader(new MemoryStream(zip), new ZipReaderOptions { Limits = limits });
        while (reader.GetNextEntry() is not null)
        {
            count++;
        }

        Assert.Equal(70_000, count);
        Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new ZipReader(new MemoryStream(zip), new ZipReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 1000 } });
            r.GetNextEntry();
        });
    }

    [Fact]
    public void RandomAccessOpensEntriesOutOfOrder()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Zstd });
        using var reader = new ZipReader(new MemoryStream(zip));
        var all = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            all.Add(e);
        }

        using Stream last = reader.OpenEntryStream(all.Single(e => e.Name == "tiny"));
        using Stream big = reader.OpenEntryStream(all.Single(e => e.Name == "docs/a.txt"));
        var ms = new MemoryStream();
        big.CopyTo(ms);
        Assert.Equal(Text(100_000, 1), ms.ToArray());
        Assert.Equal(120, last.ReadByte());
    }

    [Fact]
    public void ZipContentsMatchBclReader()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Store });
        using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        Assert.Equal(Text(100_000, 1), Read(archive.GetEntry("docs/a.txt")!));

        static byte[] Read(ZipArchiveEntry e)
        {
            using Stream s = e.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    [Fact]
    public void ReadsBclWrittenZip()
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream s = archive.CreateEntry("a/b.txt", CompressionLevel.Optimal).Open())
            {
                s.Write(Text(20_000, 9));
            }

            archive.CreateEntry("dir/");
            using (Stream s = archive.CreateEntry("é.txt", CompressionLevel.Fastest).Open())
            {
                s.Write("hi"u8);
            }
        }

        foreach (Stream source in new Stream[] { new MemoryStream(ms.ToArray()), new ForwardOnly(new MemoryStream(ms.ToArray())) })
        {
            var got = ReadAll(source);
            Assert.Equal(Text(20_000, 9), got["a/b.txt"]);
            Assert.Equal("hi"u8.ToArray(), got["é.txt"]);
            Assert.True(got.ContainsKey("dir"));
        }
    }

    [Fact]
    public void WriterRejectsWhatZipCannotHold()
    {
        var ms = new MemoryStream();
        using var w = new ZipWriter(ms, new ZipWriterOptions { Method = ZipMethod.Store }, leaveOpen: true);
        Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "h", Kind = ArchiveEntryKind.HardLink, LinkTarget = "x" }, (Stream?)null));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "f", Size = 5 }, new byte[3]));
        Assert.Throws<ArgumentException>(() => new ZipWriter(new WriteOnly(new MemoryStream()), new ZipWriterOptions { DataDescriptors = ZipDataDescriptorMode.Never }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void DeflateEntriesRoundTripSeekableAndStreaming(int level)
    {
        byte[] data = Text(200_000, 9);
        foreach (bool streaming in new[] { false, true })
        {
            var ms = new MemoryStream();
            Stream sink = streaming ? new WriteOnly(ms) : ms;
            using (var w = new ZipWriter(sink, new ZipWriterOptions { Method = ZipMethod.Deflate, Level = level }, leaveOpen: true))
            {
                w.WriteEntry(new ArchiveEntry { Name = "f", Size = data.Length }, data);
                w.Finish();
            }

            using var reader = new ZipReader(new MemoryStream(ms.ToArray()));
            Assert.Equal("f", reader.GetNextEntry()!.Name);
            using Stream s = reader.OpenEntryStream();
            var o = new MemoryStream();
            s.CopyTo(o);
            Assert.Equal(data, o.ToArray());

            using var forward = new ZipReader(new ForwardOnly(new MemoryStream(ms.ToArray())));
            forward.GetNextEntry();
            var o2 = new MemoryStream();
            forward.OpenEntryStream().CopyTo(o2);
            Assert.Equal(data, o2.ToArray());
        }
    }
}
