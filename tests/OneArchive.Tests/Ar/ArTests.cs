using System.IO.Compression;
using System.Text;
using RecursiveAI.OneArchive.Ar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

public class ArTests
{
    private const string LongName = "a-very-long-member-name-here.txt";

    private static readonly (string Name, byte[] Data)[] s_flat =
    [
        ("a.txt", "hello\n"u8.ToArray()),
        ("b.bin", "xyz"u8.ToArray()),
        ("empty", []),
        (LongName, ContainerVectors.Big(1001)),
        ("sp ace.txt", "spaced"u8.ToArray()),
        ("sixteen-chars-ok", "16"u8.ToArray()),
    ];

    private static List<(ArEntry Entry, byte[] Data)> ReadAll(Stream stream, ArReaderOptions? options = null)
    {
        var list = new List<(ArEntry, byte[])>();
        using var reader = new ArReader(stream, options);
        while (reader.GetNextEntry() is { } e)
        {
            using var ms = new MemoryStream();
            using (Stream s = reader.OpenEntryStream())
            {
                s.CopyTo(ms);
            }

            list.Add((e, ms.ToArray()));
        }

        return list;
    }

    private static void CheckFlat(List<(ArEntry Entry, byte[] Data)> items)
    {
        Assert.Equal(s_flat.Select(f => f.Name), items.Select(i => i.Entry.Name));
        for (int i = 0; i < s_flat.Length; i++)
        {
            Assert.Equal(s_flat[i].Data, items[i].Data);
            Assert.Equal(s_flat[i].Data.Length, items[i].Entry.Size);
            Assert.Equal(ArchiveEntryKind.File, items[i].Entry.Kind);
        }
    }

    [Theory]
    [InlineData("flat.gnu.a")]
    [InlineData("flat.bsd.a")]
    public void ReadsReferenceToolArchives(string vector)
    {
        byte[] bytes = ContainerVectors.Read("ar", vector);
        CheckFlat(ReadAll(new MemoryStream(bytes)));
        CheckFlat(ReadAll(new TarTests.ForwardOnly(bytes)));
    }

    [Fact]
    public void NameStylesAndHeaderFieldsAreExposed()
    {
        var gnu = ReadAll(new MemoryStream(ContainerVectors.Read("ar", "flat.gnu.a")));
        Assert.Equal(ArNameStyle.Inline, gnu[0].Entry.NameStyle);
        Assert.Equal("a.txt/", gnu[0].Entry.RawName);
        Assert.Equal(ArNameStyle.GnuTable, gnu.Single(i => i.Entry.Name == LongName).Entry.NameStyle);
        Assert.Equal(gnu[0].Entry.HeaderOffset + 60, gnu[0].Entry.DataOffset);
        Assert.Equal(0x1A4, (int)gnu[0].Entry.Mode! & 0x1FF);

        var bsd = ReadAll(new MemoryStream(ContainerVectors.Read("ar", "flat.bsd.a")));
        Assert.Equal(ArNameStyle.BsdInline, bsd.Single(i => i.Entry.Name == LongName).Entry.NameStyle);
        Assert.Equal(ArNameStyle.BsdInline, bsd.Single(i => i.Entry.Name == "sp ace.txt").Entry.NameStyle);
        Assert.Equal(1700000000, bsd[0].Entry.ModifiedTime!.Value.ToUnixTimeSeconds());
        Assert.Equal(0x8000u, bsd[0].Entry.RawMode & 0xF000);
    }

    [Fact]
    public void SpecialMembersAreHiddenUnlessAsked()
    {
        byte[] bytes = ContainerVectors.Read("ar", "symbols.gnu.a");
        Assert.Equal(["f.o"], ReadAll(new MemoryStream(bytes)).Select(i => i.Entry.Name));
        var all = ReadAll(new MemoryStream(bytes), new ArReaderOptions { IncludeSpecialMembers = true });
        Assert.Equal(ArMemberKind.SymbolTable, all[0].Entry.MemberKind);
        Assert.Equal("/", all[0].Entry.Name);
        Assert.Equal(8 + 60, all[0].Entry.DataOffset);
        Assert.True(all[0].Data.Length > 8);
        Assert.Equal(ArMemberKind.Regular, all[1].Entry.MemberKind);

        var withTable = ReadAll(new MemoryStream(ContainerVectors.Read("ar", "flat.gnu.a")), new ArReaderOptions { IncludeSpecialMembers = true });
        Assert.Equal(ArMemberKind.LongNameTable, withTable[0].Entry.MemberKind);
        Assert.Contains("a-very-long-member-name-here.txt/\n", Encoding.ASCII.GetString(withTable[0].Data), StringComparison.Ordinal);
    }

    [Fact]
    public void ADebPackageListsAndExtracts()
    {
        byte[] deb = ContainerVectors.Read("ar", "mini.deb");
        var items = ReadAll(new MemoryStream(deb));
        Assert.Equal(["debian-binary", "control.tar.gz", "data.tar.gz"], items.Select(i => i.Entry.Name));
        Assert.Equal("2.0\n", Encoding.ASCII.GetString(items[0].Data));

        using var t = new TempDir();
        Archive.Extract(new MemoryStream(deb), t.Combine("pkg"));
        Assert.True(File.Exists(t.Combine("pkg/data.tar.gz")));

        // The members are tar.gz: open one through the happy path, without seeking.
        using var data = Archive.Open(new MemoryStream(items[2].Data));
        Assert.Contains(data.Entries, e => e.Name.EndsWith("README", StringComparison.Ordinal));
        using var gz = new GZipStream(new MemoryStream(items[1].Data), CompressionMode.Decompress);
        Assert.NotEqual(0, gz.ReadByte());
    }

    [Fact]
    public void ThinArchivesAreUnsupported()
    {
        var ex = Assert.Throws<ArchiveUnsupportedException>(() => ReadAll(new MemoryStream("!<thin>\n"u8.ToArray())));
        Assert.Equal("ar", ex.Format);
        Assert.Throws<ArchiveUnsupportedException>(() => Archive.Open(new MemoryStream("!<thin>\n"u8.ToArray())).Entries.ToList());
    }

    [Fact]
    public void AnArchiveWithNoMembersIsEmpty()
    {
        Assert.Empty(ReadAll(new MemoryStream("!<arch>\n"u8.ToArray())));
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream("!<arch>"u8.ToArray())));
        Assert.Equal(0, Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream("!<arch>X"u8.ToArray()))).Offset);
    }

    // ---- writer ----

    private static byte[] Write(ArFormat format, IEnumerable<(string Name, byte[] Data)> members, long memory = ArWriterOptions.DefaultMaxMemoryBuffer)
    {
        var ms = new MemoryStream();
        using (var w = new ArWriter(ms, new ArWriterOptions { Format = format, MaxMemoryBuffer = memory }, leaveOpen: true))
        {
            foreach (var (name, data) in members)
            {
                w.WriteEntry(new ArchiveEntry { Name = name, Size = data.Length, Mode = (UnixFileMode)0x1ED, ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1700000000) }, data);
            }
        }

        return ms.ToArray();
    }

    private static readonly (string, byte[])[] s_odd =
    [
        ("odd", "12345"u8.ToArray()),
        ("/leading-slash", "x"u8.ToArray()),
        ("ends-with-slash/", "y"u8.ToArray()),
        ("dir/sub/file.txt", "zz"u8.ToArray()),
        ("#1/looks-bsd", "q"u8.ToArray()),
        ("\u00fcn\u00ef-\u65e5\u672c-long-name-that-needs-the-table.txt", "unicode"u8.ToArray()),
        ("trailing space ", "t"u8.ToArray()),
        ("//", "t"u8.ToArray()),
        ("/", "t"u8.ToArray()),
        ("/SYM64/", "t"u8.ToArray()),
    ];

    [Theory]
    [InlineData(ArFormat.Gnu)]
    [InlineData(ArFormat.Bsd)]
    public void RoundTripsOurOwnOutput(ArFormat format)
    {
        var members = s_flat.Concat(s_odd).ToArray();
        byte[] bytes = Write(format, members);
        var items = ReadAll(new MemoryStream(bytes));
        Assert.Equal(members.Length, items.Count);
        for (int i = 0; i < members.Length; i++)
        {
            Assert.Equal(members[i].Item1, items[i].Entry.Name);
            Assert.Equal(members[i].Item2, items[i].Data);
            Assert.Equal(0x1ED, (int)items[i].Entry.Mode!);
            Assert.Equal(1700000000, items[i].Entry.ModifiedTime!.Value.ToUnixTimeSeconds());
        }

        Assert.Equal(members.Length, ReadAll(new TarTests.ForwardOnly(bytes)).Count);
    }

    [Fact]
    public void TheCommonVariantRefusesLongNames()
    {
        var short1 = new (string, byte[])[] { ("a.txt", [1, 2, 3]), ("fifteen-chars-x", [4]) };
        Assert.Equal(2, ReadAll(new MemoryStream(Write(ArFormat.Common, short1))).Count);
        Assert.Throws<ArchiveUnsupportedException>(() => Write(ArFormat.Common, [("sixteen-chars-xyz", [1])]));
    }

    [Fact]
    public void WritingIsDeterministicAndUsesTheGnuLayout()
    {
        Assert.Equal(Write(ArFormat.Gnu, s_flat), Write(ArFormat.Gnu, s_flat));
        byte[] one = Write(ArFormat.Gnu, [("f", "ab"u8.ToArray())]);
        Assert.Equal("!<arch>\nf/              1700000000  0     0     100755  2         `\nab", Encoding.ASCII.GetString(one));
        byte[] odd = Write(ArFormat.Gnu, [("f", "abc"u8.ToArray())]);
        Assert.Equal(8 + 60 + 4, odd.Length);
        Assert.Equal((byte)'\n', odd[^1]);
    }

    [Fact]
    public void GnuOutputLargerThanTheMemoryBufferStillComesOutInOrder()
    {
        byte[] bytes = Write(ArFormat.Gnu, s_flat, memory: 100);
        Assert.Equal(Write(ArFormat.Gnu, s_flat), bytes);
        CheckFlat(ReadAll(new MemoryStream(bytes)));
    }

    [Fact]
    public void SymbolTablesAreWrittenAsGiven()
    {
        var ms = new MemoryStream();
        using (var w = new ArWriter(ms, leaveOpen: true))
        {
            w.SetSymbolTable(new byte[] { 0, 0, 0, 0 });
            w.WriteEntry(new ArchiveEntry { Name = "o.o", Size = 1 }, new byte[] { 9 });
            Assert.Throws<InvalidOperationException>(() => w.SetSymbolTable(new byte[1]));
        }

        var all = ReadAll(new MemoryStream(ms.ToArray()), new ArReaderOptions { IncludeSpecialMembers = true });
        Assert.Equal(["/", "o.o"], all.Select(i => i.Entry.Name));
        Assert.Equal(new byte[4], all[0].Data);
        Assert.Throws<NotSupportedException>(() => new ArWriter(new MemoryStream(), new ArWriterOptions { Format = ArFormat.Bsd }).SetSymbolTable(new byte[1]));
    }

    [Fact]
    public void OnlyRegularFilesAndValidFieldsAreWritten()
    {
        using var w = new ArWriter(new MemoryStream());
        Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "d", Kind = ArchiveEntryKind.Directory }));
        Assert.Throws<ArchiveUnsupportedException>(() => w.WriteEntry(new ArchiveEntry { Name = "u", Uid = 1_000_000 }));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "a\nb" }));
        Assert.Throws<ArgumentException>(() => w.WriteEntry(new ArchiveEntry { Name = "short", Size = 5 }, new byte[2]));
    }

    // ---- hostile input ----

    private static byte[] Sample(ArFormat format) => Write(format, [("one.txt", "12345"u8.ToArray()), ("a-name-longer-than-sixteen.txt", "xy"u8.ToArray()), ("e", [])]);

    [Theory]
    [InlineData(ArFormat.Gnu)]
    [InlineData(ArFormat.Bsd)]
    public void TruncationAtEveryOffsetIsATypedErrorOrAShorterListing(ArFormat format)
    {
        byte[] bytes = Sample(format);
        for (int cut = 0; cut < bytes.Length; cut++)
        {
            byte[] part = bytes.AsSpan(0, cut).ToArray();
            try
            {
                ReadAll(new MemoryStream(part));
            }
            catch (CompressionFormatException)
            {
            }

            try
            {
                ReadAll(new TarTests.ForwardOnly(part));
            }
            catch (CompressionFormatException)
            {
            }
        }

        // Cutting inside data or a header is always an error; at a member boundary it is a shorter archive (ar has no trailer).
        byte[] cutInData = Write(ArFormat.Common, [("x", "abcd"u8.ToArray())]).AsSpan(0, 8 + 60 + 2).ToArray();
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(cutInData)));
        Assert.Throws<CompressionFormatException>(() => ReadAll(new TarTests.ForwardOnly(cutInData)));
    }

    [Theory]
    [InlineData(ArFormat.Gnu)]
    [InlineData(ArFormat.Bsd)]
    public void BitFlipsNeverEscapeTheErrorHierarchy(ArFormat format)
    {
        byte[] bytes = Sample(format);
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int bit = 0; bit < 8; bit += 2)
            {
                byte[] copy = (byte[])bytes.Clone();
                copy[i] ^= (byte)(1 << bit);
                try
                {
                    ReadAll(new MemoryStream(copy), new ArReaderOptions { IncludeSpecialMembers = bit == 0 });
                }
                catch (CompressionException)
                {
                }
            }
        }
    }

    [Fact]
    public void BadEndMarkerFieldsAndReferencesNameTheOffset()
    {
        byte[] bytes = Sample(ArFormat.Gnu);
        byte[] marker = (byte[])bytes.Clone();
        marker[8 + 58] = (byte)'X';
        Assert.Equal(8 + 58, Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(marker))).Offset);

        byte[] size = (byte[])bytes.Clone();
        size[8 + 48] = (byte)'z';
        Assert.Equal(8 + 48, Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(size))).Offset);

        // A "/N" name with no table before it, and one past the table's end.
        byte[] noTable = Encoding.ASCII.GetBytes("!<arch>\n" + "/0".PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "644".PadRight(8) + "0".PadRight(10) + "`\n");
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(noTable)));
    }

    [Fact]
    public void HugeSizesAndNamesFailBeforeAllocating()
    {
        string header(string name, string size) => name.PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "644".PadRight(8) + size.PadRight(10) + "`\n";
        byte[] Make(string name, string size) => Encoding.ASCII.GetBytes("!<arch>\n" + header(name, size));

        // A long-name table of 9,999,999,999 bytes.
        var table = Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(Make("//", "9999999999"))));
        Assert.Equal(nameof(ArReaderOptions.MaxNameTableSize), table.LimitName);

        // A BSD name of 1,000,000 bytes in a 9,999,999,999-byte member.
        var bsd = Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(Make("#1/1000000", "9999999999"))));
        Assert.Equal(nameof(ArchiveLimits.MaxPathLength), bsd.LimitName);

        // A member bigger than the total-size limit.
        var total = Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(Make("big/", "9999999999"))));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), total.LimitName);

        // A BSD name longer than the member.
        Assert.Throws<CompressionFormatException>(() => ReadAll(new MemoryStream(Make("#1/20", "10"))));
    }

    [Fact]
    public void PathEscapesAreRefusedAtExtraction()
    {
        foreach (string name in new[] { "../evil", "a/../../evil", "/etc/evil" })
        {
            foreach (ArFormat format in new[] { ArFormat.Gnu, ArFormat.Bsd })
            {
                using var t = new TempDir();
                byte[] bytes = Write(format, [(name, [1])]);
                Assert.Throws<ArchiveSafetyException>(() => Archive.Extract(new MemoryStream(bytes), t.Combine("out")));
                Assert.False(File.Exists(t.Combine("evil")));
            }
        }
    }

    [Fact]
    public void LimitsApply()
    {
        byte[] bytes = Write(ArFormat.Gnu, Enumerable.Range(0, 5).Select(i => ("f" + i, new byte[10])));
        var count = new ArReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 4 } };
        Assert.Equal(nameof(ArchiveLimits.MaxEntryCount), Assert.Throws<CompressionLimitException>(() => ReadAll(new MemoryStream(bytes), count)).LimitName);
    }

    // ---- the Archive happy path ----

    [Fact]
    public void ArchiveDetectsAndExtractsAr()
    {
        using var t = new TempDir();
        Archive.Extract(new MemoryStream(ContainerVectors.Read("ar", "flat.gnu.a")), t.Combine("out"));
        Assert.Equal("hello\n", File.ReadAllText(t.Combine("out/a.txt")));
        Assert.Equal(1001, new FileInfo(t.Combine("out/" + LongName)).Length);
        using Archive a = Archive.Open(new MemoryStream(ContainerVectors.Read("ar", "flat.bsd.a")));
        Assert.Equal(ArchiveFormat.Ar, a.Format);
        Assert.Equal(s_flat.Length, a.Entries.Count);
    }

    [Fact]
    public void ArchiveCreatesArByExtension()
    {
        using var t = new TempDir();
        string src = t.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "d"));
        File.WriteAllText(Path.Combine(src, "d", "x.txt"), "x contents");
        File.WriteAllText(Path.Combine(src, "y.txt"), "y");
        foreach (string name in new[] { "out.a", "out.ar" })
        {
            string path = t.Combine(name);
            Archive.Create(path).Add(src, "").Save();
            using Archive a = Archive.Open(path);
            Assert.Equal(ArchiveFormat.Ar, a.Format);
            Assert.Equal(["d/x.txt", "y.txt"], a.Entries.Select(e => e.Name).Order(StringComparer.Ordinal).ToArray());
        }
    }
}
