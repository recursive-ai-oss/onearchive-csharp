using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Raw entry copy: ZipReader.OpenRawEntryStream and ZipWriter.WriteRawEntry.</summary>
public class ZipRawCopyTests
{
    private const string Password = "fake-password-1";

    internal sealed class NoSeekWrite(Stream inner) : Stream
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

    private sealed class NoSeekRead(byte[] data) : Stream
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
            int n = Math.Min(Math.Min(count, 1 + (_at * 7 % 311)), data.Length - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    // Names, extracted content and raw bytes of every entry, in archive order.
    internal static List<(ZipEntry Entry, byte[] Raw, byte[] Content)> ReadAll(byte[] zip, ZipReaderOptions options)
    {
        var list = new List<(ZipEntry, byte[], byte[])>();
        using var reader = new ZipReader(new MemoryStream(zip), options);
        var entries = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            entries.Add(e);
        }

        foreach (ZipEntry e in entries)
        {
            byte[] raw = Drain(reader.OpenRawEntryStream(e));
            byte[] content = Drain(reader.OpenEntryStream(e));
            list.Add((e, raw, content));
        }

        return list;
    }

    // Copies every entry of `zip` raw into a new archive (opening them in reverse order, to show any order works).
    internal static byte[] Copy(byte[] zip, ZipReaderOptions ro, ZipWriterOptions? wo, bool seekableOutput)
    {
        var output = new MemoryStream();
        using (var reader = new ZipReader(new MemoryStream(zip), ro))
        {
            var entries = new List<ZipEntry>();
            while (reader.GetNextEntry() is { } e)
            {
                entries.Add(e);
            }

            var raws = new byte[entries.Count][];
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                raws[i] = Drain(reader.OpenRawEntryStream(entries[i]));
            }

            Stream target = seekableOutput ? output : new NoSeekWrite(output);
            using var writer = new ZipWriter(target, wo, leaveOpen: true);
            for (int i = 0; i < entries.Count; i++)
            {
                writer.WriteRawEntry(ZipRawEntry.FromEntry(entries[i]), new MemoryStream(raws[i]));
            }
        }

        return output.ToArray();
    }

    internal static void AssertCopy(byte[] src, string password, ZipWriterOptions? wo = null, bool seekableOutput = true, bool lazy = false)
    {
        var ro = new ZipReaderOptions { Password = password, LazyDirectory = lazy };
        byte[] copy = Copy(src, ro, wo, seekableOutput);
        var a = ReadAll(src, ro);
        var b = ReadAll(copy, ro);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            ZipEntry x = a[i].Entry, y = b[i].Entry;
            Assert.Equal(x.Name, y.Name);
            Assert.True(x.RawName.Span.SequenceEqual(y.RawName.Span));
            Assert.Equal(x.Method, y.Method);
            Assert.Equal(x.Flags, y.Flags);
            Assert.Equal(x.Crc32, y.Crc32);
            Assert.Equal(x.CompressedSize, y.CompressedSize);
            Assert.Equal(x.UncompressedSize, y.UncompressedSize);
            Assert.Equal(x.DosTime, y.DosTime);
            Assert.Equal(x.DosDate, y.DosDate);
            Assert.Equal(x.ExternalAttributes, y.ExternalAttributes);
            Assert.Equal(x.VersionMadeBy, y.VersionMadeBy);
            Assert.Equal(x.InternalAttributes, y.InternalAttributes);
            Assert.Equal(x.Kind, y.Kind);
            Assert.True(x.RawComment.Span.SequenceEqual(y.RawComment.Span));
            Assert.Equal(x.ModifiedTime, y.ModifiedTime);
            Assert.Equal(a[i].Raw, b[i].Raw);
            Assert.Equal(a[i].Content, b[i].Content);
        }
    }

    private static byte[] Build(ZipWriterOptions options, bool seekable = true)
    {
        var ms = new MemoryStream();
        Stream target = seekable ? ms : new NoSeekWrite(ms);
        using (var w = new ZipWriter(target, options, leaveOpen: true))
        {
            w.WriteEntry(new ZipEntry { Name = "dir/", Kind = ArchiveEntryKind.Directory });
            w.WriteEntry(new ZipEntry { Name = "empty.txt", Kind = ArchiveEntryKind.File, Size = 0 }, Stream.Null);
            byte[] text = ZipTests.Text(200_000, 3);
            w.WriteEntry(new ZipEntry { Name = "text.txt", Kind = ArchiveEntryKind.File, Size = text.Length, ModifiedTime = new DateTimeOffset(2023, 3, 4, 5, 6, 8, TimeSpan.Zero), Mode = (UnixFileMode)0x1ED }, new MemoryStream(text));
            byte[] noise = new Random(9).GetBytes(5000);
            w.WriteEntry(new ZipEntry { Name = "ünï/日本語.bin", Kind = ArchiveEntryKind.File, Size = noise.Length }, new MemoryStream(noise));
            w.WriteEntry(new ZipEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "text.txt" });
        }

        return ms.ToArray();
    }

    public static IEnumerable<object[]> BuilderOptions()
    {
        foreach (ZipMethod m in new[] { ZipMethod.Store, ZipMethod.Deflate, ZipMethod.Deflate64, ZipMethod.BZip2, ZipMethod.Zstd, ZipMethod.Ppmd })
        {
            yield return new object[] { m, ZipZip64Mode.Auto, ZipDataDescriptorMode.Auto, false };
            yield return new object[] { m, ZipZip64Mode.Always, ZipDataDescriptorMode.Always, false };
            yield return new object[] { m, ZipZip64Mode.Auto, ZipDataDescriptorMode.Auto, true };
        }
    }

    [Theory]
    [MemberData(nameof(BuilderOptions))]
    public void OurWriterArchivesCopyRaw(ZipMethod method, ZipZip64Mode zip64, ZipDataDescriptorMode descriptors, bool nonSeekableSource)
    {
        byte[] src = Build(new ZipWriterOptions { Method = method, Zip64 = zip64, DataDescriptors = descriptors }, !nonSeekableSource);
        AssertCopy(src, string.Empty);
        AssertCopy(src, string.Empty, lazy: true);
        AssertCopy(src, string.Empty, new ZipWriterOptions { Zip64 = ZipZip64Mode.Always }, seekableOutput: false);
    }

    [Theory]
    [InlineData(ZipAesStrength.Aes128, ZipAesVersion.Ae1)]
    [InlineData(ZipAesStrength.Aes256, ZipAesVersion.Ae2)]
    public void EncryptedEntriesCopyRawWithoutAPassword(ZipAesStrength strength, ZipAesVersion version)
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Deflate, Password = Password, AesStrength = strength, AesVersion = version });
        AssertCopy(src, Password);

        // The raw copy needs no password to be made, only to be read afterwards.
        byte[] copy = Copy(src, new ZipReaderOptions(), null, true);
        Assert.Equal(ReadAll(src, new ZipReaderOptions { Password = Password }).Select(e => e.Content), ReadAll(copy, new ZipReaderOptions { Password = Password }).Select(e => e.Content));
    }

    public static IEnumerable<object[]> VectorFiles()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "testdata");
        foreach (string f in Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            yield return new object[] { Path.GetRelativePath(root, f) };
        }
    }

    [Theory]
    [MemberData(nameof(VectorFiles))]
    public void CommittedVectorsCopyRaw(string relative)
    {
        // The hostile vectors lie about their sizes, so their content cannot be read; they are only copied.
        Assert.SkipWhen(relative.StartsWith("raw-copy-", StringComparison.Ordinal), "covered by its own test");
        byte[] src = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", relative));
        AssertCopy(src, Password);
        AssertCopy(src, Password, seekableOutput: false, lazy: true);
    }

    // Found by the zip-raw-copy fuzz target: made-by host 0 (FAT) with the DOS directory bit. Copying it with the writer's own host (Unix)
    // turned the directory into a file of 8 GiB.
    [Fact]
    public void HostSystemOfAFatEntryIsKept()
    {
        byte[] src = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "raw-copy-version-made-by-zero.zip"));
        using var reader = new ZipReader(new MemoryStream(Copy(src, new ZipReaderOptions(), null, true)));
        ZipEntry e = reader.GetNextEntry()!;
        Assert.Equal(ArchiveEntryKind.Directory, e.Kind);
        Assert.Equal(0, e.HostSystem);
    }

    [Fact]
    public void RawStreamIsTheStoredBytes()
    {
        byte[] text = ZipTests.Text(50_000, 5);
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, new ZipWriterOptions { Method = ZipMethod.Deflate }, leaveOpen: true))
        {
            w.WriteEntry(new ZipEntry { Name = "a.txt", Kind = ArchiveEntryKind.File, Size = text.Length }, new MemoryStream(text));
        }

        using var reader = new ZipReader(new MemoryStream(ms.ToArray()));
        ZipEntry e = reader.GetNextEntry()!;
        byte[] raw = Drain(reader.OpenRawEntryStream(e));
        Assert.Equal(e.CompressedSize, raw.Length);
        Assert.True(raw.Length < text.Length);
        using var inflated = new MemoryStream();
        using (var d = new System.IO.Compression.DeflateStream(new MemoryStream(raw), System.IO.Compression.CompressionMode.Decompress))
        {
            d.CopyTo(inflated);
        }

        Assert.Equal(text, inflated.ToArray());
    }

    [Fact]
    public void ForwardOnlyOpensEntriesWithKnownSizes()
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Deflate, DataDescriptors = ZipDataDescriptorMode.Never });
        var expected = ReadAll(src, new ZipReaderOptions());
        using var reader = new ZipReader(new NoSeekRead(src));
        int i = 0;
        var output = new MemoryStream();
        using (var writer = new ZipWriter(output, leaveOpen: true))
        {
            while (reader.GetNextEntry() is { } e)
            {
                if (i % 2 == 0)
                {
                    byte[] raw = Drain(reader.OpenRawEntryStream(e));
                    Assert.Equal(expected[i].Raw, raw);
                    writer.WriteRawEntry(ZipRawEntry.FromEntry(e), new MemoryStream(raw));
                }

                i++;
            }
        }

        Assert.Equal(expected.Count, i);
        Assert.Equal(expected.Where((_, k) => k % 2 == 0).Select(x => x.Content), ReadAll(output.ToArray(), new ZipReaderOptions()).Select(x => x.Content));
    }

    [Fact]
    public void ForwardOnlyRefusesDataDescriptorEntries()
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Deflate }, seekable: false);
        using var reader = new ZipReader(new NoSeekRead(src));
        bool refused = false;
        while (reader.GetNextEntry() is { } e)
        {
            if (e.HasDataDescriptor && !e.IsSizeKnown)
            {
                ArchiveUnsupportedException ex = Assert.Throws<ArchiveUnsupportedException>(() => reader.OpenRawEntryStream(e));
                Assert.Contains("data descriptor", ex.Message, StringComparison.Ordinal);
                Assert.Throws<ArgumentException>(() => ZipRawEntry.FromEntry(e));
                refused = true;
            }
        }

        Assert.True(refused);
    }

    [Fact]
    public void ForwardOnlyOnlyOpensTheCurrentEntryOnce()
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Deflate, DataDescriptors = ZipDataDescriptorMode.Never });
        using var reader = new ZipReader(new NoSeekRead(src));
        ZipEntry first = reader.GetNextEntry()!;
        ZipEntry second = reader.GetNextEntry()!;
        Assert.Throws<ArgumentException>(() => reader.OpenRawEntryStream(first));
        using Stream s = reader.OpenRawEntryStream(second);
        Assert.Throws<InvalidOperationException>(() => reader.OpenRawEntryStream(second));
        Assert.Throws<InvalidOperationException>(() => reader.OpenEntryStream());
    }

    [Fact]
    public void ForeignEntriesAreRefused()
    {
        byte[] src = Build(new ZipWriterOptions());
        using var one = new ZipReader(new MemoryStream(src));
        using var two = new ZipReader(new MemoryStream(src));
        ZipEntry e = one.GetNextEntry()!;
        two.GetNextEntry();
        Assert.Throws<ArgumentException>(() => two.OpenRawEntryStream(e));
        Assert.Throws<ArgumentNullException>(() => two.OpenRawEntryStream(null!));
    }

    [Fact]
    public void RawLengthCountsAgainstMaxTotalSize()
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Store });
        using var reader = new ZipReader(new MemoryStream(src), new ZipReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 250_000 } });
        var entries = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            entries.Add(e);
        }

        ZipEntry text = entries.Single(x => x.Name == "text.txt");
        Drain(reader.OpenRawEntryStream(text));
        CompressionLimitException ex = Assert.Throws<CompressionLimitException>(() => reader.OpenRawEntryStream(text));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), ex.LimitName);
    }

    private static ZipRawEntry Header(long compressed, byte[]? name = null, ushort flags = 0, IReadOnlyList<ZipExtraField>? extra = null) => new()
    {
        RawName = name ?? "x.bin"u8.ToArray(),
        Method = ZipMethod.Store,
        Flags = flags,
        CompressedSize = compressed,
        UncompressedSize = compressed,
        Crc32 = 0x12345678,
        ExtraFields = extra ?? [],
    };

    private static (ushort Local, ushort Central) WrittenVersions(ZipRawEntry header, ZipWriterOptions? options = null)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, options ?? new ZipWriterOptions(), leaveOpen: true))
        {
            w.WriteRawEntry(header, new MemoryStream(new byte[header.CompressedSize]));
        }

        byte[] zip = ms.ToArray();
        int local = zip.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 3, 4 });
        int central = zip.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 1, 2 });
        Assert.Equal(0, local);
        Assert.True(central > 0);
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(local + 4)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(central + 6)));
    }

    private static ZipRawEntry Raw(ZipMethod method, ushort? version, ushort flags = 0, byte[]? name = null, long size = 2) => new()
    {
        RawName = name ?? "x.bin"u8.ToArray(),
        Method = method,
        Flags = flags,
        CompressedSize = size,
        UncompressedSize = size,
        VersionNeeded = version,
    };

    [Fact]
    public void UnsetVersionNeededFollowsTheMethod()
    {
        Assert.Equal(((ushort)10, (ushort)10), WrittenVersions(Raw(ZipMethod.Store, null)));
        Assert.Equal(((ushort)20, (ushort)20), WrittenVersions(Raw(ZipMethod.Deflate, null)));
        Assert.Equal(((ushort)20, (ushort)20), WrittenVersions(Raw(ZipMethod.Store, null, name: "dir/"u8.ToArray(), size: 0)));

        // An explicit value is kept, small ones included; descriptor and zip64 still raise it.
        Assert.Equal(((ushort)0, (ushort)0), WrittenVersions(Raw(ZipMethod.Store, 0)));
        Assert.Equal(((ushort)63, (ushort)63), WrittenVersions(Raw(ZipMethod.Store, 63)));
        Assert.Equal(((ushort)20, (ushort)20), WrittenVersions(Raw(ZipMethod.Deflate, 0, flags: 8)));
        Assert.Equal(((ushort)45, (ushort)45), WrittenVersions(Raw(ZipMethod.Store, 10), new ZipWriterOptions { Zip64 = ZipZip64Mode.Always }));
        Assert.Equal(((ushort)45, (ushort)45), WrittenVersions(Raw(ZipMethod.Store, null), new ZipWriterOptions { Zip64 = ZipZip64Mode.Always }));
    }

    [Fact]
    public void ShortAndLongDataAreRefused()
    {
        using var w1 = new ZipWriter(new MemoryStream(), leaveOpen: true);
        ArgumentException a = Assert.Throws<ArgumentException>(() => w1.WriteRawEntry(Header(10), new MemoryStream(new byte[9])));
        Assert.Equal("rawData", a.ParamName);
        Assert.Throws<InvalidOperationException>(() => w1.WriteRawEntry(Header(10), new MemoryStream(new byte[10])));

        using var w2 = new ZipWriter(new MemoryStream(), leaveOpen: true);
        a = Assert.Throws<ArgumentException>(() => w2.WriteRawEntry(Header(10), new MemoryStream(new byte[11])));
        Assert.Equal("rawData", a.ParamName);
    }

    [Fact]
    public void BadHeadersAreRefusedBeforeAnythingIsWritten()
    {
        var ms = new MemoryStream();
        using var w = new ZipWriter(ms, leaveOpen: true);
        Assert.Throws<ArgumentException>(() => w.WriteRawEntry(Header(0, []), Stream.Null));
        Assert.Throws<ArgumentException>(() => w.WriteRawEntry(Header(0, "a\0b"u8.ToArray()), Stream.Null));
        Assert.Throws<ArgumentException>(() => w.WriteRawEntry(Header(-1), Stream.Null));
        Assert.Equal(0, ms.Length);
        w.WriteRawEntry(Header(0), Stream.Null);
    }

    [Fact]
    public void Zip64NeverRefusesBigEntries()
    {
        using var w = new ZipWriter(new MemoryStream(), new ZipWriterOptions { Zip64 = ZipZip64Mode.Never }, leaveOpen: true);
        var big = new ZipRawEntry { RawName = "big"u8.ToArray(), CompressedSize = 5L << 30, UncompressedSize = 5L << 30 };
        Assert.Throws<ArgumentException>(() => w.WriteRawEntry(big, Stream.Null));
    }

    [Fact]
    public void StaleZip64AndAlignmentExtrasAreRewritten()
    {
        var extras = new List<ZipExtraField>
        {
            new(1, new byte[16]),
            new(0xD935, new byte[8]),
            new(0xCAFE, Array.Empty<byte>()),
            new(0x9901, new byte[] { 2, 0, (byte)'A', (byte)'E', 3, 8, 0 }),
        };
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, leaveOpen: true))
        {
            w.WriteRawEntry(Header(4, extra: extras), new MemoryStream([1, 2, 3, 4]));
        }

        using var reader = new ZipReader(new MemoryStream(ms.ToArray()));
        ZipEntry e = reader.GetNextEntry()!;
        Assert.False(e.UsesZip64);
        Assert.Equal([0xCAFE, 0x9901], e.ExtraFields.Select(f => (int)f.Id).ToArray());
        Assert.Equal([1, 2, 3, 4], Drain(reader.OpenRawEntryStream(e)));

        // Zip64 on request: exactly one zip64 field, in the local header and the directory alike.
        ms = new MemoryStream();
        using (var w = new ZipWriter(ms, new ZipWriterOptions { Zip64 = ZipZip64Mode.Always }, leaveOpen: true))
        {
            w.WriteRawEntry(Header(4, extra: extras), new MemoryStream([1, 2, 3, 4]));
        }

        byte[] zip = ms.ToArray();
        using var forward = new ZipReader(new NoSeekRead(zip));
        ZipEntry local = forward.GetNextEntry()!;
        Assert.Equal(1, local.ExtraFields.Count(f => f.Id == 1));
        Assert.Equal(4, local.CompressedSize);
        using var seekable = new ZipReader(new MemoryStream(zip));
        ZipEntry central = seekable.GetNextEntry()!;
        Assert.DoesNotContain(central.ExtraFields, f => f.Id == 1);
    }

    [Fact]
    public async Task AsyncWriteMatchesSync()
    {
        byte[] src = Build(new ZipWriterOptions { Method = ZipMethod.Zstd });
        byte[] sync = Copy(src, new ZipReaderOptions(), null, true);
        var entries = ReadAll(src, new ZipReaderOptions());
        var ms = new MemoryStream();
        await using (var w = new ZipWriter(ms, leaveOpen: true))
        {
            foreach (var x in entries)
            {
                await w.WriteRawEntryAsync(ZipRawEntry.FromEntry(x.Entry), new MemoryStream(x.Raw), TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(sync, ms.ToArray());
    }

    [Fact]
    public void DisposedReaderReleasesItsDirectory()
    {
        byte[] src = Build(new ZipWriterOptions());
        var reader = new ZipReader(new MemoryStream(src));
        reader.GetNextEntry();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.GetNextEntry());
    }
}
