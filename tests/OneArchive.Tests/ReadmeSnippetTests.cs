using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using RecursiveAI.OneArchive.Zip;
using RecursiveAI.OneCompress;
using RecursiveAI.OneCompress.Checksums;
using RecursiveAI.OneCompress.Deflate;
using RecursiveAI.OneCompress.Lz4;
using RecursiveAI.OneCompress.Lzip;
using RecursiveAI.OneCompress.Lzma;
using RecursiveAI.OneCompress.Xz;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// The code in README.md and PACKAGE.md, compiled and run. A snippet that stops compiling or stops
/// round-tripping fails here, so the documentation cannot drift from the API. Each test is named
/// after the "readme:" marker of the snippet it holds.
/// </summary>
public sealed class ReadmeSnippetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "onearchive-readme-" + Guid.NewGuid().ToString("N"));

    public ReadmeSnippetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp directory is not a test failure.
        }
    }

    private static byte[] SampleText(int length = 100_000)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (sb.Length < length)
        {
            sb.Append("line ").Append(i++).Append(": the quick brown fox jumps over the lazy dog\n");
        }

        return Encoding.ASCII.GetBytes(sb.ToString(0, length));
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;

    private static string Normalize(IEnumerable<string> lines) =>
        string.Join('\n', lines.Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>
    /// Every snippet marked in this file must appear, line for line, in the README or docs page it names, so the
    /// text that readers copy is the text that compiled.
    /// </summary>
    [Fact]
    public void SnippetsAppearVerbatimInTheDocs()
    {
        string source = SourcePath();
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));
        string[] lines = File.ReadAllLines(source);
        int checkedCount = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            string? target = t.StartsWith("// readme: ", StringComparison.Ordinal) ? "README.md"
                : t.StartsWith("// docs: ", StringComparison.Ordinal) ? Path.Combine("docs", t["// docs: ".Length..])
                : null;
            if (target is null)
            {
                continue;
            }

            var body = new List<string>();
            for (i++; i < lines.Length && lines[i].Trim() != "// end"; i++)
            {
                body.Add(lines[i]);
            }

            string page = Normalize(File.ReadAllLines(Path.Combine(root, target)));
            Assert.True(page.Contains(Normalize(body), StringComparison.Ordinal), $"snippet near line {i + 1} of ReadmeSnippetTests.cs is not in {target}");
            checkedCount++;
        }

        Assert.True(checkedCount >= 10);
    }

    [Fact]
    public void Tier1_OneLiners()
    {
        byte[] data = SampleText();

        // readme: tier1
        byte[] packed = Zstd.Compress(data, new ZstdCompressionOptions { Level = 19 });
        byte[] again = Zstd.Decompress(packed);
        // end

        Assert.Equal(data, again);
    }

    [Fact]
    public void Tier2_EncoderAndStream()
    {
        byte[] data = SampleText();
        var output = new MemoryStream();

        // readme: tier2
        var encoder = new ZstdEncoder(new ZstdCompressionOptions { Level = 5 });
        byte[] buffer = new byte[ZstdEncoder.GetMaxCompressedLength(data.Length)];
        OperationStatus status = encoder.Compress(data, buffer, out int consumed, out int written, isFinalBlock: true);
        // status is Done once the frame is complete: buffer[..written] holds it.

        using (var stream = new ZstdStream(output, new ZstdCompressionOptions { Level = 3 }, leaveOpen: true))
        {
            stream.Write(data);
        }
        // end

        Assert.Equal(OperationStatus.Done, status);
        Assert.Equal(data.Length, consumed);
        Assert.Equal(data, Zstd.Decompress(buffer.AsSpan(0, written)));
        Assert.Equal(data, Zstd.Decompress(output.ToArray()));
    }

    [Fact]
    public void Tier3_RawParts()
    {
        byte[] data = SampleText(1000);

        // readme: tier3
        // Optimal length-limited Huffman code lengths, the part deflate's block writer uses.
        uint[] frequencies = new uint[286];
        foreach (byte b in data)
        {
            frequencies[b]++;
        }

        byte[] lengths = new byte[286];
        new DeflateHuffmanBuilder().BuildLengths(frequencies, maxBits: 15, lengths);

        uint crc = Crc32.Compute(data);
        // end

        Assert.Contains(lengths, l => l > 0);
        Assert.NotEqual(0u, crc);
    }

    [Fact]
    public void Zstd_Dictionary_And_Training()
    {
        // readme: zstd-dictionary
        var samples = new List<ReadOnlyMemory<byte>>();
        for (int i = 0; i < 400; i++)
        {
            samples.Add(Encoding.ASCII.GetBytes($"{{\"id\":{i},\"kind\":\"move\",\"x\":{i * 7 % 100},\"y\":{i * 13 % 100},\"owner\":\"player-{i % 4}\"}}"));
        }

        ZstdCompressionDictionary dictionary = ZstdCompressionDictionary.Train(samples, new ZstdDictionaryTrainingOptions { DictionarySize = 2048 });

        byte[] message = Encoding.ASCII.GetBytes("{\"id\":9001,\"kind\":\"move\",\"x\":42,\"y\":17,\"owner\":\"player-1\"}");
        byte[] packed = Zstd.Compress(message, new ZstdCompressionOptions { Level = 3, Dictionary = dictionary });
        byte[] again = Zstd.Decompress(packed, new ZstdDecompressionOptions { Dictionary = dictionary.ForDecompression });
        // end

        Assert.Equal(message, again);
        Assert.True(packed.Length < message.Length);
    }

    [Fact]
    public void Lz4_Gzip_Xz()
    {
        byte[] data = SampleText();

        // readme: lz4-gzip-xz
        byte[] lz4 = Lz4.Compress(data, new Lz4CompressionOptions { Level = 9 });
        byte[] fromLz4 = Lz4.Decompress(lz4);

        byte[] gz = GZip.Compress(data, new DeflateOptions { Level = 6 });
        byte[] fromGz = GZip.Decompress(gz);

        byte[] xz = Xz.Compress(data, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = 6 }, Check = XzCheck.Crc64 });
        byte[] fromXz = Xz.Decompress(xz);
        // end

        Assert.Equal(data, fromLz4);
        Assert.Equal(data, fromGz);
        Assert.Equal(data, fromXz);
    }

    [Fact]
    public void DocPages_Xz_Lzip_Gzip()
    {
        byte[] data = SampleText();

        // docs: xz.md
        byte[] xz = Xz.Compress(data, new XzCompressionOptions { Lzma = new LzmaOptions { Preset = 9 }, Check = XzCheck.Sha256 });
        byte[] fromXz = Xz.Decompress(xz);
        // end

        // docs: lzip.md
        byte[] lz = Lzip.Compress(data, new LzipOptions { Level = 9 });
        byte[] fromLz = Lzip.Decompress(lz);
        // end

        // docs: gzip-zlib.md
        byte[] gz = GZip.Compress(data, new DeflateOptions { Level = 9 });
        byte[] fromGz = GZip.Decompress(gz);
        // end

        Assert.Equal(data, fromXz);
        Assert.Equal(data, fromLz);
        Assert.Equal(data, fromGz);
    }

    [Fact]
    public void Archive_Create_Open_Extract()
    {
        string source = Path.Combine(_dir, "assets");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "hello");
        string archivePath = Path.Combine(_dir, "backup.tar.zst");
        string outDir = Path.Combine(_dir, "out");

        // readme: archive
        Archive.Create(archivePath).Add(source, "assets").Save();    // format and compression come from the extension

        using (Archive archive = Archive.Open(archivePath))           // entries; random access when the source can seek
        {
            foreach (ArchiveEntry entry in archive.Entries)
            {
                Console.WriteLine($"{entry.Name} {entry.Size}");
            }
        }

        ArchiveExtractResult result = Archive.Extract(archivePath, outDir);   // safe by default: nothing is written outside outDir
        // end

        Assert.Equal("hello", File.ReadAllText(Path.Combine(outDir, "assets", "a.txt")));
        Assert.True(result.EntriesExtracted >= 1);
    }

    [Fact]
    public void Zip_With_Aes()
    {
        string source = Path.Combine(_dir, "secret");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "top secret");
        string zipPath = Path.Combine(_dir, "secret.zip");
        string outDir = Path.Combine(_dir, "unzipped");

        // readme: zip-aes
        Archive.Create(zipPath, new ArchiveCreateOptions
        {
            Zip = new ZipWriterOptions { Password = "correct horse", AesStrength = ZipAesStrength.Aes256 },
        }).Add(source, "secret").Save();

        Archive.Extract(zipPath, outDir, new ArchiveExtractOptions { Password = "correct horse" });
        // end

        Assert.Equal("top secret", File.ReadAllText(Path.Combine(outDir, "secret", "a.txt")));
    }

    [Fact]
    public void Zip_Recovery()
    {
        string source = Path.Combine(_dir, "docs");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), new string('a', 5000));
        string zipPath = Path.Combine(_dir, "damaged.zip");
        Archive.Create(zipPath).Add(source, "docs").Save();
        byte[] bytes = File.ReadAllBytes(zipPath);
        File.WriteAllBytes(zipPath, bytes.AsSpan(0, bytes.Length - 30).ToArray());   // lose the end of the directory

        // readme: zip-recovery
        using FileStream file = File.OpenRead(zipPath);
        using var reader = new ZipReader(file, new ZipReaderOptions { Recovery = true });
        while (reader.GetNextEntry() is { } entry)
        {
            using Stream content = reader.OpenEntryStream();
            // read what survives
        }

        foreach (ZipDamage damage in reader.Damage)
        {
            Console.WriteLine($"{damage.Kind} at {damage.Offset}: {damage.Reason}");
        }
        // end

        Assert.True(reader.IsRecovered);
    }

    [Fact]
    public void Safety_Limits()
    {
        string source = Path.Combine(_dir, "big");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "x");
        string archivePath = Path.Combine(_dir, "big.tar.gz");
        Archive.Create(archivePath).Add(source, "big").Save();

        string safeDir = Path.Combine(_dir, "safe");

        // readme: safety
        var options = new ArchiveExtractOptions
        {
            Limits = new ArchiveLimits { MaxTotalSize = 100L << 20, MaxEntryCount = 1000 },
        };

        try
        {
            Archive.Extract(archivePath, safeDir, options);
        }
        catch (CompressionException e)
        {
            Console.WriteLine(e.Message);   // names the format, the offset and what was expected
        }
        // end

        Assert.True(Directory.Exists(safeDir));
    }
}
