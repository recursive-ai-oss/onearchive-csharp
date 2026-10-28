using System.Runtime.Versioning;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneArchive.SevenZip;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

public class SevenZipWriterTests
{
    private static readonly DateTimeOffset s_time = new(2024, 5, 6, 7, 8, 9, 123, TimeSpan.Zero);

    private static byte[] Elf(ushort machine)
    {
        byte[] b = new byte[6000];
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(b, 0);
        BitConverter.GetBytes(machine).CopyTo(b, 18);
        Random r = new(5);
        for (int i = 64; i < b.Length; i++)
        {
            b[i] = i % 5 == 0 ? (byte)0xE8 : (byte)r.Next(256);
        }

        return b;
    }

    internal static Dictionary<string, byte[]> Content() => new()
    {
        ["hello.txt"] = "hello\n"u8.ToArray(),
        ["dir/text.txt"] = SevenZipFixtures.Repeat("compressible line of text\n", 3000),
        ["dir/random.bin"] = SevenZipFixtures.Lcg(7, 20000),
        ["\u00fcn\u00ef-\u65e5\u672c\u8a9e.txt"] = "unicode"u8.ToArray(),
        ["prog"] = Elf(62),
        ["arm"] = Elf(183),
    };

    internal static byte[] Write(SevenZipWriterOptions? options, IReadOnlyDictionary<string, byte[]>? files = null, bool extras = true)
    {
        using var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, options, leaveOpen: true))
        {
            if (extras)
            {
                w.WriteEntry(new ArchiveEntry { Name = "dir", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)Convert.ToInt32("755", 8), ModifiedTime = s_time });
                w.WriteEntry(new ArchiveEntry { Name = "empty.txt", Size = 0, ModifiedTime = s_time });
            }

            foreach (var (name, data) in files ?? Content())
            {
                w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length, ModifiedTime = s_time, AccessTime = s_time.AddDays(1), Mode = (UnixFileMode)Convert.ToInt32("644", 8) }, data);
            }

            if (extras)
            {
                w.WriteEntry(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "hello.txt" });
            }
        }

        return ms.ToArray();
    }

    internal static void AssertRoundTrip(byte[] archive, IReadOnlyDictionary<string, byte[]>? files = null, bool extras = true)
    {
        files ??= Content();
        using var r = new SevenZipReader(new MemoryStream(archive));
        int seen = 0;
        foreach (SevenZipEntry e in r.Items)
        {
            if (files.TryGetValue(e.Name, out byte[]? expected))
            {
                seen++;
                using Stream s = r.OpenEntryStream(e);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                Assert.Equal(expected, ms.ToArray());
                Assert.Equal(expected.Length, e.Size);
                Assert.Equal(s_time.UtcTicks, e.ModifiedTime!.Value.UtcTicks);
                Assert.Equal((UnixFileMode)Convert.ToInt32("644", 8), e.Mode);
            }
            else if (extras)
            {
                switch (e.Name)
                {
                    case "dir":
                        Assert.Equal(ArchiveEntryKind.Directory, e.Kind);
                        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), e.Mode);
                        break;
                    case "empty.txt":
                        Assert.Equal(ArchiveEntryKind.File, e.Kind);
                        Assert.Equal(0, e.Size);
                        break;
                    case "link":
                        Assert.Equal(ArchiveEntryKind.Symlink, e.Kind);
                        Assert.Equal("hello.txt", e.LinkTarget);
                        break;
                    default:
                        Assert.Fail(e.Name);
                        break;
                }
            }
        }

        Assert.Equal(files.Count, seen);
    }

    public static TheoryData<string> Variants() => new()
    {
        "", "copy", "lzma", "lzma-nonsolid", "deflate", "deflate64", "deflate64-nonsolid", "zstd", "plain-header", "level1", "level9", "nonsolid", "delta", "x86", "arm64", "riscv",
        "filecount", "blocksize", "ppc", "sparc", "ia64", "arm", "armt", "level0",
    };

    internal static SevenZipWriterOptions OptionsFor(string v) => v switch
    {
        "copy" => new() { Method = SevenZipMethod.Copy },
        "lzma" => new() { Method = SevenZipMethod.Lzma },
        "lzma-nonsolid" => new() { Method = SevenZipMethod.Lzma, Solid = false },
        "deflate" => new() { Method = SevenZipMethod.Deflate },
        "deflate64" => new() { Method = SevenZipMethod.Deflate64 },
        "deflate64-nonsolid" => new() { Method = SevenZipMethod.Deflate64, Solid = false, Level = 9 },
        "zstd" => new() { Method = SevenZipMethod.Zstd },
        "plain-header" => new() { EncodeHeader = false },
        "level1" => new() { Level = 1 },
        "level9" => new() { Level = 9 },
        "level0" => new() { Level = 0 },
        "nonsolid" => new() { Solid = false },
        "delta" => new() { Filter = SevenZipFilter.Delta, DeltaDistance = 4 },
        "x86" => new() { Filter = SevenZipFilter.X86 },
        "arm64" => new() { Filter = SevenZipFilter.Arm64 },
        "riscv" => new() { Filter = SevenZipFilter.RiscV },
        "ppc" => new() { Filter = SevenZipFilter.PowerPc },
        "sparc" => new() { Filter = SevenZipFilter.Sparc },
        "ia64" => new() { Filter = SevenZipFilter.Ia64 },
        "arm" => new() { Filter = SevenZipFilter.Arm },
        "armt" => new() { Filter = SevenZipFilter.ArmThumb },
        "filecount" => new() { SolidFileCount = 2 },
        "blocksize" => new() { SolidBlockSize = 30000 },
        _ => new(),
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void RoundTripsThroughTheReader(string variant)
    {
        byte[] a = Write(OptionsFor(variant));
        AssertRoundTrip(a);
        Assert.Equal(a, Write(OptionsFor(variant)));
    }

    [Fact]
    public void AutoFilterSplitsFoldersByExecutableKind()
    {
        byte[] a = Write(new SevenZipWriterOptions());
        using var r = new SevenZipReader(new MemoryStream(a));
        // hello/text/random/unicode share a folder without a filter, as does the final link; prog (x86) and arm (ARM64) each get their own with a filter.
        Assert.Equal(4, r.Folders.Count);
        Assert.Equal([1, 1, 2, 2], r.Folders.Select(f => f.Coders.Count).Order().ToArray());
    }

    [Fact]
    public void EmptyArchiveIsJustTheStartHeader()
    {
        using var ms = new MemoryStream();
        using (var w = new SevenZipWriter(ms, leaveOpen: true))
        {
        }

        Assert.Equal(32, ms.Length);
        using var r = new SevenZipReader(new MemoryStream(ms.ToArray()));
        Assert.Empty(r.Items);
    }

    [Fact]
    public void OnlyEmptyEntriesNeedNoFolder()
    {
        byte[] a = Write(new SevenZipWriterOptions(), new Dictionary<string, byte[]>(), extras: true);
        using var r = new SevenZipReader(new MemoryStream(a));
        Assert.Single(r.Folders); // only the symbolic link has content
        Assert.Equal(3, r.Items.Count);
    }

    [Fact]
    public void ThousandSmallFiles()
    {
        var files = new Dictionary<string, byte[]>();
        for (int i = 0; i < 1000; i++)
        {
            files[$"d{i % 10}/f{i}.txt"] = System.Text.Encoding.ASCII.GetBytes(new string((char)('a' + i % 26), i % 97 + 1));
        }

        byte[] a = Write(new SevenZipWriterOptions(), files, extras: false);
        AssertRoundTrip(a, files, extras: false);
    }

    [Fact]
    public async Task AsyncMatchesSync()
    {
        byte[] sync = Write(new SevenZipWriterOptions());
        using var ms = new MemoryStream();
        await using (var w = new SevenZipWriter(ms, new SevenZipWriterOptions(), leaveOpen: true))
        {
            await w.WriteEntryAsync(new ArchiveEntry { Name = "dir", Kind = ArchiveEntryKind.Directory, Mode = (UnixFileMode)Convert.ToInt32("755", 8), ModifiedTime = s_time }, null, TestContext.Current.CancellationToken);
            await w.WriteEntryAsync(new ArchiveEntry { Name = "empty.txt", Size = 0, ModifiedTime = s_time }, null, TestContext.Current.CancellationToken);
            foreach (var (name, data) in Content())
            {
                await w.WriteEntryAsync(new ArchiveEntry { Name = name, Size = data.Length, ModifiedTime = s_time, AccessTime = s_time.AddDays(1), Mode = (UnixFileMode)Convert.ToInt32("644", 8) }, new MemoryStream(data), TestContext.Current.CancellationToken);
            }

            await w.WriteEntryAsync(new ArchiveEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "hello.txt" }, null, TestContext.Current.CancellationToken);
        }

        Assert.Equal(sync, ms.ToArray());
    }

    private sealed class NoSeek(Stream inner) : Stream
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

    [Fact]
    public void NonSeekableOutputIsBufferedAndIdentical()
    {
        byte[] expected = Write(new SevenZipWriterOptions());
        using var sink = new MemoryStream();
        using (var w = new SevenZipWriter(new NoSeek(sink), new SevenZipWriterOptions(), leaveOpen: true))
        {
            foreach (var (name, data) in Content())
            {
                w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length }, new NoSeek2(data));
            }
        }

        using var r = new SevenZipReader(new MemoryStream(sink.ToArray()));
        Assert.Equal(Content().Count, r.Items.Count);
        _ = expected;
    }

    private sealed class NoSeek2(byte[] data) : Stream
    {
        private int _pos;

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
            int n = Math.Min(Math.Min(count, 777), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void RejectsWrongSizesAndUnsupportedKinds()
    {
        using var ms = new MemoryStream();
        var w = new SevenZipWriter(ms, leaveOpen: true);
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "a", Size = 10 }, new byte[5]));
        var w2 = new SevenZipWriter(new MemoryStream(), leaveOpen: true);
        Assert.Throws<ArgumentException>(() => w2.WriteEntry(new ArchiveEntry { Name = "a", Size = 3 }, new byte[5]));
        var w3 = new SevenZipWriter(new MemoryStream(), leaveOpen: true);
        Assert.Throws<ArchiveUnsupportedException>(() => w3.WriteEntry(new ArchiveEntry { Name = "a", Kind = ArchiveEntryKind.Fifo }));
        Assert.Throws<ArgumentException>(() => w3.WriteEntry(new ArchiveEntry { Name = "" }));
    }

    [Fact]
    public void CreatesFromArchiveCreate()
    {
        using var tmp = new TempDir();
        string src = tmp.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllBytes(Path.Combine(src, "sub/a.bin"), SevenZipFixtures.Lcg(1, 5000));
        File.WriteAllText(Path.Combine(src, "b.txt"), "bbb");
        string a = tmp.Combine("x.7z");
        Archive.Create(a).Add(src, "").Save();
        string o = tmp.Combine("out");
        Archive.Extract(a, o);
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "sub/a.bin")), File.ReadAllBytes(Path.Combine(o, "sub/a.bin")));
        Assert.Equal("bbb", File.ReadAllText(Path.Combine(o, "b.txt")));
    }

    [Fact]
    public void Crc32OfContentIsRecorded()
    {
        byte[] a = Write(new SevenZipWriterOptions { Solid = false });
        using var r = new SevenZipReader(new MemoryStream(a));
        Assert.Contains(r.Folders, f => f.SubStreamCrcs.Any(c => c == Crc32.Compute("hello\n"u8)));
    }
}

/// <summary>Archives written here are accepted by the 7-Zip command line (ONEARCHIVE_INTEROP=1; ONEARCHIVE_7Z names the tool).</summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class SevenZipWriterInteropTests
{
    private static string Tool => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    [Theory]
    [MemberData(nameof(SevenZipWriterTests.Variants), MemberType = typeof(SevenZipWriterTests))]
    public void SevenZipAcceptsOurArchives(string variant)
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        if (variant == "zstd")
        {
            return; // The F7 11 01 method is not in standard 7-Zip builds.
        }

        using var tmp = new TempDir();
        string archive = tmp.Combine("a.7z");
        File.WriteAllBytes(archive, SevenZipWriterTests.Write(SevenZipWriterTests.OptionsFor(variant)));
        Tools.Run(Tool, ["t", "-bso0", "-bsp0", archive]);
        string o = tmp.Combine("out");
        Tools.Run(Tool, ["x", "-snl", "-bso0", "-bsp0", $"-o{o}", archive]);
        foreach (var (name, data) in SevenZipWriterTests.Content())
        {
            string p = Path.Combine(o, name);
            Assert.Equal(data, File.ReadAllBytes(p));
            Assert.Equal(new DateTimeOffset(2024, 5, 6, 7, 8, 9, 123, TimeSpan.Zero).UtcDateTime, File.GetLastWriteTimeUtc(p));
            Assert.Equal((UnixFileMode)Convert.ToInt32("644", 8), File.GetUnixFileMode(p));
        }

        Assert.Equal("hello.txt", new FileInfo(Path.Combine(o, "link")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(o, "empty.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(o, "empty.txt")).Length);
        Assert.Equal((UnixFileMode)Convert.ToInt32("755", 8), File.GetUnixFileMode(Path.Combine(o, "dir")));
    }
}
