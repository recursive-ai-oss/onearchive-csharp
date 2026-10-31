using System.Diagnostics;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Recovery mode of <see cref="ZipReader"/>: damaged archives made from our own writer, a hand-built writer and Info-ZIP.</summary>
public class ZipRecoveryTests
{
    private sealed record Got(string Name, byte[]? Data, string? Error);

    private static ZipReaderOptions Recover(ArchiveLimits? limits = null, long? maxBytes = null) =>
        new() { Recovery = true, Limits = limits ?? ArchiveLimits.Default, MaxRecoveryBytes = maxBytes ?? (1L << 30) };

    private static (List<Got> Entries, List<ZipDamage> Damage) Run(byte[] zip, bool seekable, ZipReaderOptions? options = null)
    {
        Stream s = seekable ? new MemoryStream(zip) : new ForwardOnly(new MemoryStream(zip), 997);
        using var reader = new ZipReader(s, options ?? Recover());
        var got = new List<Got>();
        while (reader.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            try
            {
                using Stream c = reader.OpenEntryStream();
                var ms = new MemoryStream();
                c.CopyTo(ms);
                got.Add(new Got(e.Name, ms.ToArray(), null));
            }
            catch (CompressionFormatException ex)
            {
                got.Add(new Got(e.Name, null, ex.Message));
            }
        }

        return (got, [.. reader.Damage]);
    }

    private static readonly byte[] s_endSig = [0x50, 0x4B, 5, 6], s_centralSig = [0x50, 0x4B, 1, 2];

    private static readonly byte[] s_a = ZipTests.Text(5000, 1), s_b = ZipTests.Text(7000, 2), s_c = ZipTests.Text(3000, 3);

    private static RawZip.Entry Make(string name, byte[] data, bool deflate, bool descriptor = false)
    {
        RawZip.Entry e = deflate ? RawZip.Deflated(name, data) : RawZip.Stored(name, data);
        e.Descriptor = descriptor;
        return e;
    }

    private static (byte[] Zip, RawZip.Entry A, RawZip.Entry B, RawZip.Entry C, long DirectoryStart) Build(bool deflate = false, bool descriptor = false)
    {
        var raw = new RawZip();
        RawZip.Entry a = Make("a.txt", s_a, deflate, descriptor), b = Make("b.txt", s_b, deflate, descriptor), c = Make("c.txt", s_c, deflate, descriptor);
        raw.Add(a).Add(b).Add(c);
        long dir = raw.Position;
        return (raw.Finish(), a, b, c, dir);
    }

    private static byte[] Splice(byte[] zip, long at, byte[] insert) => [.. zip.AsSpan(0, (int)at), .. insert, .. zip.AsSpan((int)at)];

    private static void AssertEntries(List<Got> got, params (string Name, byte[]? Data)[] expected)
    {
        Assert.Equal(expected.Select(x => x.Name), got.Select(g => g.Name));
        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i].Data is { } data)
            {
                Assert.True(got[i].Error is null, $"{got[i].Name}: {got[i].Error}");
                Assert.Equal(data, got[i].Data);
            }
            else
            {
                Assert.NotNull(got[i].Error);
            }
        }
    }

    public static TheoryData<bool, bool> Modes => new() { { true, false }, { false, false }, { true, true }, { false, true } };

    [Theory]
    [MemberData(nameof(Modes))]
    public void CleanArchiveReportsNothing(bool seekable, bool deflate)
    {
        var (zip, _, _, _, _) = Build(deflate);
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        Assert.Empty(damage);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GarbageBetweenEntriesIsSkippedAndReported(bool seekable, bool deflate)
    {
        var (zip, a, b, _, _) = Build(deflate);
        byte[] garbage = new byte[300];
        new Random(7).NextBytes(garbage);
        garbage[10] = 0x50;
        garbage[11] = 0x4B;
        garbage[12] = 3;
        garbage[13] = 4; // a signature that is not a header
        zip = Splice(zip, b.LocalOffset, garbage);
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(b.LocalOffset, skipped.Offset);
        Assert.Equal(300, skipped.Length);
        Assert.Equal(seekable, damage.Any(d => d.Kind == ZipDamageKind.DirectoryUnusable));
        _ = a;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void GarbageBeforeTheFirstEntryIsSkipped(bool seekable, bool deflate)
    {
        var (zip, _, _, _, _) = Build(deflate);
        zip = Splice(zip, 0, "#!/bin/sh\nexit 0\n"u8.ToArray());
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        if (seekable)
        {
            Assert.Empty(damage); // the directory accounts for data in front of the archive
            return;
        }

        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(0, skipped.Offset);
        Assert.Equal(17, skipped.Length);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ACorruptedLocalHeaderLosesOnlyThatEntry(bool seekable, bool deflate)
    {
        var (zip, a, b, c, _) = Build(deflate);
        zip[b.LocalOffset + 26] = 0; // a name length of zero: the header cannot be used
        zip[b.LocalOffset + 27] = 0;
        var (got, damage) = Run(zip, seekable);
        if (seekable)
        {
            // The directory is intact and is what a seekable read trusts; the entry whose local header disagrees with it is reported.
            AssertEntries(got, ("a.txt", s_a), ("b.txt", null), ("c.txt", s_c));
            Assert.Equal(b.LocalOffset, Assert.Single(damage).Offset);
            return;
        }

        AssertEntries(got, ("a.txt", s_a), ("c.txt", s_c));
        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(b.LocalOffset, skipped.Offset);
        Assert.Equal(c.LocalOffset - b.LocalOffset, skipped.Length);
        _ = a;
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ACorruptedSignatureLosesOnlyThatEntry(bool seekable, bool deflate)
    {
        var (zip, _, b, c, _) = Build(deflate);
        zip[b.LocalOffset + 2] = 9;
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("a.txt", s_a), ("c.txt", s_c));
        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(b.LocalOffset, skipped.Offset);
        Assert.Equal(c.LocalOffset - b.LocalOffset, skipped.Length);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TruncatedLastEntryIsReportedNotTruncatedSilently(bool seekable, bool deflate)
    {
        var (zip, _, _, c, _) = Build(deflate);
        byte[] cut = zip.AsSpan(0, (int)c.LocalOffset + 30 + 5 + 100).ToArray();
        var (got, damage) = Run(cut, seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", null));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.Truncated && d.EntryName == "c.txt" && d.Offset == c.LocalOffset);
        Assert.Equal(seekable, damage.Any(d => d.Kind == ZipDamageKind.DirectoryUnusable));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void MissingCentralDirectoryKeepsEveryEntry(bool seekable, bool deflate)
    {
        var (zip, _, _, _, dir) = Build(deflate);
        var (got, damage) = Run(zip.AsSpan(0, (int)dir).ToArray(), seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.Truncated && d.Offset == dir);
        Assert.Equal(seekable, damage.Any(d => d.Kind == ZipDamageKind.DirectoryUnusable));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptEndRecordFallsBackToLocalHeaders(bool deflate)
    {
        var (zip, _, _, _, _) = Build(deflate);
        int end = zip.AsSpan().LastIndexOf(s_endSig);
        zip[end + 2] = 0;
        var (got, damage) = Run(zip, seekable: true);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.DirectoryUnusable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptCentralDirectoryOffsetFallsBackToLocalHeaders(bool deflate)
    {
        var (zip, _, _, _, _) = Build(deflate);
        int end = zip.AsSpan().LastIndexOf(s_endSig);
        zip[end + 16] ^= 0x55; // the directory offset
        var (got, damage) = Run(zip, seekable: true);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.DirectoryUnusable);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ADamagedEntryIsReportedAndTheNextOneIsRead(bool seekable, bool deflate)
    {
        var (zip, _, b, c, _) = Build(deflate);
        zip[(b.LocalOffset + 30 + 5 + 50)] ^= 0xFF;
        var (got, damage) = Run(zip, seekable);
        // Stored data keeps its length, so only the CRC fails; damaged deflate data may fail anywhere.
        AssertEntries(got, ("a.txt", s_a), ("b.txt", null), ("c.txt", s_c));
        ZipDamage d = Assert.Single(damage, x => x.EntryName == "b.txt");
        Assert.Equal(b.LocalOffset, d.Offset);
        _ = c;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataDescriptorEntriesRecoverAroundGarbageAndDamage(bool seekable)
    {
        // Deflated: a stored entry whose size is only in a descriptor cannot be read forward at all.
        var (zip, _, b, c, _) = Build(deflate: true, descriptor: true);
        zip[b.LocalOffset + 30 + 5 + 40] ^= 0xFF;
        zip = Splice(zip, c.LocalOffset, new byte[64]);
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("a.txt", s_a), ("b.txt", null), ("c.txt", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Contains(damage, d => d.EntryName == "b.txt");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WriterOutputWithZip64AndDescriptorsRecovers(bool seekable)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(new WriteOnly(ms), new ZipWriterOptions { Zip64 = ZipZip64Mode.Always, DataDescriptors = ZipDataDescriptorMode.Always }, leaveOpen: true))
        {
            w.WriteEntry(new ArchiveEntry { Name = "one.bin", Size = s_a.Length }, new MemoryStream(s_a));
            w.WriteEntry(new ArchiveEntry { Name = "two.bin", Size = s_b.Length }, new MemoryStream(s_b));
            w.WriteEntry(new ArchiveEntry { Name = "three.bin", Size = s_c.Length }, new MemoryStream(s_c));
        }

        byte[] zip = ms.ToArray();
        Assert.Empty(Run(zip, seekable).Damage);
        int second = zip.AsSpan().IndexOf("two.bin"u8) - 30;
        byte[] damaged = Splice(zip, second, "not a zip at all"u8.ToArray());
        var (got, damage) = Run(damaged, seekable);
        AssertEntries(got, ("one.bin", s_a), ("two.bin", s_b), ("three.bin", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.SkippedBytes && d.Offset == second && d.Length == 16);
    }

    [Fact]
    public void ImplausibleHeadersInsideLostDataAreNotEntries()
    {
        // The first entry's own header is damaged, so the scan walks through its data, which holds header-like bytes
        // (an unknown method, then a name with a control character). Only the real second entry is found.
        byte[] fake = new byte[200];
        "PK\u0003\u0004"u8.CopyTo(fake);
        fake[4] = 20;
        fake[8] = 0x77;
        fake[26] = 5;
        "PK\u0003\u0004"u8.CopyTo(fake.AsSpan(100));
        fake[104] = 20;
        fake[126] = 4;
        fake[130] = 7;
        var raw = new RawZip();
        RawZip.Entry x = Make("x.bin", fake, false);
        raw.Add(x).Add(Make("y.txt", s_c, false));
        byte[] zip = raw.Finish();
        zip = zip.AsSpan(0, zip.AsSpan().IndexOf(s_centralSig)).ToArray();
        zip[26] = 0;
        zip[27] = 0;
        var (got, damage) = Run(zip, seekable: false);
        AssertEntries(got, ("y.txt", s_c));
        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(0, skipped.Offset);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignatureDenseGarbageIsLinearAndReportedOnce(bool seekable)
    {
        var (zip, _, b, _, _) = Build(false);
        byte[] dense = new byte[8 << 20];
        for (int i = 0; i < dense.Length; i += 4)
        {
            dense[i] = 0x50;
            dense[i + 1] = 0x4B;
            dense[i + 2] = 3;
            dense[i + 3] = 4;
        }

        zip = Splice(zip, b.LocalOffset, dense);
        var sw = Stopwatch.StartNew();
        var (got, damage) = Run(zip, seekable);
        sw.Stop();
        AssertEntries(got, ("a.txt", s_a), ("b.txt", s_b), ("c.txt", s_c));
        ZipDamage skipped = Assert.Single(damage, d => d.Kind == ZipDamageKind.SkippedBytes);
        Assert.Equal(dense.Length, skipped.Length);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), sw.Elapsed.ToString());
    }

    [Fact]
    public void ManyPlausibleHeadersHitTheEntryLimit()
    {
        // 30-byte header of an empty stored file named "a", repeated: each one is a real entry.
        byte[] header = new byte[31];
        "PK\u0003\u0004"u8.CopyTo(header);
        header[4] = 20;
        header[12] = 0x21;
        header[26] = 1;
        header[30] = (byte)'a';
        byte[] data = [.. Enumerable.Repeat(header, 5000).SelectMany(h => h)];
        var limits = new ArchiveLimits { MaxEntryCount = 100 };
        Assert.Throws<CompressionLimitException>(() => Run(data, seekable: false, Recover(limits)));
    }

    [Fact]
    public void RecoveryBytesLimitIsTyped()
    {
        var (zip, _, b, _, _) = Build(false);
        zip = Splice(zip, b.LocalOffset, new byte[10_000]);
        CompressionLimitException ex = Assert.Throws<CompressionLimitException>(() => Run(zip, seekable: false, Recover(maxBytes: 1000)));
        Assert.Contains(nameof(ZipReaderOptions.MaxRecoveryBytes), ex.Message);
    }

    [Fact]
    public void RecoveryIsOffByDefault()
    {
        var (zip, _, b, _, _) = Build(false);
        zip = Splice(zip, b.LocalOffset, new byte[100]);
        Assert.Throws<CompressionFormatException>(() => Run(zip, seekable: false, new ZipReaderOptions()));
        Assert.Throws<CompressionFormatException>(() => Run(zip.AsSpan(0, zip.Length - 30).ToArray(), seekable: true, new ZipReaderOptions()));
    }

    [Fact]
    public void ArchiveOpenPassesTheOption()
    {
        var (zip, _, b, _, _) = Build(false);
        zip = Splice(zip, b.LocalOffset, new byte[100]);
        string dir = Path.Combine(Path.GetTempPath(), "onearchive-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var s = new ForwardOnly(new MemoryStream(zip), 500);
            Archive.Extract(s, dir, new ArchiveExtractOptions { Zip = new ZipReaderOptions { Recovery = true } });
            Assert.Equal(s_b, File.ReadAllBytes(Path.Combine(dir, "b.txt")));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InfoZipArchivesRecover(bool seekable)
    {
        string? zipTool = File.Exists("/usr/bin/zip") ? "/usr/bin/zip" : null;
        if (zipTool is null)
        {
            return;
        }

        using var temp = new TempDir();
        string src = temp.Combine("src");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "one.txt"), s_a);
        File.WriteAllBytes(Path.Combine(src, "two.txt"), s_b);
        File.WriteAllBytes(Path.Combine(src, "three.txt"), s_c);
        string archive = temp.Combine("t.zip");
        Tools.Run(zipTool, ["-q", "-r", archive, "one.txt", "two.txt", "three.txt"], src);
        byte[] zip = File.ReadAllBytes(archive);
        int two = zip.AsSpan().IndexOf("two.txt"u8) - 30;
        zip = Splice(zip, two, new byte[77]);
        zip = zip.AsSpan(0, zip.AsSpan().IndexOf(s_centralSig)).ToArray();
        var (got, damage) = Run(zip, seekable);
        AssertEntries(got, ("one.txt", s_a), ("two.txt", s_b), ("three.txt", s_c));
        Assert.Contains(damage, d => d.Kind == ZipDamageKind.SkippedBytes && d.Offset == two && d.Length == 77);
    }
}
