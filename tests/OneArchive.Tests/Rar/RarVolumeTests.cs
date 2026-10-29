using System.Security.Cryptography;
using RecursiveAI.OneArchive.Rar;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests.Rar;

/// <summary>Multi-volume sets written by `rar -v40k` (tools/rar-testdata/generate.sh): big.bin spans eight volumes.</summary>
public class RarVolumeTests
{
    private static string First => RarReaderTests.Fixture("volumes/vol.part1.rar");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static Dictionary<string, string> Sources() =>
        File.ReadAllLines(RarReaderTests.Fixture("sources.sha256")).ToDictionary(l => l[66..], l => l[..64]);

    [Fact]
    public void FileSpanningVolumesReadsAsOneStream()
    {
        Dictionary<string, string> sources = Sources();
        using var r = new RarReader(File.OpenRead(First), new RarReaderOptions { Volumes = RarVolumes.ForFile(First) });
        Assert.True(r.Header.IsVolume);
        RarEntry big = r.GetNextEntry()!;
        Assert.Equal("big.bin", big.Name);
        Assert.Equal(300000, big.Size);
        Assert.True(big.ContinuesInNextVolume);
        Assert.True(big.PackedSize < big.Size);
        Assert.Null(big.Crc32);
        Assert.Equal((UnixFileMode)0b100_111_101_101, big.Mode);
        Assert.Equal(sources["big.bin"], Sha(RarReaderTests.ReadAll(r.OpenEntryStream())));

        RarEntry hello = r.GetNextEntry()!;
        Assert.Equal("hello.txt", hello.Name);
        Assert.Equal(7, hello.FirstVolume);
        Assert.Equal(sources["hello.txt"], Sha(RarReaderTests.ReadAll(r.OpenEntryStream())));
        Assert.Null(r.GetNextEntry());
    }

    [Fact]
    public void SkippingASpanningFileMovesThroughTheVolumes()
    {
        Dictionary<string, string> sources = Sources();
        using var r = new RarReader(File.OpenRead(First), new RarReaderOptions { Volumes = RarVolumes.ForFile(First) });
        r.GetNextEntry();
        RarEntry hello = r.GetNextEntry()!;
        Assert.Equal(sources["hello.txt"], Sha(RarReaderTests.ReadAll(r.OpenEntryStream())));
        Assert.Equal("hello.txt", hello.Name);
    }

    [Fact]
    public void ForwardOnlyVolumesWork()
    {
        Dictionary<string, string> sources = Sources();
        var opened = new List<int>();
        RarVolumeProvider provider = n =>
        {
            opened.Add(n);
            string path = RarVolumes.GetVolumePath(First, n)!;
            return File.Exists(path) ? new ForwardOnlyStream(File.ReadAllBytes(path), 777) : null;
        };
        using var r = new RarReader(new ForwardOnlyStream(File.ReadAllBytes(First), 500), new RarReaderOptions { Volumes = provider });
        Assert.Equal("big.bin", r.GetNextEntry()!.Name);
        Assert.Equal(sources["big.bin"], Sha(RarReaderTests.ReadAll(r.OpenEntryStream())));
        Assert.Equal("hello.txt", r.GetNextEntry()!.Name);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], opened);
    }

    [Fact]
    public void AMissingVolumeIsATruncationErrorWhereItIsNeeded()
    {
        RarVolumeProvider missing4 = n => n == 3 ? null : File.OpenRead(RarVolumes.GetVolumePath(First, n)!);
        using var r = new RarReader(File.OpenRead(First), new RarReaderOptions { Volumes = missing4 });
        r.GetNextEntry();
        Stream s = r.OpenEntryStream();
        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => RarReaderTests.ReadAll(s));
        Assert.True(ex.IsTruncated);
        Assert.Contains("volume 4", ex.Message, StringComparison.Ordinal);

        // No provider at all: the same error, naming the option.
        using var alone = new RarReader(File.OpenRead(First));
        alone.GetNextEntry();
        ex = Assert.Throws<CompressionFormatException>(() => RarReaderTests.ReadAll(alone.OpenEntryStream()));
        Assert.True(ex.IsTruncated);
        Assert.Contains("RarReaderOptions.Volumes", ex.Message, StringComparison.Ordinal);

        // Skipping hits the same wall.
        using var skipping = new RarReader(File.OpenRead(First));
        skipping.GetNextEntry();
        Assert.Throws<CompressionFormatException>(() => skipping.GetNextEntry());
    }

    [Fact]
    public void AVolumeOutOfOrderIsRejected()
    {
        RarVolumeProvider swapped = n => File.OpenRead(RarVolumes.GetVolumePath(First, n == 2 ? 3 : n)!);
        using var r = new RarReader(File.OpenRead(First), new RarReaderOptions { Volumes = swapped });
        r.GetNextEntry();
        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => RarReaderTests.ReadAll(r.OpenEntryStream()));
        Assert.Contains("volume 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StartingFromALaterVolumeIsRefused()
    {
        using var r = new RarReader(File.OpenRead(RarVolumes.GetVolumePath(First, 2)!));
        ArchiveUnsupportedException ex = Assert.Throws<ArchiveUnsupportedException>(() => r.GetNextEntry());
        Assert.Contains("first volume", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CorruptDataInAMiddleVolumeFailsThatPartsChecksum()
    {
        using var t = new TempDir();
        for (int i = 0; i < 8; i++)
        {
            File.Copy(RarVolumes.GetVolumePath(First, i)!, Path.Combine(t.Root, Path.GetFileName(RarVolumes.GetVolumePath(First, i)!)));
        }

        string part3 = Path.Combine(t.Root, "vol.part3.rar");
        byte[] bytes = File.ReadAllBytes(part3);
        bytes[20000] ^= 1;
        File.WriteAllBytes(part3, bytes);
        using var r = new RarReader(File.OpenRead(Path.Combine(t.Root, "vol.part1.rar")), new RarReaderOptions { Volumes = RarVolumes.ForFile(Path.Combine(t.Root, "vol.part1.rar")) });
        r.GetNextEntry();
        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => RarReaderTests.ReadAll(r.OpenEntryStream()));
        Assert.Contains("CRC-32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VolumeNamesFollowBothSchemes()
    {
        Assert.Equal(Path.Combine("d", "a.part02.rar"), RarVolumes.GetVolumePath(Path.Combine("d", "a.part01.rar"), 1));
        Assert.Equal(Path.Combine("d", "a.part100.rar"), RarVolumes.GetVolumePath(Path.Combine("d", "a.part099.rar"), 1));
        Assert.Equal(Path.Combine("d", "A.PART3.RAR"), RarVolumes.GetVolumePath(Path.Combine("d", "A.PART1.RAR"), 2));
        Assert.Equal(Path.Combine("d", "a.r00"), RarVolumes.GetVolumePath(Path.Combine("d", "a.rar"), 1));
        Assert.Equal(Path.Combine("d", "a.r99"), RarVolumes.GetVolumePath(Path.Combine("d", "a.rar"), 100));
        Assert.Equal(Path.Combine("d", "a.s00"), RarVolumes.GetVolumePath(Path.Combine("d", "a.rar"), 101));
        Assert.Equal(Path.Combine("d", "a.R00"), RarVolumes.GetVolumePath(Path.Combine("d", "a.RAR"), 1));
        Assert.Null(RarVolumes.GetVolumePath("a.bin", 1));
        Assert.Null(RarVolumes.GetVolumePath("a.rar", -1));
    }

    [Fact]
    public void ArchiveOpensTheWholeSetFromThePathAndExtractsIt()
    {
        using var t = new TempDir();
        string target = t.Combine("out");
        ArchiveExtractResult result = Archive.Extract(First, target);
        Assert.Equal(2, result.EntriesExtracted);
        Dictionary<string, string> sources = Sources();
        Assert.Equal(sources["big.bin"], Sha(File.ReadAllBytes(Path.Combine(target, "big.bin"))));
        Assert.Equal(sources["hello.txt"], Sha(File.ReadAllBytes(Path.Combine(target, "hello.txt"))));
    }

    [Fact]
    public void OldStyleVolumeNamesWork()
    {
        using var t = new TempDir();
        string first = t.Combine("old.rar");
        File.Copy(First, first);
        for (int i = 1; i < 8; i++)
        {
            File.Copy(RarVolumes.GetVolumePath(First, i)!, t.Combine($"old.r{i - 1:D2}"));
        }

        string target = t.Combine("out");
        Archive.Extract(first, target);
        Assert.Equal(Sources()["big.bin"], Sha(File.ReadAllBytes(Path.Combine(target, "big.bin"))));
    }

    [Fact]
    public void ArchiveOnAMissingPartReportsTheTruncation()
    {
        using var t = new TempDir();
        for (int i = 0; i < 8; i++)
        {
            if (i != 4)
            {
                File.Copy(RarVolumes.GetVolumePath(First, i)!, t.Combine(Path.GetFileName(RarVolumes.GetVolumePath(First, i)!)));
            }
        }

        CompressionFormatException ex = Assert.Throws<CompressionFormatException>(() => Archive.Extract(t.Combine("vol.part1.rar"), t.Combine("out")));
        Assert.True(ex.IsTruncated);
        Assert.Contains("volume 5", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(t.Combine("out/big.bin")));
    }
}
