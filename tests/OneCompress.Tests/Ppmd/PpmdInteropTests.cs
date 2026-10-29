using System.Diagnostics;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Interop with 7-Zip, run as a black box: the PPMd streams it writes for 7z and zip decode to the original bytes, and the streams we write
/// are byte-identical to its. These run only when ONEARCHIVE_INTEROP=1; settings are environment variables: ONEARCHIVE_TOOLS (the directory
/// holding <c>7zz</c>, default ~/onearchive/tools/bin), ONEARCHIVE_CORPORA (public corpus files used as extra inputs; those cases are
/// skipped when it is not set), ONEARCHIVE_INTEROP_TMP (scratch directory).
/// </summary>
[Trait("Category", "Interop")]
public class PpmdInteropTests
{
    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Scratch { get; } = MakeScratch();

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "ppmd-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static string SevenZip
    {
        get
        {
            foreach (string name in new[] { "7zz", "7z" })
            {
                string path = Path.Combine(Tools, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            throw new FileNotFoundException($"7zz is not in {Tools}");
        }
    }

    private static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo(SevenZip) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Scratch };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"7zz {string.Join(' ', args)} failed: {output} {err.Result}");
    }

    private static IEnumerable<(string Name, byte[] Data)> Inputs()
    {
        yield return ("text", PpmdRoundTripTests.Sample(700_000, 1));
        yield return ("small", PpmdRoundTripTests.Sample(1_000, 2));
        yield return ("one", [42]);
        var rng = new Random(3);
        byte[] random = new byte[200_000];
        rng.NextBytes(random);
        yield return ("random", random);
        yield return ("zeros", new byte[400_000]);

        string? corpora = Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA");
        if (corpora is not null)
        {
            foreach (string file in new[] { "dickens", "xml", "mozilla" })
            {
                string path = Path.Combine(corpora, file);
                if (!File.Exists(path))
                {
                    path = Path.Combine(corpora, "x", file);
                }

                if (File.Exists(path))
                {
                    using FileStream fs = File.OpenRead(path);
                    byte[] part = new byte[2_000_000];
                    int n = fs.Read(part, 0, part.Length);
                    yield return (file, part[..n]);
                }
            }
        }
    }

    // With a plain (non-solid, header-uncompressed) one-file archive the packed stream follows the 32-byte start header.
    private static byte[] PackedStreamOf7z(string archive)
    {
        byte[] file = File.ReadAllBytes(archive);
        long nextHeaderOffset = BitConverter.ToInt64(file, 12);
        return file.AsSpan(32, (int)nextHeaderOffset).ToArray();
    }

    // 7-Zip may shrink the model for a small input, so the properties it stored are what the stream was made with.
    // The coder record in the (uncompressed) header is: flags 0x23, ID 03 04 01, properties size 5, order, memory size.
    private static (int Order, int MemorySize) PropertiesOf7z(string archive)
    {
        byte[] file = File.ReadAllBytes(archive);
        ReadOnlySpan<byte> pattern = [0x23, 0x03, 0x04, 0x01, 0x05];
        int at = file.AsSpan(32).LastIndexOf(pattern) + 32;
        Assert.True(at >= 32, "no PPMd coder record in the header");
        return (file[at + 5], (int)BitConverter.ToUInt32(file, at + 6));
    }

    [Fact]
    public void SevenZipWritesTheStreamsWeWriteAndWeReadTheirs()
    {
        RequireEnabled();
        foreach ((string name, byte[] data) in Inputs())
        {
            foreach ((int order, int memMb) in new[] { (2, 1), (4, 1), (6, 16), (16, 8), (32, 4) })
            {
                string input = Path.Combine(Scratch, "in.bin");
                string archive = Path.Combine(Scratch, "out.7z");
                File.WriteAllBytes(input, data);
                File.Delete(archive);
                Run("a", "-bd", "-mhc=off", "-mx=5", $"-m0=PPMd:o={order}:mem={memMb}m", archive, "in.bin");
                byte[] theirs = PackedStreamOf7z(archive);

                (int usedOrder, int usedMemory) = PropertiesOf7z(archive);
                Assert.Equal(order, usedOrder);
                Assert.True(usedMemory <= memMb << 20);
                var options = new PpmdHOptions { Order = usedOrder, MemorySize = usedMemory };
                byte[] ours = PpmdH.Compress(data, options);
                Assert.True(ours.AsSpan().SequenceEqual(theirs), $"{name} o{order} mem{memMb}m (used {usedMemory}): our stream is not the bytes 7-Zip wrote ({ours.Length} vs {theirs.Length})");

                byte[] back = PpmdH.Decompress(theirs, new PpmdHDecompressionOptions { Order = usedOrder, MemorySize = usedMemory, OutputSize = data.Length });
                Assert.True(back.AsSpan().SequenceEqual(data), $"{name} o{order}: decoding 7-Zip's stream");
            }
        }
    }

    [Fact]
    public void SevenZipLevelsMatch()
    {
        RequireEnabled();
        foreach ((string name, byte[] data) in Inputs())
        {
            foreach (int level in new[] { 1, 3, 5, 7, 9 })
            {
                string input = Path.Combine(Scratch, "in.bin");
                string archive = Path.Combine(Scratch, "out.7z");
                File.WriteAllBytes(input, data);
                File.Delete(archive);
                Run("a", "-bd", "-mhc=off", $"-mx={level}", "-m0=PPMd", archive, "in.bin");
                byte[] theirs = PackedStreamOf7z(archive);
                (int order, int memory) = PropertiesOf7z(archive);
                byte[] ours = PpmdH.Compress(data, new PpmdHOptions { Order = order, MemorySize = memory });
                Assert.True(ours.AsSpan().SequenceEqual(theirs), $"{name} -mx={level}: stream differs");
            }
        }
    }

    // A zip local file header is 30 bytes, then the name and the extra field; the entry's data follows.
    private static (ushort Header, byte[] Data, long Size) ZipEntryOf(string archive)
    {
        byte[] file = File.ReadAllBytes(archive);
        if (BitConverter.ToUInt16(file, 8) != 98)
        {
            return (0, [], -1); // 7-Zip stored the file because PPMd did not shrink it
        }

        int nameLength = BitConverter.ToUInt16(file, 26);
        int extraLength = BitConverter.ToUInt16(file, 28);
        long compressed = BitConverter.ToUInt32(file, 18);
        long size = BitConverter.ToUInt32(file, 22);
        int start = 30 + nameLength + extraLength;
        ushort header = BitConverter.ToUInt16(file, start);
        return (header, file.AsSpan(start + 2, (int)compressed - 2).ToArray(), size);
    }

    [Fact]
    public void ZipWritesTheStreamsWeWriteAndWeReadTheirs()
    {
        RequireEnabled();
        foreach ((string name, byte[] data) in Inputs())
        {
            foreach (int level in new[] { 1, 3, 5, 6, 7, 9 })
            {
                string input = Path.Combine(Scratch, "in.bin");
                string archive = Path.Combine(Scratch, "out.zip");
                File.WriteAllBytes(input, data);
                File.Delete(archive);
                Run("a", "-bd", "-tzip", "-mm=PPMd", $"-mx={level}", archive, "in.bin");
                (ushort header, byte[] theirs, long size) = ZipEntryOf(archive);
                if (size < 0)
                {
                    continue;
                }

                Assert.Equal(data.Length, size);

                PpmdIDecompressionOptions read = PpmdIDecompressionOptions.FromHeader(header, data.Length);
                byte[] back = PpmdI.Decompress(theirs, read);
                Assert.True(back.AsSpan().SequenceEqual(data), $"{name} -mx={level}: decoding 7-Zip's stream");

                var options = new PpmdIOptions { Order = read.Order, MemorySize = read.MemorySize, RestoreMethod = read.RestoreMethod, };
                Assert.Equal(header, options.ToHeader());
                byte[] ours = PpmdI.Compress(data, options);
                Assert.True(ours.AsSpan().SequenceEqual(theirs), $"{name} -mx={level} (o{read.Order}, {read.MemorySize >> 20} MiB, restore {read.RestoreMethod}): our stream is not the bytes 7-Zip wrote ({ours.Length} vs {theirs.Length})");
            }
        }
    }

    [Fact]
    public void ZipRestoreMethodsUnderMemoryPressure()
    {
        RequireEnabled();
        string? corpora = Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA");
        Assert.SkipWhen(corpora is null, "set ONEARCHIVE_CORPORA to run the large-input cases");
        string path = Path.Combine(corpora!, "x", "dickens");
        if (!File.Exists(path))
        {
            path = Path.Combine(corpora!, "dickens");
        }

        byte[] data = File.ReadAllBytes(path);
        // 7-Zip picks the restore method from the level: 0 up to -mx=6, the cut-off method from -mx=7.
        foreach ((int order, int memMb, int restore, int level) in new[] { (8, 1, 0, 5), (8, 1, 1, 9), (12, 2, 1, 8), (4, 3, 0, 3) })
        {
            string input = Path.Combine(Scratch, "in.bin");
            string archive = Path.Combine(Scratch, "out.zip");
            File.WriteAllBytes(input, data);
            File.Delete(archive);
            Run("a", "-bd", "-tzip", "-mm=PPMd", $"-mx={level}", $"-mmem={memMb}m", $"-mo={order}", archive, "in.bin");
            (ushort header, byte[] theirs, _) = ZipEntryOf(archive);
            PpmdIDecompressionOptions read = PpmdIDecompressionOptions.FromHeader(header, data.Length);
            Assert.Equal(restore, (int)read.RestoreMethod);
            byte[] ours = PpmdI.Compress(data, new PpmdIOptions { Order = read.Order, MemorySize = read.MemorySize, RestoreMethod = read.RestoreMethod });
            Assert.True(ours.AsSpan().SequenceEqual(theirs), $"o{order} {memMb} MiB restore {restore}: stream differs ({ours.Length} vs {theirs.Length})");
            Assert.True(PpmdI.Decompress(theirs, read).AsSpan().SequenceEqual(data));
        }
    }
}
