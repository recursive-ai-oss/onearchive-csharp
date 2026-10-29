using System.Text;
using RecursiveAI.OneArchive.Iso;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Reads the images made by xorriso and genisoimage (testdata/iso/generate.py) and hand-built ones.</summary>
public class IsoReaderTests
{
    private static readonly string s_long = new string('x', 120) + ".txt";
    private static readonly string s_name254 = new string('n', 250) + ".txt";
    private static readonly string s_unicode = "\u00fcn\u00ef-\u65e5\u672c.txt";
    private const long Mtime = 1_700_000_000;

    private static readonly string s_deep = string.Join('/', Enumerable.Range(1, 9).Select(static i => "d" + i));

    // What testdata/iso/generate.py puts in the tree: name, kind, content, permission bits (0 = not checked), link target.
    private static readonly (string Name, ArchiveEntryKind Kind, byte[] Data, int Mode, string? Link)[] s_tree =
    [
        ("a.txt", ArchiveEntryKind.File, "hello\n"u8.ToArray(), 0x1A4, null),
        ("sub", ArchiveEntryKind.Directory, [], 0, null),
        ("sub/b.bin", ArchiveEntryKind.File, "xyz"u8.ToArray(), 0x1A4, null),
        ("big.bin", ArchiveEntryKind.File, ContainerVectors.Big(5000), 0x180, null),
        ("empty", ArchiveEntryKind.File, [], 0x1A4, null),
        ("exec.sh", ArchiveEntryKind.File, "#!/bin/sh\n"u8.ToArray(), 0x1ED, null),
        (s_unicode, ArchiveEntryKind.File, "unicode"u8.ToArray(), 0x1A4, null),
        ("sub/" + s_long, ArchiveEntryKind.File, "long path"u8.ToArray(), 0x1A4, null),
        (s_name254, ArchiveEntryKind.File, "name of 254 bytes"u8.ToArray(), 0x1A4, null),
        ("private", ArchiveEntryKind.Directory, [], 0x1C0, null),
        ("private/p.txt", ArchiveEntryKind.File, "secret"u8.ToArray(), 0x180, null),
        (s_deep, ArchiveEntryKind.Directory, [], 0, null),
        (s_deep + "/f.txt", ArchiveEntryKind.File, "deep"u8.ToArray(), 0x1A4, null),
        ("link", ArchiveEntryKind.Symlink, [], 0x1FF, "a.txt"),
        ("sub/up", ArchiveEntryKind.Symlink, [], 0x1FF, "../a.txt"),
        ("abs", ArchiveEntryKind.Symlink, [], 0x1FF, "/etc/passwd"),
        ("longlink", ArchiveEntryKind.Symlink, [], 0x1FF, string.Join('/', Enumerable.Range(0, 60).Select(static i => $"segment-{i:D3}")) + "/end"),
    ];

    private static IsoReader Open(string vector, IsoReaderOptions? options = null) => new(new MemoryStream(ContainerVectors.Read("iso", vector)), options);

    private static Dictionary<string, (IsoEntry Entry, byte[] Data)> ReadAll(IsoReader reader)
    {
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

    public static TheoryData<string> RockRidgeVectors => new() { "rockridge.iso", "rockridge-joliet.iso" };

    [Theory]
    [MemberData(nameof(RockRidgeVectors))]
    public void RockRidgeListsTheWholeTreeWithModesLinksAndTimes(string vector)
    {
        using IsoReader reader = Open(vector);
        Assert.True(reader.Volume.HasRockRidge);
        var all = ReadAll(reader);
        foreach ((string name, ArchiveEntryKind kind, byte[] data, int mode, string? link) in s_tree)
        {
            Assert.True(all.TryGetValue(name, out var found), "missing " + name);
            Assert.Equal(kind, found.Entry.Kind);
            Assert.Equal(IsoTree.Primary, found.Entry.Tree);
            Assert.Equal(data, found.Data);
            Assert.Equal(kind == ArchiveEntryKind.File ? data.Length : 0, found.Entry.Size);
            if (mode != 0)
            {
                Assert.Equal((UnixFileMode)mode, found.Entry.Mode);
            }

            Assert.Equal(link, found.Entry.LinkTarget);
            Assert.Equal(Mtime, found.Entry.ModifiedTime?.ToUnixTimeSeconds());
            Assert.NotNull(found.Entry.RockRidge);
        }

        Assert.Equal(s_tree.Length + 8, all.Count);   // plus d1 to d8
        Assert.Equal("D1/D2/D3/D4/D5/D6/D7/D8/D9/F.TXT", all[s_deep + "/f.txt"].Entry.IsoPath);
        Assert.Equal("A.TXT;1", all["a.txt"].Entry.Identifier);
    }

    [Fact]
    public void JolietAloneListsUcs2NamesCutShorterThanTheRockRidgeName()
    {
        using IsoReader reader = Open("joliet.iso");
        Assert.True(reader.Volume.HasJoliet);
        Assert.False(reader.Volume.HasRockRidge);
        var all = ReadAll(reader);
        Assert.All(all.Values, static v => Assert.Equal(IsoTree.Joliet, v.Entry.Tree));
        Assert.Equal("hello\n"u8.ToArray(), all["a.txt"].Data);
        Assert.Equal("unicode"u8.ToArray(), all[s_unicode].Data);
        Assert.Equal(ContainerVectors.Big(5000), all["big.bin"].Data);
        Assert.Equal("deep"u8.ToArray(), all[s_deep + "/f.txt"].Data);
        string[] longOnes = [.. all.Keys.Where(static k => k.StartsWith("sub/x", StringComparison.Ordinal))];
        Assert.Single(longOnes);
        Assert.True(longOnes[0].Length < "sub/".Length + s_long.Length, $"the Joliet name was not cut: {longOnes[0].Length}");
        Assert.Equal("long path"u8.ToArray(), all[longOnes[0]].Data);
        Assert.DoesNotContain(all.Values, static v => v.Entry.Kind == ArchiveEntryKind.Symlink);
        Assert.All(all.Values, static v => Assert.Null(v.Entry.RockRidge));
    }

    [Fact]
    public void JolietCanBeChosenOnAnImageWithRockRidge()
    {
        using IsoReader reader = Open("rockridge-joliet.iso", new IsoReaderOptions { Tree = IsoTree.Joliet });
        var all = ReadAll(reader);
        Assert.All(all.Values, static v => Assert.Equal(IsoTree.Joliet, v.Entry.Tree));
        Assert.Equal("hello\n"u8.ToArray(), all["a.txt"].Data);
        Assert.Null(all["a.txt"].Entry.Mode);
    }

    [Fact]
    public void RockRidgeCanBeTurnedOffToSeeTheIsoNames()
    {
        using IsoReader reader = Open("rockridge.iso", new IsoReaderOptions { UseRockRidge = false });
        var all = ReadAll(reader);
        Assert.Equal("hello\n"u8.ToArray(), all["A.TXT"].Data);
        Assert.Contains("SUB/B.BIN", all.Keys);
        Assert.All(all.Values, static v => Assert.Null(v.Entry.Mode));
    }

    [Theory]
    [InlineData("plain1.iso")]
    [InlineData("plain3.iso")]
    public void PlainIso9660UsesUpperCaseIdentifiersWithoutVersions(string vector)
    {
        using IsoReader reader = Open(vector);
        Assert.False(reader.Volume.HasRockRidge);
        Assert.False(reader.Volume.HasJoliet);
        Assert.Equal(vector.StartsWith("plain1", StringComparison.Ordinal) ? "PLAIN1" : "PLAIN3", reader.Volume.VolumeId);
        var all = ReadAll(reader);
        Assert.Equal("hello\n"u8.ToArray(), all["A.TXT"].Data);
        Assert.Equal("xyz"u8.ToArray(), all["SUB/B.BIN"].Data);
        Assert.Equal(ContainerVectors.Big(5000), all["BIG.BIN"].Data);
        Assert.Equal(ArchiveEntryKind.Directory, all["D1/D2/D3"].Entry.Kind);
        Assert.Equal("deep"u8.ToArray(), all["D1/D2/D3/D4/D5/D6/D7/D8/D9/F.TXT"].Data);
        Assert.Empty(all["EMPTY"].Data);
        Assert.All(all.Values, static v => Assert.Equal(IsoTree.Primary, v.Entry.Tree));
        Assert.All(all.Values, static v => Assert.False(v.Entry.Name.Contains(';', StringComparison.Ordinal)));
    }

    [Fact]
    public void RelocatedDirectoriesAppearWhereTheyBelongAndTheHoldingDirectoryIsHidden()
    {
        using IsoReader reader = Open("relocated.iso");
        var all = ReadAll(reader);
        string d10 = string.Join('/', Enumerable.Range(1, 10).Select(static i => "d" + i));
        string d8 = string.Join('/', Enumerable.Range(1, 8).Select(static i => "d" + i));
        Assert.Equal(ArchiveEntryKind.Directory, all[d10].Entry.Kind);
        Assert.Equal(ArchiveEntryKind.Directory, all[d8].Entry.Kind);   // a placeholder record (CL) on the image
        Assert.True(all[d8].Entry.RockRidge!.IsChildLink);
        Assert.Equal("deep"u8.ToArray(), all[d10 + "/f.txt"].Data);
        Assert.Equal("a.txt", all["link"].Entry.LinkTarget);
        Assert.DoesNotContain(all.Keys, static k => k.Contains("rr_moved", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(13, all.Count);
    }

    [Fact]
    public void OpeningEntriesOutOfOrderWorksAndStreamsSeek()
    {
        using IsoReader reader = Open("rockridge-joliet.iso");
        Assert.True(reader.SupportsRandomAccess);
        IsoEntry big = reader.Items.First(static e => e.Name == "big.bin");
        IsoEntry a = reader.Items.First(static e => e.Name == "a.txt");
        using Stream bs = reader.OpenEntryStream(big);
        using Stream s = reader.OpenEntryStream(a);
        Assert.Equal(6, s.Length);
        s.Position = 3;
        byte[] buf = new byte[10];
        Assert.Equal(3, s.Read(buf, 0, 10));
        Assert.Equal("lo\n"u8.ToArray(), buf[..3]);
        bs.Position = 4000;
        byte[] tail = new byte[2000];
        int n = 0;
        while (n < 1000)
        {
            int r = bs.Read(tail, n, 2000 - n);
            Assert.True(r > 0);
            n += r;
        }

        Assert.Equal(ContainerVectors.Big(5000)[4000..], tail[..n]);
        Assert.Throws<ArgumentException>(() => reader.OpenEntryStream(new ArchiveEntry { Name = "x" }));
    }

    [Fact]
    public void VolumeInformationComesFromThePrimaryDescriptor()
    {
        using IsoReader reader = Open("rockridge-joliet.iso");
        IsoVolumeInfo v = reader.Volume;
        Assert.Equal("RRJOLIET", v.VolumeId);
        Assert.True(v.HasJoliet);
        Assert.Equal(3, v.JolietLevel);
        Assert.True(v.VolumeSpaceBlocks > 16);
        Assert.Null(v.BootCatalogBlock);
    }

    [Fact]
    public void EntryLocationsPointAtTheData()
    {
        byte[] image = ContainerVectors.Read("iso", "rockridge-joliet.iso");
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry a = reader.Items.First(static e => e.Name == "a.txt");
        Assert.Single(a.Extents);
        Assert.Equal(6, a.Extents[0].Length);
        Assert.Equal("hello\n", Encoding.ASCII.GetString(image, (int)(a.ExtentLocation * 2048), 6));
        Assert.True(a.RecordOffset > 0);
        Assert.Equal(Mtime, a.RecordingTime?.ToUnixTimeSeconds());
        Assert.Equal(0x81A4u, a.RockRidge!.RawMode);
    }

    [Fact]
    public void TheHappyPathDetectsIsoFromTheBytesAndExtractsIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onearchive-iso-" + Guid.NewGuid().ToString("N"));
        try
        {
            // No file name, and a source that cannot seek: only the bytes at 32769 identify the format.
            var forward = new ForwardOnly(ContainerVectors.Read("iso", "rockridge-joliet.iso"));
            using Archive archive = Archive.Open(forward);
            Assert.Equal(ArchiveFormat.Iso, archive.Format);
            var result = archive.Extract(dir, new ArchiveExtractOptions { Filter = static e => e.Kind != ArchiveEntryKind.Symlink || e.Name == "link" });
            Assert.True(result.EntriesExtracted > 10);
            Assert.Equal("hello\n", File.ReadAllText(Path.Combine(dir, "a.txt")));
            Assert.Equal(ContainerVectors.Big(5000), File.ReadAllBytes(Path.Combine(dir, "big.bin")));
            Assert.Equal("deep", File.ReadAllText(Path.Combine(dir, s_deep, "f.txt")));
            Assert.Equal("unicode", File.ReadAllText(Path.Combine(dir, s_unicode)));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal((UnixFileMode)0x180, File.GetUnixFileMode(Path.Combine(dir, "big.bin")));
                Assert.Equal(Mtime, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(dir, "a.txt")), TimeSpan.Zero).ToUnixTimeSeconds());
            }
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void ExtractionRefusesALinkThatEscapesUnlessAllowed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "onearchive-iso-" + Guid.NewGuid().ToString("N"));
        try
        {
            using Archive archive = Archive.Open(new MemoryStream(ContainerVectors.Read("iso", "rockridge.iso")));
            var ex = Assert.Throws<ArchiveSafetyException>(() => archive.Extract(dir));
            Assert.Equal("link-escape", ex.Rule);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void DetectionNeedsTheExtendedPrefix()
    {
        byte[] image = ContainerVectors.Read("iso", "plain1.iso");
        Assert.Equal(ArchiveFormat.Unknown, ArchiveDetector.DetectFormat(image.AsSpan(0, ArchiveDetector.PrefixLength)));
        Assert.Equal(ArchiveFormat.Iso, ArchiveDetector.DetectFormat(image.AsSpan(0, ArchiveDetector.ExtendedPrefixLength)));
        Assert.Equal(ArchiveFormat.Iso, ArchiveDetector.FromFileName("disc.ISO").Format);
    }

    // ---- hand-built images ----

    [Fact]
    public void MultiExtentFilesAreJoined()
    {
        byte[] s1 = RawIso.Sector("AAAA"u8.ToArray());
        byte[] s2 = RawIso.Sector("BBBB"u8.ToArray());
        byte[] s3 = RawIso.Sector("CCCC"u8.ToArray());
        byte[] image = RawIso.Image(
            [RawIso.Rec("F.;1", 19, 2048, 0x80), RawIso.Rec("F.;1", 20, 2048, 0x80), RawIso.Rec("F.;1", 21, 4, 0)], true, s1, s2, s3);
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry e = Assert.Single(reader.Items);
        Assert.Equal("F", e.Name);
        Assert.Equal(4100, e.Size);
        Assert.Equal(3, e.Extents.Count);
        using Stream s = reader.OpenEntryStream(e);
        byte[] data = new byte[4100];
        s.ReadExactly(data);
        Assert.Equal("AAAA", Encoding.ASCII.GetString(data, 0, 4));
        Assert.Equal("BBBB", Encoding.ASCII.GetString(data, 2048, 4));
        Assert.Equal("CCCC", Encoding.ASCII.GetString(data, 4096, 4));
        s.Position = 2046;
        byte[] across = new byte[4];
        Assert.Equal(2, s.Read(across, 0, 4));
        Assert.Equal(2, s.Read(across, 2, 2));
        Assert.Equal(new byte[] { 0, 0, (byte)'B', (byte)'B' }, across);
    }

    [Fact]
    public void FilesOverFourGibAreSeenThroughSeveralExtents()
    {
        // 5.5 GiB in three extents, the image itself mostly a hole: only the descriptors and the directory are real bytes.
        const long Chunk = 0xFFFFF800L;
        long total = Chunk + Chunk + (1L << 30);
        long sectors = 19 + ((total + 2047) / 2048);
        byte[] head = RawIso.Image(
            [RawIso.Rec("BIG.;1", 19, Chunk, 0x80), RawIso.Rec("BIG.;1", 19 + (Chunk / 2048), Chunk, 0x80), RawIso.Rec("BIG.;1", 19 + (2 * (Chunk / 2048)), 1L << 30, 0)]);
        using var reader = new IsoReader(new SparseTailStream(head, sectors * 2048), new IsoReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 8L << 30 } });
        IsoEntry e = Assert.Single(reader.Items);
        Assert.Equal(total, e.Size);
        Assert.Equal(3, e.Extents.Count);
        using Stream s = reader.OpenEntryStream(e);
        Assert.Equal(total, s.Length);
        s.Seek(Chunk - 3, SeekOrigin.Begin);
        byte[] buf = new byte[8];
        int n = 0;
        while (n < 8)
        {
            int r = s.Read(buf, n, 8 - n);
            Assert.True(r > 0);
            n += r;
        }

        Assert.All(buf, static b => Assert.Equal(0, b));
        s.Seek(-4, SeekOrigin.End);
        Assert.Equal(4, s.Read(buf, 0, 8));
        Assert.Equal(0, s.Read(buf, 0, 8));
    }

    [Fact]
    public void RockRidgeSymlinkComponentsAreJoinedAcrossEntries()
    {
        byte[] sl1 = RawIso.Su("SL", [1, 0x08, 0, 0x00, 3, (byte)'u', (byte)'s', (byte)'r', 0x01, 2, (byte)'l', (byte)'i']);
        byte[] sl2 = RawIso.Su("SL", [0, 0x00, 1, (byte)'b', 0x04, 0, 0x02, 0]);
        byte[] sys = RawIso.Concat(RawIso.Px(0xA1FF), RawIso.Nm("lnk"), sl1, sl2);
        byte[] image = RawIso.Image([RawIso.Rec("LNK.;1", 0, 0, 0, sys)]);
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry e = Assert.Single(reader.Items);
        Assert.Equal(ArchiveEntryKind.Symlink, e.Kind);
        Assert.Equal("/usr/lib/../.", e.LinkTarget);
        Assert.Equal("lnk", e.Name);
    }

    [Fact]
    public void ThirtySixByteAndLongFormRockRidgeEntriesAreRead()
    {
        byte[] px36 = new byte[32];
        RawIso.Both32(px36, 0, 0x81ED);
        RawIso.Both32(px36, 8, 1);
        RawIso.Both32(px36, 16, 1234);
        RawIso.Both32(px36, 24, 5678);
        byte[] tf = new byte[1 + 17];
        tf[0] = 0x02 | 0x80;
        Encoding.ASCII.GetBytes("2021030405060700").CopyTo(tf, 1);
        byte[] sys = RawIso.Concat(RawIso.Su("PX", px36), RawIso.Su("TF", tf), RawIso.Nm("x.sh"));
        byte[] image = RawIso.Image([RawIso.Rec("X.SH;1", 0, 0, 0, sys)]);
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry e = Assert.Single(reader.Items);
        Assert.Equal((UnixFileMode)0x1ED, e.Mode);
        Assert.Equal(1234, e.Uid);
        Assert.Equal(5678, e.Gid);
        Assert.Equal(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero), e.ModifiedTime);
        Assert.Equal(0, e.RockRidge!.SerialNumber);
    }

    [Fact]
    public void DeviceNumbersAndFifosCarryTheirKind()
    {
        byte[] pn = new byte[16];
        RawIso.Both32(pn, 0, 0);
        RawIso.Both32(pn, 8, (4 << 8) | 7);
        byte[] image = RawIso.Image(
        [
            RawIso.Rec("TTY.;1", 0, 0, 0, RawIso.Concat(RawIso.Px(0x21B6), RawIso.Nm("tty"), RawIso.Su("PN", pn))),
            RawIso.Rec("FIFO.;1", 0, 0, 0, RawIso.Concat(RawIso.Px(0x11A4), RawIso.Nm("fifo"))),
        ]);
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry tty = reader.Items.Single(static e => e.Name == "tty");
        Assert.Equal(ArchiveEntryKind.CharacterDevice, tty.Kind);
        Assert.Equal(4, tty.DeviceMajor);
        Assert.Equal(7, tty.DeviceMinor);
        Assert.Equal(ArchiveEntryKind.Fifo, reader.Items.Single(static e => e.Name == "fifo").Kind);
    }

    [Fact]
    public void ZisofsFilesAreListedAndOpeningThemIsUnsupported()
    {
        byte[] zf = RawIso.Su("ZF", [(byte)'p', (byte)'z', 4, 15, 0, 0, 0, 0, 0x10, 0, 0, 0, 0, 0, 0, 0x10]);
        byte[] image = RawIso.Image([RawIso.Rec("Z.;1", 19, 20, 0, RawIso.Concat(RawIso.Px(0x81A4), RawIso.Nm("z.txt"), zf))], true, RawIso.Sector([1, 2, 3]));
        using var reader = new IsoReader(new MemoryStream(image));
        IsoEntry e = Assert.Single(reader.Items);
        Assert.True(e.IsZisofs);
        Assert.Equal(20, e.Size);
        Assert.Throws<ArchiveUnsupportedException>(() => reader.OpenEntryStream(e));
    }

    private sealed class ForwardOnly(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 4000));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
