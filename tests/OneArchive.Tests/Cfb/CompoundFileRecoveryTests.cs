using System.Text.Json;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>
/// Recovery mode over the damaged vectors written by tools/cfb/gen_damaged.py. Each vector is a good file with one kind of damage;
/// the manifest says which streams a reader should still find, how many bytes of each, and their SHA-256.
/// </summary>
public class CompoundFileRecoveryTests
{
    private static readonly CompoundFileReaderOptions s_recover = new() { Recovery = true, Limits = new ArchiveLimits { MaxPathDepth = 200, MaxEntryCount = 100000 } };

    private static JsonElement Manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "testdata", "cfb", "damaged", "manifest.json")));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> Vectors() =>
        Manifest().GetProperty("vectors").EnumerateArray().Select(v => new object[] { v.GetProperty("file").GetString()! });

    public static IEnumerable<object[]> Recoverable() =>
        Manifest().GetProperty("vectors").EnumerateArray().Where(v => !v.GetProperty("unrecoverable").GetBoolean()).Select(v => new object[] { v.GetProperty("file").GetString()! });

    private static JsonElement Vector(string file) =>
        Manifest().GetProperty("vectors").EnumerateArray().First(v => v.GetProperty("file").GetString() == file);

    private static byte[] Data(string file) => CfbTestData.Read("damaged/" + file);

    private static string HexName(string raw) => Convert.ToHexStringLower(System.Text.Encoding.Unicode.GetBytes(raw));

    private static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // The stored-name path of an entry, as the manifest writes it.
    private static string[] RawPath(Dictionary<int, CfbEntry> byId, CfbEntry e)
    {
        var parts = new List<string>();
        CfbEntry cur = e;
        while (cur.StreamId != 0 && byId.ContainsKey(cur.StreamId))
        {
            parts.Add(HexName(cur.RawName));
            if (!byId.TryGetValue(cur.ParentStreamId, out CfbEntry? parent))
            {
                break;
            }

            cur = parent;
        }

        parts.Reverse();
        return parts.ToArray();
    }

    private static List<CfbEntry> AllEntries(CompoundFileReader r)
    {
        var list = new List<CfbEntry>();
        var stack = new Stack<CfbEntry>();
        stack.Push(r.Root);
        while (stack.Count > 0)
        {
            CfbEntry s = stack.Pop();
            foreach (CfbEntry k in r.GetChildren(s))
            {
                list.Add(k);
                if (k.Kind == ArchiveEntryKind.Directory)
                {
                    stack.Push(k);
                }
            }
        }

        return list;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void TheDamageIsRefusedByDefault(string file)
    {
        byte[] data = Data(file);
        Assert.Throws<CompressionFormatException>(() =>
        {
            using var r = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = s_recover.Limits });
            _ = r.Header;
            while (r.GetNextEntry() is { } e)
            {
                if (e.Kind == ArchiveEntryKind.File)
                {
                    using Stream s = r.OpenEntryStream(e);
                    s.CopyTo(Stream.Null);
                }
            }
        });
    }

    [Theory]
    [MemberData(nameof(Recoverable))]
    public void RecoversTheStreamsThatSurvive(string file)
    {
        JsonElement v = Vector(file);
        byte[] data = Data(file);
        bool byName = v.GetProperty("match").GetString() == "name";
        foreach (bool viaStream in new[] { false, true })
        {
            using var r = viaStream ? new CompoundFileReader(new MemoryStream(data), s_recover) : new CompoundFileReader(data, s_recover);
            Assert.True(r.IsRecovered, "a damaged file is reported");
            var kinds = r.Damage.Select(d => d.Kind.ToString()).ToHashSet();
            foreach (JsonElement k in v.GetProperty("kinds").EnumerateArray())
            {
                Assert.Contains(k.GetString()!, kinds);
            }

            List<CfbEntry> entries = AllEntries(r);
            List<CfbEntry> streams = entries.Where(e => e.ObjectType == CfbObjectType.Stream).ToList();
            Dictionary<int, CfbEntry> byId = entries.ToDictionary(e => e.StreamId);
            JsonElement[] expected = v.GetProperty("streams").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, streams.Count);
            long total = 0;
            foreach (JsonElement x in expected)
            {
                CfbEntry e = byName
                    ? streams.Single(s => HexName(s.RawName) == x.GetProperty("name").GetString())
                    : streams.Single(s => RawPath(byId, s).SequenceEqual(x.GetProperty("path").EnumerateArray().Select(p => p.GetString()!)));
                Assert.Equal(x.GetProperty("length").GetInt64(), e.Size);
                Assert.Equal(x.GetProperty("declared").GetInt64(), e.DeclaredSize);
                bool damaged = x.GetProperty("damaged").GetBoolean();
                if (damaged)
                {
                    Assert.True(e.IsDamaged);
                    Assert.Contains(r.Damage, d => d.StreamId == e.StreamId && d.Kind == CfbDamageKind.DamagedStream);
                }

                using Stream s = r.OpenEntryStream(e);
                Assert.Equal(e.Size, s.Length);
                byte[] got = ReadAll(s);
                Assert.Equal(e.Size, got.Length);
                Assert.Equal(x.GetProperty("sha256").GetString(), CfbTestData.Sha(got));
                total += got.Length;
            }

            // Sectors are never shared, so what comes back never exceeds what the file holds.
            Assert.True(total <= data.Length, $"{total} bytes recovered from {data.Length}");
        }
    }

    [Theory]
    [MemberData(nameof(Recoverable))]
    public void TheListingIsTheSameThroughArchiveOpen(string file)
    {
        byte[] data = Data(file);
        var limits = s_recover.Limits;
        using var direct = new CompoundFileReader(data, s_recover);
        int expected = AllEntries(direct).Count;
        using var viaArchive = Archive.Open(new MemoryStream(data), new ArchiveOpenOptions { Limits = limits, CompoundFile = new CompoundFileReaderOptions { Recovery = true } });
        int n = viaArchive.Entries.Count;
        var reader = Assert.IsType<CompoundFileReader>(viaArchive.OpenReader());
        Assert.NotEmpty(reader.Damage);


        Assert.Equal(expected, n);
        Assert.Throws<CompressionFormatException>(() =>
        {
            using var strict = Archive.Open(new MemoryStream(data), new ArchiveOpenOptions { Limits = limits });
            foreach (ArchiveEntry e in strict.Entries)
            {
                if (e.Kind == ArchiveEntryKind.File)
                {
                    using Stream s = strict.OpenEntry(e);
                    s.CopyTo(Stream.Null);
                }
            }
        });
    }

    [Theory]
    [InlineData("unrecoverable-header-only.cfb")]
    [InlineData("unrecoverable-no-fat.cfb.gz")]
    public void NothingLeftToRecoverIsStillAnError(string file)
    {
        Assert.Throws<CompressionFormatException>(() =>
        {
            using var r = new CompoundFileReader(Data(file), s_recover);
            _ = r.Header;
        });
    }

    [Fact]
    public void GoodFilesAreReadTheSameWithRecoveryOn()
    {
        var files = new List<string>();
        foreach (object[] o in CfbTestData.Good())
        {
            files.Add((string)o[0]);
        }

        files.AddRange(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "testdata", "cfb", "office")).Where(f => !f.EndsWith(".json", StringComparison.Ordinal) && !f.EndsWith(".txt", StringComparison.Ordinal)).Select(f => "office/" + Path.GetFileName(f)));
        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            byte[] data = CfbTestData.Read(file);
            var roomy = new ArchiveLimits { MaxPathDepth = 200, MaxEntryCount = 100000 };
            using var strict = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = roomy });
            using var lenient = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = roomy, Recovery = true });
            Assert.Empty(lenient.Damage);
            Assert.False(lenient.IsRecovered);
            while (strict.GetNextEntry() is { } a)
            {
                CfbEntry b = lenient.GetNextEntry()!;
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Size, b.Size);
                Assert.Equal(a.Size, b.DeclaredSize);
                Assert.False(b.IsDamaged);
                if (a.Kind == ArchiveEntryKind.File)
                {
                    using Stream sa = strict.OpenEntryStream(a);
                    using Stream sb = lenient.OpenEntryStream(b);
                    Assert.Equal(CfbTestData.Sha(ReadAll(sa)), CfbTestData.Sha(ReadAll(sb)));
                }
            }

            Assert.Null(lenient.GetNextEntry());
        }
    }

    [Fact]
    public void TheWorkBoundStopsTheDirectorySearch()
    {
        byte[] data = Data("dir-start-bad.cfb.gz");
        var tight = new CompoundFileReaderOptions { Recovery = true, MaxRecoveryBytes = 100 };
        var ex = Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data, tight);
            _ = r.Header;
        });
        Assert.Contains("MaxRecoveryBytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDirectoryLimitStillHolds()
    {
        byte[] data = Data("dir-start-bad.cfb.gz");
        var tight = new CompoundFileReaderOptions { Recovery = true, MaxDirectoryEntries = 4 };
        Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data, tight);
            _ = r.Header;
        });
    }

    [Fact]
    public void TheEntryLimitStillHolds()
    {
        byte[] data = Data("dir-root-wrecked.cfb");
        var tight = new CompoundFileReaderOptions { Recovery = true, Limits = new ArchiveLimits { MaxEntryCount = 3 } };
        Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data, tight);
            _ = r.Header;
        });
    }

    [Fact]
    public void LoopingTablesEndAtOnce()
    {
        // Every FAT entry points at its own sector, and the directory entries link to each other: nothing may loop or grow.
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        var c = (byte[])data.Clone();
        int fatSector = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(76));
        int fatAt = (fatSector + 1) * 512;
        for (int i = 0; i < 128; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(c.AsSpan(fatAt + (i * 4)), (uint)i);
        }

        using var r = new CompoundFileReader(c, s_recover);
        long total = 0;
        foreach (CfbEntry e in AllEntries(r).Where(e => e.Kind == ArchiveEntryKind.File))
        {
            using Stream s = r.OpenEntryStream(e);
            total += ReadAll(s).Length;
        }

        Assert.True(total <= c.Length);
        Assert.NotEmpty(r.Damage);
    }

    [Fact]
    public void RandomDamageNeverEscapesTheExceptionHierarchyOrTheFileSize()
    {
        string[] sources = ["v3-msg-like.cfb", "v4-basic.cfb", "v3-fragmented.cfb", "office/small.doc", "office/small.xls", "v3-names.cfb"];
        var rng = new Random(20261031);
        int recovered = 0;
        foreach (string source in sources)
        {
            byte[] good = CfbTestData.Read(source);
            for (int round = 0; round < 250; round++)
            {
                var data = (byte[])good.Clone();
                int flips = 1 + rng.Next(6);
                for (int i = 0; i < flips; i++)
                {
                    // Bias toward the header and the tables, where one byte changes the most.
                    int at = rng.Next(3) == 0 ? rng.Next(data.Length) : rng.Next(Math.Min(data.Length, 1024));
                    data[at] = (byte)rng.Next(256);
                }

                if (rng.Next(4) == 0)
                {
                    Array.Resize(ref data, rng.Next(512, data.Length));
                }

                try
                {
                    using var r = new CompoundFileReader(data, new CompoundFileReaderOptions { Recovery = true, Limits = new ArchiveLimits { MaxEntryCount = 5000, MaxPathDepth = 64 } });
                    long total = 0;
                    foreach (CfbEntry e in AllEntries(r).Where(e => e.Kind == ArchiveEntryKind.File))
                    {
                        using Stream s = r.OpenEntryStream(e);
                        total += ReadAll(s).Length;
                    }

                    Assert.True(total <= data.Length, $"{source} round {round}: {total} bytes from {data.Length}");
                    recovered++;
                }
                catch (CompressionException)
                {
                }
            }
        }

        Assert.True(recovered > 300, $"only {recovered} damaged files opened");
    }
}
