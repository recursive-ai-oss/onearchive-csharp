using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>ZipReaderOptions.LazyDirectory: the same entries and errors as the default mode, with memory that does not grow with the entry count.</summary>
/// <remarks>The collection runs alone: the memory tests read the process-wide managed heap, which other tests would move.</remarks>
[Collection(nameof(ZipLazyTests))]
public class ZipLazyTests
{
    private static readonly ZipReaderOptions s_lazy = new() { LazyDirectory = true };
    private static readonly DateTimeOffset s_time = new(2024, 5, 17, 12, 34, 56, TimeSpan.Zero);

    private static string Describe(ZipEntry e) => string.Join(
        "|",
        e.Name, e.Kind, e.Size, e.ModifiedTime, e.AccessTime, e.ChangeTime, e.Mode, e.LinkTarget, e.Method, e.Flags, e.Crc32, e.CompressedSize,
        e.VersionMadeBy, e.VersionNeeded, e.InternalAttributes, e.ExternalAttributes, e.Comment, e.LocalHeaderOffset, Convert.ToHexString(e.RawName.Span),
        string.Join(",", e.ExtraFields.Select(f => f.Id + ":" + Convert.ToHexString(f.Data.Span))), e.UsesZip64, e.IsSizeKnown);

    private static byte[] Drain(Stream s)
    {
        using (s)
        {
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    // Everything a full read shows: each entry's description and content, as listed, and the content again by random access.
    private static List<string> Walk(byte[] zip, ZipReaderOptions options)
    {
        var lines = new List<string>();
        var kept = new List<ZipEntry>();
        using var reader = new ZipReader(new MemoryStream(zip), options);
        while (reader.GetNextEntry() is { } e)
        {
            kept.Add(e);
            string content;
            try
            {
                content = Convert.ToHexString(Drain(reader.OpenEntryStream()));
            }
            catch (Exception ex) when (ex is CompressionException or ArchiveUnsupportedException or ArchivePasswordException)
            {
                content = ex.GetType().Name;
            }

            lines.Add(Describe(e) + "|" + content);
        }

        lines.Add("comment:" + reader.ArchiveComment);
        for (int i = kept.Count - 1; i >= 0; i--)
        {
            string again;
            try
            {
                again = Convert.ToHexString(Drain(reader.OpenEntryStream(kept[i])));
            }
            catch (Exception ex) when (ex is CompressionException or ArchiveUnsupportedException or ArchivePasswordException)
            {
                again = ex.GetType().Name;
            }

            Assert.EndsWith("|" + again, lines[i], StringComparison.Ordinal);
        }

        return lines;
    }

    private static byte[] Write(Action<ZipWriter> fill, ZipWriterOptions? options = null)
    {
        var ms = new MemoryStream();
        using (var w = new ZipWriter(ms, options, leaveOpen: true))
        {
            fill(w);
        }

        return ms.ToArray();
    }

    private static void Standard(ZipWriter w)
    {
        w.WriteEntry(new ArchiveEntry { Name = "docs", Kind = ArchiveEntryKind.Directory, ModifiedTime = s_time, Mode = (UnixFileMode)0b111_101_101 }, (Stream?)null);
        byte[] a = ZipTests.Text(100_000, 1);
        w.WriteEntry(new ArchiveEntry { Name = "docs/a.txt", Size = a.Length, ModifiedTime = s_time, Mode = (UnixFileMode)0b110_100_100 }, a);
        w.WriteEntry(new ArchiveEntry { Name = "empty", Size = 0, ModifiedTime = s_time }, (Stream?)null);
        w.WriteEntry(new ArchiveEntry { Name = "docs/link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "a.txt", ModifiedTime = s_time }, (Stream?)null);
        byte[] u = ZipTests.Text(1000, 2);
        w.WriteEntry(new ArchiveEntry { Name = "ünïcödé-日本語.txt", Size = u.Length, ModifiedTime = s_time }, u);
        w.WriteEntry(new ArchiveEntry { Name = "tiny", Size = 1 }, "x"u8.ToArray());
    }

    [Theory]
    [InlineData(ZipMethod.Store, ZipZip64Mode.Auto, ZipDataDescriptorMode.Auto)]
    [InlineData(ZipMethod.Deflate, ZipZip64Mode.Always, ZipDataDescriptorMode.Always)]
    [InlineData(ZipMethod.Zstd, ZipZip64Mode.Auto, ZipDataDescriptorMode.Always)]
    [InlineData(ZipMethod.BZip2, ZipZip64Mode.Always, ZipDataDescriptorMode.Auto)]
    public void ListsTheSameEntriesAsTheDefaultMode(ZipMethod method, ZipZip64Mode zip64, ZipDataDescriptorMode descriptors)
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = method, Zip64 = zip64, DataDescriptors = descriptors, Comment = "héllo" });
        List<string> eager = Walk(zip, ZipReaderOptions.Default);
        Assert.Equal(7, eager.Count);
        Assert.Equal(eager, Walk(zip, s_lazy));
    }

    [Fact]
    public void EncryptedEntriesListWithoutAPasswordAndOpenWithOne()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Deflate, Password = "secret" });
        Assert.Equal(Walk(zip, ZipReaderOptions.Default), Walk(zip, s_lazy));
        var withPassword = new ZipReaderOptions { LazyDirectory = true, Password = "secret" };
        List<string> lines = Walk(zip, withPassword);
        Assert.Equal(Walk(zip, new ZipReaderOptions { Password = "secret" }), lines);
        Assert.Contains(lines, l => l.StartsWith("docs/a.txt|", StringComparison.Ordinal) && l.EndsWith(Convert.ToHexString(ZipTests.Text(100_000, 1)), StringComparison.Ordinal));
    }

    private static RawZip Raw(int count, bool reverse)
    {
        var raw = new RawZip { ReverseDirectory = reverse };
        for (int i = 0; i < count; i++)
        {
            raw.Add(i % 2 == 0 ? RawZip.Stored("s" + i.ToString(CultureInfo.InvariantCulture), ZipTests.Text(20 + i, i)) : RawZip.Deflated("d" + i.ToString(CultureInfo.InvariantCulture), ZipTests.Text(300 + i, i)));
        }

        return raw;
    }

    [Fact]
    public void DirectoryOutOfOffsetOrderListsTheSameEntries()
    {
        byte[] zip = Raw(25, reverse: true).Finish();
        List<string> eager = Walk(zip, ZipReaderOptions.Default);
        Assert.Equal(25, eager.Count - 1);
        Assert.StartsWith("s24|", eager[0], StringComparison.Ordinal);
        Assert.Equal(eager, Walk(zip, s_lazy));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingAndSharedEntriesAreRefusedLikeTheDefaultMode(bool reverse)
    {
        // An entry whose compressed size runs into the next one.
        var raw = new RawZip { ReverseDirectory = reverse };
        raw.Add(RawZip.Stored("a", new byte[100]));
        var lie = RawZip.Stored("b", new byte[100]);
        lie.DeclaredCompressed = 5000;
        lie.DeclaredSize = 5000;
        raw.Add(lie);
        raw.Add(RawZip.Stored("c", new byte[100]));
        byte[] overlap = raw.Finish();
        Assert.Throws<CompressionFormatException>(() => Walk(overlap, ZipReaderOptions.Default));
        Assert.Throws<CompressionFormatException>(() => Walk(overlap, s_lazy));

        // Two entries that name one local header.
        var shared = new RawZip { ReverseDirectory = reverse };
        var first = RawZip.Stored("a", new byte[10]);
        shared.Add(first);
        var second = RawZip.Stored("b", new byte[10]);
        shared.Add(second);
        shared.Add(RawZip.Stored("c", new byte[10]));
        second.OffsetOverride = first.LocalOffset;
        byte[] twice = shared.Finish();
        Assert.Throws<CompressionFormatException>(() => Walk(twice, ZipReaderOptions.Default));
        Assert.Throws<CompressionFormatException>(() => Walk(twice, s_lazy));
    }

    [Fact]
    public void ErrorsComeBeforeTheFirstEntry()
    {
        var raw = Raw(10, reverse: false);
        byte[] zip = raw.Finish(directoryOffsetOverride: 3);
        using var reader = new ZipReader(new MemoryStream(zip), s_lazy);
        Assert.Throws<CompressionFormatException>(() => reader.GetNextEntry());

        // A damaged record in the middle of the directory fails the first call, not the call that reaches it.
        byte[] good = Raw(10, reverse: false).Finish();
        byte[] bad = [.. good];
        int dir = BinaryPrimitives.ReadInt32LittleEndian(bad.AsSpan(bad.Length - 22 + 16));
        int fifth = dir;
        for (int i = 0; i < 5; i++)
        {
            fifth += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bad.AsSpan(fifth + 28));
        }

        bad[fifth] = 0;
        using var reader2 = new ZipReader(new MemoryStream(bad), s_lazy);
        Assert.Throws<CompressionFormatException>(() => reader2.GetNextEntry());
    }

    [Fact]
    public void LimitsApplyAndAreTypedErrors()
    {
        byte[] zip = Raw(40, reverse: false).Finish();
        foreach (ArchiveLimits limits in new[] { new ArchiveLimits { MaxEntryCount = 10 }, new ArchiveLimits { MaxTotalSize = 500 }, new ArchiveLimits { MaxPathLength = 1 } })
        {
            var options = new ZipReaderOptions { LazyDirectory = true, Limits = limits };
            Assert.Throws<CompressionLimitException>(() => Walk(zip, options));
            Assert.Throws<CompressionLimitException>(() => Walk(zip, new ZipReaderOptions { Limits = limits }));
        }

        // The memory limit on the directory is about holding it; the lazy mode does not.
        var tight = new ZipReaderOptions { MaxCentralDirectorySize = 100 };
        Assert.Throws<CompressionLimitException>(() => Walk(zip, tight));
        Assert.Equal(Walk(zip, ZipReaderOptions.Default), Walk(zip, new ZipReaderOptions { LazyDirectory = true, MaxCentralDirectorySize = 100 }));
    }

    [Fact]
    public void EntriesOfAnotherReaderAreRefused()
    {
        byte[] zip = Raw(5, reverse: false).Finish();
        using var one = new ZipReader(new MemoryStream(zip), s_lazy);
        using var two = new ZipReader(new MemoryStream(zip), s_lazy);
        ZipEntry fromOne = one.GetNextEntry()!;
        two.GetNextEntry();
        Assert.Throws<ArgumentException>(() => two.OpenEntryStream(fromOne));
        Assert.Throws<ArgumentException>(() => two.OpenEntryStream(new ArchiveEntry { Name = "s0" }));
        Assert.Equal(ZipTests.Text(20, 0), Drain(one.OpenEntryStream(fromOne)));

        // After the walk has ended, and with other entries open, an earlier entry still opens by seeking to it.
        ZipEntry last = fromOne;
        while (one.GetNextEntry() is { } e)
        {
            last = e;
        }

        Assert.Null(one.GetNextEntry());
        Assert.Equal(ZipTests.Text(20, 0), Drain(one.OpenEntryStream(fromOne)));
        using Stream a = one.OpenEntryStream(last);
        using Stream b = one.OpenEntryStream(fromOne);
        Assert.Equal(ZipTests.Text(20, 0), Drain(b));
        Assert.Equal(ZipTests.Text(24, 4), Drain(a));
    }

    [Fact]
    public void OtherStreamsAndForceStreamingBehaveAsBefore()
    {
        byte[] zip = Write(Standard, new ZipWriterOptions { Method = ZipMethod.Deflate });
        var forced = new ZipReaderOptions { LazyDirectory = true, ForceStreaming = true };
        using var reader = new ZipReader(new MemoryStream(zip), forced);
        Assert.False(reader.SupportsRandomAccess);
        Assert.NotNull(reader.GetNextEntry());
        using var forward = new ZipReader(new ForwardOnly(new MemoryStream(zip)), s_lazy);
        Assert.False(forward.SupportsRandomAccess);
        Assert.Equal("docs", forward.GetNextEntry()!.Name);
    }

    [Fact]
    public void RecoveryFallsBackToLocalHeadersAndListsTheSameDamage()
    {
        var raw = new RawZip();
        var a = RawZip.Stored("a", ZipTests.Text(500, 1));
        var b = RawZip.Stored("b", ZipTests.Text(500, 2));
        var c = RawZip.Stored("c", ZipTests.Text(500, 3));
        raw.Add(a).Add(b).Add(c);
        byte[] zip = raw.Finish();
        byte[] broken = [.. zip];
        broken[b.LocalOffset] = (byte)'X';      // the directory is well formed but the second local header is gone

        foreach (byte[] input in new[] { zip, broken })
        {
            var results = new List<(List<string> Names, List<ZipDamage> Damage)>();
            foreach (bool lazy in new[] { false, true })
            {
                var names = new List<string>();
                using var reader = new ZipReader(new MemoryStream(input), new ZipReaderOptions { Recovery = true, LazyDirectory = lazy });
                while (reader.GetNextEntry() is { } e)
                {
                    names.Add(e.Name);
                    Drain(reader.OpenEntryStream());
                }

                results.Add((names, [.. reader.Damage]));
            }

            Assert.Equal(results[0].Names, results[1].Names);
            Assert.Equal(results[0].Damage, results[1].Damage);
        }
    }

    [Fact]
    public void HostileSetEndsTheSameWayInBothModes()
    {
        string? python = ZipHostileSetTests.FindPython();
        if (python is null)
        {
            Assert.SkipUnless(!Tools.InteropEnabled, "python3 is needed to generate the hostile set");
            return;
        }

        using var temp = new TempDir();
        string set = temp.Combine("set");
        Tools.Run(python, [ZipHostileSetTests.Script(), set]);
        string[] files = Directory.GetFiles(set, "*.zip");
        Assert.True(files.Length > 50);
        foreach (string file in files)
        {
            byte[] zip = File.ReadAllBytes(file);
            string eager = Outcome(zip, ZipReaderOptions.Default);
            string lazy = Outcome(zip, s_lazy);
            // Where one mode fails the other fails with the same kind of error; where both read, they read the same.
            if (eager.StartsWith("ok", StringComparison.Ordinal) || lazy.StartsWith("ok", StringComparison.Ordinal))
            {
                Assert.True(eager == lazy, $"{Path.GetFileName(file)}: default {Trim(eager)} / lazy {Trim(lazy)}");
            }
            else
            {
                Assert.True(eager.Split(':')[0] == lazy.Split(':')[0], $"{Path.GetFileName(file)}: default {Trim(eager)} / lazy {Trim(lazy)}");
            }
        }

        static string Trim(string s) => s.Length > 200 ? s[..200] : s;

        static string Outcome(byte[] zip, ZipReaderOptions options)
        {
            try
            {
                return "ok:" + string.Join("\n", Walk(zip, options));
            }
            catch (Exception ex) when (ex is CompressionException or ArchiveUnsupportedException or ArchivePasswordException)
            {
                return ex.GetType().Name + ":" + ex.Message;
            }
        }
    }

    [Fact]
    public void ArchiveOpenAndExtractUseTheMode()
    {
        using var temp = new TempDir();
        string path = temp.Combine("many.zip");
        ZipManyEntriesFixture.Write(path, 3000);
        var options = new ArchiveOpenOptions { Zip = s_lazy };
        using (Archive archive = Archive.Open(path, options))
        {
            Assert.Equal(3000, archive.Entries.Count);
            Assert.Equal(ZipManyEntriesFixture.ContentOf(1234), Drain(archive.OpenEntry(archive.Entries[1234])));
        }

        string target = temp.Combine("out");
        ArchiveExtractResult result = Archive.Extract(path, target, new ArchiveExtractOptions { Zip = s_lazy });
        Assert.Equal(3000, result.EntriesExtracted);
        Assert.Equal(ZipManyEntriesFixture.ContentOf(2999), File.ReadAllBytes(Path.Combine(target, ZipManyEntriesFixture.NameOf(2999))));
        Assert.Equal(3000, Directory.GetFiles(target, "*", SearchOption.AllDirectories).Length);
    }

    private static string Tool(string variable, string fallback) => Environment.GetEnvironmentVariable(variable) ?? fallback;

    /// <summary>Archives made by Info-ZIP, 7-Zip and bsdtar (zip64, data descriptors, Unicode names, more than 65535 entries) read the same in both modes.</summary>
    [Fact]
    [Trait("Category", "Interop")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void ArchivesFromReferenceToolsReadTheSameInBothModes()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string zip = Tool("ONEARCHIVE_ZIP", "zip"), sevenZip = Tool("ONEARCHIVE_7Z", "7zz"), bsdtar = Tool("ONEARCHIVE_BSDTAR", "bsdtar");
        using var temp = new TempDir();
        string tree = temp.Combine("tree");
        Directory.CreateDirectory(Path.Combine(tree, "dir/nested"));
        Directory.CreateDirectory(Path.Combine(tree, "emptydir"));
        File.WriteAllBytes(Path.Combine(tree, "dir/nested/data.bin"), ZipTests.Text(300_000, 11));
        File.WriteAllBytes(Path.Combine(tree, "dir/random.bin"), new Random(5).GetBytes(20_000));
        File.WriteAllText(Path.Combine(tree, "ünïcödé-日本語.txt"), "unicode");
        File.WriteAllText(Path.Combine(tree, "zero"), string.Empty);
        File.CreateSymbolicLink(Path.Combine(tree, "link"), "zero");
        string many = temp.Combine("many");
        Directory.CreateDirectory(many);
        Tools.Run("bash", ["-c", "for i in $(seq 1 70000); do : > f$i; done"], many);

        var archives = new List<(string Path, int MinEntries)>();
        void Make(string name, int min, string tool, string[] args, string cwd)
        {
            Tools.Run(tool, args.Select(a => a.Replace("{out}", temp.Combine(name), StringComparison.Ordinal)), cwd);
            archives.Add((temp.Combine(name), min));
        }

        Make("info.zip", 6, zip, ["-qry", "{out}", "."], tree);
        Make("info-zip64.zip", 6, zip, ["-qry", "-fz", "{out}", "."], tree);
        Make("info-many.zip", 70_000, zip, ["-qr", "-0", "{out}", "."], many);
        Tools.Run("bash", ["-c", $"set -o pipefail; cat dir/nested/data.bin | '{zip}' -q '{temp.Combine("stream.zip")}' -"], tree);
        archives.Add((temp.Combine("stream.zip"), 1));
        Make("7z.zip", 6, sevenZip, ["a", "-tzip", "-bd", "-y", "{out}", "."], tree);
        Make("7z-many.zip", 70_000, sevenZip, ["a", "-tzip", "-mx=0", "-bd", "-y", "{out}", "."], many);
        Make("bsdtar.zip", 6, bsdtar, ["-a", "-cf", "{out}", "."], tree);

        foreach ((string path, int min) in archives)
        {
            byte[] bytes = File.ReadAllBytes(path);
            List<string> eager = Walk(bytes, ZipReaderOptions.Default);
            Assert.True(eager.Count - 1 >= min, $"{Path.GetFileName(path)} has {eager.Count - 1} entries");
            Assert.Equal(eager, Walk(bytes, s_lazy));
        }
    }

    // What a full enumeration holds, in the lazy and the default mode.
    private static (long Retained, long PeakLive, long Allocated, double Seconds) Measure(string path, int count, bool lazy, ITestOutputHelper? log)
    {
        var limits = new ArchiveLimits { MaxEntryCount = int.MaxValue, MaxTotalSize = long.MaxValue };
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(true);
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long peak = before;
        var sw = Stopwatch.StartNew();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var reader = new ZipReader(stream, new ZipReaderOptions { LazyDirectory = lazy, Limits = limits, MaxCentralDirectorySize = 1L << 34 }, leaveOpen: true);
        int seen = 0;
        var sample = new List<ZipEntry>();
        while (reader.GetNextEntry() is { } e)
        {
            Assert.Equal(ZipManyEntriesFixture.NameOf(seen), e.Name);
            if (seen % 100_000 == 7)
            {
                sample.Add(e);
            }

            if (++seen % 20_000 == 0)
            {
                // The live heap at this point (after a full collection), not the garbage the allocator has not yet returned.
                peak = Math.Max(peak, GC.GetTotalMemory(true));
            }
        }

        sw.Stop();
        Assert.Equal(count, seen);
        peak = Math.Max(peak, GC.GetTotalMemory(true));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        foreach (ZipEntry e in sample)
        {
            int i = int.Parse(e.Name.AsSpan(e.Name.Length - 12, 8), CultureInfo.InvariantCulture);
            Assert.Equal(ZipManyEntriesFixture.ContentOf(i), Drain(reader.OpenEntryStream(e)));
        }

        long retained = GC.GetTotalMemory(true) - before;
        log?.WriteLine($"{(lazy ? "lazy" : "default"),-8} n={count,9:N0}  retained={retained / 1024.0 / 1024,8:F2} MiB  peak(live)={(peak - before) / 1024.0 / 1024,8:F2} MiB  allocated={allocated / 1024.0 / 1024,9:F1} MiB  enumerate={sw.Elapsed.TotalSeconds:F2} s");
        GC.KeepAlive(reader);
        return (retained, peak - before, allocated, sw.Elapsed.TotalSeconds);
    }

    [Fact]
    public void MemoryDoesNotGrowWithTheEntryCount()
    {
        using var temp = new TempDir();
        var small = (Count: 10_000, Path: temp.Combine("small.zip"));
        var large = (Count: 100_000, Path: temp.Combine("large.zip"));
        ZipManyEntriesFixture.Write(small.Path, small.Count);
        ZipManyEntriesFixture.Write(large.Path, large.Count);
        var log = TestContext.Current.TestOutputHelper;
        var lazySmall = Measure(small.Path, small.Count, lazy: true, log);
        var lazyLarge = Measure(large.Path, large.Count, lazy: true, log);
        var eagerLarge = Measure(large.Path, large.Count, lazy: false, log);

        // What the reader holds after a full walk is a few buffers, whatever the count; the default mode holds the entries.
        Assert.True(lazyLarge.Retained < 1 << 20, $"lazy reader retained {lazyLarge.Retained} bytes after {large.Count} entries");
        Assert.True(lazyLarge.Retained - lazySmall.Retained < 256 * 1024, $"retained {lazySmall.Retained} at {small.Count} entries, {lazyLarge.Retained} at {large.Count}");
        Assert.True(lazyLarge.PeakLive < 4 << 20, $"lazy peak {lazyLarge.PeakLive}");
        Assert.True(eagerLarge.Retained > 20 * Math.Max(lazyLarge.Retained, 1 << 18), $"default {eagerLarge.Retained} vs lazy {lazyLarge.Retained}");
    }

    /// <summary>The 10k, 100k and 1M figures: run with ONEARCHIVE_ZIP_MEASURE=1 (the archive of a million entries is about 110 MB, written under the temp directory).</summary>
    [Fact]
    public void ReportMemoryAtThreeSizes()
    {
        if (Environment.GetEnvironmentVariable("ONEARCHIVE_ZIP_MEASURE") != "1")
        {
            return;
        }

        using var temp = new TempDir();
        foreach (int count in new[] { 10_000, 100_000, 1_000_000 })
        {
            string path = temp.Combine("n" + count.ToString(CultureInfo.InvariantCulture) + ".zip");
            ZipManyEntriesFixture.Write(path, count);
            foreach (bool lazy in new[] { true, false })
            {
                Measure(path, count, lazy, TestContext.Current.TestOutputHelper);
            }

            File.Delete(path);
        }
    }
}

/// <summary>The collection of <see cref="ZipLazyTests"/>; no other test runs beside it.</summary>
[CollectionDefinition(nameof(ZipLazyTests), DisableParallelization = true)]
public sealed class ZipLazyGroup;
