using RecursiveAI.OneArchive.Zip;
using static RecursiveAI.OneArchive.Tests.SevenZip.SevenZipFixtures;

namespace RecursiveAI.OneArchive.Tests.SevenZip;

/// <summary>Zip entries stored with method 14 (LZMA) and 95 (xz), written by 7-Zip (testdata/7z/generate.py).</summary>
public class ZipLzmaXzTests
{
    private static string ZipVector(string name) => Vector(name).Replace(".7z", ".zip", StringComparison.Ordinal);

    [Theory]
    [InlineData("zip-lzma", false)]
    [InlineData("zip-lzma", true)]
    [InlineData("zip-lzma-eos", false)]
    [InlineData("zip-lzma-eos", true)]
    [InlineData("zip-xz", false)]
    [InlineData("zip-xz", true)]
    public void ReadsEntriesByteExact(string name, bool forwardOnly)
    {
        var expected = Files();
        using var fs = File.OpenRead(ZipVector(name));
        using var r = new ZipReader(fs, new ZipReaderOptions { ForceStreaming = forwardOnly });
        int files = 0;
        var methods = new HashSet<ZipMethod>();
        while (r.GetNextEntry() is { } e)
        {
            if (e.Kind != ArchiveEntryKind.File)
            {
                continue;
            }

            methods.Add(e.Method);
            using var m = new MemoryStream();
            r.OpenEntryStream().CopyTo(m);
            if (e.Name == "link-to-a")
            {
                // A forward-only read does not see the link type: the entry holds the target.
                Assert.Equal("a.txt"u8.ToArray(), m.ToArray());
                continue;
            }

            Assert.Equal(expected[e.Name], m.ToArray());
            files++;
        }

        Assert.Equal(expected.Count, files);
        Assert.Contains(name == "zip-xz" ? ZipMethod.Xz : ZipMethod.Lzma, methods);
    }

    [Fact]
    public void ExtractsThroughArchive()
    {
        using var tmp = new TempDir();
        Archive.Extract(ZipVector("zip-xz"), tmp.Root);
        foreach ((string rel, byte[] data) in Files())
        {
            Assert.Equal(data, File.ReadAllBytes(Path.Combine(tmp.Root, rel)));
        }
    }

    [Fact]
    public void DamagedEntriesEndInTypedErrors()
    {
        foreach (string name in new[] { "zip-lzma", "zip-xz" })
        {
            byte[] full = File.ReadAllBytes(ZipVector(name));
            for (int i = 30; i < full.Length; i += 61)
            {
                byte[] bad = (byte[])full.Clone();
                bad[i] ^= (byte)(1 << (i % 8));
                foreach (bool forward in new[] { false, true })
                {
                    try
                    {
                        using var r = new ZipReader(new MemoryStream(bad), new ZipReaderOptions { ForceStreaming = forward });
                        while (r.GetNextEntry() is not null)
                        {
                            r.OpenEntryStream().CopyTo(Stream.Null);
                        }
                    }
                    catch (RecursiveAI.OneCompress.CompressionException)
                    {
                    }
                }
            }
        }
    }
}
