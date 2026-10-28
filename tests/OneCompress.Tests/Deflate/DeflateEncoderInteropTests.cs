using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Encoder interop: what our encoder writes, at every level and strategy, decodes to the original bytes with the reference tools
/// (run as black boxes) and with the BCL's decoders, over public corpora. These tests run only when ONEARCHIVE_INTEROP=1; with it
/// set, a missing tool or corpus fails the run. Settings (environment): ONEARCHIVE_TOOLS (default ~/onearchive/tools/bin),
/// ONEARCHIVE_CORPORA (default ~/onearchive/corpora), ONEARCHIVE_DEFLATE_FILES (comma list of corpus file names, default a
/// spread of Canterbury and Silesia files and enwik8), ONEARCHIVE_DEFLATE_LEVELS (default 0 to 12).
/// The gzip-format decoders: gzip, pigz, zlib's minigzip, zlib-ng's minigzip, libdeflate-gzip. Raw and zlib streams are checked
/// with the BCL decoders and, when python3 is on the PATH, with its zlib module (which also takes preset dictionaries).
/// </summary>
[Trait("Category", "Interop")]
public class DeflateEncoderInteropTests
{
    private static readonly string[] DefaultFiles =
        ["alice29.txt", "kennedy.xls", "ptt5", "E.coli", "random.txt", "aaa.txt", "dickens", "mozilla", "mr", "nci", "ooffice", "osdb", "reymont", "samba", "sao", "webster", "x-ray", "xml", "enwik8"];

    private static readonly (string Tool, string[] Args)[] GZipDecoders =
    [
        ("gzip", ["-dc"]),
        ("pigz", ["-dc"]),
        ("minigzip-zlib", ["-d"]),
        ("minigzip-ng", ["-d"]),
        ("libdeflate-gzip", ["-d", "-c"]),
    ];

    private static string Tools => Environment.GetEnvironmentVariable("ONEARCHIVE_TOOLS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "tools", "bin");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive", "corpora");

    private static IEnumerable<string> Files => (Environment.GetEnvironmentVariable("ONEARCHIVE_DEFLATE_FILES") is { Length: > 0 } list ? list.Split(',') : DefaultFiles);

    private static IEnumerable<int> Levels => Environment.GetEnvironmentVariable("ONEARCHIVE_DEFLATE_LEVELS") is { Length: > 0 } list
        ? list.Split(',').Select(static x => int.Parse(x, System.Globalization.CultureInfo.InvariantCulture))
        : Enumerable.Range(0, 13);

    private static void RequireEnabled() =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");

    private static byte[] ReadCorpus(string name)
    {
        foreach (string candidate in new[]
        {
            Path.Combine(Corpora, name), Path.Combine(Corpora, "silesia", name), Path.Combine(Corpora, "enwik8", name),
            Path.Combine(Corpora, "canterbury", "cantrbry", name), Path.Combine(Corpora, "canterbury", "large", name),
            Path.Combine(Corpora, "canterbury", "artificl", name), Path.Combine(Corpora, "cantrbry", name),
            Path.Combine(Corpora, "large", name), Path.Combine(Corpora, "artificl", name),
        })
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllBytes(candidate);
            }
        }

        throw new FileNotFoundException($"corpus file '{name}' not found under {Corpora}");
    }

    // Runs a decoder with the compressed bytes on stdin and returns the SHA-256 and length of what it writes.
    private static (string Hash, long Length) RunTool(string tool, string[] args, byte[] compressed)
    {
        string path = Path.Combine(Tools, tool);
        Assert.True(File.Exists(path), $"tool missing: {path}");
        var info = new ProcessStartInfo(path) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        Task feed = Task.Run(() =>
        {
            try
            {
                process.StandardInput.BaseStream.Write(compressed);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The tool stopped reading: its exit code says why.
            }
        });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        var buffer = new byte[1 << 16];
        Stream output = process.StandardOutput.BaseStream;
        int n;
        while ((n = output.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            length += n;
        }

        feed.Wait();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{tool} exited {process.ExitCode}: {error.Result}");
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), length);
    }

    private static void AssertToolsDecode(byte[] original, byte[] gzip, string what)
    {
        string expected = Convert.ToHexStringLower(SHA256.HashData(original));
        foreach ((string tool, string[] args) in GZipDecoders)
        {
            (string hash, long length) = RunTool(tool, args, gzip);
            Assert.True(length == original.Length && hash == expected, $"{what}: {tool} decoded different bytes");
        }

        using var input = new MemoryStream(gzip);
        using var bcl = new GZipStream(input, CompressionMode.Decompress);
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(bcl)));
    }

    [Fact]
    public void ToolsDecodeEveryLevel()
    {
        RequireEnabled();
        foreach (string file in Files)
        {
            byte[] data = ReadCorpus(file);
            foreach (int level in Levels)
            {
                // Slow levels on a big file are sampled so a run stays within minutes.
                if (data.Length > 20_000_000 && level is 10 or 11)
                {
                    continue;
                }

                byte[] gzip = GZip.Compress(data, new DeflateOptions { Level = level });
                AssertToolsDecode(data, gzip, $"{file} level {level}");
            }
        }
    }

    [Fact]
    public void ToolsDecodeEveryStrategy()
    {
        RequireEnabled();
        foreach (string file in Files.Where(static f => f is "alice29.txt" or "kennedy.xls" or "ptt5" or "dickens" or "x-ray"))
        {
            byte[] data = ReadCorpus(file);
            foreach (DeflateStrategy strategy in Enum.GetValues<DeflateStrategy>())
            {
                foreach (int level in new[] { 1, 6, 11 })
                {
                    byte[] gzip = GZip.Compress(data, new DeflateOptions { Level = level, Strategy = strategy });
                    AssertToolsDecode(data, gzip, $"{file} level {level} {strategy}");
                }
            }
        }
    }

    [Fact]
    public void ToolsDecodeStreamsWithFlushes()
    {
        RequireEnabled();
        foreach (string file in Files.Where(static f => f is "alice29.txt" or "ptt5" or "dickens" or "ooffice"))
        {
            byte[] data = ReadCorpus(file);
            foreach (int level in new[] { 1, 6, 9, 12 })
            {
                var output = new MemoryStream();
                using (var s = new DeflateEncoderStream(output, DeflateFormat.GZip, new DeflateOptions { Level = level }, leaveOpen: true))
                {
                    int step = 0;
                    for (int offset = 0; offset < data.Length; offset += 40_001, step++)
                    {
                        s.Write(data, offset, Math.Min(40_001, data.Length - offset));
                        if (step % 3 == 1)
                        {
                            s.Flush(DeflateFlush.Sync);
                        }
                        else if (step % 3 == 2)
                        {
                            s.Flush(DeflateFlush.Full);
                        }
                    }
                }

                AssertToolsDecode(data, output.ToArray(), $"{file} level {level} with flushes");
            }
        }
    }

    [Fact]
    public void ZLibAndRawStreamsAndDictionariesDecodeWithTheBclAndPython()
    {
        RequireEnabled();
        byte[] dictionary = ReadCorpus("alice29.txt").AsSpan(0, 30_000).ToArray();
        foreach (string file in Files.Where(static f => f is "alice29.txt" or "dickens" or "kennedy.xls"))
        {
            byte[] data = ReadCorpus(file);
            string expected = Convert.ToHexStringLower(SHA256.HashData(data));
            foreach (int level in new[] { 1, 6, 12 })
            {
                foreach (DeflateFormat format in new[] { DeflateFormat.Raw, DeflateFormat.ZLib })
                {
                    byte[] plain = format == DeflateFormat.Raw ? Deflate.Deflate.Compress(data, new DeflateOptions { Level = level }) : ZLib.Compress(data, new DeflateOptions { Level = level });
                    using Stream bcl = format == DeflateFormat.Raw
                        ? new System.IO.Compression.DeflateStream(new MemoryStream(plain), CompressionMode.Decompress)
                        : new ZLibStream(new MemoryStream(plain), CompressionMode.Decompress);
                    Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(bcl)));
                    if (PythonAvailable())
                    {
                        Assert.Equal(expected, PythonDecode(plain, format, null));
                    }

                    // Dictionaries: the BCL cannot take one, so python does.
                    if (PythonAvailable())
                    {
                        var options = new DeflateOptions { Level = level, Dictionary = dictionary };
                        byte[] withDictionary = format == DeflateFormat.Raw ? Deflate.Deflate.Compress(data, options) : ZLib.Compress(data, options);
                        Assert.Equal(expected, PythonDecode(withDictionary, format, dictionary));
                    }
                }
            }
        }
    }

    private static bool PythonAvailable() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator).Any(static d => File.Exists(Path.Combine(d, "python3")));

    private static string PythonDecode(byte[] compressed, DeflateFormat format, byte[]? dictionary)
    {
        string dir = Path.Combine(Path.GetTempPath(), "deflate-enc-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "in.bin");
        string dict = Path.Combine(dir, "dict.bin");
        File.WriteAllBytes(input, compressed);
        if (dictionary is not null)
        {
            File.WriteAllBytes(dict, dictionary);
        }

        // zdict works for both: raw streams get it up front, zlib streams use it when the header asks for one.
        const string script = "import zlib,sys,hashlib;d=open(sys.argv[1],'rb').read();w=int(sys.argv[2]);"
            + "o=zlib.decompressobj(w,zdict=open(sys.argv[3],'rb').read()) if len(sys.argv)>3 else zlib.decompressobj(w);"
            + "r=o.decompress(d);assert o.eof;print(hashlib.sha256(r).hexdigest())";
        var info = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(input);
        info.ArgumentList.Add(format == DeflateFormat.Raw ? "-15" : "15");
        if (dictionary is not null)
        {
            info.ArgumentList.Add(dict);
        }

        using Process p = Process.Start(info)!;
        string output = p.StandardOutput.ReadToEnd();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, "python3 failed: " + error);
        return output.Trim();
    }
}
