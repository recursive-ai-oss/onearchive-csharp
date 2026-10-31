using RecursiveAI.OneArchive.Cfb;
using RecursiveAI.OneArchive.Tests.Cfb;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

[CollectionDefinition(nameof(ExtractionReportingTests), DisableParallelization = true)]
public class ExtractionReportingSerial;

/// <summary>Streaming entry enumeration, reporting of damaged entries during extraction, and the wording of the total-size limit message.</summary>
[Collection(nameof(ExtractionReportingTests))]
public class ExtractionReportingTests
{
    private static readonly ArchiveLimits s_limits = new() { MaxPathDepth = 200, MaxEntryCount = 100_000 };

    private static ArchiveOpenOptions Recovering() => new() { Limits = s_limits, CompoundFile = new CompoundFileReaderOptions { Recovery = true, Limits = s_limits } };

    private static ArchiveExtractOptions Extract(DamagedEntryPolicy policy, Action<ArchiveEntry>? callback = null) => new()
    {
        Limits = s_limits,
        CompoundFile = new CompoundFileReaderOptions { Recovery = true, Limits = s_limits },
        DamagedEntries = policy,
        OnDamagedEntry = callback,
    };

    private const string Vector = "damaged/size-beyond-chain-xls.cfb";

    private static int DamagedFilesInVector()
    {
        using Archive a = Archive.Open(new MemoryStream(CfbTestData.Read(Vector)), Recovering());
        return a.EnumerateEntries().Count(e => e is CfbEntry { IsDamaged: true, Kind: ArchiveEntryKind.File });
    }

    [Fact]
    public void DefaultWritesShortenedStreamsButReportsThem()
    {
        int damaged = DamagedFilesInVector();
        Assert.True(damaged > 0);
        using var t = new TempDir();
        var seen = new List<string>();
        ArchiveExtractResult r = Archive.Extract(new MemoryStream(CfbTestData.Read(Vector)), t.Combine("o"), Extract(DamagedEntryPolicy.Extract, e => seen.Add(e.Name)));
        Assert.Equal(damaged, r.EntriesDamaged);
        Assert.Equal(damaged, r.DamagedEntryNames.Count);
        Assert.Equal(seen, r.DamagedEntryNames);
        Assert.Equal(0, r.EntriesSkipped);
        foreach (string name in r.DamagedEntryNames)
        {
            Assert.True(File.Exists(Path.Combine(t.Combine("o"), name)));
        }
    }

    [Fact]
    public void SkipWritesNothingForDamagedStreams()
    {
        int damaged = DamagedFilesInVector();
        using var t = new TempDir();
        ArchiveExtractResult r = Archive.Extract(new MemoryStream(CfbTestData.Read(Vector)), t.Combine("o"), Extract(DamagedEntryPolicy.Skip));
        Assert.Equal(damaged, r.EntriesDamaged);
        Assert.Equal(damaged, r.EntriesSkipped);
        foreach (string name in r.DamagedEntryNames)
        {
            Assert.False(File.Exists(Path.Combine(t.Combine("o"), name)));
        }
    }

    [Fact]
    public void FailThrowsNamingTheEntry()
    {
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionFormatException>(() =>
            Archive.Extract(new MemoryStream(CfbTestData.Read(Vector)), t.Combine("o"), Extract(DamagedEntryPolicy.Fail)));
        Assert.Contains("compound", ex.Format, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("recovery could not follow it", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntactFileReportsNothing()
    {
        using var t = new TempDir();
        ArchiveExtractResult r = Archive.Extract(new MemoryStream(CfbTestData.Read("v3-basic.cfb")), t.Combine("o"), Extract(DamagedEntryPolicy.Fail));
        Assert.Equal(0, r.EntriesDamaged);
        Assert.Empty(r.DamagedEntryNames);
    }

    [Fact]
    public void EnumerateEntriesListsTheSameEntriesAsEntriesAndCanRepeat()
    {
        using Archive a = Archive.Open(new MemoryStream(CfbTestData.Read("v3-basic.cfb")), new ArchiveOpenOptions { Limits = s_limits });
        string[] first = a.EnumerateEntries().Select(e => e.Name).ToArray();
        string[] second = a.EnumerateEntries().Select(e => e.Name).ToArray();
        Assert.Equal(first, second);
        Assert.Equal(a.Entries.Select(e => e.Name).ToArray(), first);
    }

    [Fact]
    public void EnumerateEntriesOverALazyZipHoldsNothingThatGrowsWithTheCount()
    {
        using var temp = new TempDir();
        string path = temp.Combine("many.zip");
        const int Count = 150_000;
        ZipManyEntriesFixture.Write(path, Count);
        var open = new ArchiveOpenOptions { Limits = ArchiveLimits.LargeArchive, Zip = new ZipReaderOptions { LazyDirectory = true } };
        using Archive a = Archive.Open(path, open);
        GC.Collect();
        long baseline = GC.GetTotalMemory(true);
        long worst = 0;
        int n = 0;
        foreach (ArchiveEntry e in a.EnumerateEntries())
        {
            if (++n % 25_000 == 0)
            {
                worst = Math.Max(worst, GC.GetTotalMemory(true) - baseline);
            }
        }

        Assert.Equal(Count, n);
        long listed = a.Entries.Count;
        long withList = GC.GetTotalMemory(true) - baseline;
        TestContext.Current.SendDiagnosticMessage($"enumerating {Count} entries held at most {worst} bytes; Entries holds {withList} bytes");
        // The list behind Entries costs about 100 MiB at this count; streaming stays within the reader's fixed buffers.
        Assert.True(worst < 8 << 20, $"held {worst} bytes while enumerating {Count} entries");
    }

    [Fact]
    public void TotalSizeMessageSaysTheLimitWasPassedNotThatTheArchiveNeedsIt()
    {
        byte[] basic = CfbTestData.Read("v3-basic.cfb");
        using var t = new TempDir();
        var ex = Assert.Throws<CompressionLimitException>(() =>
            Archive.Extract(new MemoryStream(basic), t.Combine("b"), new ArchiveExtractOptions { Limits = new ArchiveLimits { MaxTotalSize = 500, MaxPathDepth = 100 } }));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalSize), ex.LimitName);
        Assert.Equal(500, ex.Limit);
        Assert.True(ex.Requested > 500);
        Assert.Contains($"the total reached {ex.Requested} when it passed the limit of 500", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("needs", ex.Message, StringComparison.Ordinal);

        // A codec-style limit keeps its wording: there N really is what the input asks for.
        var codec = new CompressionLimitException("zstd", "MaxWindowSize", 10, 20);
        Assert.Contains("the input needs 20 but the limit is 10", codec.Message, StringComparison.Ordinal);
    }
}
