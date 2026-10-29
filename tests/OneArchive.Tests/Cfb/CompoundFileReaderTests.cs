using System.Text.Json;
using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>Reads the generated compound file vectors (tools/cfb/gen_cfb.py), checks every field and byte, and rejects the damaged ones.</summary>
public class CompoundFileReaderTests
{
    public static IEnumerable<object[]> GoodFiles() => CfbTestData.Good();

    public static IEnumerable<object[]> BadFiles() => CfbTestData.Bad();

    private static string Name(string hex)
    {
        byte[] b = Convert.FromHexString(hex);
        var chars = new char[b.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(b[i * 2] | (b[(i * 2) + 1] << 8));
        }

        return new string(chars);
    }

    private static ArchiveLimits Roomy => new() { MaxPathDepth = 200, MaxEntryCount = 100000 };

    [Theory]
    [MemberData(nameof(GoodFiles))]
    public void ReadsEveryEntryAndByte(string file)
    {
        JsonElement expected = CfbTestData.GoodEntry(file);
        byte[] data = CfbTestData.Read(file);
        Assert.Equal(expected.GetProperty("length").GetInt32(), data.Length);
        var options = new CompoundFileReaderOptions { Limits = Roomy };
        foreach (bool viaStream in new[] { false, true })
        {
            using var reader = viaStream ? new CompoundFileReader(new MemoryStream(data), options) : new CompoundFileReader(data, options);
            Assert.Equal(expected.GetProperty("version").GetInt32(), reader.Header.MajorVersion);
            Assert.Equal(expected.GetProperty("version").GetInt32() == 3 ? 512 : 4096, reader.Header.SectorSize);
            List<JsonElement> items = expected.GetProperty("entries").EnumerateArray().ToList();
            int i = 0;
            var seen = new List<CfbEntry>();
            while (reader.GetNextEntry() is { } e)
            {
                seen.Add(e);
                JsonElement item = items[i++];
                string[] path = item.GetProperty("path").EnumerateArray().Select(x => Name(x.GetString()!)).ToArray();
                Assert.Equal(path[^1], e.RawName);
                Assert.Equal(string.Join('/', path.Select(CompoundFileReader.SanitizeName)), e.Name);
                if (item.GetProperty("kind").GetString() == "stream")
                {
                    Assert.Equal(ArchiveEntryKind.File, e.Kind);
                    Assert.Equal(CfbObjectType.Stream, e.ObjectType);
                    Assert.Equal(item.GetProperty("size").GetInt64(), e.Size);
                    using Stream s = reader.OpenEntryStream();
                    var ms = new MemoryStream();
                    s.CopyTo(ms);
                    Assert.Equal(item.GetProperty("sha256").GetString(), CfbTestData.Sha(ms.ToArray()));
                    Assert.Equal(e.Size < 4096 && e.Size > 0, e.IsInMiniStream);

                    // By stored path (names that contain a separator cannot be addressed this way).
                    if (path.All(p => !p.Contains('/')))
                    {
                        using Stream byPath = reader.OpenStream(string.Join('/', path));
                        Assert.Equal(e.Size, byPath.Length);
                    }
                }
                else
                {
                    Assert.Equal(ArchiveEntryKind.Directory, e.Kind);
                    Assert.Equal(CfbObjectType.Storage, e.ObjectType);
                    Assert.Equal(0, e.Size);
                    Assert.Equal(item.GetProperty("clsid").GetString(), Convert.ToHexStringLower(e.ClassId.ToByteArray()));
                    Assert.Equal((uint)item.GetProperty("state").GetInt64(), e.StateBits);
                    Assert.Equal((ulong)item.GetProperty("mtime").GetInt64(), e.ModifiedFileTime);
                }
            }

            Assert.Equal(items.Count, i);

            // Random access, backwards.
            for (int k = seen.Count - 1; k >= 0; k--)
            {
                using Stream s = reader.OpenEntryStream(seen[k]);
                Assert.Equal(seen[k].Size, s.Length);
                if (seen[k].Kind == ArchiveEntryKind.File)
                {
                    byte[] all = new byte[s.Length];
                    s.ReadExactly(all);
                    JsonElement item = items[k];
                    Assert.Equal(item.GetProperty("sha256").GetString(), CfbTestData.Sha(all));
                    if (all.Length > 10)
                    {
                        s.Position = all.Length - 10;
                        byte[] tail = new byte[10];
                        s.ReadExactly(tail);
                        Assert.Equal(all[^10..], tail);
                    }
                }
            }
        }
    }

    [Fact]
    public void ExposesHeaderRootAndStorageFields()
    {
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        using var r = new CompoundFileReader(data);
        CompoundFileHeader h = r.Header;
        Assert.Equal(3, h.MajorVersion);
        Assert.Equal(0x3E, h.MinorVersion);
        Assert.Equal(9, h.SectorShift);
        Assert.Equal(6, h.MiniSectorShift);
        Assert.Equal(4096u, h.MiniStreamCutoff);
        Assert.Equal(1u, h.FatSectorCount);
        Assert.Equal(0xFFFFFFFEu, h.FirstDifatSector);
        Assert.Equal(109, h.HeaderDifat.Count);
        Assert.Equal(CfbObjectType.RootStorage, r.Root.ObjectType);
        Assert.Equal(string.Empty, r.Root.Name);
        CfbEntry st = r.Find("storage 1")!;
        Assert.NotNull(st);
        Assert.Equal("Storage 1", st.RawName);
        Assert.Equal(new Guid(Enumerable.Range(1, 16).Select(x => (byte)x).ToArray()), st.ClassId);
        Assert.Equal(7u, st.StateBits);
        Assert.Equal(DateTime.FromFileTimeUtc(133000000000000000), st.ModifiedTime!.Value.UtcDateTime);
        Assert.Null(st.CreationTime);
        Assert.Equal(2, r.GetChildren(st).Count);
        Assert.Null(r.Find("storage 1/missing"));
        Assert.Equal("Stream 1", r.Find("STORAGE 1/stream 1")!.RawName);

        using var v4 = new CompoundFileReader(CfbTestData.Read("v4-basic.cfb"));
        Assert.Equal(4, v4.Header.MajorVersion);
        Assert.Equal(12, v4.Header.SectorShift);
        Assert.True(v4.Header.DirectorySectorCount > 0);
    }

    [Fact]
    public void Version3IgnoresHighStreamSizeBits()
    {
        using var r = new CompoundFileReader(CfbTestData.Read("v3-high-size-garbage.cfb"));
        while (r.GetNextEntry() is { } e)
        {
            Assert.True(e.Size < 100000);
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = r.OpenEntryStream();
                Assert.Equal(e.Size, s.CopyToCount());
            }
        }
    }

    [Fact]
    public void ReadsDifatBeyondTheHeader()
    {
        byte[] data = CfbTestData.Read("v3-difat.cfb.gz");
        using var r = new CompoundFileReader(data);
        Assert.True(r.Header.DifatSectorCount >= 1);
        Assert.True(r.Header.FatSectorCount > 109);
        CfbEntry large = r.Find("large")!;
        Assert.Equal(7_200_000, large.Size);
        byte[] expected = new byte[7_200_000];
        byte[] unit = "cfb difat vector "u8.ToArray();
        for (int i = 0; i < expected.Length; i++)
        {
            expected[i] = unit[i % unit.Length];
        }

        using Stream s = r.OpenEntryStream(large);
        byte[] got = new byte[expected.Length];
        s.ReadExactly(got);
        Assert.True(expected.AsSpan().SequenceEqual(got));
    }

    [Fact]
    public void ListsParentsBeforeChildrenInNameOrder()
    {
        using var r = new CompoundFileReader(CfbTestData.Read("v3-basic.cfb"), new CompoundFileReaderOptions { Limits = Roomy });
        var names = new List<string>();
        while (r.GetNextEntry() is { } e)
        {
            names.Add(e.Name);
        }

        // Shorter names sort first (MS-CFB 2.6.4), each storage is followed by its own children.
        Assert.Equal(["big", "deep", "deep/a", "deep/a/b", "deep/a/b/c", "deep/a/b/c/leaf", "small", "Storage 1", "Storage 1/empty", "Storage 1/Stream 1"], names);
    }

    [Fact]
    public void DepthLimitApplies()
    {
        byte[] data = CfbTestData.Read("v3-deep-70.cfb");
        var ex = Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data);
            r.GetNextEntry();
        });
        Assert.Contains("MaxPathDepth", ex.Message);
        using var ok = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = new ArchiveLimits { MaxPathDepth = 100 } });
        int n = 0;
        while (ok.GetNextEntry() is not null)
        {
            n++;
        }

        Assert.Equal(71, n);
    }

    [Fact]
    public void EntryCountLimitApplies()
    {
        byte[] data = CfbTestData.Read("v3-many-entries.cfb");
        Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = new ArchiveLimits { MaxEntryCount = 100 } });
            while (r.GetNextEntry() is not null)
            {
            }
        });
    }

    [Fact]
    public void TotalSizeLimitApplies()
    {
        byte[] data = CfbTestData.Read("v3-basic.cfb");
        Assert.Throws<CompressionLimitException>(() =>
        {
            using var r = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = new ArchiveLimits { MaxTotalSize = 1000, MaxPathDepth = 100 } });
            while (r.GetNextEntry() is not null)
            {
            }
        });
    }

    [Theory]
    [MemberData(nameof(BadFiles))]
    public void RejectsDamagedFilesWithTypedErrors(string file)
    {
        JsonElement expected = CfbTestData.BadEntry(file);
        byte[] data = CfbTestData.Read(file);
        var options = expected.GetProperty("limit").ValueKind == JsonValueKind.Object
            ? new CompoundFileReaderOptions { MaxDirectoryEntries = expected.GetProperty("limit").GetProperty("MaxDirectoryEntries").GetInt32() }
            : new CompoundFileReaderOptions { Limits = Roomy };
        Exception ex = Assert.ThrowsAny<Exception>(() =>
        {
            using var r = new CompoundFileReader(data, options);
            while (r.GetNextEntry() is not null)
            {
            }
        });
        Assert.True(ex is CompressionFormatException or CompressionLimitException, ex.ToString());
        Assert.Contains(expected.GetProperty("contains").GetString()!, ex.Message);
        if (ex is CompressionFormatException)
        {
            Assert.Contains("cfb", ex.Message);
        }
    }

    [Fact]
    public void TruncationAtEveryOffsetEndsInTypedErrors()
    {
        foreach (string file in new[] { "v3-basic.cfb", "v4-basic.cfb" })
        {
            byte[] data = CfbTestData.Read(file);
            int step = data.Length > 16384 ? 7 : 1;
            for (int len = 0; len < data.Length; len += step)
            {
                Assert.True(Drain(data.AsMemory(0, len)), $"{file} truncated to {len} bytes was accepted");
            }
        }
    }

    [Fact]
    public void BitFlipsEndInTypedErrorsOrCleanReads()
    {
        foreach (string file in new[] { "v3-basic.cfb", "v4-basic.cfb", "v3-names.cfb" })
        {
            byte[] data = CfbTestData.Read(file);
            int step = data.Length > 16384 ? 5 : 1;
            for (int i = 0; i < data.Length; i += step)
            {
                for (int bit = 0; bit < 8; bit += 3)
                {
                    byte[] copy = (byte[])data.Clone();
                    copy[i] ^= (byte)(1 << bit);
                    Drain(copy);
                }
            }
        }
    }

    // True when the data was rejected with a typed error, false when it was read without one.
    private static bool Drain(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var r = new CompoundFileReader(data, new CompoundFileReaderOptions { Limits = Roomy });
            while (r.GetNextEntry() is not null)
            {
                using Stream s = r.OpenEntryStream();
                s.CopyTo(Stream.Null);
            }

            return false;
        }
        catch (Exception e) when (e is CompressionFormatException or CompressionLimitException)
        {
            return true;
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a/b", "a%002Fb")]
    [InlineData("a\\b", "a%005Cb")]
    [InlineData("c:d", "c%003Ad")]
    [InlineData("\u0005Summary", "%0005Summary")]
    [InlineData("100%", "100%0025")]
    [InlineData(".", "%002E")]
    [InlineData("..", "%002E%002E")]
    [InlineData("end.", "end%002E")]
    [InlineData("end ", "end%0020")]
    [InlineData("caf\u00e9", "caf\u00e9")]
    [InlineData("q?*", "q%003F%002A")]
    public void SanitizeNameMapsUnsafeCharacters(string raw, string expected)
    {
        Assert.Equal(expected, CompoundFileReader.SanitizeName(raw));
    }

    [Fact]
    public void SanitizeNameKeepsPairsAndEscapesLoneSurrogates()
    {
        Assert.Equal("a\ud83d\ude00b", CompoundFileReader.SanitizeName("a\ud83d\ude00b"));
        Assert.Equal("a%D800b", CompoundFileReader.SanitizeName("a\ud800b"));
        Assert.Equal("%DC00", CompoundFileReader.SanitizeName("\udc00"));
    }

    [Fact]
    public void SanitizedNamesAreSafeAndDistinct()
    {
        using var r = new CompoundFileReader(CfbTestData.Read("v3-names.cfb"));
        var seen = new HashSet<string>();
        int count = 0;
        while (r.GetNextEntry() is { } e)
        {
            count++;
            Assert.True(seen.Add(e.Name.ToUpperInvariant()), e.Name);
            Assert.DoesNotContain('/', e.Name);
            Assert.All(e.Name, c => Assert.True(c >= 0x20 && c != '\\' && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|'));
            Assert.NotEqual(".", e.Name);
            Assert.NotEqual("..", e.Name);
            Assert.Equal(e.Name, CompoundFileReader.SanitizeName(e.RawName));
        }

        Assert.True(count > 15);
    }

    [Fact]
    public void RejectsForeignAndDisposedUse()
    {
        using var a = new CompoundFileReader(CfbTestData.Read("v3-basic.cfb"));
        using var b = new CompoundFileReader(CfbTestData.Read("v3-basic.cfb"));
        CfbEntry e = a.GetNextEntry()!;
        Assert.Throws<ArgumentException>(() => b.OpenEntryStream(e));
        Assert.Throws<ArgumentException>(() => b.OpenEntryStream(new CfbEntry { Name = "x", RawName = "x" }));
        Assert.Throws<ArgumentException>(() => new CompoundFileReader(new NonSeekable(new MemoryStream(new byte[600]))));
        a.Dispose();
        Assert.Throws<ObjectDisposedException>(() => a.GetNextEntry());
    }

    [Fact]
    public async Task AsyncEnumerationMatchesSync()
    {
        using var r = new CompoundFileReader(CfbTestData.Read("v3-boundaries.cfb"));
        int n = 0;
        while (await r.GetNextEntryAsync(TestContext.Current.CancellationToken) is { } e)
        {
            n++;
            await using Stream s = r.OpenEntryStream();
            Assert.Equal(e.Size, await s.CopyToCountAsync());
        }

        Assert.Equal(13, n);
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

internal static class StreamCountExtensions
{
    public static long CopyToCount(this Stream s)
    {
        long total = 0;
        byte[] buf = new byte[4096];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            total += n;
        }

        return total;
    }

    public static async Task<long> CopyToCountAsync(this Stream s)
    {
        long total = 0;
        byte[] buf = new byte[4096];
        int n;
        while ((n = await s.ReadAsync(buf)) > 0)
        {
            total += n;
        }

        return total;
    }
}
