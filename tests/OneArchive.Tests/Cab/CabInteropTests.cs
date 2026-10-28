using RecursiveAI.OneArchive.Cab;
using RecursiveAI.OneCompress.Tests.Lzx;

namespace RecursiveAI.OneArchive.Tests.Cab;

/// <summary>
/// Cabinets built here (stored, MSZIP, LZX) and by gcab are extracted by cabextract, run as a black box, and compared with the
/// inputs and with what <see cref="CabReader"/> returns. They run only with ONEARCHIVE_INTEROP=1, and then a missing tool fails the
/// run. ONEARCHIVE_CABEXTRACT and ONEARCHIVE_GCAB name the executables (defaults: cabextract and gcab on PATH).
/// </summary>
[Trait("Category", "Interop")]
public class CabInteropTests
{
    private static string CabExtract => Environment.GetEnvironmentVariable("ONEARCHIVE_CABEXTRACT") ?? "cabextract";

    private static string Gcab => Environment.GetEnvironmentVariable("ONEARCHIVE_GCAB") ?? "gcab";

    private static void Require()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        Assert.Contains("cabextract", Tools.Run(CabExtract, ["--version"]), StringComparison.Ordinal);
    }

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cabx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, true);
    }

    private static string RunBoth(string tool, string[] args, string workingDirectory)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output + err.Result;
    }

    /// <summary>Extracts with cabextract; returns the tool's output files by relative path, and its log.</summary>
    private static Dictionary<string, byte[]> Extract(byte[] cab, out string log)
    {
        using var scratch = new Scratch();
        string file = Path.Combine(scratch.Path, "t.cab");
        File.WriteAllBytes(file, cab);
        string output = Path.Combine(scratch.Path, "out");
        Directory.CreateDirectory(output);
        log = RunBoth(CabExtract, ["-d", output, file], scratch.Path);
        var result = new Dictionary<string, byte[]>();
        foreach (string f in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
        {
            result[Path.GetRelativePath(output, f).Replace('\\', '/')] = File.ReadAllBytes(f);
        }

        return result;
    }

    private static Dictionary<string, byte[]> ReadWithOurs(byte[] cab)
    {
        using var r = new CabReader(new MemoryStream(cab));
        var result = new Dictionary<string, byte[]>();
        while (r.GetNextEntry() is { } e)
        {
            result[e.Name] = CabReaderTests.ReadAll(r.OpenEntryStream());
        }

        return result;
    }

    private static void AssertSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual, string what)
    {
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach ((string k, byte[] v) in expected)
        {
            Assert.True(v.AsSpan().SequenceEqual(actual[k]), $"{what}: '{k}' differs");
        }
    }

    private static CabTestBuilder BuildCab(CabTestBuilder.Method method, int window, int blockSize, bool reserve = false)
    {
        var b = new CabTestBuilder();
        if (reserve)
        {
            b.HeaderReserve = [1, 2, 3, 4, 5];
            b.FolderReserveSize = 3;
            b.DataReserveSize = 5;
            b.DataReserveFill = [9, 8, 7, 6, 5];
        }

        var f1 = new CabTestBuilder.FolderSpec { Method = method, WindowBits = window, BlockSize = blockSize };
        f1.Files.Add(new CabTestBuilder.FileSpec { Name = "one.txt", Data = TestData.Mixed(70_001, 1, true) });
        f1.Files.Add(new CabTestBuilder.FileSpec { Name = "sub/two.bin", Data = TestData.Mixed(129_999, 2) });
        f1.Files.Add(new CabTestBuilder.FileSpec { Name = "empty", Data = [] });
        b.Folders.Add(f1);
        var f2 = new CabTestBuilder.FolderSpec { Method = method, WindowBits = window, BlockSize = blockSize };
        f2.Files.Add(new CabTestBuilder.FileSpec { Name = "three.dat", Data = TestData.Mixed(40_000, 3) });
        f2.Files.Add(new CabTestBuilder.FileSpec { Name = "\u00fcn\u00ef.txt", Data = TestData.Mixed(100, 4), Utf8Name = true });
        b.Folders.Add(f2);
        return b;
    }

    private static Dictionary<string, byte[]> Inputs(CabTestBuilder b) =>
        b.Folders.SelectMany(f => f.Files).ToDictionary(f => f.Name, f => f.Data);

    [Theory]
    [InlineData(CabTestBuilder.Method.None, 16, 32768, false)]
    [InlineData(CabTestBuilder.Method.None, 16, 5000, true)]
    [InlineData(CabTestBuilder.Method.Mszip, 16, 32768, false)]
    [InlineData(CabTestBuilder.Method.Mszip, 16, 32768, true)]
        [InlineData(CabTestBuilder.Method.MszipNoHistory, 16, 32768, false)]
    [InlineData(CabTestBuilder.Method.Lzx, 15, 32768, false)]
    [InlineData(CabTestBuilder.Method.Lzx, 16, 32768, true)]
    [InlineData(CabTestBuilder.Method.Lzx, 18, 32768, false)]
    [InlineData(CabTestBuilder.Method.Lzx, 21, 32768, true)]
    public void CabextractAndOurReaderAgreeOnBuiltCabinets(CabTestBuilder.Method method, int window, int blockSize, bool reserve)
    {
        Require();
        CabTestBuilder b = BuildCab(method, window, blockSize, reserve);
        byte[] cab = b.Build();
        Dictionary<string, byte[]> expected = Inputs(b);
        Dictionary<string, byte[]> viaTool = Extract(cab, out string log);
        Assert.True(viaTool.Count == expected.Count, "cabextract: " + log);
        foreach (var kv in expected) { if (!kv.Value.AsSpan().SequenceEqual(viaTool[kv.Key])) { int at = 0; while (at < kv.Value.Length && at < viaTool[kv.Key].Length && kv.Value[at] == viaTool[kv.Key][at]) at++; Assert.Fail($"{kv.Key}: len {kv.Value.Length} vs {viaTool[kv.Key].Length}, first diff at {at}; log {log}"); } }
        AssertSame(expected, viaTool, "cabextract");
        AssertSame(expected, ReadWithOurs(cab), "our reader");
    }

    [Fact]
    public void LzxWithEveryBlockTypeAndCallTranslationMatchesCabextract()
    {
        Require();
        LzxTestEncoder.Kind[] kinds = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Uncompressed, LzxTestEncoder.Kind.Aligned, LzxTestEncoder.Kind.Verbatim];
        foreach ((int window, int blockSize, uint? translation) in new (int, int, uint?)[] { (16, 40_000, null), (16, 1500, null), (17, 33_333, 100_000), (21, 50_000, 5_000_000), (15, 9_999, 0) })
        {
            var f = new CabTestBuilder.FolderSpec
            {
                Method = CabTestBuilder.Method.Lzx,
                WindowBits = window,
                Lzx = new LzxTestEncoder.Settings { WindowBits = window, BlockSize = blockSize, Kinds = kinds, TranslationSize = translation },
            };
            f.Files.Add(new CabTestBuilder.FileSpec { Name = "x.bin", Data = TestData.Mixed(300_000, window, true) });
            var b = new CabTestBuilder();
            b.Folders.Add(f);
            byte[] cab = b.Build();
            Dictionary<string, byte[]> expected = Inputs(b);
            AssertSame(expected, Extract(cab, out _), $"cabextract w{window} b{blockSize} t{translation}");
            AssertSame(expected, ReadWithOurs(cab), $"ours w{window} b{blockSize} t{translation}");
        }
    }

    // Choices the format documents leave open, pinned by what cabextract 1.11 accepts. A stream that follows the other reading
    // fails in the reference tool, which is how each one was settled.
    [Fact]
    public void FormatChoicesPinnedByTheReferenceTool()
    {
        Require();
        bool Works(LzxTestEncoder.Settings s, byte[]? data = null)
        {
            var f = new CabTestBuilder.FolderSpec { Method = CabTestBuilder.Method.Lzx, WindowBits = s.WindowBits, Lzx = s };
            f.Files.Add(new CabTestBuilder.FileSpec { Name = "a.bin", Data = data ?? TestData.Mixed(400_000, 1, true) });
            var b = new CabTestBuilder();
            b.Folders.Add(f);
            Dictionary<string, byte[]> got = Extract(b.Build(), out _);
            return got.TryGetValue("a.bin", out byte[]? bytes) && bytes.AsSpan().SequenceEqual(f.Files[0].Data);
        }

        // 0. A data block's checksum covers the compressed bytes and then cbData and cbUncomp only, not the data reserve.
        foreach ((int variant, bool expected) in new[] { (1, true), (0, false) })
        {
            CabTestBuilder cab = BuildCab(CabTestBuilder.Method.None, 16, 32768, reserve: true);
            cab.ChecksumVariant = variant;
            Dictionary<string, byte[]> got = Extract(cab.Build(), out _);
            Assert.Equal(expected, got.TryGetValue("one.txt", out byte[]? one) && one.Length > 0);
        }

        LzxTestEncoder.Kind[] alternate = [LzxTestEncoder.Kind.Verbatim, LzxTestEncoder.Kind.Uncompressed];

        // 1. A repeat code (19) in a tree description sets every length in its run to one value, not a run of equal differences.
        Assert.True(Works(new() { BlockSize = 20_000, Kinds = [LzxTestEncoder.Kind.Verbatim] }));
        Assert.False(Works(new() { BlockSize = 20_000, Kinds = [LzxTestEncoder.Kind.Verbatim], RunsAreDeltas = true }));

        // 2. The padding before an uncompressed block's data is 1 to 16 bits: a header that ends on a word boundary is followed by a whole zero word.
        Assert.True(Works(new() { BlockSize = 1500, Kinds = alternate, FullWordPadding = true }));
        Assert.False(Works(new() { BlockSize = 1500, Kinds = alternate, FullWordPadding = false }));

        // 3. Matches have at most 257 bytes; the extra-length field of LZX DELTA is not part of cabinet LZX.
        byte[] runs = TestData.Mixed(100_000, 5);
        Assert.True(Works(new() { MaxMatch = 257 }, runs));
        Assert.False(Works(new() { MaxMatch = 3000, ExtraLengths = true }, runs));

        // 4. Call translation skips the four operand bytes of every E8, translated or not.
        byte[] unit = [0xE8, 0xE8, 0x00, 0x00, 0xF0, 0x00, 1, 2, 3, 4, 5, 6, 7];
        byte[] calls = new byte[100_000];
        for (int i = 0; i < calls.Length; i++)
        {
            calls[i] = unit[i % unit.Length];
        }

        Assert.True(Works(new() { TranslationSize = 100_000_000 }, calls));
        Assert.False(Works(new() { TranslationSize = 100_000_000, SkipOnlyWhenTranslated = true }, calls));
    }

    [Fact]
    public void CabinetsWrittenByGcabAreReadByteExact()
    {
        Require();
        string gcab = Gcab;
        Assert.Contains("gcab", Tools.Run(gcab, ["--version"]), StringComparison.Ordinal);
        foreach (bool zip in new[] { false, true })
        {
            using var scratch = new Scratch();
            string src = Path.Combine(scratch.Path, "src");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            var files = new Dictionary<string, byte[]>
            {
                ["a.txt"] = "hello\n"u8.ToArray(),
                ["big.bin"] = TestData.Mixed(400_000, 11, true),
                ["empty.txt"] = [],
                ["sub/x.txt"] = TestData.Mixed(5_000, 12),
                ["\u00fcn\u00ef-\u65e5\u672c.txt"] = TestData.Mixed(777, 13),
            };
            foreach ((string name, byte[] data) in files)
            {
                File.WriteAllBytes(Path.Combine(src, name), data);
            }

            string cabPath = Path.Combine(scratch.Path, "g.cab");
            var args = new List<string> { "-c" };
            if (zip)
            {
                args.Add("-z");
            }

            args.AddRange([cabPath, .. files.Keys]);
            Tools.Run(gcab, args, src);
            byte[] cab = File.ReadAllBytes(cabPath);
            AssertSame(files, ReadWithOurs(cab), "our reader on gcab " + (zip ? "MSZIP" : "stored"));
            AssertSame(files, Extract(cab, out _), "cabextract on gcab");
            using var r = new CabReader(new MemoryStream(cab));
            Assert.Equal(zip ? CabCompression.MsZip : CabCompression.None, r.Folders[0].Compression);
        }
    }
}
