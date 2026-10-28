using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using RecursiveAI.OneCompress.Brotli;
using BclBrotliStream = System.IO.Compression.BrotliStream;
using BR = RecursiveAI.OneCompress.Brotli.Brotli;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Decoder interop: streams written by the brotli tool (run as a black box) and by the BCL's encoder decode to the original bytes.
/// These tests run only when ONEARCHIVE_INTEROP=1; with it set, a missing tool or corpus fails the run. Environment variables:
/// ONEARCHIVE_TOOLS (directory holding <c>brotli</c>, default ~/onearchive/tools/bin), ONEARCHIVE_CORPORA (default ~/onearchive/corpora),
/// ONEARCHIVE_INTEROP_TMP (scratch directory).
/// </summary>
[Trait("Category", "Interop")]
public class BrotliInteropTests
{
    internal static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    internal static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    internal static string Scratch { get; } = MakeScratch();

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "brotli-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    internal static string Tool()
    {
        string pinned = Path.Combine(Tools, "brotli");
        if (File.Exists(pinned))
        {
            return pinned;
        }

        throw new FileNotFoundException($"the brotli tool is not in {Tools}");
    }

    private static byte[] Compress(string path, int quality, int window)
    {
        string output = path + $".q{quality}w{window}.br";
        var psi = new ProcessStartInfo(Tool()) { RedirectStandardError = true };
        foreach (string a in new[] { "-q", quality.ToString(CultureInfo.InvariantCulture), "-w", window.ToString(CultureInfo.InvariantCulture), "-f", "-o", output, path })
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"brotli failed: {err}");
        byte[] bytes = File.ReadAllBytes(output);
        File.Delete(output);
        return bytes;
    }

    private static void Check(byte[] original, byte[] compressed, string label)
    {
        byte[] whole = BR.Decompress(compressed);
        Assert.True(original.AsSpan().SequenceEqual(whole), label + ": one-shot output differs");
        if (original.Length < (1 << 20))
        {
            byte[] pieces = BrotliTestData.DecodeInPieces(compressed, 1021, 4093);
            Assert.True(original.AsSpan().SequenceEqual(pieces), label + ": piecewise output differs");
        }
    }

    internal static Dictionary<string, byte[]> GeneratedInputs()
    {
        var r = new Random(77);
        var random = new byte[100_000];
        r.NextBytes(random);
        var runs = new byte[200_000];
        for (int i = 0; i < runs.Length; i += 1 + r.Next(300))
        {
            Array.Fill(runs, (byte)r.Next(256), i, Math.Min(runs.Length - i, 1 + r.Next(300)));
        }

        var words = new MemoryStream();
        var buffer = new byte[BrotliTransforms.MaxTransformedLength];
        for (int t = 0; t < BrotliTransforms.Count; t++)
        {
            for (int k = 0; k < 24; k++)
            {
                int len = 4 + r.Next(21);
                ReadOnlySpan<byte> w = BrotliWords.GetWord(len, r.Next(BrotliWords.GetWordCount(len)));
                int n = BrotliTransforms.Apply(t, w, buffer);
                words.Write(buffer, 0, n);
                words.WriteByte((byte)' ');
            }
        }

        return new Dictionary<string, byte[]>
        {
            ["empty"] = [],
            ["one"] = [42],
            ["text"] = BZip2TestData.Text(9, 300_000),
            ["random"] = random,
            ["runs"] = runs,
            ["dictwords"] = words.ToArray(),
            ["multi-mb"] = [.. BZip2TestData.Text(1, 3_000_000), .. random, .. BZip2TestData.Text(2, 2_000_000), .. runs],
        };
    }

    [Fact]
    public void ToolStreamsOfGeneratedInputs()
    {
        RequireEnabled();
        foreach ((string name, byte[] data) in GeneratedInputs())
        {
            string path = Path.Combine(Scratch, name + ".bin");
            File.WriteAllBytes(path, data);
            foreach (int window in new[] { 10, 16, 22, 24 })
            {
                for (int q = 0; q <= 11; q++)
                {
                    if (q == 11 && data.Length > 1_000_000 && window != 22)
                    {
                        continue;
                    }

                    Check(data, Compress(path, q, window), $"{name} q{q} w{window}");
                }
            }
        }
    }

    [Theory]
    [InlineData("dickens")]
    [InlineData("xml")]
    [InlineData("x-ray")]
    public void ToolStreamsOfSilesiaFiles(string file)
    {
        RequireEnabled();
        string path = new[] { Path.Combine(Corpora, file), Path.Combine(Corpora, "silesia", file), Path.Combine(Corpora, "x", file) }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"corpus file {file} not found under {Corpora}");
        byte[] data = File.ReadAllBytes(path);
        foreach (int q in new[] { 0, 1, 5, 9, 11 })
        {
            Check(data, Compress(path, q, 24), $"{file} q{q}");
        }
    }

    [Fact]
    public void BclEncoderStreams()
    {
        RequireEnabled();
        foreach ((string name, byte[] data) in GeneratedInputs())
        {
            for (int q = 0; q <= 11; q++)
            {
                {
                    var ms = new MemoryStream();
                    using (var enc = new BclBrotliStream(ms, new BrotliCompressionOptions { Quality = q }, leaveOpen: true))
                    {
                        enc.Write(data);
                    }

                    Check(data, ms.ToArray(), $"bcl {name} q{q}");
                }
            }
        }
    }
}
