using System.Text.Json;
using RecursiveAI.OneArchive.Cfb;

namespace RecursiveAI.OneArchive.Tests.Cfb;

/// <summary>
/// Files written by LibreOffice (testdata/cfb/office, see tools/cfb/make-office-vectors.sh) against the listing and stream hashes that the
/// olefile package reported for them when the files were made. Both tools were run as black boxes.
/// </summary>
public class CfbInteropTests
{
    private static readonly string s_dir = Path.Combine(AppContext.BaseDirectory, "testdata", "cfb", "office");

    public static IEnumerable<object[]> OfficeFiles() =>
        Directory.GetFiles(s_dir).Where(f => f.EndsWith(".doc", StringComparison.Ordinal) || f.EndsWith(".xls", StringComparison.Ordinal) || f.EndsWith(".ppt", StringComparison.Ordinal)).Order().Select(f => new object[] { Path.GetFileName(f) });

    private static string Hex(string name) => Convert.ToHexStringLower(System.Text.Encoding.Unicode.GetBytes(name));

    [Theory]
    [MemberData(nameof(OfficeFiles))]
    public void ListingAndStreamsMatchOlefile(string file)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(s_dir, "olefile.json")));
        JsonElement expected = doc.RootElement.GetProperty(file).GetProperty("entries");
        byte[] data = File.ReadAllBytes(Path.Combine(s_dir, file));
        using var r = new CompoundFileReader(new MemoryStream(data));
        var got = new List<(string Path, string Kind, long Size, string Hash)>();
        var byId = new Dictionary<int, CfbEntry>();
        while (r.GetNextEntry() is { } e)
        {
            byId[e.StreamId] = e;
            var parts = new List<string>();
            for (CfbEntry? c = e; c is not null && c.StreamId != 0; c = c.ParentStreamId == 0 ? null : byId[c.ParentStreamId])
            {
                parts.Insert(0, Hex(c.RawName));
            }

            string hash = string.Empty;
            if (e.Kind == ArchiveEntryKind.File)
            {
                using Stream s = r.OpenEntryStream();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                hash = CfbTestData.Sha(ms.ToArray());
            }

            got.Add((string.Join('/', parts), e.Kind == ArchiveEntryKind.File ? "stream" : "storage", e.Size, hash));
        }

        var want = expected.EnumerateArray().Select(i => (
            Path: string.Join('/', i.GetProperty("path").EnumerateArray().Select(x => x.GetString()!)),
            Kind: i.GetProperty("kind").GetString()!,
            Size: i.GetProperty("kind").GetString() == "stream" ? i.GetProperty("size").GetInt64() : 0L,
            Hash: i.GetProperty("kind").GetString() == "stream" ? i.GetProperty("sha256").GetString()! : string.Empty)).ToList();

        Assert.Equal(want.OrderBy(x => x.Path, StringComparer.Ordinal).ToList(), got.OrderBy(x => x.Path, StringComparer.Ordinal).ToList());
        Assert.True(got.Count > 3);
    }

    [Fact]
    public void ArchiveExtractsAnOfficeFile()
    {
        using var t = new TempDir();
        var result = Archive.Extract(Path.Combine(s_dir, "report.doc"), t.Combine("out"));
        Assert.True(result.EntriesExtracted >= 4);
        Assert.True(File.Exists(Path.Combine(t.Combine("out"), "WordDocument")));
    }

    [Fact]
    public void DamagedOfficeFilesEndInTypedErrors()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(s_dir, "small.xls"));
        for (int len = 0; len < data.Length; len += 3)
        {
            Assert.True(Rejected(data.AsMemory(0, len)), $"truncated to {len}");
        }

        for (int i = 0; i < data.Length; i += 2)
        {
            byte[] copy = (byte[])data.Clone();
            copy[i] ^= (byte)(1 << (i % 8));
            Rejected(copy);
        }
    }

    private static bool Rejected(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var r = new CompoundFileReader(data);
            while (r.GetNextEntry() is not null)
            {
                using Stream s = r.OpenEntryStream();
                s.CopyTo(Stream.Null);
            }

            return false;
        }
        catch (Exception e) when (e is RecursiveAI.OneCompress.CompressionFormatException or RecursiveAI.OneCompress.CompressionLimitException)
        {
            return true;
        }
    }
}
