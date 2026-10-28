using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cab;

/// <summary>Hostile and unsafe cabinets: damaged headers and blocks, limits, and the extraction path rules.</summary>
public class CabHostileTests
{
    private static byte[] Small(CabTestBuilder.Method method, int window = 16)
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = method, WindowBits = window };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "a.txt", Data = TestData.Mixed(40_000, 1, true) });
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "dir/b.bin", Data = TestData.Mixed(300, 2) });
        b.Folders.Add(f);
        return b.Build();
    }

    private static byte[] Named(string name)
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.Mszip };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = name, Data = "evil"u8.ToArray() });
        b.Folders.Add(f);
        return b.Build();
    }

    // Reads everything the reader offers; the only exceptions allowed out are the library's typed ones.
    private static void Drain(byte[] cab, bool verify = true)
    {
        try
        {
            using var r = new CabReader(new MemoryStream(cab), new CabReaderOptions { VerifyChecksum = verify });
            while (r.GetNextEntry() is not null)
            {
                try
                {
                    using Stream s = r.OpenEntryStream();
                    s.CopyTo(Stream.Null);
                }
                catch (ArchiveUnsupportedException)
                {
                }
            }
        }
        catch (Exception e) when (e is CompressionFormatException or ArchiveUnsupportedException or CompressionLimitException)
        {
        }
    }

    [Theory]
    [InlineData(CabTestBuilder.Method.None)]
    [InlineData(CabTestBuilder.Method.Mszip)]
    [InlineData(CabTestBuilder.Method.Lzx)]
    public void TruncationAtEveryOffsetIsATypedError(CabTestBuilder.Method method)
    {
        byte[] cab = Small(method);
        for (int length = 0; length < cab.Length; length += length < 400 ? 1 : 37)
        {
            Drain(cab.AsSpan(0, length).ToArray());
        }
    }

    [Theory]
    [InlineData(CabTestBuilder.Method.None)]
    [InlineData(CabTestBuilder.Method.Mszip)]
    [InlineData(CabTestBuilder.Method.Lzx)]
    public void DamagedBytesAreTypedErrorsWithAndWithoutChecksums(CabTestBuilder.Method method)
    {
        byte[] cab = Small(method);
        var rng = new Random(5);
        for (int i = 0; i < 1500; i++)
        {
            byte[] copy = (byte[])cab.Clone();
            int at = i < 200 ? i : rng.Next(copy.Length);
            copy[at] ^= (byte)(1 << rng.Next(8));
            Drain(copy, verify: i % 2 == 0);
        }
    }

    [Fact]
    public void BadSignatureVersionAndReserveAreFormatErrors()
    {
        byte[] cab = Small(CabTestBuilder.Method.None);

        byte[] bad = (byte[])cab.Clone();
        bad[0] = (byte)'X';
        var ex = Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());
        Assert.Equal("cab", ex.Format);
        Assert.Equal(0, ex.Offset);

        bad = (byte[])cab.Clone();
        bad[25] = 2; // versionMajor
        ex = Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Header.ToString());
        Assert.Equal(25, ex.Offset);

        var b = new CabTestBuilder { ReserveFlag = true };
        b.Folders.Add(new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None });
        byte[] reserve = b.Build();
        reserve[36] = 0x61;
        reserve[37] = 0xEA; // 60001 bytes of header reserve
        ex = Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(reserve)).Header.ToString());
        Assert.Contains("header reserve", ex.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void TablesThatPointOutsideTheCabinetAreFormatErrors()
    {
        byte[] cab = Small(CabTestBuilder.Method.None);

        // coffFiles past the end.
        byte[] bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(16), (uint)cab.Length + 10);
        Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Header.ToString());

        // A file count that the file table cannot hold.
        bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(28), (ushort)60000);
        Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());

        // A folder whose data starts beyond the end.
        bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(36), (uint)cab.Length + 5);
        Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());

        // A file that claims more bytes than its folder's blocks can hold.
        using var probe = new CabReader(new MemoryStream(cab));
        long fileTable = probe.Header.FilesOffset;
        bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan((int)fileTable), 0x7FFF0000u);
        var ex = Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());
        Assert.Equal(fileTable, ex.Offset);

        // A folder index past the folder count.
        bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan((int)fileTable + 8), (ushort)9);
        Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());

        // A name without a terminator.
        bad = (byte[])cab.Clone();
        for (int i = (int)fileTable + 16; i < (int)fileTable + 16 + 300 && i < bad.Length; i++)
        {
            bad[i] = (byte)'a';
        }

        Assert.Throws<CompressionFormatException>(() => new CabReader(new MemoryStream(bad)).Items.ToString());
    }

    [Fact]
    public void BlockHeadersThatLieAreFormatErrors()
    {
        byte[] cab = Small(CabTestBuilder.Method.Mszip);
        using var probe = new CabReader(new MemoryStream(cab));
        int block = (int)probe.Folders[0].DataOffset;

        // cbUncomp over 32768.
        byte[] bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(block + 6), (ushort)40000);
        using (var r = new CabReader(new MemoryStream(bad), new CabReaderOptions { VerifyChecksum = false }))
        {
            var ex = Assert.Throws<CompressionFormatException>(() => CabReaderTests.ReadAll(r.OpenEntryStream(r.Items[0])));
            Assert.Equal(block + 6, ex.Offset);
        }

        // cbData past the end of the file.
        bad = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(bad.AsSpan(block + 4), ushort.MaxValue);
        using (var r = new CabReader(new MemoryStream(bad), new CabReaderOptions { VerifyChecksum = false }))
        {
            var ex = Assert.Throws<CompressionFormatException>(() => CabReaderTests.ReadAll(r.OpenEntryStream(r.Items[0])));
            Assert.True(ex.IsTruncated);
        }

        // The MSZIP signature.
        bad = (byte[])cab.Clone();
        bad[block + 8] = (byte)'X';
        using (var r = new CabReader(new MemoryStream(bad), new CabReaderOptions { VerifyChecksum = false }))
        {
            var ex = Assert.Throws<CompressionFormatException>(() => CabReaderTests.ReadAll(r.OpenEntryStream(r.Items[0])));
            Assert.Equal(block + 8, ex.Offset);
            Assert.Contains("CK", ex.Expected, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ADeclaredSizeOverTheTotalLimitIsALimitError()
    {
        byte[] cab = Small(CabTestBuilder.Method.None);
        using var probe = new CabReader(new MemoryStream(cab));
        long fileTable = probe.Header.FilesOffset;
        byte[] big = (byte[])cab.Clone();
        BitConverter.TryWriteBytes(big.AsSpan((int)fileTable), 0x7FFF0000u);
        // Make the file fit its folder by declaring enough blocks, so the size limit (not the table check) is what refuses it.
        BitConverter.TryWriteBytes(big.AsSpan(40), (ushort)ushort.MaxValue);
        using var r = new CabReader(new MemoryStream(big), new CabReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 1 << 20 } });
        var ex = Assert.Throws<CompressionLimitException>(() => r.GetNextEntry());
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), ex.LimitName);
    }

    [Fact]
    public void AHighlyCompressedFolderStopsAtTheExpansionRatio()
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.Mszip };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "zeros", Data = new byte[60_000_000] });
        b.Folders.Add(f);
        byte[] cab = b.Build();
        Assert.True(cab.Length < 300_000);
        using var r = new CabReader(new MemoryStream(cab), new CabReaderOptions { Limits = new ArchiveLimits { MaxExpansionRatio = 100 } });
        r.GetNextEntry();
        var ex = Assert.Throws<CompressionLimitException>(() => r.OpenEntryStream().CopyTo(Stream.Null));
        Assert.Equal(nameof(ArchiveLimits.MaxExpansionRatio), ex.LimitName);

        using var open = new CabReader(new MemoryStream(cab), new CabReaderOptions { Limits = new ArchiveLimits { MaxExpansionRatio = 0 } });
        open.GetNextEntry();
        open.OpenEntryStream().CopyTo(Stream.Null);
    }

    [Fact]
    public void AnLzxWindowOverTheLimitIsALimitError()
    {
        byte[] cab = Small(CabTestBuilder.Method.Lzx, 21);
        using var r = new CabReader(new MemoryStream(cab), new CabReaderOptions { Limits = new ArchiveLimits { MaxWindowSize = 1 << 20 } });
        r.GetNextEntry();
        var ex = Assert.Throws<CompressionLimitException>(() => r.OpenEntryStream());
        Assert.Equal(nameof(ArchiveLimits.MaxWindowSize), ex.LimitName);
    }

    [Fact]
    public void LzxWindowsOutsideTheCabinetRangeAreUnsupported()
    {
        var b = new CabTestBuilder();
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None, RawType = (ushort)(3 | (25 << 8)) };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "a", Data = [1] });
        b.Folders.Add(f);
        Assert.Throws<ArchiveUnsupportedException>(() => new CabReader(new MemoryStream(b.Build())).Items.ToString());
    }

    [Theory]
    [InlineData("../evil.txt", "path-traversal")]
    [InlineData("a\\..\\..\\evil.txt", "path-traversal")]
    [InlineData("..\\..\\evil.txt", "path-traversal")]
    [InlineData("/etc/evil.txt", "absolute-path")]
    [InlineData("\\\\server\\share\\evil.txt", "absolute-path")]
    [InlineData("C:\\evil.txt", "absolute-path")]
    [InlineData("c:evil.txt", "absolute-path")]
    public void ExtractionRefusesUnsafeNames(string name, string rule)
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        Directory.CreateDirectory(target);
        var ex = Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(Named(name)), target));
        Assert.Equal(rule, ex.Rule);
        Assert.Equal("cab", ex.Format);
        Assert.False(File.Exists(t.Combine("evil.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact]
    public void SanitizeKeepsEverythingInside()
    {
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(Named("..\\..\\up\\evil.txt")), t.Combine("out"), new ArchiveExtractOptions { UnsafePaths = UnsafePathHandling.Sanitize });
        Assert.False(File.Exists(t.Combine("evil.txt")));
        Assert.Single(Directory.GetFiles(t.Combine("out"), "evil.txt", SearchOption.AllDirectories));
    }

    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ArchiveDetectsAndExtractsCabinetsFromAnyStream()
    {
        byte[] cab = Small(CabTestBuilder.Method.Lzx);
        foreach (bool seekable in new[] { true, false })
        {
            using var t = new TempDir();
            Stream source = seekable ? new MemoryStream(cab) : new ForwardOnly(new MemoryStream(cab));
            Archive.Extract(source, t.Combine("out"));
            Assert.Equal(TestData.Mixed(40_000, 1, true), File.ReadAllBytes(t.Combine("out/a.txt")));
            Assert.Equal(TestData.Mixed(300, 2), File.ReadAllBytes(t.Combine("out/dir/b.bin")));
        }

        using Archive a = Archive.Open(new MemoryStream(cab));
        Assert.Equal(ArchiveFormat.Cab, a.Format);
        Assert.Equal(2, a.Entries.Count);
        Assert.Equal((ArchiveFormat.Cab, ArchiveCompression.None), ArchiveDetector.FromFileName("setup.CAB"));
    }

    [Fact]
    public void SpanningCabinetsFailExtractionWithATypedError()
    {
        var b = new CabTestBuilder { NextCabinet = "n.cab", NextDisk = "n" };
        var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.None };
        f.Files.Add(new CabTestBuilder.FileSpec { Name = "tail", Data = [1, 2], RawFolderIndex = 0xFFFE });
        b.Folders.Add(f);
        using var t = new TempDir();
        Assert.Throws<ArchiveUnsupportedException>(() => Archive.Extract(new MemoryStream(b.Build()), t.Combine("out")));
    }
}
