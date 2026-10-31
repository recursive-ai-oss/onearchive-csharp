using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>ArchiveLimits.LargeArchive with a generated million-entry zip, the size of a mail export. Nothing here is committed: the fixture is written to a temporary directory.</summary>
public class LargeArchiveTests
{
    private const int Count = 1_000_000;

    [Fact]
    public void PresetKeepsEveryOtherDefault()
    {
        ArchiveLimits large = ArchiveLimits.LargeArchive, d = ArchiveLimits.Default;
        Assert.True(large.MaxEntryCount >= Count && large.MaxTotalSize > 4L << 30);
        Assert.Equal(d.MaxPathLength, large.MaxPathLength);
        Assert.Equal(d.MaxPathDepth, large.MaxPathDepth);
        Assert.Equal(d.MaxExpansionRatio, large.MaxExpansionRatio);
        Assert.Equal(d.MaxWindowSize, large.MaxWindowSize);
        Assert.Equal(d.MaxPpmdMemorySize, large.MaxPpmdMemorySize);
        Assert.Equal(ArchiveLimits.DefaultMaxEntryCount, d.MaxEntryCount);
        Assert.Equal(ArchiveLimits.DefaultMaxTotalSize, d.MaxTotalSize);
    }

    [Fact]
    public void MillionEntryZipOpensListsAndExtractsUnderThePresetAndIsRefusedByDefault()
    {
        using var temp = new TempDir();
        string path = temp.Combine("many.zip");
        ZipManyEntriesFixture.Write(path, Count);
        var zip = new ZipReaderOptions { LazyDirectory = true };

        // Defaults: refused with the entry-count limit, as before.
        var e = Assert.Throws<CompressionLimitException>(() =>
        {
            using Archive a = Archive.Open(path, new ArchiveOpenOptions { Zip = zip });
            _ = a.Entries.Count;
        });
        Assert.Equal(nameof(ArchiveLimits.MaxEntryCount), e.LimitName);

        var open = new ArchiveOpenOptions { Limits = ArchiveLimits.LargeArchive, Zip = zip };
        using (Archive archive = Archive.Open(path, open))
        {
            int n = 0;
            foreach (ArchiveEntry entry in archive.Entries)
            {
                if (n % 99_991 == 0)
                {
                    Assert.Equal(ZipManyEntriesFixture.NameOf(n), entry.Name);
                }

                n++;
            }

            Assert.Equal(Count, n);
        }

        string target = temp.Combine("out");
        ArchiveExtractResult result = Archive.Extract(path, target, new ArchiveExtractOptions { Limits = ArchiveLimits.LargeArchive, Zip = zip });
        Assert.Equal(Count, result.EntriesExtracted);
        Assert.Equal(ZipManyEntriesFixture.ContentOf(999_999), File.ReadAllBytes(Path.Combine(target, ZipManyEntriesFixture.NameOf(999_999))));
        Assert.Equal(ZipManyEntriesFixture.ContentOf(500_001), File.ReadAllBytes(Path.Combine(target, ZipManyEntriesFixture.NameOf(500_001))));
    }
}
